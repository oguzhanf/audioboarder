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
public sealed class OpenAITranscribeService : ITranscriptionService, ITranscriptionDiagnosticsSource
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
    private TranscriptionDiagnostics _diagnostics = TranscriptionDiagnostics.Healthy;
    private long _droppedBytes;
    private TimeSpan _droppedDuration;

    public event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;

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
    public TranscriptionDiagnostics Diagnostics
    {
        get
        {
            lock (_bufferGate) return _diagnostics;
        }
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            PublishDiagnostics(TranscriptionRuntimeState.Fatal, "configuration");
            throw new InvalidOperationException("OpenAITranscribeService requires Endpoint + DeploymentName.");
        }
        _ready = true;
        _logger.LogInformation("Cloud transcription ready: {Name}", Name);
        PublishDiagnostics(TranscriptionRuntimeState.Healthy);
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
            var blockAlign = Math.Max(1, chunk.Format.Channels * chunk.Format.BytesPerSample);
            var alignedLength = chunk.Samples.Length - (chunk.Samples.Length % blockAlign);
            if (alignedLength == 0)
                return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());
            rb.Stream.Write(chunk.Samples.Span[..alignedLength]);
            rb.LastAppendAt = DateTimeOffset.UtcNow;
            rb.WindowEnd = chunk.CapturedAt + TimeSpan.FromSeconds(
                alignedLength / (double)chunk.Format.BytesPerSecond);
            TrimToBacklogLimit(rb, chunk.Role);
            PublishDiagnosticsLocked();
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
                    // "force" may bypass our own short transient backoff during stop,
                    // but never an authoritative Azure Retry-After deadline.
                    var retryBlocked = now < rb.RetryNotBefore &&
                        (!force || string.Equals(
                            rb.SafeErrorCode, "rate_limited", StringComparison.Ordinal));
                    if (rb.Stream.Length == 0 || retryBlocked) continue;
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
                        rb.WindowEnd));
                    rb.Stream.SetLength(0);
                }
                PublishDiagnosticsLocked();
            }

            if (pending.Count == 0) return Array.Empty<TranscriptSegment>();
            var tasks = pending.Select(batch => TranscribeBatchAsync(batch, ct)).ToArray();
            try
            {
                var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
                return batches.SelectMany(x => x).ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Mic and loopback are independent batches. If one completed while
                // its sibling was canceled during Retry-After, preserve the completed
                // transcript instead of losing it with the aggregate cancellation.
                var completed = tasks
                    .Where(task => task.IsCompletedSuccessfully)
                    .SelectMany(task => task.Result)
                    .ToArray();
                if (completed.Length > 0) return completed;
                throw;
            }
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
                            rb.SafeErrorCode = null;
                        }
                        PublishDiagnosticsLocked();
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
                    var wait = NormalizeRetryDelay(
                        ex.RetryAfter ?? TimeSpan.FromMilliseconds(500 * attempt));
                    // Persist the authoritative deadline before awaiting it. If stop
                    // cancels this first wait, the batch is requeued but a forced
                    // final flush must still not bypass Azure's Retry-After.
                    MarkRateLimited(batch.Role, wait);
                    if (attempt >= 2)
                    {
                        _logger.LogWarning(
                            "Cloud transcribe rate limited; category={Category} bufferedWindow={Window:F1}s retryIn={Wait:F1}s",
                            "rate_limited", _options.WindowSeconds * Volatile.Read(ref _windowScale), wait.TotalSeconds);
                        Requeue(batch, "rate_limited");
                        return Array.Empty<TranscriptSegment>();
                    }
                    PublishDiagnostics(TranscriptionRuntimeState.RateLimited, "rate_limited",
                        DateTimeOffset.UtcNow + wait);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < 3 && IsTransient(ex, ct))
                {
                    var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                    var code = SafeErrorCode(ex);
                    _logger.LogWarning(
                        "Cloud transcribe attempt failed; attempt={Attempt} category={Category} retryInMs={Delay}",
                        attempt, code, delay.TotalMilliseconds);
                    PublishDiagnostics(TranscriptionRuntimeState.Retrying, code,
                        DateTimeOffset.UtcNow + delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(batch, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            var code = SafeErrorCode(ex);
            Requeue(batch, code);
            _logger.LogWarning(
                "Cloud transcribe failed; category={Category} audioBytes={Bytes} durationMs={DurationMs:F0} retained=true",
                code, batch.Pcm.Length, (batch.End - batch.Start).TotalMilliseconds);
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
        var requestId = TryGetRequestId(resp);
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
                    "Cloud transcription was rate limited.", retryAfter);
            }

            throw new HttpRequestException(
                $"Cloud transcription request failed with status {(int)resp.StatusCode}.",
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
            _logger.LogInformation(
                "Dropped likely-hallucinated transcript; chars={Chars} requestId={RequestId}",
                text.Length, requestId);
            return Array.Empty<TranscriptSegment>();
        }
        _logger.LogInformation(
            "Cloud transcript completed: speaker={Speaker} chars={Chars} window={Sec:F1}s requestId={RequestId}",
            speaker, text.Length, (end - start).TotalSeconds, requestId);
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
            {
                var serverDeadline = DateTimeOffset.UtcNow + wait;
                if (serverDeadline > rb.RetryNotBefore)
                    rb.RetryNotBefore = serverDeadline;
                rb.SafeErrorCode = "rate_limited";
            }
            PublishDiagnosticsLocked("rate_limited");
        }
    }

    /// <summary>
    /// A valid server Retry-After is authoritative. Shortening it creates a tight
    /// 429 loop that prolongs throttling and eventually drops buffered speech.
    /// </summary>
    private static TimeSpan NormalizeRetryDelay(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait;

    private void Requeue(PendingBatch batch, string safeErrorCode)
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
            rb.WindowEnd = rb.WindowEnd > batch.End ? rb.WindowEnd : batch.End;
            if (rb.LastAppendAt == default) rb.LastAppendAt = batch.End;
            rb.FailureCount++;
            var activeServerThrottle =
                string.Equals(rb.SafeErrorCode, "rate_limited", StringComparison.Ordinal) &&
                rb.RetryNotBefore > DateTimeOffset.UtcNow;
            if (!activeServerThrottle)
                rb.SafeErrorCode = safeErrorCode;

            TrimToBacklogLimit(rb, batch.Role);

            // Short, bounded backoff. This is a live transcript, not a durable queue.
            var backoff = Math.Min(
                Math.Max(0, _options.MaxRetryBackoffSeconds),
                0.25 * Math.Pow(2, rb.FailureCount));
            var localRetryNotBefore =
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(backoff);
            if (localRetryNotBefore > rb.RetryNotBefore)
                rb.RetryNotBefore = localRetryNotBefore;
            PublishDiagnosticsLocked(
                activeServerThrottle ? "rate_limited" : safeErrorCode);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException) return !ct.IsCancellationRequested;
        if (ex is not HttpRequestException http) return false;
        return http.StatusCode is null
            or System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            || (int)http.StatusCode.Value >= 500;
    }

    private void TrimToBacklogLimit(RoleBuffer rb, AudioStreamRole role)
    {
        var format = AudioFormat.Mono16kPcm16;
        var blockAlign = format.Channels * format.BytesPerSample;
        var configuredBytes = Math.Max(0L,
            (long)Math.Floor(format.BytesPerSecond * Math.Max(0, _options.MaxBufferedSeconds)));
        var maxBytes = configuredBytes - (configuredBytes % blockAlign);
        if (rb.Stream.Length <= maxBytes) return;

        var all = rb.Stream.ToArray();
        var dropBytes = all.LongLength - maxBytes;
        dropBytes -= dropBytes % blockAlign;
        if (dropBytes <= 0) return;
        rb.Stream.SetLength(0);
        rb.Stream.Write(all.AsSpan((int)dropBytes));
        var dropped = TimeSpan.FromSeconds(dropBytes / (double)format.BytesPerSecond);
        rb.WindowStart += dropped;
        _droppedBytes += dropBytes;
        _droppedDuration += dropped;
        _logger.LogWarning(
            "Transcription audio dropped; role={Role} category={Category} droppedBytes={DroppedBytes} droppedMs={DroppedMs:F0} maxBacklogMs={MaxMs:F0}",
            role, "backlog_limit", dropBytes, dropped.TotalMilliseconds,
            Math.Max(0, _options.MaxBufferedSeconds) * 1000);
    }

    private void PublishDiagnostics(
        TranscriptionRuntimeState requestedState,
        string? safeErrorCode = null,
        DateTimeOffset? retryAt = null)
    {
        TranscriptionDiagnostics? changed;
        lock (_bufferGate)
            changed = UpdateDiagnosticsLocked(requestedState, safeErrorCode, retryAt);
        if (changed is not null) NotifyDiagnostics(changed);
    }

    private void PublishDiagnosticsLocked(string? safeErrorCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        var retryAt = _buffers.Values
            .Select(b => b.RetryNotBefore)
            .Where(t => t > now)
            .DefaultIfEmpty()
            .Min();
        safeErrorCode ??= _buffers.Values
            .Where(b => b.RetryNotBefore > now)
            .Select(b => b.SafeErrorCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        var pendingSeconds = _buffers.Values.Sum(
            b => b.Stream.Length / (double)AudioFormat.Mono16kPcm16.BytesPerSecond);
        var state = _droppedBytes > 0
            ? TranscriptionRuntimeState.AudioDropped
            : retryAt != default && string.Equals(safeErrorCode, "rate_limited", StringComparison.Ordinal)
                ? TranscriptionRuntimeState.RateLimited
                : retryAt != default
                    ? TranscriptionRuntimeState.Retrying
                    : pendingSeconds >= Math.Max(_options.WindowSeconds, _options.MaxBufferedSeconds * 0.5)
                        ? TranscriptionRuntimeState.Backlogged
                        : TranscriptionRuntimeState.Healthy;
        var changed = UpdateDiagnosticsLocked(
            state, safeErrorCode, retryAt == default ? null : retryAt);
        if (changed is not null)
            NotifyDiagnostics(changed);
    }

    private TranscriptionDiagnostics? UpdateDiagnosticsLocked(
        TranscriptionRuntimeState state,
        string? safeErrorCode,
        DateTimeOffset? retryAt)
    {
        var pendingBytes = _buffers.Values.Sum(b => b.Stream.Length);
        var pending = TimeSpan.FromSeconds(
            pendingBytes / (double)AudioFormat.Mono16kPcm16.BytesPerSecond);
        var next = new TranscriptionDiagnostics(
            state,
            pending,
            retryAt,
            _droppedDuration,
            _droppedBytes,
            safeErrorCode);
        if (next == _diagnostics) return null;
        _diagnostics = next;
        return next;
    }

    private void NotifyDiagnostics(TranscriptionDiagnostics diagnostics)
    {
        try { DiagnosticsChanged?.Invoke(this, diagnostics); }
        catch
        {
            _logger.LogWarning(
                "Transcription diagnostics observer failed; category={Category}",
                "diagnostics_observer");
        }
    }

    private static string SafeErrorCode(Exception ex) => ex switch
    {
        RateLimitedException => "rate_limited",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestTimeout } => "request_timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => "service_unavailable",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        InvalidDataException => "invalid_response",
        _ => "transcription_failure",
    };

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-request-id", "apim-request-id", "request-id" })
            if (response.Headers.TryGetValues(name, out var values))
                return values.FirstOrDefault();
        return null;
    }

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
        public DateTimeOffset WindowEnd { get; set; }
        public DateTimeOffset LastAppendAt { get; set; }
        public DateTimeOffset RetryNotBefore { get; set; }
        public int FailureCount { get; set; }
        public string? SafeErrorCode { get; set; }
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
