using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Layout;

/// <summary>Selects a deterministic layout from the applied diagram intent.</summary>
public sealed class IntentLayoutEngine : ILayoutEngine
{
    private readonly MindMapLayoutEngine _discussion = new();
    private readonly IReadOnlyDictionary<DiagramIntent, ILayoutEngine> _architecture;

    public IntentLayoutEngine()
    {
        _architecture = Enum.GetValues<DiagramIntent>()
            .Where(intent => intent != DiagramIntent.DiscussionSummary)
            .ToDictionary(
                intent => intent,
                intent => (ILayoutEngine)new ArchitectureIntentLayoutEngine(intent));
    }

    public string Name => "IntentLayoutEngine";

    public ILayoutEngine ResolveEngine(DiagramIntent intent) =>
        intent == DiagramIntent.DiscussionSummary
            ? _discussion
            : _architecture[intent];

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options) =>
        ResolveEngine(graph.IntentState.AppliedIntent).Apply(graph, options);
}

/// <summary>
/// Group-preserving, left-to-right architecture layout. The same graph, intent, and
/// options always produce the same coordinates.
/// </summary>
public sealed class ArchitectureIntentLayoutEngine(DiagramIntent intent) : ILayoutEngine
{
    public DiagramIntent Intent { get; } = intent;
    public string Name => $"{Intent}LayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        var ranks = ComputeRanks(graph);
        var orderedGroups = OrderGroups(graph);
        var buckets = new List<(string? GroupId, IReadOnlyList<SceneNode> Nodes)>();

        var ungrouped = graph.Nodes.Values
            .Where(n => string.IsNullOrWhiteSpace(n.GroupId) || !graph.Groups.ContainsKey(n.GroupId))
            .ToArray();
        if (ungrouped.Length > 0) buckets.Add((null, ungrouped));

        foreach (var group in orderedGroups)
        {
            var members = graph.Nodes.Values
                .Where(n => string.Equals(n.GroupId, group.Id, StringComparison.Ordinal))
                .ToArray();
            if (members.Length > 0) buckets.Add((group.Id, members));
        }

        var targets = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var y = options.Padding;
        var maxRight = options.Padding;
        foreach (var bucket in buckets)
        {
            var byRank = bucket.Nodes
                .OrderBy(n => ranks[n.Id])
                .ThenBy(n => RoleRank(n))
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .GroupBy(n => ranks[n.Id])
                .OrderBy(g => g.Key)
                .ToArray();

            var x = options.Padding;
            var bucketHeight = 0d;
            foreach (var column in byRank)
            {
                var columnWidth = column.Max(n => Width(n));
                var columnY = y;
                foreach (var node in column)
                {
                    var h = Height(node);
                    targets[node.Id] = (x + columnWidth / 2, columnY + h / 2);
                    columnY += h + options.VerticalSpacing;
                }
                bucketHeight = Math.Max(bucketHeight, columnY - y - options.VerticalSpacing);
                x += columnWidth + options.HorizontalSpacing;
            }
            maxRight = Math.Max(maxRight, x - options.HorizontalSpacing);
            y += Math.Max(bucketHeight, 60) + options.VerticalSpacing * 1.5 + 54;
        }

