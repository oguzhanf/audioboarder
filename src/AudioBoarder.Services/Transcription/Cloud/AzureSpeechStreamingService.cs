using Azure.Core;
using Azure.Identity;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Transcription;
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
/// Recognition is event-driven rather than waiting for a fixed recording window.
/// </summary>
public sealed class AzureSpeechStreamingService : IStreamingTranscriptionService, ITranscriptionDiagnosticsSource
{
    private readonly AzureSpeechSettings _settings;
    private readonly ILogger<AzureSpeechStreamingService> _logger;
    private readonly Dictionary<AudioStreamRole, RoleRecognizer> _recognizers = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _recognizerGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAadToken;
    private DateTimeOffset _cachedAadExpires;
    private AuthorizationContext? _cachedAuthorizationContext;
    private bool _ready;
    private TranscriptionDiagnostics _diagnostics = TranscriptionDiagnostics.Healthy;
    public TranscriptionDiagnostics Diagnostics { get { lock (_gate) return _diagnostics; } }
    public event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;

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
    private static TokenCredential ResolveCredential(AuthorizationContext context)
    {
        if (context.Credential is not null) return context.Credential;
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(context.TenantId) ? null : context.TenantId,
            ExcludeInteractiveBrowserCredential = false,
            ExcludeAzurePowerShellCredential = true,
            AdditionallyAllowedTenants = { "*" },
        });
    }

    public string Name => $"AzureSpeech.Streaming/{_settings.Region}";
    public bool IsReady => _ready;
    public async Task InitializeAsync(CancellationToken ct)
    {
        _ready = false;
        if (!_settings.IsConfigured)
            throw new TranscriptionInitializationException(
                "AzureSpeechStreamingService requires Region and either ApiKey or ResourceId.",
                "configuration");

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            try
            {
                var token = await AcquireAadTokenAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                    throw new TranscriptionInitializationException(
                        "The Azure Speech credential returned no usable token.",
                        "credential_unavailable");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var code = TranscriptionInitializationException.SafeCode(ex);
                if (ex is TranscriptionInitializationException initialization)
                    throw initialization;
                throw new TranscriptionInitializationException(
                    "Azure Speech authentication could not be initialized.",
                    code,
                    ex);
            }
        }

        // A cached token alone does not prove the Speech endpoint accepts this identity.
        var probe = await CreateRecognizerAsync(AudioStreamRole.Microphone, AudioFormat.Mono16kPcm16, ct, probeOnly: true)
            .ConfigureAwait(false);
        await StopRecognizerAsync(probe, ct).ConfigureAwait(false);
        _ready = true;
        Publish(TranscriptionDiagnostics.Healthy);
        _logger.LogInformation("Azure Speech streaming ready: region={Region}", _settings.Region);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready)
            throw new TranscriptionInitializationException("Live speech is not connected.", "speech_not_connected");

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

    public async Task PrepareStreamsAsync(IReadOnlyList<AudioStreamRole> roles, AudioFormat format, CancellationToken ct)
    {
        await _recognizerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var role in roles.Distinct())
            {
                lock (_gate) if (_recognizers.ContainsKey(role)) continue;
                var recognizer = await CreateRecognizerAsync(role, format, ct).ConfigureAwait(false);
                lock (_gate) _recognizers[role] = recognizer;
            }
        }
        finally { _recognizerGate.Release(); }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
    {
        if (!force) return Array.Empty<TranscriptSegment>();
        List<RoleRecognizer> list;
        lock (_gate)
        {
            list = _recognizers.Values.ToList();
            _recognizers.Clear();
        }
        await Task.WhenAll(list.Select(recognizer => StopRecognizerAsync(recognizer, ct))).ConfigureAwait(false);
        return Array.Empty<TranscriptSegment>();
    }

    private async Task<RoleRecognizer> CreateRecognizerAsync(
        AudioStreamRole role, AudioFormat format, CancellationToken ct, bool probeOnly = false)
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
        audioFormat.Dispose();
        var connection = Connection.FromRecognizer(recognizer);
        rr.Connection = connection;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Connected += (_, _) => connected.TrySetResult();

        var speaker = role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local;
        // Partial hypotheses stream in continuously as the person speaks — this
        // is what gives the instant, Teams-style "words appear as you talk" feel.
        recognizer.Recognizing += (_, e) =>
        {
            var partial = e.Result.Text?.Trim();
            if (probeOnly || string.IsNullOrEmpty(partial)) return;
            var now = DateTimeOffset.UtcNow;
            InterimReady?.Invoke(this, new TranscriptSegment(Guid.Empty, speaker, partial!, now, now));
        };
        recognizer.Recognized += (_, e) =>
        {
            if (probeOnly || e.Result.Reason != ResultReason.RecognizedSpeech) return;
            var text = e.Result.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
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
            {
                _logger.LogWarning(
                    "Speech recognizer cancelled: code={Code} category={Category}",
                    e.ErrorCode, "speech_service_failure");
                var category = e.ErrorCode is CancellationErrorCode.AuthenticationFailure or CancellationErrorCode.Forbidden
                    ? "authentication_required" : "speech_connection";
                connected.TrySetException(new TranscriptionInitializationException(
                    "Azure Speech rejected the streaming connection. Run Workspace setup to repair access.", category));
                if (!probeOnly)
                {
                    _ready = false;
                    Publish(new TranscriptionDiagnostics(TranscriptionRuntimeState.Fatal, TimeSpan.Zero,
                        SafeErrorCode: category, StatusMessage: "Live speech disconnected. Run Workspace setup or retry your connection."));
                }
            }
        };
        recognizer.SessionStopped += (_, _) => _logger.LogInformation("Speech session stopped for {Role}", role);

        try
        {
            await recognizer.StartContinuousRecognitionAsync().WaitAsync(ct).ConfigureAwait(false);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            return rr;
        }
        catch
        {
            recognizer.Dispose();
            connection.Dispose();
            audioConfig.Dispose();
            pushStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Raised whenever the Speech service finalises an utterance. The
    /// <see cref="AudioPipeline"/> subscribes via the same SegmentEmitted
    /// adapter as classic <see cref="ITranscriptionService.TranscribeAsync"/>.
    /// </summary>
    public event EventHandler<TranscriptSegment>? SegmentReady;
    public event EventHandler<TranscriptSegment>? InterimReady;

    private AuthorizationContext CurrentAuthorization =>
        new(_settings.Credential, _settings.TenantId, _settings.ResourceId, _settings.Region);

    private bool HasCurrentToken(AuthorizationContext context) =>
        _cachedAadToken is not null && _cachedAuthorizationContext == context &&
        DateTimeOffset.UtcNow < _cachedAadExpires - TimeSpan.FromMinutes(2);

    internal async Task<string> AcquireAadTokenAsync(CancellationToken ct)
    {
        if (HasCurrentToken(CurrentAuthorization)) return _cachedAadToken!;
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = CurrentAuthorization;
            if (HasCurrentToken(context)) return _cachedAadToken!;
            var credential = ResolveCredential(context);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
                ct).ConfigureAwait(false);
            if (context != CurrentAuthorization)
                throw new TranscriptionInitializationException(
                    "The Azure Speech account changed. Reconnect live speech.", "configuration_changed");
            _cachedAadToken = token.Token;
            _cachedAadExpires = token.ExpiresOn;
            _cachedAuthorizationContext = context;
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
            HasCurrentToken(CurrentAuthorization))
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
        await FlushAsync(CancellationToken.None, force: true).ConfigureAwait(false);
        _recognizerGate.Dispose();
        _tokenGate.Dispose();
    }

    private static async Task StopRecognizerAsync(RoleRecognizer rr, CancellationToken ct)
    {
        try
        {
            rr.PushStream.Close();
            await rr.Recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        finally
        {
            rr.Recognizer.Dispose();
            rr.Connection?.Dispose();
            rr.AudioConfig.Dispose();
            rr.PushStream.Dispose();
        }
    }

    private void Publish(TranscriptionDiagnostics diagnostics)
    {
        lock (_gate) _diagnostics = diagnostics;
        DiagnosticsChanged?.Invoke(this, diagnostics);
    }

    private sealed class RoleRecognizer
    {
        public AudioStreamRole Role { get; }
        public SpeechRecognizer Recognizer { get; }
        public PushAudioInputStream PushStream { get; }
        public AudioConfig AudioConfig { get; }
        public Connection? Connection { get; set; }
        public RoleRecognizer(AudioStreamRole role, SpeechRecognizer rec, PushAudioInputStream ps, AudioConfig ac)
        { Role = role; Recognizer = rec; PushStream = ps; AudioConfig = ac; }
    }

    private sealed record AuthorizationContext(
        TokenCredential? Credential, string? TenantId, string? ResourceId, string? Region);
}
