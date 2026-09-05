using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioBoarder.Services.LLM;
using Wpf.Ui.Controls;

namespace AudioBoarder.App.Setup;

public partial class AzureSetupWindow : FluentWindow
{
    private readonly AzureSetupViewModel _viewModel;
    private readonly Func<AzureModelSelection, CancellationToken, Task>? _save;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _saving;

    public AzureSetupWindow(
        AzureSetupViewModel viewModel,
        Func<AzureModelSelection, CancellationToken, Task>? save = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _save = save;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += (_, e) => e.Cancel = _saving;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _viewModel.Dispose();
            _lifetime.Dispose();
        };
    }

    public AzureModelSelection? Selection { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasLoaded)
            await _viewModel.RefreshAsync(_lifetime.Token);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_saving || _viewModel.IsBusy) return;
        await _viewModel.RefreshAsync(_lifetime.Token);
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        await _viewModel.SignInAsync(_lifetime.Token);
    }

    private async void OnSubscriptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_viewModel.IsBusy && _viewModel.SelectedSubscription is not null)
            await _viewModel.RefreshAccountsAsync(_lifetime.Token);
    }

    private void OnStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Steps || BackButton is null) return;
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        NextButton.Visibility = Steps.SelectedIndex < 2 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = Steps.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        if (Steps.SelectedIndex == 0 && !_viewModel.CanProceed)
        {
            _viewModel.StatusMessage = _viewModel.SelectionProblem ?? "Choose a resource with a ready chat deployment.";
            return;
        }
        if (Steps.SelectedIndex == 1 && !_viewModel.CanSave)
        {
            _viewModel.StatusMessage = _viewModel.SelectionProblem ?? "Complete the required model choices.";
            return;
        }
        Steps.SelectedIndex = Math.Min(2, Steps.SelectedIndex + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Steps.SelectedIndex = Math.Max(0, Steps.SelectedIndex - 1);
    private void OnClearFast(object sender, RoutedEventArgs e) => _viewModel.SelectedFastChat = null;
    private void OnClearTranscription(object sender, RoutedEventArgs e) => _viewModel.SelectedTranscription = null;
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private async void OnFinish(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave) return;
        var selection = _viewModel.GetSelection();
        _saving = true;
        _viewModel.IsBusy = true;
        Steps.IsEnabled = false;
        try
        {
            if (_save is not null)
                await _save(selection, _lifetime.Token);
            Selection = selection;
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _viewModel.StatusMessage = "The configuration could not be saved. Check the local settings file and folder permissions, then retry. Your running configuration has not changed.";
        }
        finally
        {
            _saving = false;
            _viewModel.IsBusy = false;
            Steps.IsEnabled = true;
        }
    }

    private async void OnOpenAzureOpenAI(object sender, RoutedEventArgs e) =>
        await ProvisionAsync(kind: "OpenAI");

    private async void OnOpenFoundry(object sender, RoutedEventArgs e) =>
        await ProvisionAsync(kind: "AIServices");

    private async void OnDeployChat(object sender, RoutedEventArgs e) => await ProvisionAsync(AzureModelRole.Chat);
    private async void OnDeployTranscription(object sender, RoutedEventArgs e) => await ProvisionAsync(AzureModelRole.Transcription);
    private async void OnDeployImage(object sender, RoutedEventArgs e) => await ProvisionAsync(AzureModelRole.Image);

    private async Task ProvisionAsync(AzureModelRole? role = null, string kind = "AIServices")
    {
        if (!_viewModel.CanProvision)
        {
            _viewModel.StatusMessage = "Sign in and select a subscription before provisioning.";
            return;
        }
        using var viewModel = _viewModel.CreateProvisioning(role);
        viewModel.Kind = kind;
        var dialog = new AzureProvisioningWindow(viewModel) { Owner = this };
        if (dialog.ShowDialog() == true)
            await _viewModel.SelectProvisionedAsync(viewModel, _lifetime.Token);
    }

    private void OnSubscriptionAccess(object sender, RoutedEventArgs e) =>
        OpenPortal("https://portal.azure.com/#view/Microsoft_Azure_Billing/SubscriptionsBlade");

    private void OnManageResource(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAccount is not { } account)
        {
            _viewModel.StatusMessage = "Choose a resource first, or use a setup link to create one.";
            return;
        }
        var path = string.Join("/", account.Id.Split('/').Select(Uri.EscapeDataString));
        OpenPortal($"https://portal.azure.com/#resource{path}/overview");
    }

    private void OpenPortal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            _viewModel.StatusMessage = $"The browser could not be opened. Open {url} manually, then return and Refresh.";
        }
    }
}
