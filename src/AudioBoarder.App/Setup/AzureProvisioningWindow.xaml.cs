using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace AudioBoarder.App.Setup;

public partial class AzureProvisioningWindow : FluentWindow
{
    public AzureProvisioningViewModel ViewModel { get; }

    public AzureProvisioningWindow(AzureProvisioningViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        Closing += (_, e) => e.Cancel = viewModel.IsCreating;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();
    private async void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !ViewModel.IsBusy) await ViewModel.LoadAsync();
    }
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (HasBindingErrors(this))
        {
            ViewModel.StatusMessage = "Correct the highlighted fields before creating anything in Azure.";
            return;
        }
        if (await ViewModel.CreateAsync()) DialogResult = true;
    }
    private void OnStopWaiting(object sender, RoutedEventArgs e) => ViewModel.StopWaiting();
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool HasBindingErrors(DependencyObject element)
    {
        if (Validation.GetHasError(element)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            if (HasBindingErrors(child)) return true;
        return false;
    }
}
