using Wpf.Ui.Controls;

namespace AudioBoarder.App.Onboarding;

public partial class WelcomeWindow : FluentWindow
{
    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void OnGetStarted(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
