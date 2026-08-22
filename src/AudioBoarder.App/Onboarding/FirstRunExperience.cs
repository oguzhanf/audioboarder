using System.IO;
using System.Windows;

namespace AudioBoarder.App.Onboarding;

public static class FirstRunExperience
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioBoarder",
        "onboarding-v1.complete");

    public static bool IsComplete => File.Exists(MarkerPath);

    public static void Show(Window? owner, bool markComplete)
    {
        var window = new WelcomeWindow();
        if (owner?.IsVisible == true)
            window.Owner = owner;
        if (window.ShowDialog() == true && markComplete)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}
