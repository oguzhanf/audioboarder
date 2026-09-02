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

        // Cluster by group FIRST, across the whole graph. A group frame is drawn from
        // the bounds of ALL its members, so a group must be exactly one cluster — if
        // its members are split across connectivity components they get tiled into
        // different cells and the single frame stretches across both, overlapping its
        // neighbours. That is the original hairball symptom.
        var clusters = BuildClusters(ids, adj, graph);

        var local = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var boxes = new List<ComponentBox>();

        foreach (var comp in clusters)
        {
            // Confine traversal to this cluster, otherwise the spanning tree would
            // walk out through an edge that leaves the group and place foreign nodes.
            var members = new HashSet<string>(comp, StringComparer.Ordinal);
            var localAdj = comp.ToDictionary(
                id => id,
                id => new HashSet<string>(adj[id].Where(members.Contains), StringComparer.Ordinal),
                StringComparer.Ordinal);

            var root = ChooseRoot(comp, localAdj);
            roots.Add(root);
            var children = BuildSpanningTree(root, localAdj, comp);
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

        // Pack clusters onto shelves (rows sized to their own tallest member) rather
        // than a uniform grid. Clusters vary hugely — one 20-node group beside several
        // single-node strays — and uniform cells would pad every small cluster out to
        // the largest, inflating the canvas with whitespace and driving zoom-to-fit
        // back down to single digits.
        //
        // Gaps must clear BOTH the group frame padding the Excalidraw converter adds
        // (34px per side) and the frame's name, which renders in a band above the box.
        // Too small a gap here is exactly what made adjacent group labels collide.
        const double FramePadding = 34;
        const double FrameLabelBand = 44;
        var gapX = options.HorizontalSpacing * 2 + FramePadding * 2 + 40;
        var gapY = options.VerticalSpacing * 2 + FramePadding * 2 + FrameLabelBand + 24;

        // Keep the drawing roughly square so it fits a landscape canvas well.
        var totalArea = boxes.Sum(b => (b.Width + gapX) * (b.Height + gapY));
        var shelfLimit = Math.Max(
            boxes.Max(b => b.Width) + gapX,
            Math.Sqrt(totalArea * 1.6));

        var global = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        double shelfX = 0, shelfY = 0, shelfHeight = 0;
        foreach (var box in boxes.OrderByDescending(b => b.Height))
        {
            if (shelfX > 0 && shelfX + box.Width + gapX > shelfLimit)
            {
                shelfY += shelfHeight + gapY;
                shelfX = 0;
                shelfHeight = 0;
            }

            var originX = shelfX + box.Width / 2;
            var originY = shelfY + box.Height / 2;
            foreach (var id in box.Nodes)
            {
                var (lx, ly) = local[id];
                global[id] = (lx - box.CenterX + originX, ly - box.CenterY + originY);
            }

            shelfX += box.Width + gapX;
            shelfHeight = Math.Max(shelfHeight, box.Height);
        }

        double gMinX = global.Values.Min(p => p.X), gMinY = global.Values.Min(p => p.Y);
        double gMaxX = global.Values.Max(p => p.X), gMaxY = global.Values.Max(p => p.Y);
        var offX = options.Padding - gMinX;
        var offY = options.Padding - gMinY;

        var positioned = 0;
        foreach (var node in graph.Nodes.Values)
        {
            // Emphasise the central idea so the hub reads as the main topic.
            if (roots.Contains(node.Id) && (options.ReflowPinned || !node.Locked))
            {
                node.Width = Math.Max(node.Width, 184);
                node.Height = Math.Max(node.Height, 78);
            }

            var (gx, gy) = global[node.Id];
            if (options.ReflowPinned || !node.Locked || node.X is null || node.Y is null)
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

    /// <summary>
    /// Builds layout clusters: every group becomes exactly one cluster containing all
    /// of its members (regardless of connectivity), and the ungrouped remainder is
    /// split into its natural connected components.
    /// </summary>
    private static List<List<string>> BuildClusters(
        List<string> ids, Dictionary<string, HashSet<string>> adj, SceneGraph graph)
    {
        var byGroup = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var ungrouped = new List<string>();

        foreach (var id in ids)
        {
            var groupId = graph.Nodes[id].GroupId;
            if (string.IsNullOrEmpty(groupId) || !graph.Groups.ContainsKey(groupId))
            {
                ungrouped.Add(id);
                continue;
            }
            if (!byGroup.TryGetValue(groupId, out var bucket))
            {
                bucket = new List<string>();
                byGroup[groupId] = bucket;
            }
            bucket.Add(id);
        }

        var clusters = new List<List<string>>(byGroup.Values);

        // Components of the ungrouped remainder only — edges into grouped nodes must
        // not drag a grouped node back out of its cluster.
        var ungroupedSet = new HashSet<string>(ungrouped, StringComparer.Ordinal);
        var ungroupedAdj = ungrouped.ToDictionary(
            id => id,
            id => new HashSet<string>(adj[id].Where(ungroupedSet.Contains), StringComparer.Ordinal),
            StringComparer.Ordinal);
        clusters.AddRange(FindComponents(ungrouped, ungroupedAdj));

        return clusters;
    }

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

    private static Dictionary<string, List<string>> BuildSpanningTree(
        string root, Dictionary<string, HashSet<string>> adj, IEnumerable<string>? allMembers = null)
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

        // A cluster grouped by label need not be internally connected. Any member the
        // traversal could not reach still has to be positioned, so hang it off the
        // root as its own branch rather than leaving it without coordinates.
        if (allMembers is not null)
        {
            foreach (var id in allMembers.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!visited.Add(id)) continue;
                children[root].Add(id);
                children[id] = new List<string>();
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
