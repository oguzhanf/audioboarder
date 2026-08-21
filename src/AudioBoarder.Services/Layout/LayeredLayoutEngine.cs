using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Layout;

/// <summary>
/// A pragmatic layered (Sugiyama-style) layout. Builds layers by longest-path
/// from source nodes, then positions nodes inside each layer with even spacing.
/// Locked nodes keep their coordinates. Disconnected nodes form an extra layer.
/// </summary>
public sealed class LayeredLayoutEngine : ILayoutEngine
{
    public string Name => "LayeredLayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        var adjacency = BuildAdjacency(graph);
        var layerIndex = ComputeLayers(graph, adjacency);

        var layerGroups = layerIndex
            .GroupBy(kv => kv.Value, kv => kv.Key)
            .OrderBy(g => g.Key)
            .ToList();

        // Order each layer to minimise edge crossings (barycenter heuristic):
        // a node is placed near the average position of its neighbours in the
        // adjacent layers. A few up/down sweeps converge quickly.
        var ordered = layerGroups
            .Select(g => g.OrderBy(id => graph.Nodes[id].Label, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();
        OrderByBarycenter(ordered, graph);

        var positioned = 0;
        double maxRowWidth = 0;
        var y = options.Padding;
        var maxRowWidthAllowed = Math.Max(options.CanvasWidth - options.Padding * 2, 200);

        foreach (var layerIds in ordered)
        {
            var nodes = layerIds.Select(id => graph.Nodes[id]).ToList();

            // Wrap a wide layer into multiple sub-rows so it never overflows the
            // canvas width or crams every node onto one crowded line.
            var subRows = new List<List<SceneNode>>();
            var current = new List<SceneNode>();
            double currentWidth = 0;
            foreach (var node in nodes)
            {
                var add = node.Width + (current.Count > 0 ? options.HorizontalSpacing : 0);
                if (current.Count > 0 && currentWidth + add > maxRowWidthAllowed)
                {
                    subRows.Add(current);
                    current = new List<SceneNode>();
                    currentWidth = 0;
                    add = node.Width;
                }
                current.Add(node);
                currentWidth += add;
            }
            if (current.Count > 0) subRows.Add(current);

            foreach (var row in subRows)
            {
                var rowWidth = row.Sum(n => n.Width) + options.HorizontalSpacing * (row.Count - 1);
                maxRowWidth = Math.Max(maxRowWidth, rowWidth);
                var rowMaxHeight = row.Max(n => n.Height);
                var x = Math.Max(options.Padding, (options.CanvasWidth - rowWidth) / 2);

                foreach (var node in row)
                {
                    if (!node.Locked || node.X is null || node.Y is null)
                    {
                        node.X = x + node.Width / 2;
                        node.Y = y + rowMaxHeight / 2;
                        positioned++;
                    }
                    x += node.Width + options.HorizontalSpacing;
                }
                y += rowMaxHeight + options.VerticalSpacing;
            }
        }

        return new LayoutResult(positioned, maxRowWidth + options.Padding * 2, y + options.Padding);
    }

    private static void OrderByBarycenter(List<List<string>> layers, SceneGraph graph)
    {
        if (layers.Count < 2) return;

        // Undirected neighbour map so a node is pulled toward connections in both
        // the layer above and below it.
        var neighbours = graph.Nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in graph.Edges.Values)
        {
            if (neighbours.ContainsKey(e.FromNodeId) && neighbours.ContainsKey(e.ToNodeId))
            {
                neighbours[e.FromNodeId].Add(e.ToNodeId);
                neighbours[e.ToNodeId].Add(e.FromNodeId);
            }
        }

        Dictionary<string, int> IndexMap() =>
            layers.SelectMany(l => l.Select((id, i) => (id, i)))
                  .ToDictionary(t => t.id, t => t.i, StringComparer.Ordinal);

        for (var sweep = 0; sweep < 4; sweep++)
        {
            var pos = IndexMap();
            // alternate direction each sweep for stability
            var order = sweep % 2 == 0 ? Enumerable.Range(0, layers.Count) : Enumerable.Range(0, layers.Count).Reverse();
            foreach (var li in order)
            {
                var layer = layers[li];
                if (layer.Count < 2) continue;
                double Bary(string id)
                {
                    var ns = neighbours[id];
                    if (ns.Count == 0) return pos[id]; // keep stable if isolated
                    return ns.Average(n => (double)pos[n]);
                }
                layers[li] = layer
                    .Select(id => (id, b: Bary(id)))
                    .OrderBy(t => t.b)
                    .Select(t => t.id)
                    .ToList();
                pos = IndexMap();
            }
        }
    }

    private static Dictionary<string, List<string>> BuildAdjacency(SceneGraph graph)
    {
        var adj = graph.Nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in graph.Edges.Values)
        {
            if (adj.TryGetValue(e.FromNodeId, out var outs) && adj.ContainsKey(e.ToNodeId))
                outs.Add(e.ToNodeId);
        }
        return adj;
    }

    private static Dictionary<string, int> ComputeLayers(
        SceneGraph graph,
        Dictionary<string, List<string>> adj)
    {
        var layer = graph.Nodes.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);

        // Iterate to a fixed point — bounded by |V| iterations even with cycles
        // because we cap layer values at |V|.
        var n = graph.Nodes.Count;
        for (var iter = 0; iter < n + 1; iter++)
        {
            var changed = false;
            foreach (var (from, outs) in adj)
            {
                foreach (var to in outs)
                {
                    if (layer[to] <= layer[from])
                    {
                        var next = Math.Min(layer[from] + 1, n);
                        if (next != layer[to]) { layer[to] = next; changed = true; }
                    }
                }
            }
            if (!changed) break;
        }
        return layer;
    }
}
