using System.Collections.Concurrent;
using System.Net.Http;
using Azure;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription;
using AudioBoarder.Services.Transcription.Cloud;
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
public sealed class StartupHealthService : IHealthProbeRunner
{
    public const string AudioKey = "audio";
    public const string TranscriptionKey = "transcription";
    public const string LlmKey = "llm";

    private readonly ConcurrentDictionary<string, HealthState> _states = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupHealthService> _logger;
    private readonly AudioBoarderSettings _settings;
    private readonly IAzureCredentialProvider _credentials;
    private readonly IAzureManagementTokenProbe _tokenProbe;
    private readonly IFoundryDiscovery _discovery;
    // Monotonic generation counter per probe key. A probe captures the generation
    // it started in; if a newer probe has bumped the counter by the time this one
    // finishes, its result is discarded so a stale Whisper probe can't overwrite
    // a fresh cloud-transcription pill.
    private readonly ConcurrentDictionary<string, long> _probeGen = new();

    public StartupHealthService(
        IServiceProvider services,
        IOptions<AudioBoarderSettings> settings,
        IAzureCredentialProvider credentials,
        IAzureManagementTokenProbe tokenProbe,
        IFoundryDiscovery discovery,
        ILogger<StartupHealthService>? logger = null)
    {
        _services = services;
        _settings = settings.Value;
        _credentials = credentials;
        _tokenProbe = tokenProbe;
        _discovery = discovery;
        _logger = logger ?? NullLogger<StartupHealthService>.Instance;
        Set(AudioKey, new HealthState(ComponentStatus.Unknown, "Audio devices", "Not checked yet", DateTimeOffset.UtcNow, AudioKey));
        Set(TranscriptionKey, new HealthState(ComponentStatus.Unknown, "Transcription", "Not checked yet", DateTimeOffset.UtcNow, TranscriptionKey));
        Set(LlmKey, new HealthState(
            ComponentStatus.Checking,
            "Azure OpenAI",
            "Restoring Azure sign-in…",
            DateTimeOffset.UtcNow,
            LlmKey,
            Condition: HealthCondition.Restoring));
    }

    public event EventHandler<HealthState>? StateChanged;

    public IReadOnlyDictionary<string, HealthState> States => _states;
    public DiscoveryResult? LastDiscovery { get; private set; }

    public HealthState GetState(string key) => _states.TryGetValue(key, out var s) ? s
        : new HealthState(ComponentStatus.Unknown, key, "no probe", DateTimeOffset.UtcNow);

    public void MarkLlmChecking(string detail, HealthCondition condition = HealthCondition.Unknown)
    {
        var gen = NextGeneration(LlmKey);
        SetIfLatest(LlmKey, gen, new HealthState(
            ComponentStatus.Checking,
            "Azure OpenAI",
            detail,
            DateTimeOffset.UtcNow,
            Action: HealthAction.None,
            Condition: condition));
    }

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
        // Selection validates credentials and tries the ordered fallbacks without
        // sending a billed transcription request.
        var selector = _services.GetRequiredService<ITranscriptionServiceSelector>();
        TranscriptionSelection selection;
        try { selection = await selector.SelectAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError("Transcription selection failed; category={Category}", SafeCategory(ex));
            SetIfLatest(TranscriptionKey, gen, Failed("Transcription", ex));
            return;
        }
        var svc = selection.Service;

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
            var status = selection.IsFallback ? ComponentStatus.Degraded : ComponentStatus.Ready;
            var detail = selection.IsFallback
                ? $"Degraded: {selection.StatusMessage} ({svc.Name})"
                : $"Ready ({svc.Name})";
            SetIfLatest(TranscriptionKey, gen, new HealthState(status, title,
                detail, DateTimeOffset.UtcNow));
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
        var needsChat = string.IsNullOrWhiteSpace(azureSection.Endpoint) || string.IsNullOrWhiteSpace(azureSection.DeploymentName);
        // Discovery also resolves a pinned image/transcription deployment to the
            // account endpoint that actually hosts it. Skipping discovery merely
            // because the names are populated incorrectly pairs cross-account pins
            // with the chat endpoint seeded during configuration binding.
            var needsDiscovery = azureSection.AutoDiscover;
            TokenCredential? verifiedCredential = null;

            if (!azureSection.AutoDiscover && needsChat)
            {
                SetIfLatest(LlmKey, gen, ConfigurationRequired());
                return;
            }

