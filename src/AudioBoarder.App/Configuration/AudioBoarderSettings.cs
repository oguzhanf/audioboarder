namespace AudioBoarder.App.Configuration;

public sealed class AudioBoarderSettings
{
    public string Theme { get; set; } = "Light";
    public TimeSpan TranscriptWindow { get; set; } = TimeSpan.FromMinutes(5);
    public AzureOpenAISettings AzureOpenAI { get; set; } = new();
    public WhisperSettings Whisper { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public RealtimeSettings Realtime { get; set; } = new();
    public ImageGenerationSettings ImageGeneration { get; set; } = new();
    public CloudTranscriptionSettings CloudTranscription { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public SessionSettings Sessions { get; set; } = new();
    public AzureSpeechAppSettings AzureSpeech { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (AzureOpenAI.AutoDiscover && string.IsNullOrWhiteSpace(AzureOpenAI.SubscriptionId))
            problems.Add("AzureOpenAI.AutoDiscover requires SubscriptionId");
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
        return problems;
    }
}

public sealed class AzureOpenAISettings
{
    public string? TenantId { get; set; }
    public string? SubscriptionId { get; set; }
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
    /// <summary>How often the continuous loop runs an automatic DEEP pass
    /// (the smart model that groups + cleans up the diagram, like Deep Refine)
    /// instead of a quick fast-model update. 0 disables automatic deep passes.</summary>
    public double DeepPassIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum nodes kept on the live board. Continuous passes only add, so
    /// without a cap a long meeting grows into an unreadable hairball. Architecture
    /// diagrams are legitimately dense, so this is generous. Content restored from a
    /// prior session is never trimmed below what was restored. Negative disables.</summary>
    public int MaxNodes { get; set; } = 80;

    /// <summary>Maximum notes kept in the rail. General commentary is dropped before
    /// decisions, action items, risks and questions. Negative disables the cap.</summary>
    public int MaxNotes { get; set; } = 24;
}

public sealed class ImageGenerationSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Deployment name. Auto-populated from FoundryDiscovery if blank.</summary>
    public string? DeploymentName { get; set; }
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class CloudTranscriptionSettings
{
    /// <summary>"auto"/"cloud" (gpt-4o-transcribe), "speech" (Azure Speech streaming), or "local"/"whisper".</summary>
    public string Backend { get; set; } = "auto";
    /// <summary>Cloud deployment name. Auto-populated from FoundryDiscovery if blank.</summary>
    public string? DeploymentName { get; set; }
    public string Language { get; set; } = "en";
    /// <summary>Max length of a buffered utterance before a forced flush (seconds).</summary>
    public double WindowSeconds { get; set; } = 14.0;
    /// <summary>Trailing-silence gap that ends an utterance and triggers a flush (ms).</summary>
    public int SilenceFlushMs { get; set; } = 380;
    /// <summary>Domain prompt biasing recognition toward expected vocabulary.</summary>
    public string? Prompt { get; set; }
    /// <summary>Transcription sampling temperature. 0 = most literal.</summary>
    public double Temperature { get; set; } = 0.0;
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";
}

public sealed class DiagnosticsSettings
{
    public bool VerbosePayloadLogging { get; set; }
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
