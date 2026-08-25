using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.Transcription.Cloud;

/// <summary>
/// Cloud transcription via Azure OpenAI gpt-4o-transcribe deployments. Buffers
/// PCM-16 audio PER ROLE so the mic and loopback streams are transcribed
/// independently (mixing their bytes corrupts the audio sent to the model).
/// </summary>
public sealed class OpenAITranscribeService : ITranscriptionService
{
    private readonly CloudTranscriptionOptions _options;
    private readonly ILogger<OpenAITranscribeService> _logger;
    private readonly HttpClient _http;
    private TokenCredential? _credential;
    private readonly object _credGate = new();
    private readonly Dictionary<AudioStreamRole, RoleBuffer> _buffers = new();
    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _ready;

    public OpenAITranscribeService(
        IOptions<CloudTranscriptionOptions> options,
        HttpClient? http = null,
        ILogger<OpenAITranscribeService>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<OpenAITranscribeService>.Instance;
        _http = http ?? new HttpClient { Timeout = _options.RequestTimeout };
        // Credential is built lazily on first auth so that TenantId/ApiKey/UseManagedIdentity
        // values populated AFTER discovery (which mutates the options instance) still take effect.
    }

    public string Name => $"AzureOpenAI.Transcribe/{_options.DeploymentName ?? "?"}";
    public bool IsReady => _ready;

