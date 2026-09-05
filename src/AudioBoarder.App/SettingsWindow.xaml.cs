using System.Windows;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Setup;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.LLM;
using Wpf.Ui.Controls;

namespace AudioBoarder.App;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsService _settingsService;
    private readonly LocalDataService _localDataService;
    private readonly ILocalDataDeletionConfirmation _deletionConfirmation;
    private readonly AudioBoarderSettings _settings;
    private readonly IAzureModelInventory _inventory;
    private readonly IAzureCredentialProvider _credentials;
    private readonly IAzureProvisioningService? _provisioning;
    private readonly string? _originalTenant;
    private readonly string? _originalEndpoint;
    private readonly string? _originalApiKey;
    private bool _refreshingProfiles;
    private readonly HashSet<(string Tenant, string Endpoint)> _entraConnections = [];

    public SettingsWindow(
        SettingsService settingsService,
        LocalDataService localDataService,
        ILocalDataDeletionConfirmation deletionConfirmation,
        IAzureModelInventory inventory,
        IAzureCredentialProvider credentials,
        IAzureProvisioningService? provisioning = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _localDataService = localDataService;
        _deletionConfirmation = deletionConfirmation;
        _inventory = inventory;
        _credentials = credentials;
        _provisioning = provisioning;
        _settings = settingsService.Load();
        var azureSettings = _settings.AzureOpenAI;
        _originalTenant = azureSettings.TenantId;
        _originalEndpoint = azureSettings.Endpoint;
        _originalApiKey = azureSettings.ApiKey;

        ThemeCombo.ItemsSource = new[] { "System", "Light", "Dark" };
        BackendCombo.ItemsSource = new[] { "auto", "cloud", "speech", "local" };
        IntentModeCombo.ItemsSource = Enum.GetValues<DiagramIntentSelectionMode>();
        PinnedIntentCombo.ItemsSource = Enum.GetValues<DiagramIntent>();
        RefreshModelAccounts();
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
        CaptureSelectedModelAccount();
        var problems = _settings.Validate();
        if (problems.Count > 0)
        {
            StatusText.Text = string.Join(" ", problems);
            return;
        }

        try
        {
            StatusText.Text = "Saving…";
            var connectionChanged = !string.Equals(_originalTenant, _settings.AzureOpenAI.TenantId, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(_originalEndpoint, _settings.AzureOpenAI.Endpoint, StringComparison.OrdinalIgnoreCase);
            await _settingsService.SaveAsync(
                _settings,
                new SettingsSecrets(
                    AzureOpenAIApiKeyBox.Password,
                    AzureSpeechApiKeyBox.Password,
                    ClearAzureOpenAIApiKeyBox.IsChecked == true ||
                        ((_entraConnections.Contains(CurrentConnection) || connectionChanged ||
                          string.IsNullOrWhiteSpace(_settings.AzureOpenAI.ApiKey)) &&
                         string.IsNullOrWhiteSpace(AzureOpenAIApiKeyBox.Password)),
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

    private void OnChooseModels(object sender, RoutedEventArgs e)
    {
        if (!BindingGroup.UpdateSources())
        {
            StatusText.Text = "Correct the highlighted values before choosing models.";
            return;
        }
        CaptureSelectedModelAccount();
        using var viewModel = new AzureSetupViewModel(_inventory, _credentials, _settings, _provisioning);
        var wizard = new AzureSetupWindow(viewModel) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.Selection is not { } selection) return;
        selection.ApplyTo(_settings);
        _entraConnections.Add(CurrentConnection);
        AzureOpenAIApiKeyBox.Clear();
        RefreshModelAccounts();
        DataContext = null;
        DataContext = _settings;
        StatusText.Text = "Model choices updated. Save & Restart to use them.";
    }

    public void ShowAzureSection() => AzureTab.IsSelected = true;

    private void OnModelAccountSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingProfiles || ModelAccountCombo.SelectedItem is not ModelAccountSettings profile) return;
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ModelAccountSettings previous)
        {
            if (!BindingGroup.UpdateSources())
            {
                RefreshModelAccounts(previous);
                StatusText.Text = "Correct the highlighted values before switching profiles.";
                return;
            }
            previous.CaptureFrom(_settings.AzureOpenAI, _settings.CloudTranscription, _settings.ImageGeneration);
        }
        _settings.ActiveModelAccountId = profile.Id;
        profile.ApplyTo(_settings.AzureOpenAI, _settings.CloudTranscription, _settings.ImageGeneration);
        if (CurrentConnection == ConnectionKey(_originalTenant, _originalEndpoint) &&
            !_entraConnections.Contains(CurrentConnection))
            _settings.AzureOpenAI.ApiKey = _originalApiKey;
        ModelAccountNameBox.Text = profile.Name;
        DataContext = null;
        DataContext = _settings;
    }

    private void OnSaveModelAccountProfile(object sender, RoutedEventArgs e)
    {
        if (!BindingGroup.UpdateSources())
        {
            StatusText.Text = "Correct the highlighted values before updating the profile.";
            return;
        }
        var profile = ModelAccountCombo.SelectedItem as ModelAccountSettings;
        if (profile is null)
        {
            profile = new ModelAccountSettings();
            _settings.ModelAccounts.Add(profile);
        }
        profile.Name = string.IsNullOrWhiteSpace(ModelAccountNameBox.Text)
            ? "Microsoft account"
            : ModelAccountNameBox.Text.Trim();
        profile.CaptureFrom(_settings.AzureOpenAI, _settings.CloudTranscription, _settings.ImageGeneration);
        _settings.ActiveModelAccountId = profile.Id;
        RefreshModelAccounts(profile);
        StatusText.Text = $"Saved model account profile “{profile.Name}”.";
    }

    private void OnNewModelAccountProfile(object sender, RoutedEventArgs e)
    {
        if (!BindingGroup.UpdateSources())
        {
            StatusText.Text = "Correct the highlighted values before adding a profile.";
            return;
        }
        CaptureSelectedModelAccount();
        var profile = new ModelAccountSettings
        {
            Name = "New tenant",
            AutoDiscover = true,
            UseManagedIdentity = true,
            TranscriptionBackend = "auto",
            ImagesEnabled = false,
        };
        _settings.ModelAccounts.Add(profile);
        _settings.ActiveModelAccountId = profile.Id;
        profile.ApplyTo(_settings.AzureOpenAI, _settings.CloudTranscription, _settings.ImageGeneration);
        RefreshModelAccounts(profile);
        DataContext = null;
        DataContext = _settings;
        StatusText.Text = "Enter the new tenant ID, then Save & Restart and sign in. Setup will guide you if resources are missing.";
    }

    private void OnDeleteModelAccountProfile(object sender, RoutedEventArgs e)
    {
        if (ModelAccountCombo.SelectedItem is not ModelAccountSettings profile) return;
        _settings.ModelAccounts.Remove(profile);
        if (string.Equals(_settings.ActiveModelAccountId, profile.Id, StringComparison.OrdinalIgnoreCase))
            _settings.ActiveModelAccountId = null;
        RefreshModelAccounts();
        StatusText.Text = "Removed the local model account profile. Cached tokens remain isolated by tenant.";
    }

    private void CaptureSelectedModelAccount()
    {
        if (ModelAccountCombo.SelectedItem is not ModelAccountSettings profile) return;
        profile.Name = string.IsNullOrWhiteSpace(ModelAccountNameBox.Text)
            ? profile.Name
            : ModelAccountNameBox.Text.Trim();
        profile.CaptureFrom(_settings.AzureOpenAI, _settings.CloudTranscription, _settings.ImageGeneration);
        _settings.ActiveModelAccountId = profile.Id;
    }

    private (string Tenant, string Endpoint) CurrentConnection =>
        ConnectionKey(_settings.AzureOpenAI.TenantId, _settings.AzureOpenAI.Endpoint);

    private static (string Tenant, string Endpoint) ConnectionKey(string? tenant, string? endpoint) =>
        (tenant?.Trim().ToLowerInvariant() ?? "", endpoint?.TrimEnd('/').ToLowerInvariant() ?? "");

    private void RefreshModelAccounts(ModelAccountSettings? selected = null)
    {
        selected ??= _settings.ModelAccounts.FirstOrDefault(x =>
            string.Equals(x.Id, _settings.ActiveModelAccountId, StringComparison.OrdinalIgnoreCase));
        _refreshingProfiles = true;
        try
        {
            ModelAccountCombo.ItemsSource = null;
            ModelAccountCombo.ItemsSource = _settings.ModelAccounts;
            ModelAccountCombo.SelectedItem = selected;
            ModelAccountNameBox.Text = selected?.Name ?? string.Empty;
        }
        finally
        {
            _refreshingProfiles = false;
        }
    }

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
