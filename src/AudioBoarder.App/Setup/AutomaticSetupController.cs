using System.Windows;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace AudioBoarder.App.Setup;

public sealed class AutomaticSetupController(
    AutomaticWorkspaceSetup setup,
    IAzureCredentialProvider credentials,
    SettingsService settingsService,
    IOptions<AudioBoarderSettings> settings,
    IOptions<AzureOpenAIOptions> chat,
    IOptions<CloudTranscriptionOptions> cloud,
    IOptions<ImageGeneratorOptions> images,
    IOptions<AzureSpeechSettings> speech,
    ILogger<AutomaticSetupController>? logger = null)
{
    private int _active;

    public async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        if (!credentials.TryGetSignedInCredential(out var credential) || credential is null) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (await setup.HasRequiredResourcesAsync(credential, settings.Value, timeout.Token)) return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is Azure.RequestFailedException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            logger?.LogWarning("Workspace inventory could not be checked; category={Category}", ex.GetType().Name);
        }
        await ShowAsync(ct);
    }

    public async Task ShowAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return;
        try
        {
            var app = Application.Current;
            if (app is null) throw new InvalidOperationException("The workspace setup window requires the desktop application.");
            await app.Dispatcher.InvokeAsync(() =>
            {
                var vm = new AutomaticSetupViewModel(setup, credentials, settings.Value, SaveAsync);
                var window = new AutomaticSetupWindow(vm) { Owner = app.MainWindow };
                window.ShowDialog();
            });
        }
        finally { Interlocked.Exchange(ref _active, 0); }
    }

    public async Task<AutomaticWorkspaceResult> ConfigureLocalAsync(
        bool approved, IProgress<string> progress, CancellationToken ct)
    {
        if (!credentials.TryGetSignedInCredential(out var credential) || credential is null)
            throw new InvalidOperationException("Sign in to Azure first.");
        var plan = await setup.InspectAsync(credential, settings.Value, ct);
        progress.Report(plan.Summary);
        var result = await setup.ExecuteAsync(credential, credentials.TenantId, plan, approved, progress, ct);
        await SaveAsync(result, ct);
        return result;
    }

    private async Task SaveAsync(AutomaticWorkspaceResult result, CancellationToken ct)
    {
        var draft = settingsService.Load();
        result.ApplyTo(draft);
        await settingsService.SaveAsync(draft, new SettingsSecrets(null, null, true, true), ct);
        AzureRuntimeConfiguration.Apply(draft, settings.Value, chat.Value, cloud.Value, images.Value);
        speech.Value.Region = draft.AzureSpeech.Region;
        speech.Value.ResourceId = draft.AzureSpeech.ResourceId;
        speech.Value.ApiKey = null;
        speech.Value.TenantId = draft.AzureOpenAI.TenantId;
        speech.Value.EndSilenceMs = draft.AzureSpeech.EndSilenceMs;
        speech.Value.Credential = credentials.Get();
        chat.Value.Credential = credentials.Get();
    }
}
