using Azure.Core;
using Azure.Identity;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.Transcription.Cloud;

/// <summary>
/// Truly streaming transcription via Azure AI Speech. Runs a per-role
/// SpeechRecognizer with PushAudioInputStream and emits a
/// <see cref="TranscriptSegment"/> on every Recognized event (full utterance,
/// silence-segmented by the service's built-in VAD).
///
/// Latency is roughly 200-400 ms after the speaker stops; no fixed window.
/// </summary>
public sealed class AzureSpeechStreamingService : IStreamingTranscriptionService
{
    private readonly AzureSpeechSettings _settings;
    private readonly ILogger<AzureSpeechStreamingService> _logger;
    private readonly Dictionary<AudioStreamRole, RoleRecognizer> _recognizers = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _recognizerGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAadToken;
    private DateTimeOffset _cachedAadExpires;
    private bool _ready;

    public AzureSpeechStreamingService(
        IOptions<AzureSpeechSettings> options,
        ILogger<AzureSpeechStreamingService>? logger = null)
    {
        _settings = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureSpeechStreamingService>.Instance;
    }

    /// <summary>
    /// Resolve the credential lazily so a credential poked into <see cref="AzureSpeechSettings"/>
    /// AFTER service construction (e.g. by post-signin / post-provision wiring) is honored.
    /// </summary>
    private TokenCredential ResolveCredential()
    {
        if (_settings.Credential is not null) return _settings.Credential;
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(_settings.TenantId) ? null : _settings.TenantId,
            ExcludeInteractiveBrowserCredential = false,
            ExcludeAzurePowerShellCredential = true,
            AdditionallyAllowedTenants = { "*" },
        });
    }

    public string Name => $"AzureSpeech.Streaming/{_settings.Region}";
    public bool IsReady => _ready;
    public Task InitializeAsync(CancellationToken ct)
    {
        if (!_settings.IsConfigured)
            throw new InvalidOperationException("AzureSpeechStreamingService requires Region and either ApiKey or ResourceId.");
        _ready = true;
        _logger.LogInformation("Azure Speech streaming ready: region={Region}", _settings.Region);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready) return Array.Empty<TranscriptSegment>();

        RoleRecognizer? rr;
        lock (_gate)
        {
            _recognizers.TryGetValue(chunk.Role, out rr);
        }
        if (rr is null)
        {
            await _recognizerGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate) _recognizers.TryGetValue(chunk.Role, out rr);
                if (rr is null)
                {
                    rr = await CreateRecognizerAsync(chunk.Role, chunk.Format, ct).ConfigureAwait(false);
                    lock (_gate) _recognizers[chunk.Role] = rr;
                }
            }
            finally
            {
                _recognizerGate.Release();
            }
        }
        await RefreshAuthorizationIfNeededAsync(ct).ConfigureAwait(false);
        // Push raw PCM straight into the SDK's input stream — recognition is event-driven.
        rr.PushStream.Write(chunk.Samples.ToArray());
        return Array.Empty<TranscriptSegment>();
    }

    public Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
        => Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

    private async Task<RoleRecognizer> CreateRecognizerAsync(
        AudioStreamRole role, AudioFormat format, CancellationToken ct)
    {
        // Build SpeechConfig: AAD bearer if key not set, otherwise subscription key.
        SpeechConfig speechConfig;
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            speechConfig = SpeechConfig.FromSubscription(_settings.ApiKey, _settings.Region!);
        }
        else
        {
            var token = await AcquireAadTokenAsync(ct).ConfigureAwait(false);
            // Speech SDK AAD format: "aad#{resourceUrl}#{bearerToken}".
            var authToken = $"aad#{_settings.ResourceId}#{token}";
            speechConfig = SpeechConfig.FromAuthorizationToken(authToken, _settings.Region!);
        }

        speechConfig.SpeechRecognitionLanguage = _settings.Language;
        speechConfig.SetProfanity(_settings.ProfanityMasking ? ProfanityOption.Masked : ProfanityOption.Raw);
        speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, _settings.EndSilenceMs.ToString());
        speechConfig.OutputFormat = OutputFormat.Simple;

        // Build PushAudioInputStream with our PCM format.
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(
            samplesPerSecond: (uint)format.SampleRate,
            bitsPerSample: (byte)format.BitsPerSample,
            channels: (byte)format.Channels);
        var pushStream = AudioInputStream.CreatePushStream(audioFormat);
        var audioConfig = AudioConfig.FromStreamInput(pushStream);
        var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var rr = new RoleRecognizer(role, recognizer, pushStream, audioConfig);

        var speaker = role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local;
        // Partial hypotheses stream in continuously as the person speaks — this
        // is what gives the instant, Teams-style "words appear as you talk" feel.
        recognizer.Recognizing += (_, e) =>
        {
            var partial = e.Result.Text?.Trim();
            if (string.IsNullOrEmpty(partial)) return;
            var now = DateTimeOffset.UtcNow;
            InterimReady?.Invoke(this, new TranscriptSegment(Guid.Empty, speaker, partial!, now, now));
        };
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech) return;
            var text = e.Result.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            var offset = TimeSpan.FromTicks(e.Result.OffsetInTicks);
            var duration = e.Result.Duration;
            var now = DateTimeOffset.UtcNow;
            var segment = new TranscriptSegment(Guid.NewGuid(), speaker, text!, now - duration, now);
            _logger.LogInformation("Speech recognized: speaker={Speaker} chars={Chars} dur={Dur:F1}s",
                speaker, text!.Length, duration.TotalSeconds);
            SegmentReady?.Invoke(this, segment);
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                _logger.LogWarning("Speech recognizer cancelled: code={Code} details={Details}",
                    e.ErrorCode, e.ErrorDetails);
        };
        recognizer.SessionStopped += (_, _) => _logger.LogInformation("Speech session stopped for {Role}", role);

        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        return rr;
    }

    /// <summary>
    /// Raised whenever the Speech service finalises an utterance. The
    /// <see cref="AudioPipeline"/> subscribes via the same SegmentEmitted
    /// adapter as classic <see cref="ITranscriptionService.TranscribeAsync"/>.
    /// </summary>
    public event EventHandler<TranscriptSegment>? SegmentReady;
    public event EventHandler<TranscriptSegment>? InterimReady;

    private async Task<string> AcquireAadTokenAsync(CancellationToken ct)
    {
        if (_cachedAadToken is not null && DateTimeOffset.UtcNow < _cachedAadExpires - TimeSpan.FromMinutes(2))
            return _cachedAadToken;
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedAadToken is not null && DateTimeOffset.UtcNow < _cachedAadExpires - TimeSpan.FromMinutes(2))
                return _cachedAadToken;
            var credential = ResolveCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
                ct).ConfigureAwait(false);
            _cachedAadToken = token.Token;
            _cachedAadExpires = token.ExpiresOn;
            return _cachedAadToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task RefreshAuthorizationIfNeededAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            (_cachedAadToken is not null && DateTimeOffset.UtcNow < _cachedAadExpires - TimeSpan.FromMinutes(2)))
            return;

        var token = await AcquireAadTokenAsync(ct).ConfigureAwait(false);
        var authorization = $"aad#{_settings.ResourceId}#{token}";
        lock (_gate)
        {
            foreach (var rr in _recognizers.Values)
                rr.Recognizer.AuthorizationToken = authorization;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<RoleRecognizer> list;
        lock (_gate)
        {
            list = _recognizers.Values.ToList();
            _recognizers.Clear();
        }
        foreach (var rr in list)
        {
            try { await rr.Recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
            rr.Recognizer.Dispose();
            rr.AudioConfig.Dispose();
            rr.PushStream.Dispose();
        }
        _recognizerGate.Dispose();
        _tokenGate.Dispose();
    }

    private sealed class RoleRecognizer
    {
        public AudioStreamRole Role { get; }
        public SpeechRecognizer Recognizer { get; }
        public PushAudioInputStream PushStream { get; }
        public AudioConfig AudioConfig { get; }
        public RoleRecognizer(AudioStreamRole role, SpeechRecognizer rec, PushAudioInputStream ps, AudioConfig ac)
        { Role = role; Recognizer = rec; PushStream = ps; AudioConfig = ac; }
    }
}
