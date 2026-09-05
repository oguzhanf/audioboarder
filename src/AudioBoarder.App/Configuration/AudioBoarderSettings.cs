namespace AudioBoarder.App.Configuration;

using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Transcription.Cloud;
using AudioBoarder.Services.LLM;

public sealed class AudioBoarderSettings
{
    public string Theme { get; set; } = "Light";
    public TimeSpan TranscriptWindow { get; set; } = TimeSpan.FromMinutes(5);
    public AzureOpenAISettings AzureOpenAI { get; set; } = new();
    public string? ActiveModelAccountId { get; set; }
    public List<ModelAccountSettings> ModelAccounts { get; set; } = [];
    public WhisperSettings Whisper { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public RealtimeSettings Realtime { get; set; } = new();
    public ImageGenerationSettings ImageGeneration { get; set; } = new();
    public CloudTranscriptionSettings CloudTranscription { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public SessionSettings Sessions { get; set; } = new();
    public AzureSpeechAppSettings AzureSpeech { get; set; } = new();
    public DiagramIntentSettings DiagramIntent { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!AzureOpenAI.AutoDiscover && string.IsNullOrWhiteSpace(AzureOpenAI.Endpoint))
            problems.Add("AzureOpenAI.Endpoint is required when AutoDiscover is false");
        if (!AzureOpenAI.AutoDiscover && string.IsNullOrWhiteSpace(AzureOpenAI.DeploymentName))
            problems.Add("AzureOpenAI.DeploymentName is required when AutoDiscover is false");
        if (TranscriptWindow <= TimeSpan.Zero)
            problems.Add("TranscriptWindow must be positive");
        if (Realtime.MinIntervalSeconds <= 0)
            problems.Add("Realtime.MinIntervalSeconds must be positive");
        if (Realtime.MinNewSegments <= 0)
            problems.Add("Realtime.MinNewSegments must be positive");
        if (Realtime.DeepPauseSeconds < 0)
            problems.Add("Realtime.DeepPauseSeconds cannot be negative");
        if (CloudTranscription.WindowSeconds <= 0)
            problems.Add("CloudTranscription.WindowSeconds must be positive");
        if (CloudTranscription.SilenceFlushMs < 0)
            problems.Add("CloudTranscription.SilenceFlushMs cannot be negative");
        if (CloudTranscription.MaxRetryBackoffSeconds < 0)
            problems.Add("CloudTranscription.MaxRetryBackoffSeconds cannot be negative");
        if (CloudTranscription.MaxBufferedSeconds <= 0 ||
            CloudTranscription.MaxBufferedSeconds >
            CloudTranscriptionOptions.MaximumMaxBufferedSeconds)
            problems.Add(
                $"CloudTranscription.MaxBufferedSeconds must be between 0 and {CloudTranscriptionOptions.MaximumMaxBufferedSeconds}");
        if (Whisper.WindowSeconds <= 0)
            problems.Add("Whisper.WindowSeconds must be positive");
        if (!Enum.IsDefined(DiagramIntent.SelectionMode))
            problems.Add("DiagramIntent.SelectionMode is invalid");
        if (!Enum.IsDefined(DiagramIntent.PinnedIntent))
            problems.Add("DiagramIntent.PinnedIntent is invalid");
        if (ModelAccounts.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            problems.Add("Model account profile IDs must be unique");
        return problems;
    }

    public void ApplyActiveModelAccount()
    {
        var profile = ModelAccounts.FirstOrDefault(x =>
            string.Equals(x.Id, ActiveModelAccountId, StringComparison.OrdinalIgnoreCase));
        profile?.ApplyTo(AzureOpenAI, CloudTranscription, ImageGeneration);
    }

    public sealed class DiagramIntentSettings
    {
        public DiagramIntentSelectionMode SelectionMode { get; set; } = DiagramIntentSelectionMode.Auto;
        public AudioBoarder.Core.Scene.DiagramIntent PinnedIntent { get; set; } =
            AudioBoarder.Core.Scene.DiagramIntent.SoftwareSystemArchitecture;
    }

}

public sealed class ModelAccountSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Microsoft account";
    public string? TenantId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? AccountResourceId { get; set; }
    public string? Endpoint { get; set; }
    public string? PrimaryDeployment { get; set; }
    public string? FallbackDeployment { get; set; }
    public string? TranscriptionDeployment { get; set; }
    public string? TranscriptionEndpoint { get; set; }
    public string? TranscriptionBackend { get; set; }
    public string? ImageDeployment { get; set; }
    public string? ImageEndpoint { get; set; }
    public bool? ImagesEnabled { get; set; }
    public bool? AutoDiscover { get; set; }
    public bool? UseManagedIdentity { get; set; }
    public string? PreferredRegion { get; set; }
    public DeployedModelIdentity? PrimaryModel { get; set; }
    public DeployedModelIdentity? FallbackModel { get; set; }
    public DeployedModelIdentity? TranscriptionModel { get; set; }
    public DeployedModelIdentity? ImageModel { get; set; }

    public void CaptureFrom(
        AzureOpenAISettings azure,
        CloudTranscriptionSettings transcription,
        ImageGenerationSettings image)
    {
        TenantId = azure.TenantId;
        SubscriptionId = azure.SubscriptionId;
        AccountResourceId = azure.AccountResourceId;
        Endpoint = azure.Endpoint;
        PrimaryDeployment = azure.DeploymentName;
        FallbackDeployment = azure.FallbackDeploymentName;
        TranscriptionDeployment = transcription.DeploymentName;
        TranscriptionEndpoint = transcription.Endpoint;
        TranscriptionBackend = transcription.Backend;
        ImageDeployment = image.DeploymentName;
        ImageEndpoint = image.Endpoint;
        ImagesEnabled = image.Enabled;
        AutoDiscover = azure.AutoDiscover;
        UseManagedIdentity = azure.UseManagedIdentity;
        PreferredRegion = azure.PreferredRegion;
        PrimaryModel = azure.Model;
        FallbackModel = azure.FallbackModel;
        TranscriptionModel = transcription.Model;
        ImageModel = image.Model;
    }

    public void ApplyTo(
        AzureOpenAISettings azure,
        CloudTranscriptionSettings transcription,
        ImageGenerationSettings image)
    {
        if (!string.Equals(azure.TenantId?.Trim(), TenantId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(azure.Endpoint?.TrimEnd('/'), Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            azure.ApiKey = null;
        azure.TenantId = TenantId;
        azure.SubscriptionId = SubscriptionId;
        azure.AccountResourceId = AccountResourceId;
        azure.Endpoint = Endpoint;
        azure.DeploymentName = PrimaryDeployment;
        azure.FallbackDeploymentName = FallbackDeployment;
        azure.PreferredRegion = PreferredRegion;
        transcription.DeploymentName = TranscriptionDeployment;
        transcription.Endpoint = TranscriptionEndpoint;
        if (TranscriptionBackend is not null) transcription.Backend = TranscriptionBackend;
        image.DeploymentName = ImageDeployment;
        image.Endpoint = ImageEndpoint;
        if (ImagesEnabled.HasValue) image.Enabled = ImagesEnabled.Value;
        if (AutoDiscover.HasValue) azure.AutoDiscover = AutoDiscover.Value;
        if (UseManagedIdentity.HasValue) azure.UseManagedIdentity = UseManagedIdentity.Value;
        azure.Model = PrimaryModel;
        azure.FallbackModel = FallbackModel;
        transcription.Model = TranscriptionModel;
        image.Model = ImageModel;
    }

    public override string ToString() => Name;
}

public sealed class AzureOpenAISettings
{
    public DeployedModelIdentity? Model { get; set; }
    public DeployedModelIdentity? FallbackModel { get; set; }
    public string? TenantId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? AccountResourceId { get; set; }
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? FallbackDeploymentName { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
    public bool AutoDiscover { get; set; } = true;
    public string? PreferredRegion { get; set; }
    public float? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRetries { get; set; } = 2;
}

public sealed class WhisperSettings
{
    public string ModelSize { get; set; } = "base";
    public string? ModelPath { get; set; }
    public string Language { get; set; } = "en";
    public bool AutoDownload { get; set; } = true;
    public double WindowSeconds { get; set; } = 1.5;
}

public sealed class AudioSettings
{
    public bool CaptureMicrophone { get; set; } = true;
    public bool CaptureLoopback { get; set; } = true;
    public string? SileroModelPath { get; set; }
}

public sealed class RealtimeSettings
{
    public bool Enabled { get; set; } = true;
    public double MinIntervalSeconds { get; set; } = 10;
    public int MinNewSegments { get; set; } = 3;
    public bool UseFastDeployment { get; set; } = true;
    /// <summary>
    /// Legacy fixed deep-pass interval. Timed deep passes are disabled; this remains
    /// bindable for older configuration files and defaults to off.
    /// </summary>
    public double DeepPassIntervalSeconds { get; set; } = 0;

    /// <summary>
    /// Finalized-speech pause that triggers a coalesced deep synthesis when the board
    /// contains provisional structure. 0 disables pause-triggered deep synthesis.
    /// </summary>
    public double DeepPauseSeconds { get; set; } = 25;

    /// <summary>Maximum nodes kept on the live board. Continuous passes only add, so
    /// without a cap a long meeting grows into an unreadable hairball. Architecture
    /// diagrams are legitimately dense, so this is generous. Content restored from a
    /// prior session is never trimmed below what was restored. Negative disables.</summary>
    public int MaxNodes { get; set; } = 80;

    /// <summary>Maximum notes kept in the rail. General commentary is dropped before
    /// decisions, action items, risks and questions. Negative disables the cap.</summary>
    public int MaxNotes { get; set; } = 24;

    /// <summary>
    /// Folder containing Microsoft's official Azure architecture icons, used to draw
    /// nodes with real product artwork instead of the bundled generic icons.
    /// <para>
    /// The icons are not shipped with AudioBoarder: Microsoft's terms permit copying
    /// and displaying them only for architectural diagrams, training material and
    /// documentation. Download the set from
    /// https://learn.microsoft.com/azure/architecture/icons/ (which is where you
    /// accept those terms), extract it, and point this at the folder.
    /// </para>
    /// </summary>
    public string? AzureIconsPath { get; set; }
}

public sealed class ImageGenerationSettings
{
    public DeployedModelIdentity? Model { get; set; }
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    /// <summary>Deployment name. Auto-populated from FoundryDiscovery if blank.</summary>
    public string? DeploymentName { get; set; }
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class CloudTranscriptionSettings
{
    public DeployedModelIdentity? Model { get; set; }
    public string? Endpoint { get; set; }
    /// <summary>"auto"/"cloud" (gpt-4o-transcribe), "speech" (Azure Speech streaming), or "local"/"whisper".</summary>
    public string Backend { get; set; } = "auto";
    /// <summary>Cloud deployment name. Auto-populated from FoundryDiscovery if blank.</summary>
    public string? DeploymentName { get; set; }
    public string Language { get; set; } = "en";
    /// <summary>Max length of a buffered utterance before a forced flush (seconds).</summary>
    public double WindowSeconds { get; set; } = 14.0;
    /// <summary>Trailing-silence gap that ends an utterance and triggers a flush (ms).</summary>
    public int SilenceFlushMs { get; set; } = 380;
    public double MaxRetryBackoffSeconds { get; set; } = 2.0;
    /// <summary>
    /// Per-role PCM backlog. 180 seconds at 16 kHz mono PCM-16 is 5,760,000 bytes
    /// (about 5.8 MB); the runtime also enforces this value as its hard upper bound.
    /// </summary>
    public double MaxBufferedSeconds { get; set; } = 180.0;
    /// <summary>Domain prompt biasing recognition toward expected vocabulary.</summary>
    public string? Prompt { get; set; }
    /// <summary>Transcription sampling temperature. 0 = most literal.</summary>
    public double Temperature { get; set; } = 0.0;
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";
}

public sealed class DiagnosticsSettings
{
    public bool VerbosePayloadLogging { get; set; }
    public bool EnableLocalPerformanceTelemetry { get; set; }
    public string LogLevel { get; set; } = "Information";
}

public sealed class SessionSettings
{
    public bool AutoSave { get; set; } = true;
    public bool OfferRestoreOnLaunch { get; set; } = true;
}

public sealed class AzureSpeechAppSettings
{
    /// <summary>Azure region (e.g. "eastus2"). Pre-wired for the provisioned audioboarder-speech resource.</summary>
    public string? Region { get; set; }
    /// <summary>ARM resource id of the Speech account, used to build the AAD auth token "aad#{id}#{token}".</summary>
    public string? ResourceId { get; set; }
    /// <summary>Optional explicit key — when set, used instead of AAD.</summary>
    public string? ApiKey { get; set; }
    public string Language { get; set; } = "en-US";
    public int EndSilenceMs { get; set; } = 600;
}
