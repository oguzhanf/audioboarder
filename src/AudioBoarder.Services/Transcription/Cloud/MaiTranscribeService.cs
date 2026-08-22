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
/// Cloud transcription via Microsoft MAI-Transcribe-1. Per-role buffering so
/// mic and loopback streams are transcribed independently.
/// </summary>
public sealed class MaiTranscribeService : ITranscriptionService
{
    private readonly CloudTranscriptionOptions _options;
    private readonly ILogger<MaiTranscribeService> _logger;
    private readonly HttpClient _http;
    private TokenCredential? _credential;
    private readonly object _credGate = new();
    private readonly Dictionary<AudioStreamRole, RoleBuffer> _buffers = new();
    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _ready;

    public MaiTranscribeService(
        IOptions<CloudTranscriptionOptions> options,
        HttpClient? http = null,
        ILogger<MaiTranscribeService>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<MaiTranscribeService>.Instance;
        _http = http ?? new HttpClient { Timeout = _options.RequestTimeout };
        // Credential is built lazily on first auth so options mutated after construction still apply.
    }

    public string Name => $"MAI.Transcribe/{_options.DeploymentName ?? "?"}";
    public bool IsReady => _ready;

    public Task InitializeAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("MaiTranscribeService requires Endpoint + DeploymentName.");
        _ready = true;
        _logger.LogInformation("MAI transcription ready: {Name}", Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready || chunk.Format.BitsPerSample != 16)
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

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
                var maxBytes = (int)(AudioFormat.Mono16kPcm16.BytesPerSecond * _options.WindowSeconds);
                foreach (var (role, rb) in _buffers)
                {
                    var quietMs = (now - rb.LastAppendAt).TotalMilliseconds;
                    if (rb.Stream.Length == 0 || (!force && now < rb.RetryNotBefore) ||
                        (!force && rb.Stream.Length < maxBytes &&
                         (rb.Stream.Length < minBytes || quietMs < _options.SilenceFlushMs)))
                        continue;
                    pending.Add(new PendingBatch(role, rb.Stream.ToArray(),
                        role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local,
                        rb.WindowStart, rb.LastAppendAt));
                    rb.Stream.SetLength(0);
                }
            }

            var results = await Task.WhenAll(pending.Select(batch => TranscribeBatchAsync(batch, ct)))
                .ConfigureAwait(false);
            return results.SelectMany(x => x).ToArray();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeBatchAsync(
        PendingBatch batch, CancellationToken ct)
    {
        try
        {
            var result = await CallApiAsync(batch.Pcm, AudioFormat.Mono16kPcm16,
                batch.Speaker, batch.Start, batch.End, ct).ConfigureAwait(false);
            lock (_bufferGate)
            {
                if (_buffers.TryGetValue(batch.Role, out var rb))
                {
                    rb.FailureCount = 0;
                    rb.RetryNotBefore = default;
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(batch);
            throw;
        }
        catch (Exception ex)
        {
            Requeue(batch);
            _logger.LogWarning(ex, "MAI transcribe failed; audio retained for retry");
            return Array.Empty<TranscriptSegment>();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> CallApiAsync(
        byte[] pcm, AudioFormat format, TranscriptSpeaker speaker,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (pcm.Length == 0) return Array.Empty<TranscriptSegment>();
        var wav = OpenAITranscribeService.WrapWav(pcm, format);

        var endpoint = _options.Endpoint!.TrimEnd('/');
        if (endpoint.Contains(".cognitiveservices.azure.com"))
            endpoint = endpoint.Replace(".cognitiveservices.azure.com", ".services.ai.azure.com");
        var url = $"{endpoint}/mai/v1/audio/transcriptions";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wav);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");
        form.Add(new StringContent(_options.DeploymentName!), "model");
        if (!string.IsNullOrWhiteSpace(_options.Language))
            form.Add(new StringContent(_options.Language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        await ApplyAuthAsync(req, ct).ConfigureAwait(false);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MAI transcribe HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}",
                null,
                resp.StatusCode);
        }
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("text", out var t))
            throw new InvalidDataException("MAI transcribe response did not contain a text field.");
        var text = (t.GetString() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TranscriptSegment>();
        _logger.LogInformation("MAI transcript: speaker={Speaker} chars={Chars} window={Sec:F1}s",
            speaker, text.Length, (end - start).TotalSeconds);
        return new[] { new TranscriptSegment(Guid.NewGuid(), speaker, text, start, end) };
    }

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
            rb.RetryNotBefore = DateTimeOffset.UtcNow +
                TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, rb.FailureCount)));
        }
    }

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
