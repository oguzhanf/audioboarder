using System.Windows;
using System.Windows.Automation;
using System.Collections.Specialized;
using System.ComponentModel;
using AudioBoarder.App.Controls;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Services.Rendering;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioBoarder.App;

public partial class MainWindow : FluentWindow
{
    private const double TranscriptPaneWidth = 300d;
    private const double NotesPaneWidth = 320d;
    private const double NumericTolerance = 0.5d;

    private readonly MainViewModel _viewModel;
    private bool _isTranscriptPaneVisible = true;
    private bool _isNotesPaneVisible = true;
    private bool _isThemeWatcherActive;
    private int? _activeWhiteboardRevision;

    public MainWindow(MainViewModel viewModel, SceneRenderer renderer)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplicationThemeManager.Apply(GetApplicationThemeFromSystemTheme(), WindowBackdropType.Mica, true);
        Loaded += OnLoaded;
        Closing += OnClosing;

        Canvas.Scene = viewModel.Scene;
        Canvas.Renderer = renderer;
        Whiteboard.Scene = viewModel.Scene;
        Whiteboard.UserSceneChanged += OnWhiteboardUserSceneChanged;
        Whiteboard.Refresh();
        viewModel.SceneInvalidated += (_, _) =>
        {
            _activeWhiteboardRevision = null;
            Canvas.Invalidate();
            Whiteboard.Refresh();
        };

        // Keep the live transcript scrolled to the newest text.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TranscriptDisplay))
                Dispatcher.BeginInvoke(() => TranscriptBox.ScrollToEnd());
        };

        UpdateSidePaneLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        _isThemeWatcherActive = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isThemeWatcherActive)
        {
            SystemThemeWatcher.UnWatch(this);
            _isThemeWatcherActive = false;
        }

        Whiteboard.UserSceneChanged -= OnWhiteboardUserSceneChanged;
    }

    private void OnToggleTranscriptPane(object sender, RoutedEventArgs e)
    {
        _isTranscriptPaneVisible = !_isTranscriptPaneVisible;
        UpdateSidePaneLayout();
    }

    private void OnToggleNotesPane(object sender, RoutedEventArgs e)
    {
        _isNotesPaneVisible = !_isNotesPaneVisible;
        UpdateSidePaneLayout();
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e)
        => Onboarding.FirstRunExperience.Show(this, markComplete: false);

    private void UpdateSidePaneLayout()
    {
        TranscriptPane.Visibility = _isTranscriptPaneVisible ? Visibility.Visible : Visibility.Collapsed;
        TranscriptColumn.Width = _isTranscriptPaneVisible ? new GridLength(TranscriptPaneWidth) : new GridLength(0);
        NotesPane.Visibility = _isNotesPaneVisible ? Visibility.Visible : Visibility.Collapsed;
        NotesColumn.Width = _isNotesPaneVisible ? new GridLength(NotesPaneWidth) : new GridLength(0);

        SetPaneButtonState(TranscriptPaneButton, "transcript", _isTranscriptPaneVisible);
        SetPaneButtonState(NotesPaneButton, "notes", _isNotesPaneVisible);
    }

    private static void SetPaneButtonState(Button button, string paneName, bool isVisible)
    {
        var contentName = char.ToUpperInvariant(paneName[0]) + paneName[1..];
        button.Content = isVisible ? $"Hide {contentName}" : $"Show {contentName}";
        var action = isVisible ? "Hide" : "Show";
        button.ToolTip = $"{action} the {paneName} side panel.";
        AutomationProperties.SetName(button, $"{action} {paneName} panel");
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
            Canvas.Invalidate();
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