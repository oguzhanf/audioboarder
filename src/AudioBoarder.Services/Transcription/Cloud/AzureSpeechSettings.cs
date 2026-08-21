using Azure.Core;

namespace AudioBoarder.Services.Transcription.Cloud;

public sealed class AzureSpeechSettings
{
    /// <summary>e.g. "eastus2"</summary>
    public string? Region { get; set; }
    /// <summary>Full ARM resource id of the Speech account. Used to build the Speech SDK AAD token: aad#{resourceId}#{bearerToken}.</summary>
    public string? ResourceId { get; set; }
    /// <summary>Optional explicit key. If empty, AAD via <see cref="Credential"/> (or DefaultAzureCredential) is used.</summary>
    public string? ApiKey { get; set; }
    public string? TenantId { get; set; }
    public string Language { get; set; } = "en-US";
    /// <summary>Silence in ms before the recognizer considers an utterance finished.</summary>
    public int EndSilenceMs { get; set; } = 600;
    public bool ProfanityMasking { get; set; } = false;

    /// <summary>
    /// Optional pre-built credential. When set, <see cref="AzureSpeechStreamingService"/>
    /// will use this for AAD-based authentication instead of constructing its own
    /// DefaultAzureCredential chain. The App populates this from
    /// AzureCredentialProvider so the same signed-in identity is reused.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Region) &&
        (!string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(ResourceId));
}
