using System.Windows;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Setup;

public interface IAzureSetupCoordinator
{
    Task EnsureConfiguredAsync(CancellationToken ct = default);
}

public interface IAzureSetupPresenter
{
    Task ShowAsync(
        AzureSetupViewModel viewModel,
        Func<AzureModelSelection, CancellationToken, Task> save,
        CancellationToken ct);
}

public sealed class AzureSetupCoordinator(
    IAzureCredentialProvider credentials,
    IAzureModelInventory inventory,
    SettingsService settingsService,
    IOptions<AudioBoarderSettings> settings,
    IOptions<AzureOpenAIOptions> chat,
    IOptions<CloudTranscriptionOptions> transcription,
    IOptions<ImageGeneratorOptions> images,
    IAzureSetupPresenter presenter,
    IAzureProvisioningService? provisioning = null) : IAzureSetupCoordinator
{
    private int _inFlight;

    public async Task EnsureConfiguredAsync(CancellationToken ct = default)
    {
        if (!credentials.TryGetSignedInCredential(out _) ||
            Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;
        try
        {
            using var viewModel = new AzureSetupViewModel(inventory, credentials, settings.Value, provisioning);
            await viewModel.RefreshAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!viewModel.NeedsSetup)
            {
                if (string.IsNullOrWhiteSpace(settings.Value.AzureOpenAI.SubscriptionId))
                    settings.Value.AzureOpenAI.SubscriptionId = viewModel.SelectedSubscription?.Id;
                return;
            }

            await presenter.ShowAsync(viewModel, async (selection, token) =>
            {
                if (!credentials.TryGetSignedInCredential(out _) ||
                    !string.Equals(credentials.TenantId, selection.TenantId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The signed-in tenant changed during setup.");

                var draft = settingsService.Load();
                selection.ApplyTo(draft);
                await settingsService.SaveAsync(draft,
                    new SettingsSecrets(null, null, ClearAzureOpenAIApiKey: true), token);
                AzureRuntimeConfiguration.Apply(
                    draft, settings.Value, chat.Value, transcription.Value, images.Value);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }
}

public sealed class WpfAzureSetupPresenter : IAzureSetupPresenter
{
    public async Task ShowAsync(
        AzureSetupViewModel viewModel,
        Func<AzureModelSelection, CancellationToken, Task> save,
        CancellationToken ct)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        await app.Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (app.MainWindow is not { IsVisible: true } owner) return;
            var window = new AzureSetupWindow(viewModel, save) { Owner = owner };
            window.ShowDialog();
        });
    }
}
