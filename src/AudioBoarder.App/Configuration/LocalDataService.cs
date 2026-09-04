using System.Diagnostics;
using System.IO;
using AudioBoarder.App.Sessions;

namespace AudioBoarder.App.Configuration;

public interface ILocalDataDeletionConfirmation
{
    bool ConfirmDeleteLocalData();
}

public sealed class MessageBoxLocalDataDeletionConfirmation : ILocalDataDeletionConfirmation
{
    public bool ConfirmDeleteLocalData() =>
        System.Windows.MessageBox.Show(
            "Delete saved boards, UI state, cached sign-in metadata, update files, and local web cache? " +
            "This cannot be undone. Your app settings are kept.",
            "Delete local AudioBoarder data",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
}

public sealed class LocalDataService
{
    private static readonly string[] LocalFiles =
    [
        "auth-record.json",
        "onboarding-v1.complete",
        "ui-state.json",
        "update-state.json",
    ];

    private static readonly string[] LocalDirectories =
    [
        "webview2",
        "updates",
    ];

    private readonly string _root;
    private readonly Func<CancellationToken, Task> _clearSessions;

    public LocalDataService(SessionStore sessions)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioBoarder"),
            sessions.ClearAsync)
    {
    }

    internal LocalDataService(string root, Func<CancellationToken, Task>? clearSessions = null)
    {
        _root = root;
        _clearSessions = clearSessions ?? (_ => Task.CompletedTask);
    }

    public string RootDirectory => _root;

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(_root);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_root}\"",
            UseShellExecute = true,
        });
    }

    public async Task<bool> DeleteWithConfirmationAsync(
        ILocalDataDeletionConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.ConfirmDeleteLocalData())
            return false;

        await _clearSessions(cancellationToken).ConfigureAwait(false);

        foreach (var fileName in LocalFiles)
        {
            var path = Path.Combine(_root, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        foreach (var directoryName in LocalDirectories)
        {
            var path = Path.Combine(_root, directoryName);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        return true;
    }
}