            if (string.IsNullOrWhiteSpace(azureSection.ApiKey) && azureSection.UseManagedIdentity)
            {
                var snapshot = _credentials.Snapshot;
                if (snapshot.State is AzureCredentialState.Unknown or AzureCredentialState.Restoring)
                {
                    SetIfLatest(LlmKey, gen, new HealthState(
                        ComponentStatus.Checking,
                        "Azure OpenAI",
                        "Restoring Azure sign-in…",
                        DateTimeOffset.UtcNow,
                        Action: HealthAction.None,
                        Condition: HealthCondition.Restoring));
                    return;
                }

                TokenCredential credential;
                if (_credentials.TryGetSignedInCredential(out var signedInCredential) &&
                    signedInCredential is not null)
                {
                    credential = signedInCredential;
                }
                else
                {
                    // A missing interactive auth record does not mean Azure CLI,
                    // managed identity, environment, or developer credentials are
                    // unavailable. Probe the provider's non-interactive chain before
                    // asking the user to sign in.
                    try { credential = _credentials.Get(); }
                    catch (Exception ex)
                    {
                        SetIfLatest(
                            LlmKey,
                            gen,
                            ClassifyAzureFailure(ex, ct.IsCancellationRequested));
                        return;
                    }
                }
                verifiedCredential = credential;

                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await _tokenProbe.ProbeAsync(credential, probeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SetIfLatest(LlmKey, gen, ClassifyAzureFailure(ex, ct.IsCancellationRequested));
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(azureSection.ApiKey))
            {
                SetIfLatest(LlmKey, gen, ConfigurationRequired());
                return;
            }

            if (verifiedCredential is not null)
            {
                _services.GetRequiredService<IOptions<AzureOpenAIOptions>>()
                    .Value.Credential = verifiedCredential;
                _services.GetRequiredService<IOptions<CloudTranscriptionOptions>>()
                    .Value.Credential = verifiedCredential;
                _services.GetRequiredService<IOptions<ImageGeneratorOptions>>()
                    .Value.Credential = verifiedCredential;
                var speechCredentialOptions =
                    _services.GetService<IOptions<AzureSpeechSettings>>()?.Value;
                if (speechCredentialOptions is not null)
                    speechCredentialOptions.Credential = verifiedCredential;
            }

            if (needsDiscovery)
            {
                var result = await _discovery.DiscoverAsync(
                    azureSection.TenantId, azureSection.SubscriptionId,
                    azureSection.DeploymentName, azureSection.PreferredRegion,
                    imgSection.DeploymentName, ctSection.DeploymentName,
                    credentialOverride: verifiedCredential, ct: ct).ConfigureAwait(false);
                LastDiscovery = result;
                if (!result.Success)
                {
                    SetIfLatest(LlmKey, gen, DiscoveryFailure(result));
                    return;
                }

                var azOpts = _services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
                azOpts.DeploymentName = result.DeploymentName;
                azOpts.FallbackDeploymentName = result.FallbackDeploymentName;
                azOpts.Endpoint = result.Endpoint; // write endpoint LAST — completes the commit

                // Image options — write deployment first, endpoint last so the
                // factory's IsConfigured check can never see a half-populated state.
                var imgOpts = _services.GetRequiredService<IOptions<AudioBoarder.Services.Imaging.ImageGeneratorOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(result.ImageDeploymentName))
                {
                    imgOpts.DeploymentName = result.ImageDeploymentName;
                    imgOpts.Endpoint = result.ImageEndpoint ?? result.Endpoint;
                }
                else if (!string.IsNullOrWhiteSpace(imgSection.DeploymentName))
                {
                    imgOpts.Endpoint = null;
                }

                // Cloud transcription options — same atomic-commit order. Use the
                // per-capability transcribe endpoint so a transcribe deployment
                // hosted in a different account from chat still works.
                var ctOpts = _services.GetRequiredService<IOptions<AudioBoarder.Services.Transcription.Cloud.CloudTranscriptionOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(result.TranscribeDeploymentName))
                {
                    ctOpts.DeploymentName = result.TranscribeDeploymentName;
                    ctOpts.Endpoint = result.TranscribeEndpoint ?? result.Endpoint;
                }
                else if (!string.IsNullOrWhiteSpace(ctSection.DeploymentName))
                {
                    ctOpts.Endpoint = null;
                }

                var detail = $"Ready ({result.DeploymentName})";
                if (result.FallbackDeploymentName is not null) detail += $" (fast: {result.FallbackDeploymentName})";
                if (!imgSection.Enabled) detail += " · image: disabled";
                else if (result.ImageDeploymentName is not null) detail += $" · image: {result.ImageDeploymentName}";
                if (result.TranscribeDeploymentName is not null) detail += $" · transcribe: {result.TranscribeDeploymentName}";

