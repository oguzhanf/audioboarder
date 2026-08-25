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
/// The production layout: a proper layered (Sugiyama) drawing per group, packed onto
/// shelves.
/// <para>
/// The previous radial mind-map fanned every branch out around a hub, which produced
/// a starburst of diagonal edges that read as scribble rather than a diagram. Layered
/// layout assigns ranks and minimises crossings, so flows run top-to-bottom like a
/// real flowchart and arrows can be routed as elbows.
/// </para>
/// <para>
/// Falls back to <see cref="MindMapLayoutEngine"/> if MSAGL cannot lay a cluster out,
/// so a pathological graph degrades instead of leaving nodes unpositioned.
/// </para>
/// </summary>
public sealed class LayeredGroupLayoutEngine : ILayoutEngine
{
    private readonly MindMapLayoutEngine _fallback = new();

    public string Name => "LayeredGroupLayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        var ids = graph.Nodes.Keys.ToList();
        var adj = ClusterPacker.BuildAdjacency(graph, ids);
        var clusters = ClusterPacker.BuildClusters(ids, adj, graph);

        var local = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var boxes = new List<ClusterPacker.ClusterBox>();

        foreach (var cluster in clusters)
        {
            if (!TryLayoutCluster(graph, cluster, options, local))
                return _fallback.Apply(graph, options);
            boxes.Add(ClusterPacker.Measure(cluster, graph, local));
        }

        if (boxes.Count == 0) return new LayoutResult(0, 0, 0);

        var global = ClusterPacker.Pack(
            boxes, local, options.HorizontalSpacing, options.VerticalSpacing);
        var positioned = ClusterPacker.Commit(graph, global, options.Padding);

        var width = global.Values.Max(p => p.X) - global.Values.Min(p => p.X) + options.Padding * 2;
        var height = global.Values.Max(p => p.Y) - global.Values.Min(p => p.Y) + options.Padding * 2;
        return new LayoutResult(positioned, width, height);
    }

    private static bool TryLayoutCluster(
        SceneGraph graph,
        List<string> cluster,
        LayoutOptions options,
        Dictionary<string, (double X, double Y)> local)
    {
        var members = new HashSet<string>(cluster, StringComparer.Ordinal);

        // A single node needs no layout — and MSAGL is unhappy with a trivial graph.
        if (cluster.Count == 1)
        {
            local[cluster[0]] = (0, 0);
            return true;
        }

        var geo = new GeometryGraph();
        var map = new Dictionary<string, MsaglNode>(StringComparer.Ordinal);
        foreach (var id in cluster)
        {
            var node = graph.Nodes[id];
            var w = node.Width <= 0 ? 140 : node.Width;
            var h = node.Height <= 0 ? 60 : node.Height;
            var geoNode = new MsaglNode(CurveFactory.CreateRectangle(w, h, new Point(0, 0)), id);
            geo.Nodes.Add(geoNode);
            map[id] = geoNode;
        }

        foreach (var edge in graph.Edges.Values)
        {
            if (!members.Contains(edge.FromNodeId) || !members.Contains(edge.ToNodeId)) continue;
            if (string.Equals(edge.FromNodeId, edge.ToNodeId, StringComparison.Ordinal)) continue;
            geo.Edges.Add(new MsaglEdge(map[edge.FromNodeId], map[edge.ToNodeId]));
        }

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = Math.Max(24, options.HorizontalSpacing),
            LayerSeparation = Math.Max(48, options.VerticalSpacing),
        };
        if (settings.EdgeRoutingSettings is not null)
        {
            // Only node centres are read below — Excalidraw draws the edges itself.
            // Rectilinear routing is MSAGL's slowest and most exception-prone stage,
            // and this runs under the same lock the renderer takes.
            settings.EdgeRoutingSettings.EdgeRoutingMode = EdgeRoutingMode.None;
        }

        try
        {
            LayoutHelpers.CalculateLayout(geo, settings, new CancelToken(), null);
        }
        catch
        {
            return false;
        }

        // MSAGL works Y-up; the scene is Y-down. Flip so sources sit at the top.
        double dirSum = 0;
        foreach (var edge in graph.Edges.Values)
        {
            if (map.TryGetValue(edge.FromNodeId, out var a) && map.TryGetValue(edge.ToNodeId, out var b))
                dirSum += a.Center.Y - b.Center.Y;
        }
        var flipY = dirSum >= 0;

        foreach (var id in cluster)
        {
            var c = map[id].Center;
            local[id] = (c.X, flipY ? -c.Y : c.Y);
        }
        return true;
    }
}
