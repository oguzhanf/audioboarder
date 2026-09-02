using AudioBoarder.App.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Tests.App;

public class UiStateStoreTests
{
    [Fact]
    public void DefaultsKeepTranscriptAndNotesCollapsed()
    {
        var state = new UiStateSnapshot();

        state.IsTranscriptPaneOpen.Should().BeFalse();
        state.IsNotesPaneOpen.Should().BeFalse();
    }

    [Fact]
    public void TogglePreferencesRoundTripWithoutSessionContent()
    {
        var folder = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "ui-state.json");
        try
        {
            var store = new JsonUiStateStore(path, NullLogger<JsonUiStateStore>.Instance);

            store.Save(new UiStateSnapshot(IsTranscriptPaneOpen: true, IsNotesPaneOpen: true));
            var loaded = store.Load();

            loaded.IsTranscriptPaneOpen.Should().BeTrue();
            loaded.IsNotesPaneOpen.Should().BeTrue();
            var json = File.ReadAllText(path).ToLowerInvariant();
            json.Should().NotContain("livetranscript");
            json.Should().NotContain("model");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
