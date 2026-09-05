using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Controls;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Services.LLM;
using Microsoft.Extensions.Options;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace AudioBoarder.App;

public partial class MainWindow : FluentWindow
{
    private const double NumericTolerance = 0.5d;
    public static readonly RoutedUICommand OpenSettingsCommand = new(
        "Open Settings", nameof(OpenSettingsCommand), typeof(MainWindow));

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly LocalDataService _localDataService;
    private readonly ILocalDataDeletionConfirmation _deletionConfirmation;
    private readonly IAzureModelInventory _inventory;
    private readonly IAzureCredentialProvider _credentials;
    private readonly IAzureProvisioningService _provisioning;
    private string _themePreference = "System";
    private bool _isThemeWatcherActive;
    private int? _activeWhiteboardRevision;

    public MainWindow(
        MainViewModel viewModel,
        AudioBoarder.Core.Scene.AzureIconLibrary azureIcons,
        IOptions<AudioBoarderSettings> settings,
        SettingsService settingsService,
        LocalDataService localDataService,
        ILocalDataDeletionConfirmation deletionConfirmation,
        IAzureModelInventory inventory,
        IAzureCredentialProvider credentials,
        IAzureProvisioningService provisioning)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _localDataService = localDataService;
        _deletionConfirmation = deletionConfirmation;
        _inventory = inventory;
        _credentials = credentials;
        _provisioning = provisioning;
        DataContext = viewModel;
        ApplyThemePreference(settings.Value.Theme);
        Loaded += OnLoaded;
        Closing += OnClosing;

        Whiteboard.Scene = viewModel.Scene;
        Whiteboard.AzureIcons = azureIcons;
        Whiteboard.UserSceneChanged += OnWhiteboardUserSceneChanged;
        Whiteboard.ComponentDropped += OnWhiteboardComponentDropped;
        Whiteboard.Refresh();
        viewModel.SceneInvalidated += (_, _) =>
        {
            _activeWhiteboardRevision = null;
            Whiteboard.Refresh();
        };

