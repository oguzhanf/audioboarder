using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBoarder.App.Setup;

public sealed partial class AutomaticSetupViewModel(
    AutomaticWorkspaceSetup setup,
    IAzureCredentialProvider credentials,
    AudioBoarderSettings settings,
    Func<AutomaticWorkspaceResult, CancellationToken, Task> save) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private AutomaticWorkspacePlan? _plan;
    private string? _identity;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart), nameof(CancelLabel))] private bool isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool approved;
    [ObservableProperty] private string status = "Looking at your Azure workspace...";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool completed;
    public bool CanStart => !IsBusy && Approved && _plan is not null && !Completed;
    public string Account => credentials.SignedInAs ?? "Azure account";
    public string CancelLabel => IsBusy ? "Stop waiting" : "Not now";

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            if (!credentials.TryGetSignedInCredential(out var credential) || credential is null)
                throw new InvalidOperationException("Connect your Azure account to continue.");
            _identity = credentials.SignedInAs;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            _plan = await setup.InspectAsync(credential, settings, timeout.Token);
            Summary = _plan.Summary;
            Status = "One setup. Live speech, a fast model, and saved preferences.";
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or InvalidOperationException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            Status = SafeMessage(ex);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanStart)); }
    }

    public async Task StartAsync()
    {
        if (!CanStart) return;
        IsBusy = true;
        try
        {
            if (_identity != credentials.SignedInAs || !credentials.TryGetSignedInCredential(out var credential) || credential is null)
                throw new InvalidOperationException("Your Azure account changed. Reopen workspace setup.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            var result = await setup.ExecuteAsync(credential, credentials.TenantId, _plan!, Approved,
                new Progress<string>(message => Status = message), timeout.Token);
            await save(result, timeout.Token);
            Completed = true;
            Status = "Your workspace is ready. Speech streams live, and the diagram model is connected.";
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or InvalidOperationException or OperationCanceledException or System.Net.Http.HttpRequestException or System.IO.IOException or System.ClientModel.ClientResultException or AudioBoarder.Services.Transcription.TranscriptionInitializationException or System.Text.Json.JsonException)
        {
            Status = SafeMessage(ex);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanStart)); }
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        OperationCanceledException => "Setup stopped or timed out. Created Azure resources are kept; run setup again to resume.",
        Azure.RequestFailedException => AzureProvisioningViewModel.DescribeFailure(ex),
        System.Net.Http.HttpRequestException => "The Azure connection could not be validated. Check network access and try again.",
        System.ClientModel.ClientResultException { Status: 401 or 403 } => "Azure denied model access. Your administrator must grant model inference permission, then run setup again.",
        System.ClientModel.ClientResultException { Status: 429 } => "The diagram model is at its Azure quota limit. Wait briefly or ask your administrator for quota, then run setup again.",
        System.ClientModel.ClientResultException => "The model could not generate a compatible diagram. Run setup again or choose another deployment in Settings.",
        System.Text.Json.JsonException => "The model returned an incompatible diagram. Run setup again or choose another deployment in Settings.",
        AudioBoarder.Services.Transcription.TranscriptionInitializationException => "Live Speech could not connect. Check Azure Speech access and your network, then run setup again.",
        System.IO.IOException => "Local preferences could not be saved. Check access to your AudioBoarder data folder.",
        _ => ex.Message,
    };

    public void Cancel() => _lifetime.Cancel();
    public void Dispose() { Cancel(); _lifetime.Dispose(); }
}
