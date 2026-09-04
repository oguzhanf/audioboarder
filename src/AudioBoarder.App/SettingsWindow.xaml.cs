using System.Windows;
using AudioBoarder.App.Configuration;
using AudioBoarder.Core.Scene;
using Wpf.Ui.Controls;

namespace AudioBoarder.App;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsService _settingsService;
    private readonly LocalDataService _localDataService;
    private readonly ILocalDataDeletionConfirmation _deletionConfirmation;
    private readonly AudioBoarderSettings _settings;

    public SettingsWindow(
        SettingsService settingsService,
        LocalDataService localDataService,
        ILocalDataDeletionConfirmation deletionConfirmation)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _localDataService = localDataService;
        _deletionConfirmation = deletionConfirmation;
        _settings = settingsService.Load();

        ThemeCombo.ItemsSource = new[] { "System", "Light", "Dark" };
        BackendCombo.ItemsSource = new[] { "auto", "cloud", "speech", "local" };
        IntentModeCombo.ItemsSource = Enum.GetValues<DiagramIntentSelectionMode>();
        PinnedIntentCombo.ItemsSource = Enum.GetValues<DiagramIntent>();
        DataContext = _settings;
    }

    public bool RestartRequested { get; private set; }
    public string SelectedTheme => _settings.Theme;

    private async void OnSave(object sender, RoutedEventArgs e) =>
        await SaveAndCloseAsync(restart: false);

    private async void OnSaveAndRestart(object sender, RoutedEventArgs e) =>
        await SaveAndCloseAsync(restart: true);

    private async Task SaveAndCloseAsync(bool restart)
    {
        if (!BindingGroup.UpdateSources())
        {
            StatusText.Text = "Correct the highlighted values before saving.";
            return;
        }
        var problems = _settings.Validate();
        if (problems.Count > 0)
        {
            StatusText.Text = string.Join(" ", problems);
            return;
        }

        try
        {
            StatusText.Text = "Saving…";
            await _settingsService.SaveAsync(
                _settings,
                new SettingsSecrets(
                    AzureOpenAIApiKeyBox.Password,
                    AzureSpeechApiKeyBox.Password,
                    ClearAzureOpenAIApiKeyBox.IsChecked == true,
                    ClearAzureSpeechApiKeyBox.IsChecked == true));
            RestartRequested = restart;
            DialogResult = true;
        }
        catch (Exception)
        {
            StatusText.Text = "Settings could not be saved in your local AudioBoarder data folder.";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            _localDataService.OpenDataFolder();
            StatusText.Text = _localDataService.RootDirectory;
        }
        catch (Exception)
        {
            StatusText.Text = "The local data folder could not be opened.";
        }
    }

    private async void OnDeleteLocalData(object sender, RoutedEventArgs e)
    {
        try
        {
            var deleted = await _localDataService.DeleteWithConfirmationAsync(_deletionConfirmation);
            StatusText.Text = deleted ? "Local data was deleted." : "Deletion cancelled.";
        }
        catch (Exception)
        {
            StatusText.Text = "Some local data could not be deleted while the app is running.";
        }
    }
}
