using AudioBoarder.Core.Scene;
using SkiaSharp;

namespace AudioBoarder.Services.Rendering;

/// <summary>
/// Convenience wrapper: render a scene to an in-memory PNG. Used by the
/// --smoke headless validation path and by unit tests.
/// </summary>
public static class SceneBitmapRenderer
{
    public static byte[] RenderPng(SceneGraph graph, int width, int height, SceneRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        renderer ??= new SceneRenderer();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        renderer.Render(surface.Canvas, graph, width, height);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
