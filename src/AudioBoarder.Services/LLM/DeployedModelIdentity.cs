namespace AudioBoarder.Services.LLM;

/// <summary>Model identity is scoped to a resource and deployment, not the user-chosen deployment alias.</summary>
public sealed record DeployedModelIdentity(string Endpoint, string DeploymentName, string ModelName)
{
    public string? Resolve(string? endpoint, string? deploymentName) =>
        string.Equals(Endpoint.TrimEnd('/'), endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DeploymentName, deploymentName, StringComparison.Ordinal)
            ? ModelName : deploymentName;
}
