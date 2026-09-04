using AudioBoarder.App.Health;

namespace AudioBoarder.App.Auth;

public interface IHealthProbeRunner
{
    void MarkLlmChecking(string detail, HealthCondition condition = HealthCondition.Unknown);
    Task RunAllAsync(CancellationToken ct = default);
}

public sealed class AzureSignInCoordinator
{
    private readonly IAzureCredentialProvider _credentials;
    private readonly IHealthProbeRunner _health;

    public AzureSignInCoordinator(IAzureCredentialProvider credentials, IHealthProbeRunner health)
    {
        _credentials = credentials;
        _health = health;
    }

    public async Task<(bool Success, string Message)> SignInAndRefreshAsync(CancellationToken ct)
    {
        var result = await _credentials.SignInInteractiveAsync(ct).ConfigureAwait(false);
        if (!result.Success) return result;

        _health.MarkLlmChecking("Signed in; checking Azure services…");
        await _health.RunAllAsync(ct).ConfigureAwait(false);
        return result;
    }
}
