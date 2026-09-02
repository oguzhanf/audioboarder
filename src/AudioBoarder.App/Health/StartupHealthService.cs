using System.Collections.Concurrent;
using System.Net.Http;
using AudioBoarder.App.Configuration;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;

namespace AudioBoarder.App.Health;

/// <summary>
/// Runs background probes against the three external subsystems (audio devices,
/// Whisper, Azure OpenAI) and publishes per-component <see cref="HealthState"/>
/// snapshots. UI subscribes via <see cref="StateChanged"/>.
/// </summary>
public sealed class StartupHealthService
{
    public const string AudioKey = "audio";
    public const string TranscriptionKey = "transcription";
    public const string LlmKey = "llm";

    private readonly ConcurrentDictionary<string, HealthState> _states = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupHealthService> _logger;
    private readonly AudioBoarderSettings _settings;
    // Monotonic generation counter per probe key. A probe captures the generation
    // it started in; if a newer probe has bumped the counter by the time this one
    // finishes, its result is discarded so a stale Whisper probe can't overwrite
    // a fresh cloud-transcription pill.
    private readonly ConcurrentDictionary<string, long> _probeGen = new();

    public StartupHealthService(
        IServiceProvider services,
        IOptions<AudioBoarderSettings> settings,
        ILogger<StartupHealthService>? logger = null)
    {
        _services = services;
        _settings = settings.Value;
        _logger = logger ?? NullLogger<StartupHealthService>.Instance;
        Set(AudioKey, new HealthState(ComponentStatus.Unknown, "Audio devices", "Not checked yet", DateTimeOffset.UtcNow, AudioKey));
        Set(TranscriptionKey, new HealthState(ComponentStatus.Unknown, "Transcription", "Not checked yet", DateTimeOffset.UtcNow, TranscriptionKey));
        Set(LlmKey, new HealthState(ComponentStatus.Unknown, "Azure OpenAI", "Not checked yet", DateTimeOffset.UtcNow, LlmKey));
    }

    public event EventHandler<HealthState>? StateChanged;

    public IReadOnlyDictionary<string, HealthState> States => _states;
    public DiscoveryResult? LastDiscovery { get; private set; }

    public HealthState GetState(string key) => _states.TryGetValue(key, out var s) ? s
        : new HealthState(ComponentStatus.Unknown, key, "no probe", DateTimeOffset.UtcNow);

    public async Task RunAllAsync(CancellationToken ct = default)
    {
        // LLM discovery FIRST (sequential), then audio + transcription in parallel.
        // Reason: the transcription factory reads CloudTranscriptionOptions which
        // is only populated by discovery. Running transcription before discovery
        // makes the initial probe always pick Whisper.
        await RunLlmAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(
            RunAudioAsync(ct),
            RunTranscriptionAsync(ct)).ConfigureAwait(false);
    }

