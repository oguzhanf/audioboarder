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

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready) return Array.Empty<TranscriptSegment>();
        if (chunk.Format.BitsPerSample != 16) return Array.Empty<TranscriptSegment>();

        byte[]? toFlush = null;
        DateTimeOffset windowStart = default, windowEnd = default;
        TranscriptSpeaker speaker = chunk.Role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local;

        lock (_bufferGate)
        {
            if (!_buffers.TryGetValue(chunk.Role, out var rb))
            {
                rb = new RoleBuffer();
                _buffers[chunk.Role] = rb;
            }
            if (rb.Stream.Length == 0) rb.WindowStart = chunk.CapturedAt;
            rb.Stream.Write(chunk.Samples.Span);

            var bytesPerSec = chunk.Format.BytesPerSecond;
            var elapsed = bytesPerSec == 0 ? 0 : (double)rb.Stream.Length / bytesPerSec;
            if (elapsed >= _options.WindowSeconds)
            {
                toFlush = rb.Stream.ToArray();
                windowStart = rb.WindowStart;
                windowEnd = chunk.CapturedAt;
                rb.Stream.SetLength(0);
            }
        }

        if (toFlush is null) return Array.Empty<TranscriptSegment>();
        return await CallApiAsync(toFlush, chunk.Format, speaker, windowStart, windowEnd, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
        => Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

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

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("MAI transcribe HTTP {Status}", resp.StatusCode);
                return Array.Empty<TranscriptSegment>();
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("text", out var t)) return Array.Empty<TranscriptSegment>();
            var text = (t.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TranscriptSegment>();
            _logger.LogInformation("MAI transcript: speaker={Speaker} chars={Chars} window={Sec:F1}s",
                speaker, text.Length, (end - start).TotalSeconds);
            return new[] { new TranscriptSegment(Guid.NewGuid(), speaker, text, start, end) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MAI transcribe failed");
            return Array.Empty<TranscriptSegment>();
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
        return ValueTask.CompletedTask;
    }

    private sealed class RoleBuffer
    {
        public MemoryStream Stream { get; } = new();
        public DateTimeOffset WindowStart { get; set; }
    }
}
