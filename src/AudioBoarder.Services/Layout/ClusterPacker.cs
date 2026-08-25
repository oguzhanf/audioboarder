using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Layout;

/// <summary>
/// Shared clustering and packing used by every layout engine.
/// <para>
/// A group frame is drawn from the bounding box of all its members, so a group must
/// be laid out as exactly one contiguous cluster. Clusters are then packed onto
/// shelves — rows sized to their own tallest member — rather than a uniform grid,
/// because clusters vary hugely in size and uniform cells pad the canvas out with
/// whitespace until zoom-to-fit collapses to single digits.
/// </para>
/// </summary>
internal static class ClusterPacker
{
    /// <summary>Frame padding added per side by the Excalidraw converter.</summary>
    private const double FramePadding = 34;

    /// <summary>Vertical band Excalidraw reserves above a frame for its name.</summary>
    private const double FrameLabelBand = 44;

    internal sealed record Cluster(List<string> NodeIds);

    /// <summary>
    /// Every group becomes one cluster containing all its members; the ungrouped
    /// remainder is split into its natural connected components.
    /// </summary>
    public static List<List<string>> BuildClusters(
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

        var ungroupedSet = new HashSet<string>(ungrouped, StringComparer.Ordinal);
        var ungroupedAdj = ungrouped.ToDictionary(
            id => id,
            id => new HashSet<string>(adj[id].Where(ungroupedSet.Contains), StringComparer.Ordinal),
            StringComparer.Ordinal);
        clusters.AddRange(FindComponents(ungrouped, ungroupedAdj));

        return clusters;
    }

    public static List<List<string>> FindComponents(
        List<string> ids, Dictionary<string, HashSet<string>> adj)
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

    /// <summary>Builds an undirected adjacency map over the graph's nodes.</summary>
    public static Dictionary<string, HashSet<string>> BuildAdjacency(SceneGraph graph, List<string> ids)
    {
        var adj = ids.ToDictionary(
            id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var e in graph.Edges.Values)
        {
            if (e.FromNodeId == e.ToNodeId) continue;
            if (adj.TryGetValue(e.FromNodeId, out var from) && adj.TryGetValue(e.ToNodeId, out var to))
            {
                from.Add(e.ToNodeId);
                to.Add(e.FromNodeId);
            }
        }
        return adj;
    }

    public readonly record struct ClusterBox(
        List<string> Nodes, double Width, double Height, double CenterX, double CenterY);

    /// <summary>
    /// Packs cluster-local coordinates into a single canvas, returning global
    /// positions. Gaps clear the frame padding and label band so adjacent group
    /// boundaries and their names can never touch.
    /// </summary>
    public static Dictionary<string, (double X, double Y)> Pack(
        List<ClusterBox> boxes,
        Dictionary<string, (double X, double Y)> local,
        double horizontalSpacing,
        double verticalSpacing)
    {
        var gapX = horizontalSpacing * 2 + FramePadding * 2 + 40;
        var gapY = verticalSpacing * 2 + FramePadding * 2 + FrameLabelBand + 24;

        // Keep the drawing roughly landscape so it fits the canvas well.
        var totalArea = boxes.Sum(b => (b.Width + gapX) * (b.Height + gapY));
        var shelfLimit = Math.Max(
            boxes.Max(b => b.Width) + gapX,
            Math.Sqrt(totalArea * 1.7));

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
        return global;
    }

    /// <summary>Measures a cluster's bounding box from its local coordinates.</summary>
    public static ClusterBox Measure(
        List<string> cluster, SceneGraph graph, Dictionary<string, (double X, double Y)> local)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var id in cluster)
        {
            var node = graph.Nodes[id];
            var w = node.Width <= 0 ? 140 : node.Width;
            var h = node.Height <= 0 ? 60 : node.Height;
            var (lx, ly) = local[id];
            minX = Math.Min(minX, lx - w / 2);
            maxX = Math.Max(maxX, lx + w / 2);
            minY = Math.Min(minY, ly - h / 2);
            maxY = Math.Max(maxY, ly + h / 2);
        }
        return new ClusterBox(cluster, maxX - minX, maxY - minY, (minX + maxX) / 2, (minY + maxY) / 2);
    }

    /// <summary>Writes global positions onto the graph, leaving pinned nodes alone.</summary>
    public static int Commit(
        SceneGraph graph, Dictionary<string, (double X, double Y)> global, double padding)
    {
        if (global.Count == 0) return 0;
        var offX = padding - global.Values.Min(p => p.X);
        var offY = padding - global.Values.Min(p => p.Y);

        var positioned = 0;
        foreach (var node in graph.Nodes.Values)
        {
            if (!global.TryGetValue(node.Id, out var p)) continue;
            if (node.Locked && node.X.HasValue && node.Y.HasValue) continue;
            node.X = p.X + offX;
            node.Y = p.Y + offY;
            positioned++;
        }
        return positioned;
    }
}