    public async Task RunAudioAsync(CancellationToken ct = default)
    {
        var gen = NextGeneration(AudioKey);
        SetIfLatest(AudioKey, gen, Checking("Audio devices", "Enumerating WASAPI endpoints…"));
        try
        {
            await Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? mic = null;
                MMDevice? render = null;
                try { mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { /* none */ }
                try { render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); } catch { /* none */ }

                var hasMic = mic is not null && _settings.Audio.CaptureMicrophone;
                var loopWanted = _settings.Audio.CaptureLoopback;
                var hasLoop = render is not null && loopWanted;
                var loopText = !loopWanted ? "off (mic only)" : (render is not null ? render!.FriendlyName : "missing");
                var detail = $"Mic: {(hasMic ? mic!.FriendlyName : "missing")}; Loopback: {loopText}";
                mic?.Dispose();
                render?.Dispose();

                if (!hasMic && !hasLoop)
                {
                    SetIfLatest(AudioKey, gen, new HealthState(ComponentStatus.Failed, "Audio devices",
                        "No mic or loopback device available", DateTimeOffset.UtcNow));
                }
                else if (!hasMic || (loopWanted && !hasLoop))
                {
                    SetIfLatest(AudioKey, gen, new HealthState(ComponentStatus.Degraded, "Audio devices",
                        detail, DateTimeOffset.UtcNow));
                }
                else
                {
                    SetIfLatest(AudioKey, gen, new HealthState(ComponentStatus.Ready, "Audio devices",
                        detail, DateTimeOffset.UtcNow));
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Audio probe failed; category={Category}", SafeCategory(ex));
            SetIfLatest(AudioKey, gen, Failed("Audio devices", ex));
        }
    }

    public async Task RunTranscriptionAsync(CancellationToken ct = default)
    {
        var gen = NextGeneration(TranscriptionKey);
        // Resolve via the FACTORY so the choice tracks whatever options discovery
        // populated. Resolving the ITranscriptionService singleton would freeze
        // the app on Whisper after the first call.
        var factory = _services.GetRequiredService<Func<ITranscriptionService>>();
        ITranscriptionService svc;
        try { svc = factory(); }
        catch (Exception ex)
        {
            _logger.LogError("Transcription factory failed; category={Category}", SafeCategory(ex));
            SetIfLatest(TranscriptionKey, gen, Failed("Transcription", ex));
            return;
        }

        // Title is STABLE so the WPF ItemsControl updates the existing pill in
        // place (it keys by Title). The backend name lives in Detail so the user
        // can see "Ready (AzureOpenAI.Transcribe/gpt-4o-transcribe)".
        const string title = "Transcription";
        var preparing = svc is AudioBoarder.Services.Transcription.WhisperTranscriptionService
            ? (_settings.Whisper.AutoDownload
                ? $"Preparing local model ggml-{_settings.Whisper.ModelSize}.bin (downloads on first run)…"
                : $"Loading local model {_settings.Whisper.ModelPath ?? "ggml-" + _settings.Whisper.ModelSize}…")
            : $"Connecting to {svc.Name}…";
        SetIfLatest(TranscriptionKey, gen, Checking(title, preparing));

        try
        {
            await svc.InitializeAsync(ct).ConfigureAwait(false);
            SetIfLatest(TranscriptionKey, gen, new HealthState(ComponentStatus.Ready, title,
                $"Ready ({svc.Name})", DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Transcription probe failed for {Name}; category={Category}",
                svc.Name, SafeCategory(ex));
            SetIfLatest(TranscriptionKey, gen, Failed(title, ex));
        }
    }

    public async Task RunLlmAsync(CancellationToken ct = default)
    {
        var gen = NextGeneration(LlmKey);
        SetIfLatest(LlmKey, gen, Checking("Azure OpenAI", "Checking authentication and deployment…"));

        var azureSection = _settings.AzureOpenAI;
        var ctSection = _settings.CloudTranscription;
        var imgSection = _settings.ImageGeneration;
        try
        {
            // Trigger discovery whenever ANY required capability is missing — not
            // just chat. Before this fix, discovery only ran when chat config was
            // empty, so cloud transcription/image were never auto-discovered when
            // chat was already pinned.
            var needsChat = string.IsNullOrWhiteSpace(azureSection.Endpoint) || string.IsNullOrWhiteSpace(azureSection.DeploymentName);
            var needsTranscribe = string.IsNullOrWhiteSpace(ctSection.DeploymentName);
            var needsImage = imgSection.Enabled && string.IsNullOrWhiteSpace(imgSection.DeploymentName);
            if (azureSection.AutoDiscover && (needsChat || needsTranscribe || needsImage))
            {
                var discovery = _services.GetRequiredService<FoundryDiscovery>();
                var creds = _services.GetService<AudioBoarder.App.Auth.AzureCredentialProvider>();
                if (creds is not null) discovery.SetExternalCredential(creds.Get());

                var result = await discovery.DiscoverAsync(
                    azureSection.TenantId, azureSection.SubscriptionId,
                    azureSection.DeploymentName, azureSection.PreferredRegion, ct: ct).ConfigureAwait(false);
                LastDiscovery = result;
                if (!result.Success)
                {
                    SetIfLatest(LlmKey, gen, new HealthState(ComponentStatus.Failed, "Azure OpenAI",
                        $"Discovery failed: {result.Message}. Run scripts/setup-azure.ps1 or fill AzureOpenAI in appsettings.json.",
                        DateTimeOffset.UtcNow));
                    return;
                }

                var azOpts = _services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
                azOpts.DeploymentName = result.DeploymentName;
                azOpts.FallbackDeploymentName = result.FallbackDeploymentName;
                azOpts.Endpoint = result.Endpoint; // write endpoint LAST — completes the commit

                // Image options — write deployment first, endpoint last so the
                // factory's IsConfigured check can never see a half-populated state.
                var imgOpts = _services.GetRequiredService<IOptions<AudioBoarder.Services.Imaging.ImageGeneratorOptions>>().Value;
                if (string.IsNullOrWhiteSpace(imgOpts.DeploymentName) && !string.IsNullOrWhiteSpace(result.ImageDeploymentName))
                    imgOpts.DeploymentName = result.ImageDeploymentName;
                imgOpts.Endpoint = result.ImageEndpoint ?? result.Endpoint;

                // Cloud transcription options — same atomic-commit order. Use the
                // per-capability transcribe endpoint so a transcribe deployment
                // hosted in a different account from chat still works.
                var ctOpts = _services.GetRequiredService<IOptions<AudioBoarder.Services.Transcription.Cloud.CloudTranscriptionOptions>>().Value;
                if (string.IsNullOrWhiteSpace(ctOpts.DeploymentName) && !string.IsNullOrWhiteSpace(result.TranscribeDeploymentName))
                    ctOpts.DeploymentName = result.TranscribeDeploymentName;
                ctOpts.Endpoint = result.TranscribeEndpoint ?? result.Endpoint;

                var detail = $"Ready ({result.DeploymentName})";
                if (result.FallbackDeploymentName is not null) detail += $" (fast: {result.FallbackDeploymentName})";
                if (!imgSection.Enabled) detail += " · image: disabled";
                else if (result.ImageDeploymentName is not null) detail += $" · image: {result.ImageDeploymentName}";
                if (result.TranscribeDeploymentName is not null) detail += $" · transcribe: {result.TranscribeDeploymentName}";

                SetIfLatest(LlmKey, gen, new HealthState(ComponentStatus.Ready, "Azure OpenAI", detail, DateTimeOffset.UtcNow));

                // Re-fire the transcription probe so the pill flips from Whisper
                // to the discovered cloud deployment now that CT options are set.
                // Fire-and-forget — the version guard inside RunTranscriptionAsync
                // ensures a stale earlier probe can't stomp the result.
                if (!string.IsNullOrWhiteSpace(result.TranscribeDeploymentName))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await RunTranscriptionAsync(ct).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                "Post-discovery transcription re-probe failed; category={Category}",
                                SafeCategory(ex));
                        }
                    }, ct);
                }
            }
            else
            {
                var endpoint = azureSection.Endpoint;
                var deployment = azureSection.DeploymentName;
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
                {
                    SetIfLatest(LlmKey, gen, new HealthState(ComponentStatus.Failed, "Azure OpenAI",
                        "Endpoint/DeploymentName not configured and AutoDiscover is disabled.",
                        DateTimeOffset.UtcNow));
                    return;
                }
                SetIfLatest(LlmKey, gen, new HealthState(ComponentStatus.Ready, "Azure OpenAI",
                    $"Ready ({deployment}) · image: {(imgSection.Enabled ? "enabled" : "disabled")}",
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Azure probe failed; category={Category}", SafeCategory(ex));
            SetIfLatest(LlmKey, gen, Failed("Azure OpenAI", ex));
        }
    }

    private long NextGeneration(string key) => _probeGen.AddOrUpdate(key, 1, (_, v) => v + 1);

    private void SetIfLatest(string key, long gen, HealthState state)
    {
        if (_probeGen.TryGetValue(key, out var current) && gen < current) return;
        var stamped = state with { Key = key };
        _states[key] = stamped;
        StateChanged?.Invoke(this, stamped);
    }

    private void Set(string key, HealthState state)
    {
        var stamped = state with { Key = key };
        _states[key] = stamped;
        StateChanged?.Invoke(this, stamped);
    }

    private static HealthState Checking(string title, string detail)
        => new(ComponentStatus.Checking, title, detail, DateTimeOffset.UtcNow);

    private static HealthState Failed(string title, Exception ex)
        => new(ComponentStatus.Failed, title,
            $"Unavailable ({SafeCategory(ex)}).", DateTimeOffset.UtcNow);

    private static string SafeCategory(Exception ex) => ex switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        UnauthorizedAccessException => "access_denied",
        _ => "unavailable",
    };
}
