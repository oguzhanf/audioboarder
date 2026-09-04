using Azure.Core;
using Azure.Identity;

namespace AudioBoarder.App.Auth;

public interface IAzureManagementTokenProbe
{
    Task ProbeAsync(TokenCredential credential, CancellationToken ct);
}

public sealed class AzureManagementTokenProbe : IAzureManagementTokenProbe
{
    private static readonly TokenRequestContext ManagementContext =
        new(new[] { "https://management.azure.com/.default" });
    private static readonly TokenRequestContext CognitiveServicesContext =
        new(new[] { "https://cognitiveservices.azure.com/.default" });

    public async Task ProbeAsync(TokenCredential credential, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(ManagementContext, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new CredentialUnavailableException("The Azure credential returned no management-plane token.");
        var dataToken = await credential.GetTokenAsync(
            CognitiveServicesContext, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dataToken.Token))
            throw new CredentialUnavailableException("The Azure credential returned no Azure AI data-plane token.");
    }
}
