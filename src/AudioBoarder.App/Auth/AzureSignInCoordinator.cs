using AudioBoarder.App.Health;
using AudioBoarder.App.Setup;

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
    private readonly IAzureSetupCoordinator? _setup;

    public AzureSignInCoordinator(
        IAzureCredentialProvider credentials,
        IHealthProbeRunner health,
        IAzureSetupCoordinator? setup = null)
    {
        _credentials = credentials;
        _health = health;
        _setup = setup;
    }

    public async Task<(bool Success, string Message)> SignInAndRefreshAsync(CancellationToken ct)
    {
        var result = await _credentials.SignInInteractiveAsync(ct).ConfigureAwait(false);
        if (!result.Success) return result;

        _health.MarkLlmChecking("Signed in; checking Azure services…");
        if (_setup is not null)
            await _setup.EnsureConfiguredAsync(ct).ConfigureAwait(false);
        await _health.RunAllAsync(ct).ConfigureAwait(false);
        return result;
    }
}
