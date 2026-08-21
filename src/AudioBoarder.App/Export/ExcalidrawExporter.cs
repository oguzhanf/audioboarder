using System.IO;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Scene;
using Microsoft.Win32;

namespace AudioBoarder.App.Export;

/// <summary>
/// Saves the current diagram as a real <c>.excalidraw</c> file (schema v2). The
/// customer can open it in any Excalidraw instance, VS Code, or Obsidian to view
/// and keep editing the hand-drawn whiteboard after the meeting.
/// </summary>
public sealed class ExcalidrawExporter
{
    private readonly SceneToExcalidrawConverter _converter = new();

    /// <summary>Returns the saved path, or null if the user cancelled.</summary>
    public string? Export(SceneGraph scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var dialog = new SaveFileDialog
        {
            Title = "Export Excalidraw diagram",
            FileName = $"audioboarder-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.excalidraw",
            DefaultExt = ".excalidraw",
            Filter = "Excalidraw diagram (*.excalidraw)|*.excalidraw|JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return null;

        // The converter locks SceneGraph.SyncRoot internally, so passing the live
        // scene is safe even while a background patch is mutating it.
        var json = _converter.ConvertToJson(scene);
        File.WriteAllText(dialog.FileName, json);
        return dialog.FileName;
    }
}
