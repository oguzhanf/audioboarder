using System.Windows;
using System.ComponentModel;
using AudioBoarder.App.Controls;
using AudioBoarder.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioBoarder.App;

public partial class MainWindow : FluentWindow
{
    private const double NumericTolerance = 0.5d;

    private readonly MainViewModel _viewModel;
    private bool _isThemeWatcherActive;
    private int? _activeWhiteboardRevision;

    public MainWindow(MainViewModel viewModel, AudioBoarder.Core.Scene.AzureIconLibrary azureIcons)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplicationThemeManager.Apply(GetApplicationThemeFromSystemTheme(), WindowBackdropType.Mica, true);
        Loaded += OnLoaded;
        Closing += OnClosing;

        Whiteboard.Scene = viewModel.Scene;
        Whiteboard.AzureIcons = azureIcons;
        Whiteboard.UserSceneChanged += OnWhiteboardUserSceneChanged;
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
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        _isThemeWatcherActive = true;
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
            ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
            _isThemeWatcherActive = false;
        }

        Whiteboard.UserSceneChanged -= OnWhiteboardUserSceneChanged;
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e)
        => Onboarding.FirstRunExperience.Show(this, markComplete: false);

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

    private static bool ShouldApplySceneElement(ExcalidrawSceneElementChange element) =>
        !element.IsDeleted && element.Type is not "text" and not "arrow" and not "line" and not "frame";

    private static bool AreClose(double? current, double next) =>
        current.HasValue && Math.Abs(current.Value - next) <= NumericTolerance;

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