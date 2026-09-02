using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudioBoarder.App.Configuration;

public sealed record UiStateSnapshot(
    bool IsTranscriptPaneOpen = false,
    bool IsNotesPaneOpen = false);

public interface IUiStateStore
{
    UiStateSnapshot Load();
    void Save(UiStateSnapshot state);
}

public sealed class JsonUiStateStore : IUiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<JsonUiStateStore> _logger;

    public JsonUiStateStore(ILogger<JsonUiStateStore> logger)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder",
            "ui-state.json"), logger)
    {
    }

    internal JsonUiStateStore(string path, ILogger<JsonUiStateStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public UiStateSnapshot Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UiStateSnapshot();
            return JsonSerializer.Deserialize<UiStateSnapshot>(
                       File.ReadAllText(_path), JsonOptions)
                   ?? new UiStateSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "UI state load failed; category={Category}",
                ex is JsonException ? "invalid_json" : "io_failure");
            return new UiStateSnapshot();
        }
    }

    public void Save(UiStateSnapshot state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "UI state save failed; category={Category}",
                ex is UnauthorizedAccessException ? "access_denied" : "io_failure");
        }
    }
}