                SetIfLatest(LlmKey, gen, new HealthState(
                    ComponentStatus.Ready,
                    "Azure OpenAI",
                    detail,
                    DateTimeOffset.UtcNow,
                    Action: HealthAction.None,
                    Condition: HealthCondition.Ready));

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
                    SetIfLatest(LlmKey, gen, ConfigurationRequired());
                    return;
                }
                SetIfLatest(LlmKey, gen, new HealthState(ComponentStatus.Ready, "Azure OpenAI",
                    $"Ready ({deployment}) · image: {(imgSection.Enabled ? "enabled" : "disabled")}",
                    DateTimeOffset.UtcNow,
                    Action: HealthAction.None,
                    Condition: HealthCondition.Ready));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Azure probe failed; category={Category}", SafeCategory(ex));
            SetIfLatest(LlmKey, gen, ClassifyAzureFailure(ex, ct.IsCancellationRequested));
        }
    }

    private static HealthState SignInRequired() => new(
        ComponentStatus.ActionRequired,
        "Azure OpenAI",
        "Sign in to Azure to discover deployments",
        DateTimeOffset.UtcNow,
        Action: HealthAction.SignIn,
        Condition: HealthCondition.SignInRequired);

    private static HealthState ConfigurationRequired() => new(
        ComponentStatus.ActionRequired,
        "Azure OpenAI",
        "Endpoint/DeploymentName not configured and AutoDiscover is disabled.",
        DateTimeOffset.UtcNow,
        Action: HealthAction.Configure,
        Condition: HealthCondition.ConfigurationRequired);

    private static HealthState DiscoveryFailure(DiscoveryResult result)
    {
        return result.FailureKind switch
        {
            DiscoveryFailureKind.Authentication => SignInRequired(),
            DiscoveryFailureKind.AccessDenied => AccessDenied(),
            DiscoveryFailureKind.Network => RetryFailure(
                HealthCondition.NetworkFailure,
                "Could not reach Azure. Check your network connection and retry."),
            DiscoveryFailureKind.Service => RetryFailure(
                HealthCondition.ServiceFailure,
                "Azure discovery is temporarily unavailable. Retry shortly."),
            DiscoveryFailureKind.RateLimited => RateLimited(),
            DiscoveryFailureKind.None => new HealthState(
                ComponentStatus.ActionRequired,
                "Azure OpenAI",
                "The required Azure resource or chat deployment is missing. Open Configure to set up Azure OpenAI or Microsoft Foundry.",
                DateTimeOffset.UtcNow,
                Action: HealthAction.Configure,
                Condition: HealthCondition.ConfigurationRequired),
            _ => new HealthState(
                ComponentStatus.Failed,
                "Azure OpenAI",
                $"Discovery failed: {result.Message}. Check Azure configuration and permissions.",
                DateTimeOffset.UtcNow,
                Condition: HealthCondition.Failed),
        };
    }

    private static HealthState ClassifyAzureFailure(Exception ex, bool callerCancelled)
    {
        if (callerCancelled)
        {
            return new HealthState(
                ComponentStatus.Failed,
                "Azure OpenAI",
                "Azure health check was cancelled.",
                DateTimeOffset.UtcNow,
                Condition: HealthCondition.Failed);
        }

        var request = FindException<RequestFailedException>(ex);
        if (request is not null)
        {
            return request.Status switch
            {
                401 => SignInRequired(),
                403 => AccessDenied(),
                429 => RateLimited(),
                >= 500 => RetryFailure(
                    HealthCondition.ServiceFailure,
                    "Azure is temporarily unavailable. Retry shortly."),
                _ => RetryFailure(
                    HealthCondition.ServiceFailure,
                    "Azure could not complete the health check. Retry shortly."),
            };
        }

        if (FindException<HttpRequestException>(ex) is not null)
            return RetryFailure(
                HealthCondition.NetworkFailure,
                "Could not reach Azure. Check your network connection and retry.");

        if (FindException<AuthenticationRequiredException>(ex) is not null ||
            FindException<CredentialUnavailableException>(ex) is not null)
            return SignInRequired();

        if (FindException<AuthenticationFailedException>(ex) is not null)
            return SignInRequired();

        if (ex is OperationCanceledException or TimeoutException)
            return RetryFailure(
                HealthCondition.ServiceFailure,
                "Azure authentication timed out. Check your network and retry.");

        return new HealthState(
            ComponentStatus.Failed,
            "Azure OpenAI",
            "Azure health check failed. Check configuration and permissions.",
            DateTimeOffset.UtcNow,
            Condition: HealthCondition.Failed);
    }

    private static T? FindException<T>(Exception ex) where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }

    private static HealthState AccessDenied() => new(
        ComponentStatus.Failed,
        "Azure OpenAI",
        "Signed in, but access was denied. Ask an Azure administrator for permission to read Cognitive Services accounts and deployments.",
        DateTimeOffset.UtcNow,
        Action: HealthAction.None,
        Condition: HealthCondition.AccessDenied);

    private static HealthState RetryFailure(HealthCondition condition, string detail) => new(
        ComponentStatus.Failed,
        "Azure OpenAI",
        detail,
        DateTimeOffset.UtcNow,
        Action: HealthAction.Retry,
        Condition: condition);

    private static HealthState RateLimited() => new(
        ComponentStatus.RateLimited,
        "Azure OpenAI",
        "Azure is rate limiting discovery. Wait briefly, then retry.",
        DateTimeOffset.UtcNow,
        Action: HealthAction.Retry,
        Condition: HealthCondition.RateLimited);

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
        TranscriptionInitializationException initialization => initialization.SafeErrorCode,
        TimeoutException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        UnauthorizedAccessException => "access_denied",
        _ => "unavailable",
    };
}