        var occupied = graph.Nodes.Values
            .Where(n => IsPinned(n) && !options.ReflowPinned && n.X.HasValue && n.Y.HasValue)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => Rect(n.X!.Value, n.Y!.Value, Width(n), Height(n)))
            .ToList();
        var positioned = 0;

        foreach (var node in graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (IsPinned(node) && !options.ReflowPinned && node.X.HasValue && node.Y.HasValue) continue;
            if (!targets.TryGetValue(node.Id, out var target)) continue;

            var candidate = Rect(target.X, target.Y, Width(node), Height(node));
            while (occupied.Any(other => Overlaps(candidate, other, 16)))
            {
                candidate = candidate with
                {
                    CenterY = candidate.CenterY + Height(node) + options.VerticalSpacing / 2,
                };
            }

            node.X = candidate.CenterX;
            node.Y = candidate.CenterY;
            occupied.Add(candidate);
            positioned++;
        }

        var snapshot = LayoutSnapshot.Capture(graph);
        var left = snapshot.Nodes.Values.Min(n => n.Left);
        var top = snapshot.Nodes.Values.Min(n => n.Top);
        var right = Math.Max(maxRight, snapshot.Nodes.Values.Max(n => n.Right));
        var bottom = snapshot.Nodes.Values.Max(n => n.Bottom);
        return new LayoutResult(
            positioned,
            right - Math.Min(0, left) + options.Padding,
            bottom - Math.Min(0, top) + options.Padding);
    }

    private Dictionary<string, int> ComputeRanks(SceneGraph graph)
    {
        var rank = graph.Nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);

        if (Intent == DiagramIntent.IntegrationDataFlowArchitecture)
        {
            foreach (var edge in graph.Edges.Values
                         .Where(e => e.Step.HasValue)
                         .OrderBy(e => e.Step)
                         .ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                var step = Math.Max(1, edge.Step!.Value);
                if (rank.ContainsKey(edge.FromNodeId))
                    rank[edge.FromNodeId] = Math.Max(rank[edge.FromNodeId], step * 2 - 2);
                if (rank.ContainsKey(edge.ToNodeId))
                    rank[edge.ToNodeId] = Math.Max(rank[edge.ToNodeId], step * 2 - 1);
            }
        }

        var edges = graph.Edges.Values
            .Where(e => rank.ContainsKey(e.FromNodeId) && rank.ContainsKey(e.ToNodeId))
            .Where(e => !string.Equals(e.FromNodeId, e.ToNodeId, StringComparison.Ordinal))
            .OrderBy(e => e.Step ?? int.MaxValue)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        for (var pass = 0; pass < graph.Nodes.Count; pass++)
        {
            var changed = false;
            foreach (var edge in edges)
            {
                var next = Math.Min(graph.Nodes.Count, rank[edge.FromNodeId] + 1);
                if (next <= rank[edge.ToNodeId]) continue;
                rank[edge.ToNodeId] = next;
                changed = true;
            }
            if (!changed) break;
        }

        if (Intent is DiagramIntent.CloudNetworkArchitecture or
            DiagramIntent.SecurityZeroTrustArchitecture)
        {
            foreach (var node in graph.Nodes.Values)
                rank[node.Id] = Math.Max(rank[node.Id], RoleRank(node));
        }
        return rank;
    }

    private IReadOnlyList<SceneGroup> OrderGroups(SceneGraph graph)
    {
        var result = new List<SceneGroup>();
        var children = graph.Groups.Values
            .GroupBy(g => g.ParentGroupId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(GroupRank).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        void Walk(SceneGroup group)
        {
            result.Add(group);
            if (children.TryGetValue(group.Id, out var nested))
                foreach (var child in nested) Walk(child);
        }

        foreach (var root in graph.Groups.Values
                     .Where(g => string.IsNullOrWhiteSpace(g.ParentGroupId) ||
                                 !graph.Groups.ContainsKey(g.ParentGroupId))
                     .OrderBy(GroupRank)
                     .ThenBy(g => g.Id, StringComparer.Ordinal))
            Walk(root);

        foreach (var remaining in graph.Groups.Values
                     .Where(g => result.All(x => x.Id != g.Id))
                     .OrderBy(g => g.Id, StringComparer.Ordinal))
            result.Add(remaining);
        return result;
    }

    private int GroupRank(SceneGroup group)
    {
        var text = $"{group.Label} {group.Subtitle}".ToLowerInvariant();
        return Intent switch
        {
            DiagramIntent.SaaSMultiTenantArchitecture when text.Contains("control") => 0,
            DiagramIntent.SaaSMultiTenantArchitecture when text.Contains("shared") => 1,
            DiagramIntent.SaaSMultiTenantArchitecture when group.BoundaryKind == BoundaryKind.Tenant ||
                                                           text.Contains("tenant") => 2,
            DiagramIntent.SecurityZeroTrustArchitecture when group.BoundaryKind == BoundaryKind.External => 0,
            DiagramIntent.SecurityZeroTrustArchitecture when group.BoundaryKind == BoundaryKind.TrustZone => 1,
            DiagramIntent.CloudNetworkArchitecture when group.BoundaryKind == BoundaryKind.CloudScope => 0,
            DiagramIntent.CloudNetworkArchitecture when group.BoundaryKind == BoundaryKind.Network => 1,
            _ => 3,
        };
    }

    private int RoleRank(SceneNode node)
    {
        var text = node.Label.ToLowerInvariant();
        if (Intent == DiagramIntent.SecurityZeroTrustArchitecture)
        {
            if (node.Kind is NodeKind.Actor or NodeKind.Identity || text.Contains("identity")) return 0;
            if (node.Kind == NodeKind.Security || text.Contains("policy") || text.Contains("auth")) return 1;
            return 2;
        }
        if (Intent == DiagramIntent.CloudNetworkArchitecture)
        {
            if (node.Kind is NodeKind.Actor or NodeKind.External ||
                text.Contains("ingress") || text.Contains("gateway") || text.Contains("front door")) return 0;
            if (node.Kind == NodeKind.DataStore || text.Contains("database") ||
                text.Contains("storage") || text.Contains("queue")) return 2;
            return 1;
        }
        return 0;
    }

    private static double Width(SceneNode node) =>
        double.IsFinite(node.Width) && node.Width > 0 ? node.Width : 140;

    private static double Height(SceneNode node) =>
        double.IsFinite(node.Height) && node.Height > 0 ? node.Height : 60;

    private static bool IsPinned(SceneNode node) => node.Locked;

    private static LayoutRect Rect(double x, double y, double width, double height) =>
        new(x, y, width, height);

    private static bool Overlaps(LayoutRect a, LayoutRect b, double gap) =>
        Math.Abs(a.CenterX - b.CenterX) < (a.Width + b.Width) / 2 + gap &&
        Math.Abs(a.CenterY - b.CenterY) < (a.Height + b.Height) / 2 + gap;

    private sealed record LayoutRect(double CenterX, double CenterY, double Width, double Height);
}
