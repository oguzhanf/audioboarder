namespace AudioBoarder.Services.Transcription.Cloud;

public sealed class CloudTranscriptionOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Deployment name. e.g. "gpt-4o-transcribe" or "MAI-Transcribe-1".</summary>
    public string? DeploymentName { get; set; }
    public string? TenantId { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
    public string Language { get; set; } = "en";
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";

    /// <summary>Which transcription stack to use: "auto"/"cloud" (gpt-4o-transcribe),
    /// "speech" (Azure Speech streaming), or "local"/"whisper".</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>Max length of a single buffered utterance before it is force-flushed
    /// even if the speaker hasn't paused. Guards against unbounded monologues.</summary>
    public double WindowSeconds { get; set; } = 14.0;

    /// <summary>Trailing silence (no VAD-passed audio) that marks the end of an
    /// utterance and triggers a flush. Lower = snappier, higher = fewer cuts.</summary>
    public int SilenceFlushMs { get; set; } = 380;

    /// <summary>Optional domain prompt biasing recognition toward expected vocabulary.
    /// Sent as the transcriptions API "prompt" field.</summary>
    public string? Prompt { get; set; }

    /// <summary>Sampling temperature for the transcription model. 0 = most literal,
    /// least prone to inventing words on noisy audio.</summary>
    public double Temperature { get; set; } = 0.0;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(DeploymentName);
}

