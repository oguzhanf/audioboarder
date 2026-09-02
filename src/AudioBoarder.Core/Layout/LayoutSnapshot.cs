using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Layout;

/// <summary>
/// Immutable, renderer-neutral geometry. Scene node coordinates are centres; consumers
/// convert to top-left coordinates only at their drawing boundary.
/// </summary>
public sealed record LayoutSnapshot(
    IReadOnlyDictionary<string, NodeGeometry> Nodes,
    IReadOnlyDictionary<string, GroupBounds> Groups)
{
    public static LayoutSnapshot Capture(SceneGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var nodes = new Dictionary<string, NodeGeometry>(StringComparer.Ordinal);
        var fallbackIndex = 0;
        foreach (var node in graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var width = ValidSize(node.Width, 140);
            var height = ValidSize(node.Height, 60);
            var centerX = ValidCoordinate(node.X)
                ? node.X!.Value
                : 140 + fallbackIndex % 4 * 240;
            var centerY = ValidCoordinate(node.Y)
                ? node.Y!.Value
                : 140 + fallbackIndex / 4 * 160;
            if (!ValidCoordinate(node.X) || !ValidCoordinate(node.Y)) fallbackIndex++;
            nodes[node.Id] = new NodeGeometry(node.Id, centerX, centerY, width, height);
        }

        var groups = ResolveGroups(graph, nodes);
        return new LayoutSnapshot(nodes, groups);
    }

    private static IReadOnlyDictionary<string, GroupBounds> ResolveGroups(
        SceneGraph graph,
        IReadOnlyDictionary<string, NodeGeometry> nodes)
    {
        const double sidePadding = 28;
        const double bottomPadding = 28;
        const double headerHeight = 38;

        var result = new Dictionary<string, GroupBounds>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var orderedGroups = graph.Groups.Values.OrderBy(g => g.Id, StringComparer.Ordinal).ToArray();

        GroupBounds Resolve(SceneGroup group)
        {
            if (result.TryGetValue(group.Id, out var existing)) return existing;
            if (!visiting.Add(group.Id))
            {
                var cycleFallback = new GroupBounds(group.Id, 160, 100, 240, 120, 0);
                result[group.Id] = cycleFallback;
                return cycleFallback;
            }

            var boxes = new List<(double Left, double Top, double Right, double Bottom)>();
            boxes.AddRange(graph.Nodes.Values
                .Where(n => string.Equals(n.GroupId, group.Id, StringComparison.Ordinal))
                .Where(n => nodes.ContainsKey(n.Id))
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .Select(n =>
                {
                    var p = nodes[n.Id];
                    return (p.Left, p.Top, p.Right, p.Bottom);
                }));

            foreach (var child in orderedGroups.Where(
                         g => string.Equals(g.ParentGroupId, group.Id, StringComparison.Ordinal)))
            {
                var childBounds = Resolve(child);
                boxes.Add((childBounds.Left, childBounds.Top, childBounds.Right, childBounds.Bottom));
            }

            GroupBounds resolved;
            if (boxes.Count == 0)
            {
                var ordinal = Array.FindIndex(orderedGroups, g => g.Id == group.Id);
                resolved = new GroupBounds(
                    group.Id,
                    160 + ordinal % 4 * 280,
                    100 + ordinal / 4 * 180,
                    240,
                    120,
                    GroupDepth(group, graph));
            }
            else
            {
                var left = boxes.Min(b => b.Left) - sidePadding;
                var top = boxes.Min(b => b.Top) - headerHeight;
                var right = boxes.Max(b => b.Right) + sidePadding;
                var bottom = boxes.Max(b => b.Bottom) + bottomPadding;
                resolved = new GroupBounds(
                    group.Id,
                    (left + right) / 2,
                    (top + bottom) / 2,
                    right - left,
                    bottom - top,
                    GroupDepth(group, graph));
            }

            visiting.Remove(group.Id);
            result[group.Id] = resolved;
            return resolved;
        }

        foreach (var group in orderedGroups) Resolve(group);
        return result;
    }

    private static int GroupDepth(SceneGroup group, SceneGraph graph)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal) { group.Id };
        var parentId = group.ParentGroupId;
        while (!string.IsNullOrWhiteSpace(parentId) &&
               graph.Groups.TryGetValue(parentId, out var parent) &&
               seen.Add(parent.Id))
        {
            depth++;
            parentId = parent.ParentGroupId;
        }
        return depth;
    }

    private static bool ValidCoordinate(double? value) =>
        value.HasValue && double.IsFinite(value.Value);

    private static double ValidSize(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}

public sealed record NodeGeometry(
    string Id,
    double CenterX,
    double CenterY,
    double Width,
    double Height)
{
    public double Left => CenterX - Width / 2;
    public double Top => CenterY - Height / 2;
    public double Right => CenterX + Width / 2;
    public double Bottom => CenterY + Height / 2;
}

public sealed record GroupBounds(
    string Id,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    int Depth)
{
    public double Left => CenterX - Width / 2;
    public double Top => CenterY - Height / 2;
    public double Right => CenterX + Width / 2;
    public double Bottom => CenterY + Height / 2;
}
