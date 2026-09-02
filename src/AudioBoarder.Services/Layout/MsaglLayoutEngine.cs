using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;
using Microsoft.Msagl.Core;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglEdge = Microsoft.Msagl.Core.Layout.Edge;
using MsaglNode = Microsoft.Msagl.Core.Layout.Node;

namespace AudioBoarder.Services.Layout;

/// <summary>
/// Layered (Sugiyama) layout backed by MSAGL (Microsoft Automatic Graph Layout).
/// Produces proper rank assignment and crossing-minimised ordering — a marked
/// upgrade over the hand-rolled <see cref="LayeredLayoutEngine"/> for non-trivial
/// graphs. Node positions feed both the classic SkiaSharp renderer and the
/// Excalidraw whiteboard (which routes the arrows itself).
///
/// MSAGL works in a Y-up coordinate space; the scene/renderers are Y-down, so the
/// vertical axis is flipped (direction auto-detected from edge orientation) and the
/// whole drawing is translated to start at <see cref="LayoutOptions.Padding"/>.
/// Locked (user-dragged) nodes keep their coordinates, mirroring the existing engine.
/// </summary>
public sealed class MsaglLayoutEngine : ILayoutEngine
{
    public string Name => "MsaglLayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        var geo = new GeometryGraph();
        var map = new Dictionary<string, MsaglNode>(StringComparer.Ordinal);

        foreach (var n in graph.Nodes.Values)
        {
            var w = n.Width <= 0 ? 140 : n.Width;
            var h = n.Height <= 0 ? 60 : n.Height;
            var node = new MsaglNode(CurveFactory.CreateRectangle(w, h, new Point(0, 0)), n.Id);
            geo.Nodes.Add(node);
            map[n.Id] = node;
        }

        foreach (var e in graph.Edges.Values)
        {
            if (map.TryGetValue(e.FromNodeId, out var a) &&
                map.TryGetValue(e.ToNodeId, out var b) &&
                !ReferenceEquals(a, b))
            {
                geo.Edges.Add(new MsaglEdge(a, b));
            }
        }

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = Math.Max(10, options.HorizontalSpacing),
            LayerSeparation = Math.Max(10, options.VerticalSpacing),
        };
        if (settings.EdgeRoutingSettings is not null)
            settings.EdgeRoutingSettings.EdgeRoutingMode = EdgeRoutingMode.StraightLine;

        try
        {
            LayoutHelpers.CalculateLayout(geo, settings, new CancelToken(), null);
        }
        catch
        {
            // MSAGL can throw on pathological inputs — never let layout crash the
            // diagram flow; leave any existing coordinates untouched.
            return new LayoutResult(0, 0, 0);
        }

        // Bounds in MSAGL space (computed from the laid-out node centres + sizes).
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in graph.Nodes.Values)
        {
            var node = map[n.Id];
            var w = n.Width <= 0 ? 140 : n.Width;
            var h = n.Height <= 0 ? 60 : n.Height;
            minX = Math.Min(minX, node.Center.X - w / 2);
            maxX = Math.Max(maxX, node.Center.X + w / 2);
            minY = Math.Min(minY, node.Center.Y - h / 2);
            maxY = Math.Max(maxY, node.Center.Y + h / 2);
        }

        // Decide vertical orientation from edge directions so sources end up at the
        // top regardless of MSAGL's internal convention.
        double dirSum = 0;
        foreach (var e in graph.Edges.Values)
        {
            if (map.TryGetValue(e.FromNodeId, out var a) && map.TryGetValue(e.ToNodeId, out var b))
                dirSum += a.Center.Y - b.Center.Y;
        }
        var flipY = dirSum >= 0;

        var positioned = 0;
        foreach (var n in graph.Nodes.Values)
        {
            var node = map[n.Id];
            var sx = node.Center.X - minX + options.Padding;
            var sy = (flipY ? maxY - node.Center.Y : node.Center.Y - minY) + options.Padding;

            // Mirror LayeredLayoutEngine: never move a locked node that already has a
            // position; everything else (incl. locked-but-unplaced) is laid out.
            if (options.ReflowPinned || !n.Locked || n.X is null || n.Y is null)
            {
                n.X = sx;
                n.Y = sy;
                positioned++;
            }
        }

        var boundsWidth = maxX - minX + options.Padding * 2;
        var boundsHeight = maxY - minY + options.Padding * 2;
        return new LayoutResult(positioned, boundsWidth, boundsHeight);
    }
}