        // Keep the live transcript scrolled to the newest text.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TranscriptDisplay))
                Dispatcher.BeginInvoke(() => TranscriptBox.ScrollToEnd());
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyThemePreference(_themePreference);
        SyncCanvasTheme();
        // The WebView can't observe the app theme, so push it on every change.
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
    }

    private void OnApplicationThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
        => SyncCanvasTheme();

    private void SyncCanvasTheme() =>
        Whiteboard.SetTheme(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isThemeWatcherActive)
        {
            SystemThemeWatcher.UnWatch(this);
            _isThemeWatcherActive = false;
        }
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;

        Whiteboard.UserSceneChanged -= OnWhiteboardUserSceneChanged;
        Whiteboard.ComponentDropped -= OnWhiteboardComponentDropped;
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e)
        => Onboarding.FirstRunExperience.Show(this, markComplete: false);

    private void OnOpenMoreMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
    }

    private void OnOpenAudioMenu(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshInputDevices();
        var menu = new WpfContextMenu { PlacementTarget = AudioInputButton };
        foreach (var device in _viewModel.InputDevices)
        {
            var item = new WpfMenuItem
            {
                Header = device.Name,
                IsCheckable = true,
                IsChecked = string.Equals(
                    device.Id,
                    _viewModel.SelectedInputDevice?.Id,
                    StringComparison.Ordinal),
            };
            item.Click += (_, _) => _viewModel.SelectedInputDevice = device;
            menu.Items.Add(item);
        }

        menu.Items.Add(new WpfSeparator());
        var refresh = new WpfMenuItem { Header = "Rescan microphones" };
        refresh.Click += (_, _) => _viewModel.RefreshInputDevices();
        menu.Items.Add(refresh);
        menu.IsOpen = true;
    }

    private async void OnOpenSettings(object sender, ExecutedRoutedEventArgs e)
        => await ShowSettingsAsync();

    private async void OnOpenSettingsMenuItem(object sender, RoutedEventArgs e)
        => await ShowSettingsAsync();

    private async void OnConfigureAzure(object sender, RoutedEventArgs e)
        => await ShowSettingsAsync(showAzure: true);

    private async Task ShowSettingsAsync(bool showAzure = false)
    {
        var window = new SettingsWindow(
            _settingsService, _localDataService, _deletionConfirmation, _inventory, _credentials, _provisioning)
        {
            Owner = this,
        };
        if (showAzure) window.ShowAzureSection();

        if (window.ShowDialog() != true)
            return;

        ApplyThemePreference(window.SelectedTheme);
        if (!window.RestartRequested)
            return;

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            await _viewModel.PrepareForUpdateAsync(forceSave: true);
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--restore-session",
                UseShellExecute = true,
            });
            Application.Current.Shutdown();
        }
    }

    private void OnWhiteboardUserSceneChanged(object? sender, ExcalidrawSceneChangedEventArgs e)
    {
        var change = e.Change;
        var scene = _viewModel.Scene;
        if (change.SceneRevision is int revision)
        {
            if (revision != scene.Revision && revision != _activeWhiteboardRevision)
                return;
            _activeWhiteboardRevision = revision;
        }

        var updated = false;

        lock (scene.SyncRoot)
        {
            foreach (var element in change.Elements)
            {
                if (!ShouldApplySceneElement(element) || !scene.Nodes.TryGetValue(element.Id, out var node))
                    continue;

                var width = element.Width > 0 ? element.Width : node.Width;
                var height = element.Height > 0 ? element.Height : node.Height;
                var centerX = element.X + width / 2d;
                var centerY = element.Y + height / 2d;
                var positionChanged = !AreClose(node.X, centerX) || !AreClose(node.Y, centerY);
                var sizeChanged = Math.Abs(node.Width - width) > NumericTolerance ||
                                  Math.Abs(node.Height - height) > NumericTolerance;
                var shouldLock = element.Locked || positionChanged || sizeChanged;

                if (!positionChanged && !sizeChanged && node.Locked == shouldLock)
                    continue;

                updated |= scene.TryUpdateNodeGeometry(
                    element.Id, centerX, centerY, width, height, shouldLock);
            }
        }

        if (updated)
        {
            _viewModel.NotifyUserSceneEdited();
        }
    }

    private void OnWhiteboardComponentDropped(object? sender, CanvasComponentDroppedEventArgs e)
    {
        var component = AudioBoarder.Core.Scene.MicrosoftComponentCatalog.Find(e.Change.ComponentId);
        if (component is null) return;

        var node = new AudioBoarder.Core.Scene.SceneNode
        {
            Id = $"user-{component.Id}-{Guid.NewGuid():N}",
            Kind = component.Kind,
            Label = component.Name,
            Icon = component.Icon,
            Description = component.Description,
            X = e.Change.X,
            Y = e.Change.Y,
            Width = 190,
            Height = 70,
        };

        if (_viewModel.Scene.TryAddUserNode(node))
        {
            _viewModel.NotifyUserSceneEdited();
            Whiteboard.Refresh();
        }
    }

    private static bool ShouldApplySceneElement(ExcalidrawSceneElementChange element) =>
        !element.IsDeleted && element.Type is not "text" and not "arrow" and not "line" and not "frame";

    private static bool AreClose(double? current, double next) =>
        current.HasValue && Math.Abs(current.Value - next) <= NumericTolerance;

    private void ApplyThemePreference(string? preference)
    {
        _themePreference = string.IsNullOrWhiteSpace(preference) ? "System" : preference;
        var followsSystem = string.IsNullOrWhiteSpace(preference) ||
                            string.Equals(preference, "System", StringComparison.OrdinalIgnoreCase);
        var theme = followsSystem
            ? GetApplicationThemeFromSystemTheme()
            : string.Equals(preference, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);

        if (IsLoaded && followsSystem && !_isThemeWatcherActive)
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            _isThemeWatcherActive = true;
        }
        else if (!followsSystem && _isThemeWatcherActive)
        {
            SystemThemeWatcher.UnWatch(this);
            _isThemeWatcherActive = false;
        }
    }

    private static ApplicationTheme GetApplicationThemeFromSystemTheme() =>
        SystemThemeManager.GetCachedSystemTheme() switch
        {
            SystemTheme.Dark => ApplicationTheme.Dark,
            SystemTheme.HCWhite or
            SystemTheme.HCBlack or
            SystemTheme.HC1 or
            SystemTheme.HC2 => ApplicationTheme.HighContrast,
            _ => ApplicationTheme.Light,
        };
}