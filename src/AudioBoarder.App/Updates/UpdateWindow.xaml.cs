using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AudioBoarder.App.ViewModels;
using Wpf.Ui.Controls;

namespace AudioBoarder.App.Updates;

public partial class UpdateWindow : FluentWindow
{
    private readonly GitHubUpdateService _updateService;
    private readonly UpdateRelease _release;
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _downloadCts = new(TimeSpan.FromMinutes(20));
    private string? _msiPath;
    private bool _isInstalling;
    private bool _dismissed;
    private bool _isDownloading;

    public UpdateWindow(
        GitHubUpdateService updateService,
        UpdateRelease release,
        MainViewModel viewModel)
    {
        InitializeComponent();
        _updateService = updateService;
        _release = release;
        _viewModel = viewModel;
        VersionText.Text = $"{release.Name} is ready. AudioBoarder will restart after installation.";
        if (release.IsUnsignedPreview)
        {
            VersionText.Text = $"{release.Name} is an unsigned preview. It will never install automatically.";
            PreviewConsent.Visibility = Visibility.Visible;
            UpdateNowButton.Content = "Install approved preview";
        }
        else if (release.RequiresManualInstaller)
        {
            VersionText.Text = $"{release.Name} is available. This installed build has no trusted publisher configured, so a manual signed-installer bootstrap is required.";
            UpdateNowButton.Content = "Open official release";
        }
        ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "This release does not include additional notes."
            : release.ReleaseNotes;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _downloadCts.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_release.IsUnsignedPreview || _release.RequiresManualInstaller)
        {
            DownloadProgress.IsIndeterminate = false;
            StatusText.Text = _release.IsUnsignedPreview
                ? "Approve this specific preview to download, verify its GitHub SHA-256, and install. Windows may show an unknown-publisher warning."
                : "The signed-release trust checks remain enforced. Open the official release to install it manually.";
            UpdateNowButton.IsEnabled = _release.RequiresManualInstaller;
            return;
        }
        try
        {
            DownloadProgress.IsIndeterminate = false;
            StatusText.Text = "Downloading verified installer…";
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100d;
                StatusText.Text = $"Downloading verified installer… {value:P0}";
            });

            _msiPath = await _updateService.DownloadAsync(
                _release, progress, _downloadCts.Token);
            UpdateNowButton.IsEnabled = true;
            if (_viewModel.IsListening)
            {
                StatusText.Text = "Update verified. Install it after the current listening session.";
                return;
            }

            for (var seconds = 5; seconds > 0 && !_dismissed && !_isInstalling; seconds--)
            {
                StatusText.Text = $"Download verified. Updating automatically in {seconds}…";
                await Task.Delay(1000, _downloadCts.Token);
            }

            if (!_dismissed && !_isInstalling)
                await InstallAsync();
        }
        catch (OperationCanceledException) when (_downloadCts.IsCancellationRequested)
        {
            if (!_dismissed)
                StatusText.Text = "Update download cancelled. AudioBoarder will continue running.";
        }
        catch (Exception ex)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            StatusText.Text = $"Update failed: {ex.Message} AudioBoarder will continue running.";
        }
    }

    private void OnPreviewConsentChanged(object sender, RoutedEventArgs e)
    {
        if (UpdateNowButton is not null)
            UpdateNowButton.IsEnabled = PreviewConsent.IsChecked == true && !_isInstalling && !_isDownloading;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_release.RequiresManualInstaller)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    $"https://github.com/oguzhanf/audioboarder/releases/tag/{Uri.EscapeDataString(_release.TagName)}")
                    { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                StatusText.Text = "The browser could not be opened. Visit github.com/oguzhanf/audioboarder/releases to get the signed installer.";
            }
            return;
        }
        if (_release.IsUnsignedPreview)
        {
            if (PreviewConsent.IsChecked != true || _isDownloading || _isInstalling) return;
            _isDownloading = true;
            UpdateNowButton.IsEnabled = false;
            PreviewConsent.IsEnabled = false;
            try
            {
                var progress = new Progress<double>(value =>
                {
                    DownloadProgress.Value = value * 100;
                    StatusText.Text = $"Downloading approved preview... {value:P0}";
                });
                _msiPath = await _updateService.DownloadApprovedPreviewAsync(
                    _release, true, progress, _downloadCts.Token);
                if (!_dismissed) await InstallAsync();
            }
            catch (OperationCanceledException) when (_downloadCts.IsCancellationRequested)
            {
                if (!_dismissed) StatusText.Text = "Preview download cancelled. Nothing was installed.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
            {
                StatusText.Text = $"Preview update failed: {ex.Message}";
            }
            finally
            {
                _isDownloading = false;
                PreviewConsent.IsEnabled = true;
                if (!_isInstalling) UpdateNowButton.IsEnabled = PreviewConsent.IsChecked == true;
            }
            return;
        }
        await InstallAsync();
    }

    private void OnRemindLater(object sender, RoutedEventArgs e)
    {
        _dismissed = true;
        _updateService.Defer(_release.TagName);
        _downloadCts.Cancel();
        Close();
    }

    private async Task InstallAsync()
    {
        if (_isInstalling || string.IsNullOrWhiteSpace(_msiPath))
            return;

        _isInstalling = true;
        LaterButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
        StatusText.Text = "Saving your session and preparing to restart…";
        try
        {
            await _viewModel.PrepareForUpdateAsync(forceSave: true);
            _updateService.BeginInstallAndRestart(_msiPath, _release,
                approveUnsignedPreview: _release.IsUnsignedPreview && PreviewConsent.IsChecked == true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            LaterButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = true;
            StatusText.Text = $"Could not start the update: {ex.Message}";
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isInstalling)
            return;

        _dismissed = true;
        _updateService.Defer(_release.TagName);
        _downloadCts.Cancel();
    }
}
