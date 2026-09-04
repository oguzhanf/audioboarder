using Azure.Core;

namespace AudioBoarder.Services.Imaging;

public sealed class ImageGeneratorOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Primary image deployment (e.g. "gpt-image-2", "MAI-Image-2.5").</summary>
    public string? DeploymentName { get; set; }
    /// <summary>Fallback deployment if primary is unavailable.</summary>
    public string? FallbackDeploymentName { get; set; }
    public string? TenantId { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
    public TokenCredential? Credential { get; set; }
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(DeploymentName);
}
