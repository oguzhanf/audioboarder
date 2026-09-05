using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace AudioBoarder.Services.LLM;

public interface IAzureWorkspaceAccess
{
    Task EnsureOwnInferenceAccessAsync(TokenCredential credential, string accountId, bool speech, bool approved, CancellationToken ct);
}

public sealed class AzureWorkspaceAccess : IAzureWorkspaceAccess
{
    // Public Azure built-in role identifiers; never an elevated management role.
    private const string SpeechUserRole = "f2dc8367-1007-4938-bd23-fe263f013447";
    private const string OpenAiUserRole = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd";
    private readonly HttpClient _http;

    public AzureWorkspaceAccess(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task EnsureOwnInferenceAccessAsync(
        TokenCredential credential, string accountId, bool speech, bool approved, CancellationToken ct)
    {
        if (!approved) throw new InvalidOperationException("Workspace creation and inference access require approval.");
        var id = new ResourceIdentifier(accountId);
        if (id.ResourceType != new ResourceType("Microsoft.CognitiveServices/accounts") || !Guid.TryParse(id.SubscriptionId, out _))
            throw new ArgumentException("Expected an Azure AI account resource.");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct);
        var parts = token.Token.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("The Azure login does not provide a usable user identity.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
        if (!claims.RootElement.TryGetProperty("oid", out var oid) || !Guid.TryParse(oid.GetString(), out var principal))
            throw new InvalidOperationException("The Azure login has no assignable object identity.");
        var role = speech ? SpeechUserRole : OpenAiUserRole;
        var key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}|{principal}|{role}".ToLowerInvariant()));
        var assignment = new Guid(key.AsSpan(0, 16));
        var url = $"https://management.azure.com{accountId}/providers/Microsoft.Authorization/roleAssignments/{assignment}?api-version=2022-04-01";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            properties = new
            {
                roleDefinitionId = $"/subscriptions/{id.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{role}",
                principalId = principal.ToString(),
                principalType = "User",
            },
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var error = JsonDocument.Parse(body);
            if (error.RootElement.TryGetProperty("error", out var detail) &&
                detail.TryGetProperty("code", out var code) && code.GetString() == "RoleAssignmentExists")
                return;
        }
        throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Forbidden
            ? "An Azure administrator must grant your account model/speech usage access on this resource. AudioBoarder cannot grant permissions your login does not have."
            : $"Azure could not establish inference access (HTTP {(int)response.StatusCode}).");
    }
}
