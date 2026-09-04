using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.Transcription.Cloud;

/// <summary>
/// Cloud transcription via Microsoft MAI-Transcribe-1. Per-role buffering so
/// mic and loopback streams are transcribed independently.
/// </summary>
public sealed class MaiTranscribeService : ITranscriptionService, ITranscriptionDiagnosticsSource
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
    private TranscriptionDiagnostics _diagnostics = TranscriptionDiagnostics.Healthy;
    private long _droppedBytes;
    private TimeSpan _droppedDuration;
    private readonly HashSet<string> _rejectedApiKeys = new(StringComparer.Ordinal);
    private readonly HashSet<TokenCredential> _rejectedCredentials =
        new(ReferenceEqualityComparer.Instance);

    public event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;

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
    public TranscriptionDiagnostics Diagnostics
    {
        get { lock (_bufferGate) return _diagnostics; }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _ready = false;
        if (!_options.IsConfigured)
        {
            PublishDiagnostics(TranscriptionRuntimeState.Fatal, "configuration");
            throw new TranscriptionInitializationException(
                "MaiTranscribeService requires Endpoint + DeploymentName.",
                "configuration");
        }

        try
        {
            if (IsCurrentCredentialRejected())
            {
                throw new TranscriptionInitializationException(
                    "The configured MAI transcription credential was rejected by the data plane.",
                    "authentication_required");
            }
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                var credential = GetOrCreateCredential()
                    ?? throw new TranscriptionInitializationException(
                        "No MAI transcription credential is available.",
                        "credential_unavailable");
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token.Token) || token.ExpiresOn <= DateTimeOffset.UtcNow)
                    throw new TranscriptionInitializationException(
                        "The MAI transcription credential returned no usable token.",
                        "credential_unavailable");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = TranscriptionInitializationException.SafeCode(ex);
            PublishDiagnostics(TranscriptionRuntimeState.Fatal, code);
            if (ex is TranscriptionInitializationException initialization)
                throw initialization;
            throw new TranscriptionInitializationException(
                "MAI transcription authentication could not be initialized.",
                code,
                ex);
        }

        _ready = true;
        _logger.LogInformation("MAI transcription ready: {Name}", Name);
        PublishDiagnostics(TranscriptionRuntimeState.Healthy);
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
            rb.WindowEnd = rb.WindowStart + TimeSpan.FromSeconds(
                rb.Stream.Length / (double)AudioFormat.Mono16kPcm16.BytesPerSecond);
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
                var maxBytes = (int)(AudioFormat.Mono16kPcm16.BytesPerSecond * _options.WindowSeconds);
                foreach (var (role, rb) in _buffers)
                {
                    var quietMs = (now - rb.LastAppendAt).TotalMilliseconds;
                    var retryBlocked = now < rb.RetryNotBefore &&
                        (!force || string.Equals(
                            rb.SafeErrorCode, "rate_limited", StringComparison.Ordinal));
                    if (rb.Stream.Length == 0 || retryBlocked ||
                        (!force && rb.Stream.Length < maxBytes &&
                         (rb.Stream.Length < minBytes || quietMs < _options.SilenceFlushMs)))
                        continue;
                    pending.Add(new PendingBatch(role, rb.Stream.ToArray(),
                        role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local,
                        rb.WindowStart, rb.WindowEnd));
                    rb.Stream.SetLength(0);
                }
                PublishDiagnosticsLocked();
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
        var authentication = CaptureAuthentication();
        try
        {
            var result = await CallApiAsync(batch.Pcm, AudioFormat.Mono16kPcm16,
                batch.Speaker, batch.Start, batch.End, authentication, ct).ConfigureAwait(false);
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
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(batch);
            throw;
        }
        catch (RateLimitedException ex)
        {
            var wait = ex.RetryAfter is { } retry && retry > TimeSpan.Zero
                ? retry
                : TimeSpan.FromSeconds(1);
            MarkRateLimited(batch.Role, wait);
            Requeue(batch, "rate_limited");
            _logger.LogWarning(
                "MAI transcribe rate limited; retryIn={RetrySeconds:F1}s audioBytes={Bytes} retained=true",
                wait.TotalSeconds, batch.Pcm.Length);
            return Array.Empty<TranscriptSegment>();
        }
        catch (Exception ex)
        {
            var code = SafeErrorCode(ex);
            if (code == "authentication_required")
                MarkAuthenticationRejected(authentication);
            Requeue(batch, code);
            _logger.LogWarning(
                "MAI transcribe failed; category={Category} audioBytes={Bytes} retained=true",
                code, batch.Pcm.Length);
            return Array.Empty<TranscriptSegment>();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> CallApiAsync(
        byte[] pcm, AudioFormat format, TranscriptSpeaker speaker,
        DateTimeOffset start, DateTimeOffset end,
        AuthenticationSnapshot authentication,
        CancellationToken ct)
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
        await ApplyAuthAsync(req, authentication, ct).ConfigureAwait(false);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta
                    ?? (resp.Headers.RetryAfter?.Date is { } date
                        ? date - DateTimeOffset.UtcNow
                        : (TimeSpan?)null);
                throw new RateLimitedException(
                    "MAI transcription was rate limited.",
                    retryAfter);
            }
            throw new HttpRequestException(
                $"MAI transcription request failed with status {(int)resp.StatusCode}.",
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

    private void Requeue(PendingBatch batch, string safeErrorCode = "cancelled")
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
            rb.WindowEnd = rb.WindowStart + TimeSpan.FromSeconds(
                rb.Stream.Length / (double)AudioFormat.Mono16kPcm16.BytesPerSecond);
            if (rb.LastAppendAt == default) rb.LastAppendAt = DateTimeOffset.UtcNow;
            rb.FailureCount++;
            var activeServerThrottle =
                string.Equals(rb.SafeErrorCode, "rate_limited", StringComparison.Ordinal) &&
                rb.RetryNotBefore > DateTimeOffset.UtcNow;
            if (!activeServerThrottle)
                rb.SafeErrorCode = safeErrorCode;
            var localRetry = DateTimeOffset.UtcNow +
                TimeSpan.FromSeconds(Math.Min(
                    Math.Max(0, _options.MaxRetryBackoffSeconds),
                    0.25 * Math.Pow(2, rb.FailureCount)));
            if (localRetry > rb.RetryNotBefore)
                rb.RetryNotBefore = localRetry;
            TrimToBacklogLimit(rb, batch.Role);
            PublishDiagnosticsLocked(
                activeServerThrottle ? "rate_limited" : safeErrorCode);
        }
    }

    private void MarkRateLimited(AudioStreamRole role, TimeSpan wait)
    {
        lock (_bufferGate)
        {
            if (_buffers.TryGetValue(role, out var rb))
            {
                var deadline = DateTimeOffset.UtcNow +
                    (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
                if (deadline > rb.RetryNotBefore)
                    rb.RetryNotBefore = deadline;
                rb.SafeErrorCode = "rate_limited";
            }
            PublishDiagnosticsLocked("rate_limited");
        }
    }

    private void TrimToBacklogLimit(RoleBuffer rb, AudioStreamRole role)
    {
        var format = AudioFormat.Mono16kPcm16;
        var blockAlign = format.Channels * format.BytesPerSample;
        var maxBytes = (long)Math.Floor(
            format.BytesPerSecond * _options.EffectiveMaxBufferedSeconds);
        maxBytes -= maxBytes % blockAlign;
        if (rb.Stream.Length <= maxBytes) return;

        var all = rb.Stream.ToArray();
        var dropBytes = all.LongLength - maxBytes;
        dropBytes -= dropBytes % blockAlign;
        if (dropBytes <= 0) return;
        rb.Stream.SetLength(0);
        rb.Stream.Write(all.AsSpan((int)dropBytes));
        var dropped = TimeSpan.FromSeconds(
            dropBytes / (double)format.BytesPerSecond);
        rb.WindowStart += dropped;
        _droppedBytes += dropBytes;
        _droppedDuration += dropped;

        rb.UnloggedDroppedBytes += dropBytes;
        var now = DateTimeOffset.UtcNow;
        if (rb.LastDropWarningAt == default ||
            now - rb.LastDropWarningAt >= TimeSpan.FromSeconds(5) ||
            rb.UnloggedDroppedBytes >= format.BytesPerSecond * 5L)
        {
            _logger.LogWarning(
                "MAI transcription audio dropped; role={Role} category={Category} droppedBytesSinceLast={DroppedBytes} totalDroppedBytes={TotalDroppedBytes}",
                role, "backlog_limit", rb.UnloggedDroppedBytes, _droppedBytes);
            rb.UnloggedDroppedBytes = 0;
            rb.LastDropWarningAt = now;
        }
    }

    private void PublishDiagnostics(
        TranscriptionRuntimeState state,
        string? safeErrorCode = null)
    {
        TranscriptionDiagnostics? changed;
        lock (_bufferGate)
            changed = UpdateDiagnosticsLocked(state, safeErrorCode);
        if (changed is not null) DiagnosticsChanged?.Invoke(this, changed);
    }

    private void PublishDiagnosticsLocked(string? safeErrorCode = null)
    {
        safeErrorCode ??= _buffers.Values
            .Where(b => b.FailureCount > 0 && b.Stream.Length > 0)
            .Select(b => b.SafeErrorCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        var pending = _buffers.Values.Sum(b => b.Stream.Length) /
            (double)AudioFormat.Mono16kPcm16.BytesPerSecond;
        var hasFailedBacklog = _buffers.Values.Any(
            b => b.FailureCount > 0 && b.Stream.Length > 0);
        var hasActiveRateLimit = _buffers.Values.Any(b =>
            string.Equals(b.SafeErrorCode, "rate_limited", StringComparison.Ordinal) &&
            b.RetryNotBefore > DateTimeOffset.UtcNow);
        var state = _droppedBytes > 0
            ? TranscriptionRuntimeState.AudioDropped
            : hasActiveRateLimit
                ? TranscriptionRuntimeState.RateLimited
            : hasFailedBacklog
                ? TranscriptionRuntimeState.Retrying
                : pending >= Math.Max(
                    _options.WindowSeconds,
                    _options.EffectiveMaxBufferedSeconds * 0.5)
                    ? TranscriptionRuntimeState.Backlogged
                    : TranscriptionRuntimeState.Healthy;
        var changed = UpdateDiagnosticsLocked(state, safeErrorCode);
        if (changed is not null) DiagnosticsChanged?.Invoke(this, changed);
    }

    private TranscriptionDiagnostics? UpdateDiagnosticsLocked(
        TranscriptionRuntimeState state,
        string? safeErrorCode)
    {
        var pendingBytes = _buffers.Values.Sum(b => b.Stream.Length);
        var retryAt = _buffers.Values
            .Select(b => b.RetryNotBefore)
            .Where(value => value > DateTimeOffset.UtcNow)
            .DefaultIfEmpty()
            .Min();
        var next = new TranscriptionDiagnostics(
            state,
            TimeSpan.FromSeconds(
                pendingBytes / (double)AudioFormat.Mono16kPcm16.BytesPerSecond),
            retryAt == default ? null : retryAt,
            _droppedDuration,
            _droppedBytes,
            safeErrorCode);
        if (next == _diagnostics) return null;
        _diagnostics = next;
        return next;
    }

    private static string SafeErrorCode(Exception ex) => ex switch
    {
        RateLimitedException => "rate_limited",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "authentication_required",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "authentication_required",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestTimeout } => "request_timeout",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        InvalidDataException => "invalid_response",
        _ => "transcription_failure",
    };

    private async Task ApplyAuthAsync(
        HttpRequestMessage req,
        AuthenticationSnapshot authentication,
        CancellationToken ct)
    {
        if (authentication.ApiKey is not null)
        {
            req.Headers.Add("api-key", authentication.ApiKey);
            return;
        }
        var cred = authentication.Credential
            ?? throw new InvalidOperationException("No credential available (set ApiKey or enable UseManagedIdentity).");
        var token = await cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private TokenCredential? GetOrCreateCredential()
    {
        if (_options.Credential is not null) return _options.Credential;
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

    private AuthenticationSnapshot CaptureAuthentication()
    {
        var apiKey = _options.ApiKey;
        return string.IsNullOrWhiteSpace(apiKey)
            ? new AuthenticationSnapshot(null, GetOrCreateCredential())
            : new AuthenticationSnapshot(apiKey, null);
    }

    private bool IsCurrentCredentialRejected()
    {
        var authentication = CaptureAuthentication();
        lock (_bufferGate)
            return AuthenticationMatchesRejectedLocked(authentication);
    }

    private void MarkAuthenticationRejected(AuthenticationSnapshot authentication)
    {
        lock (_bufferGate)
        {
            if (authentication.ApiKey is not null)
                _rejectedApiKeys.Add(authentication.ApiKey);
            else if (authentication.Credential is not null)
                _rejectedCredentials.Add(authentication.Credential);
        }
    }

    private bool AuthenticationMatchesRejectedLocked(AuthenticationSnapshot authentication)
    {
        return authentication.ApiKey is not null
            ? _rejectedApiKeys.Contains(authentication.ApiKey)
            : authentication.Credential is not null &&
                _rejectedCredentials.Contains(authentication.Credential);
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

    private readonly record struct AuthenticationSnapshot(
        string? ApiKey,
        TokenCredential? Credential);

    private sealed class RoleBuffer
    {
        public MemoryStream Stream { get; } = new();
        public DateTimeOffset WindowStart { get; set; }
        public DateTimeOffset WindowEnd { get; set; }
        public DateTimeOffset LastAppendAt { get; set; }
        public DateTimeOffset RetryNotBefore { get; set; }
        public int FailureCount { get; set; }
        public string? SafeErrorCode { get; set; }
        public long UnloggedDroppedBytes { get; set; }
        public DateTimeOffset LastDropWarningAt { get; set; }
    }
}
