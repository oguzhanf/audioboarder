using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Layout;

/// <summary>
/// Radial mind-map layout. Each connected component becomes its own mind map: the
/// most-connected node is the central idea, its branches fan out around it, and
/// deeper sub-ideas radiate outward in angular wedges so sibling subtrees never
/// overlap. Multiple components (separate central ideas, as the prompt is told to
/// produce when topics are unrelated) tile in a compact 2-D grid instead of growing
/// in one ever-widening horizontal row. The central node of each map is enlarged so
/// it reads as the hub. Locked (user-dragged) nodes keep their coordinates.
/// </summary>
public sealed class MindMapLayoutEngine : ILayoutEngine
{
    public string Name => "MindMapLayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        var ids = graph.Nodes.Keys.ToList();
        var adj = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var e in graph.Edges.Values)
        {
            if (e.FromNodeId == e.ToNodeId) continue;
            if (adj.TryGetValue(e.FromNodeId, out var fromSet) && adj.TryGetValue(e.ToNodeId, out var toSet))
            {
                fromSet.Add(e.ToNodeId);
                toSet.Add(e.FromNodeId);
            }
        }

        var components = FindComponents(ids, adj);

        var local = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var boxes = new List<ComponentBox>();

        foreach (var comp in components)
        {
            var root = ChooseRoot(comp, adj);
            roots.Add(root);
            var children = BuildSpanningTree(root, adj);
            var leaves = ComputeLeafCounts(root, children);

            var avgW = comp.Average(id => Size(graph.Nodes[id]).W);
            var avgH = comp.Average(id => Size(graph.Nodes[id]).H);
            var ring1 = ComputeFirstRing(children[root].Count, avgW, options);
            var levelGap = Math.Max(160, avgH + options.VerticalSpacing * 1.4);

            local[root] = (0, 0);
            PlaceChildren(root, 0, Math.PI * 2, 1, children, leaves, ring1, levelGap, local);

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var id in comp)
            {
                var (w, h) = Size(graph.Nodes[id]);
                var (lx, ly) = local[id];
                minX = Math.Min(minX, lx - w / 2);
                maxX = Math.Max(maxX, lx + w / 2);
                minY = Math.Min(minY, ly - h / 2);
                maxY = Math.Max(maxY, ly + h / 2);
            }
            boxes.Add(new ComponentBox(comp, maxX - minX, maxY - minY, (minX + maxX) / 2, (minY + maxY) / 2));
        }

        // Tile components in a compact grid so several mind maps stack in 2-D.
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(boxes.Count)));
        var margin = options.HorizontalSpacing * 2 + 60;
        var cellW = boxes.Max(b => b.Width) + margin;
        var cellH = boxes.Max(b => b.Height) + margin;

        var global = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        for (var k = 0; k < boxes.Count; k++)
        {
            int col = k % cols, row = k / cols;
            var cellCx = col * cellW + cellW / 2;
            var cellCy = row * cellH + cellH / 2;
            var box = boxes[k];
            foreach (var id in box.Nodes)
            {
                var (lx, ly) = local[id];
                global[id] = (lx - box.CenterX + cellCx, ly - box.CenterY + cellCy);
            }
        }

        double gMinX = global.Values.Min(p => p.X), gMinY = global.Values.Min(p => p.Y);
        double gMaxX = global.Values.Max(p => p.X), gMaxY = global.Values.Max(p => p.Y);
        var offX = options.Padding - gMinX;
        var offY = options.Padding - gMinY;

        var positioned = 0;
        foreach (var node in graph.Nodes.Values)
        {
            // Emphasise the central idea so the hub reads as the main topic.
            if (roots.Contains(node.Id) && !node.Locked)
            {
                node.Width = Math.Max(node.Width, 184);
                node.Height = Math.Max(node.Height, 78);
            }

            var (gx, gy) = global[node.Id];
            if (!node.Locked || node.X is null || node.Y is null)
            {
                node.X = gx + offX;
                node.Y = gy + offY;
                positioned++;
            }
        }

        var boundsW = gMaxX - gMinX + options.Padding * 2;
        var boundsH = gMaxY - gMinY + options.Padding * 2;
        return new LayoutResult(positioned, boundsW, boundsH);
    }

    private readonly record struct ComponentBox(List<string> Nodes, double Width, double Height, double CenterX, double CenterY);

    private static (double W, double H) Size(SceneNode n)
        => (n.Width <= 0 ? 140 : n.Width, n.Height <= 0 ? 60 : n.Height);

    private static List<List<string>> FindComponents(List<string> ids, Dictionary<string, HashSet<string>> adj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<List<string>>();
        foreach (var start in ids)
        {
            if (!seen.Add(start)) continue;
            var comp = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                comp.Add(cur);
                foreach (var nb in adj[cur])
                    if (seen.Add(nb)) queue.Enqueue(nb);
            }
            components.Add(comp);
        }
        return components;
    }

    private static string ChooseRoot(List<string> comp, Dictionary<string, HashSet<string>> adj)
    {
        // Central idea = the most-connected node; deterministic tie-break by id.
        var root = comp[0];
        var best = -1;
        foreach (var id in comp)
        {
            var deg = adj[id].Count;
            if (deg > best || (deg == best && string.CompareOrdinal(id, root) < 0))
            {
                best = deg;
                root = id;
            }
        }
        return root;
    }

    private static Dictionary<string, List<string>> BuildSpanningTree(string root, Dictionary<string, HashSet<string>> adj)
    {
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        children[root] = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var nb in adj[cur].OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!visited.Add(nb)) continue;
                children[cur].Add(nb);
                children[nb] = new List<string>();
                queue.Enqueue(nb);
            }
        }
        return children;
    }

    private static Dictionary<string, int> ComputeLeafCounts(string root, Dictionary<string, List<string>> children)
    {
        var leaves = new Dictionary<string, int>(StringComparer.Ordinal);

        int Count(string node)
        {
            var kids = children[node];
            if (kids.Count == 0) { leaves[node] = 1; return 1; }
            var total = 0;
            foreach (var k in kids) total += Count(k);
            leaves[node] = total;
            return total;
        }

        Count(root);
        return leaves;
    }

    private static double ComputeFirstRing(int rootChildCount, double avgWidth, LayoutOptions options)
    {
        // Make the inner ring big enough that all main branches fit around the circle.
        var needed = rootChildCount * (avgWidth + options.HorizontalSpacing) / (2 * Math.PI);
        return Math.Max(210, needed);
    }

    private static void PlaceChildren(
        string node, double a0, double a1, int depth,
        Dictionary<string, List<string>> children,
        Dictionary<string, int> leaves,
        double ring1, double levelGap,
        Dictionary<string, (double X, double Y)> local)
    {
        var kids = children[node];
        if (kids.Count == 0) return;

        var radius = ring1 + (depth - 1) * levelGap;
        double total = kids.Sum(k => leaves[k]);
        var a = a0;
        foreach (var child in kids)
        {
            var wedge = leaves[child] / total * (a1 - a0);
            var mid = a + wedge / 2;
            local[child] = (radius * Math.Cos(mid), radius * Math.Sin(mid));
            PlaceChildren(child, a, a + wedge, depth + 1, children, leaves, ring1, levelGap, local);
            a += wedge;
        }
    }
}
