using System.Windows;
using Wpf.Ui.Controls;

namespace AudioBoarder.App.Setup;

public partial class AutomaticSetupWindow : FluentWindow
{
    private readonly AutomaticSetupViewModel _viewModel;
    public AutomaticSetupWindow(AutomaticSetupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        Closing += (_, _) => viewModel.Cancel();
        Closed += (_, _) => viewModel.Dispose();
    }
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        Approval.IsEnabled = false;
        try { await _viewModel.StartAsync(); }
        finally
        {
            Approval.IsEnabled = !_viewModel.Completed;
            if (_viewModel.Completed)
            {
                Start.Visibility = Visibility.Collapsed;
                Later.Visibility = Visibility.Collapsed;
                Done.Visibility = Visibility.Visible;
            }
        }
    }
    private void OnLater(object sender, RoutedEventArgs e) => Close();
    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = true;
}
