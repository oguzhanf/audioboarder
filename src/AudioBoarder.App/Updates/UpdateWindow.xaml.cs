using System.ComponentModel;
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

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
        => await InstallAsync();

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
            await _viewModel.PrepareForUpdateAsync();
            _updateService.BeginInstallAndRestart(_msiPath, _release);
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