    public Task InitializeAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("OpenAITranscribeService requires Endpoint + DeploymentName.");
        _ready = true;
        _logger.LogInformation("Cloud transcription ready: {Name}", Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready) return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());
        if (chunk.Format.BitsPerSample != 16) return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        // BUFFER ONLY — never call the transcription HTTP API on the consumer's
        // thread. The 250ms flush loop owns all API calls (both the silence-flush
        // and the max-window force-flush), so a long monologue can't block the
        // audio channel and drop live chunks.
        lock (_bufferGate)
        {
            if (!_buffers.TryGetValue(chunk.Role, out var rb))
            {
                rb = new RoleBuffer();
                _buffers[chunk.Role] = rb;
            }
            if (rb.Stream.Length == 0) rb.WindowStart = chunk.CapturedAt;
            rb.Stream.Write(chunk.Samples.Span);
            rb.LastAppendAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());
    }

    public async Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
    {
        if (!_ready) return Array.Empty<TranscriptSegment>();
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var pending = new List<PendingBatch>();
            lock (_bufferGate)
            {
                var minBytes = (int)(AudioFormat.Mono16kPcm16.BytesPerSecond * 0.15);
                // Adaptive: under rate limiting we deliberately buffer LONGER, which
                // sends fewer, larger requests. Latency degrades gracefully instead of
                // the transcript dying in a 429 loop — and shortening the window (the
                // obvious "make it faster" move) is what pushes a modest tier over.
                var effectiveWindow = _options.WindowSeconds * Volatile.Read(ref _windowScale);
                var maxBytes = (int)(AudioFormat.Mono16kPcm16.BytesPerSecond * effectiveWindow);
                foreach (var (role, rb) in _buffers)
                {
                    if (rb.Stream.Length == 0 || (!force && now < rb.RetryNotBefore)) continue;
                    var quietMs = (now - rb.LastAppendAt).TotalMilliseconds;
                    var silencePassed = quietMs >= _options.SilenceFlushMs && rb.Stream.Length >= minBytes;
                    var windowFull = rb.Stream.Length >= maxBytes;
                    if (!force && !silencePassed && !windowFull)
                        continue;
                    _logger.LogInformation("Flushing utterance: role={Role} buffered={Bytes}B quiet={Quiet:F0}ms reason={Reason}",
                        role, rb.Stream.Length, quietMs, force ? "stop" : windowFull ? "window" : "silence");
                    pending.Add(new PendingBatch(
                        role,
                        rb.Stream.ToArray(),
                        role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local,
                        rb.WindowStart,
                        rb.LastAppendAt));
                    rb.Stream.SetLength(0);
                }
            }

            if (pending.Count == 0) return Array.Empty<TranscriptSegment>();
            var tasks = pending.Select(batch => TranscribeBatchAsync(batch, ct)).ToArray();
            var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
            return batches.SelectMany(x => x).ToArray();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeBatchAsync(PendingBatch batch, CancellationToken ct)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await CallApiAsync(
                        batch.Pcm, AudioFormat.Mono16kPcm16, batch.Speaker, batch.Start, batch.End, ct)
                        .ConfigureAwait(false);
                    lock (_bufferGate)
                    {
                        if (_buffers.TryGetValue(batch.Role, out var rb))
                        {
                            rb.FailureCount = 0;
                            rb.RetryNotBefore = default;
                        }
                    }
                    // Recover toward the configured window once calls succeed again.
                    RelaxWindow();
                    return result;
                }
                catch (RateLimitedException ex)
                {
                    // Widening the window is the only thing that actually reduces load;
                    // retrying at the same cadence just burns the quota again.
                    WidenWindow();
                    var wait = Clamp(ex.RetryAfter ?? TimeSpan.FromMilliseconds(500 * attempt));
                    if (attempt >= 2)
                    {
                        MarkRateLimited(batch.Role, wait);
                        _logger.LogWarning(
                            "Cloud transcribe rate limited; buffering {Window:F1}s per request and pausing {Wait:F1}s",
                            _options.WindowSeconds * Volatile.Read(ref _windowScale), wait.TotalSeconds);
                        Requeue(batch);
                        return Array.Empty<TranscriptSegment>();
                    }
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < 3 && IsTransient(ex, ct))
                {
                    var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(ex, "Cloud transcribe attempt {Attempt} failed; retrying in {Delay}ms",
                        attempt, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(batch);
            throw;
        }
        catch (Exception ex)
        {
            Requeue(batch);
            _logger.LogWarning(ex, "Cloud transcribe failed; audio retained for retry");
            return Array.Empty<TranscriptSegment>();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> CallApiAsync(
        byte[] pcm, AudioFormat format, TranscriptSpeaker speaker,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (pcm.Length == 0) return Array.Empty<TranscriptSegment>();

        var wav = WrapWav(pcm, format);
        var endpoint = _options.Endpoint!.TrimEnd('/');
        var url = $"{endpoint}/openai/deployments/{_options.DeploymentName}/audio/transcriptions?api-version={_options.OpenAIApiVersion}";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wav);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");
        form.Add(new StringContent(_options.DeploymentName!), "model");
        if (!string.IsNullOrWhiteSpace(_options.Language))
            form.Add(new StringContent(_options.Language), "language");
        if (!string.IsNullOrWhiteSpace(_options.Prompt))
            form.Add(new StringContent(_options.Prompt), "prompt");
        form.Add(new StringContent(_options.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature");
        form.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        await ApplyAuthAsync(req, ct).ConfigureAwait(false);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // The service tells us how long to wait; guessing is how you get
                // stuck in a 429 loop that never clears.
                var retryAfter = resp.Headers.RetryAfter?.Delta
                    ?? (resp.Headers.RetryAfter?.Date is { } d
                            ? d - DateTimeOffset.UtcNow
                            : (TimeSpan?)null);
                throw new RateLimitedException(
                    $"Cloud transcribe HTTP 429: {Truncate(body, 300)}", retryAfter);
            }

            throw new HttpRequestException(
                $"Cloud transcribe HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}",
                inner: null,
                resp.StatusCode);
        }
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("text", out var t))
            throw new InvalidDataException("Cloud transcribe response did not contain a text field.");
        var raw = (t.GetString() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<TranscriptSegment>();

        var text = TranscriptTextCleaner.Clean(raw, _options.Language);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TranscriptSegment>();
        if (!string.Equals(text, raw, StringComparison.Ordinal))
            _logger.LogInformation("Cleaned foreign-script artefacts from transcript");

        if (IsLikelyHallucination(text))
        {
            _logger.LogInformation("Dropped likely-hallucinated transcript: \"{Text}\"", Truncate(text, 80));
            return Array.Empty<TranscriptSegment>();
        }
        _logger.LogInformation("Cloud transcript: speaker={Speaker} chars={Chars} window={Sec:F1}s text=\"{Text}\"",
            speaker, text.Length, (end - start).TotalSeconds, Truncate(text, 120));
        return new[] { new TranscriptSegment(Guid.NewGuid(), speaker, text, start, end) };
    }

    /// <summary>Multiplier on <see cref="CloudTranscriptionOptions.WindowSeconds"/>,
    /// raised while the service is rate limiting us and relaxed as calls succeed.</summary>
    private double _windowScale = 1.0;
    private const double MaxWindowScale = 4.0;

    private void WidenWindow()
    {
        var next = Math.Min(MaxWindowScale, Volatile.Read(ref _windowScale) * 1.6);
        Volatile.Write(ref _windowScale, next);
    }

    private void RelaxWindow()
    {
        var current = Volatile.Read(ref _windowScale);
        if (current <= 1.0) return;
        Volatile.Write(ref _windowScale, Math.Max(1.0, current * 0.9));
    }

    private void MarkRateLimited(AudioStreamRole role, TimeSpan wait)
    {
        lock (_bufferGate)
        {
            if (_buffers.TryGetValue(role, out var rb))
                rb.RetryNotBefore = DateTimeOffset.UtcNow + wait;
        }
    }

    /// <summary>A live transcript cannot wait a minute, whatever the header says.</summary>
    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero
        : wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10)
        : wait;

    private void Requeue(PendingBatch batch)
    {
        lock (_bufferGate)
        {
            if (!_buffers.TryGetValue(batch.Role, out var rb))
            {
                rb = new RoleBuffer();
                _buffers[batch.Role] = rb;
            }
            var newer = rb.Stream.ToArray();
            rb.Stream.SetLength(0);
            rb.Stream.Write(batch.Pcm);
            rb.Stream.Write(newer);
            rb.WindowStart = batch.Start;
            if (rb.LastAppendAt == default) rb.LastAppendAt = batch.End;
            rb.FailureCount++;

            // Never let a backlog grow without bound: a long outage would otherwise
            // build an ever-larger payload that is slower and likelier to fail again.
            // Losing the oldest audio beats stalling the whole live transcript.
            var maxBytes = (long)(AudioFormat.Mono16kPcm16.BytesPerSecond * _options.MaxBufferedSeconds);
            if (rb.Stream.Length > maxBytes)
            {
                var all = rb.Stream.ToArray();
                var keep = all.AsSpan((int)(all.Length - maxBytes));
                rb.Stream.SetLength(0);
                rb.Stream.Write(keep);
                _logger.LogWarning(
                    "Transcription backlog exceeded {Max}s for role={Role}; dropped {Dropped}B of the oldest audio",
                    _options.MaxBufferedSeconds, batch.Role, all.Length - maxBytes);
            }

            // Short, bounded backoff. This is a live transcript, not a durable queue.
            var backoff = Math.Min(
                _options.MaxRetryBackoffSeconds, 0.25 * Math.Pow(2, rb.FailureCount));
            rb.RetryNotBefore = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(backoff);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {        if (ex is OperationCanceledException) return !ct.IsCancellationRequested;
        if (ex is not HttpRequestException http) return false;
        return http.StatusCode is null
            or System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            || (int)http.StatusCode.Value >= 500;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Whisper-family models (including gpt-4o-transcribe) emit stock phrases
    /// when fed near-silence. The neural/energy VAD keeps most silence out; this
    /// is a cheap whole-segment filter. Short generic words are matched only by
    /// EXACT equality (so "are you" / "see you" survive); the long YouTube boiler
    /// plate is matched as a substring.
    /// </summary>
    internal static bool IsLikelyHallucination(string text)
    {
        var t = new string(text.Where(c => !char.IsPunctuation(c)).ToArray())
            .Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        if (ExactGarbage.Contains(t)) return true;
        foreach (var phrase in SubstringGarbage)
            if (t == phrase || (t.Length <= phrase.Length + 6 && t.Contains(phrase)))
                return true;
        return false;
    }

    // Matched ONLY by exact equality — never as substrings — so legitimate
    // captions that merely contain these words are not dropped.
    private static readonly HashSet<string> ExactGarbage = new(StringComparer.Ordinal)
    {
        "you", "bye", "bye bye", "okay", "ok", "mm", "mm hmm", "uh", "um", "hmm",
        "thank you", "thanks", "thank you very much",
    };

    // Matched as substrings — distinctive enough that containment is safe.
    private static readonly string[] SubstringGarbage =
    {
        "thanks for watching", "thank you for watching",
        "thanks for watching everyone", "please subscribe", "subscribe to my channel",
        "see you next time", "see you in the next video", "subtitles by the amaraorg community",
        "subtitles by", "transcription by", "for more information visit",
    };

    private async Task ApplyAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            req.Headers.Add("api-key", _options.ApiKey);
            return;
        }
        var cred = GetOrCreateCredential()
            ?? throw new InvalidOperationException("No credential available (set ApiKey or enable UseManagedIdentity).");
        var token = await cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private TokenCredential? GetOrCreateCredential()
    {
        if (_credential is not null) return _credential;
        if (!_options.UseManagedIdentity || !string.IsNullOrWhiteSpace(_options.ApiKey)) return null;
        lock (_credGate)
        {
            if (_credential is not null) return _credential;
            _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId,
                ExcludeInteractiveBrowserCredential = false,
                ExcludeAzurePowerShellCredential = true,
            });
            return _credential;
        }
    }

    /// <summary>Wraps raw PCM-16 in a minimal RIFF/WAV header.</summary>
    internal static byte[] WrapWav(byte[] pcm, AudioFormat fmt)
    {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var byteRate = fmt.SampleRate * fmt.Channels * fmt.BytesPerSample;
            var blockAlign = (short)(fmt.Channels * fmt.BytesPerSample);
            bw.Write("RIFF".ToCharArray());
            bw.Write(36 + pcm.Length);
            bw.Write("WAVE".ToCharArray());
            bw.Write("fmt ".ToCharArray());
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)fmt.Channels);
            bw.Write(fmt.SampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)fmt.BitsPerSample);
            bw.Write("data".ToCharArray());
            bw.Write(pcm.Length);
            bw.Write(pcm);
        }
        return ms.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        lock (_bufferGate)
        {
            foreach (var rb in _buffers.Values) rb.Stream.Dispose();
            _buffers.Clear();
        }
        _flushGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct PendingBatch(
        AudioStreamRole Role,
        byte[] Pcm,
        TranscriptSpeaker Speaker,
        DateTimeOffset Start,
        DateTimeOffset End);

    private sealed class RoleBuffer
    {
        public MemoryStream Stream { get; } = new();
        public DateTimeOffset WindowStart { get; set; }
        public DateTimeOffset LastAppendAt { get; set; }
        public DateTimeOffset RetryNotBefore { get; set; }
        public int FailureCount { get; set; }
    }
}

/// <summary>
/// Signals an HTTP 429, carrying the service's own <c>Retry-After</c> hint so the
/// caller can wait exactly as long as it was told to rather than guessing.
/// </summary>
internal sealed class RateLimitedException(string message, TimeSpan? retryAfter)
    : HttpRequestException(message, null, System.Net.HttpStatusCode.TooManyRequests)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
