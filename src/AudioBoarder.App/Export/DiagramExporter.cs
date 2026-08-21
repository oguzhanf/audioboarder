using System.IO;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Rendering;
using Microsoft.Win32;

namespace AudioBoarder.App.Export;

/// <summary>
/// Saves the current diagram to disk as PNG. Opens the standard Windows
/// "Save as" dialog so the user controls the location.
/// </summary>
public sealed class DiagramExporter
{
    private readonly SceneRenderer _renderer;

    public DiagramExporter(SceneRenderer renderer)
    {
        _renderer = renderer;
    }

    /// <summary>Returns the saved path, or null if the user cancelled.</summary>
    public string? ExportPng(SceneGraph scene, int width = 1600, int height = 1000)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var dialog = new SaveFileDialog
        {
            Title = "Export diagram",
            FileName = $"audioboarder-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png",
        };
        if (dialog.ShowDialog() != true) return null;

        var bytes = SceneBitmapRenderer.RenderPng(scene, width, height, _renderer);
        File.WriteAllBytes(dialog.FileName, bytes);
        return dialog.FileName;
    }
}
