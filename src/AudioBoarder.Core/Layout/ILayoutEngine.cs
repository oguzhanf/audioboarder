using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Layout;

/// <summary>
/// Computes <c>X</c>/<c>Y</c> coordinates for any unlocated, unlocked nodes
/// in the supplied <see cref="SceneGraph"/>. Locked nodes are never moved.
/// </summary>
public interface ILayoutEngine
{
    string Name { get; }
    LayoutResult Apply(SceneGraph graph, LayoutOptions options);
}

public sealed record LayoutOptions(
    double CanvasWidth = 1200,
    double CanvasHeight = 800,
    double HorizontalSpacing = 60,
    double VerticalSpacing = 80,
    double Padding = 40,
    bool ReflowPinned = false);

public sealed record LayoutResult(int NodesPositioned, double BoundsWidth, double BoundsHeight);
