using System.Windows;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Services.Rendering;
using Wpf.Ui.Controls;

namespace AudioBoarder.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel, SceneRenderer renderer)
    {
        InitializeComponent();
        DataContext = viewModel;
        Canvas.Scene = viewModel.Scene;
        Canvas.Renderer = renderer;
        Whiteboard.Scene = viewModel.Scene;
        Whiteboard.Refresh();
        viewModel.SceneInvalidated += (_, _) =>
        {
            Canvas.Invalidate();
            Whiteboard.Refresh();
        };

        // Keep the live transcript scrolled to the newest text.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TranscriptDisplay))
                Dispatcher.BeginInvoke(() => TranscriptBox.ScrollToEnd());
        };
    }
}