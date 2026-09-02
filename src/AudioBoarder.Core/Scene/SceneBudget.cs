namespace AudioBoarder.Core.Scene;

/// <summary>
/// Caps how large a live scene may grow. Continuous generation only ever *adds*
/// (destructive ops are rejected on automatic passes so a mis-fire can never wipe
/// the board), which means a long meeting would otherwise grow the diagram without
/// bound until it is an unreadable hairball at 10% zoom.
/// </summary>
public sealed record SceneBudget(int MaxNodes = 80, int MaxNotes = 24)
{
    public static SceneBudget Default { get; } = new();
}

public sealed record SceneBudgetResult(
    int NodesEvicted,
    int NotesEvicted,
    int GroupsRemoved,
    int RemainingNodeOverage = 0,
    int RemainingNoteOverage = 0)
{
    public static SceneBudgetResult Empty { get; } = new(0, 0, 0, 0, 0);
    public bool ChangedAnything => NodesEvicted > 0 || NotesEvicted > 0 || GroupsRemoved > 0;
    public bool IsWithinBudget => RemainingNodeOverage == 0 && RemainingNoteOverage == 0;
}

/// <summary>
/// Trims a scene back to its budget by evicting the least valuable content.
/// </summary>
public static class SceneBudgetEnforcer
{
    /// <summary>
    /// Evicts the stalest, least-connected, unlocked nodes (and oldest general notes)
    /// until the scene fits its budget. Caller must hold <see cref="SceneGraph.SyncRoot"/>.
    /// </summary>
    public static SceneBudgetResult Enforce(
        SceneGraph graph,
        SceneBudget budget,
        bool provisionalOnly = false)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(budget);

        var nodesEvicted = EvictNodes(graph, budget.MaxNodes, provisionalOnly);
        // Automatic passes protect confirmed/user-edited nodes from eviction, but
        // notes have their own explicit cap and must not grow without bound.
        var notesEvicted = EvictNotes(graph, budget.MaxNotes);
        // Patch application is dependency-ordered. Once a complete patch has applied,
        // a boundary with no direct or descendant content is genuinely orphaned and
        // should not accumulate across continuous passes.
        var groupsRemoved = RemoveEmptyGroups(graph);
        var remainingNodeOverage = budget.MaxNodes < 0
            ? 0
            : Math.Max(0, graph.Nodes.Count - budget.MaxNodes);
        var remainingNoteOverage = budget.MaxNotes < 0
            ? 0
            : Math.Max(0, graph.Notes.Count - budget.MaxNotes);

        return nodesEvicted == 0 && notesEvicted == 0 && groupsRemoved == 0 &&
               remainingNodeOverage == 0 && remainingNoteOverage == 0
            ? SceneBudgetResult.Empty
            : new SceneBudgetResult(
                nodesEvicted, notesEvicted, groupsRemoved,
                remainingNodeOverage, remainingNoteOverage);
    }

    private static int EvictNodes(SceneGraph graph, int maxNodes, bool provisionalOnly)
    {
        // Negative disables the cap; zero legitimately means "keep nothing".
        if (maxNodes < 0 || graph.Nodes.Count <= maxNodes) return 0;

        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in graph.Nodes.Keys) degree[id] = 0;
        foreach (var edge in graph.Edges.Values)
        {
            if (degree.ContainsKey(edge.FromNodeId)) degree[edge.FromNodeId]++;
            if (degree.ContainsKey(edge.ToNodeId)) degree[edge.ToNodeId]++;
        }

        // Keep what a reader would keep: anything the user pinned, then well-connected
        // hubs, then grouped nodes, then whatever was discussed most recently.
        var evictionOrder = graph.Nodes.Values
            .Where(n => !n.Locked && n.LifecycleState != ElementLifecycleState.UserEdited)
            .Where(n => !provisionalOnly ||
                        n.LifecycleState == ElementLifecycleState.Provisional)
            .Where(n => !provisionalOnly || !graph.Edges.Values.Any(edge =>
                (edge.FromNodeId == n.Id || edge.ToNodeId == n.Id) &&
                edge.LifecycleState != ElementLifecycleState.Provisional))
            .OrderBy(n => degree.TryGetValue(n.Id, out var d) ? d : 0)
            .ThenBy(n => n.GroupId is null ? 0 : 1)
            .ThenBy(n => n.Sequence)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var evicted = 0;
        foreach (var node in evictionOrder)
        {
            if (graph.Nodes.Count <= maxNodes) break;
            graph.RemoveNode(node.Id);
            evicted++;
        }
        return evicted;
    }

    private static int EvictNotes(SceneGraph graph, int maxNotes)
    {
        if (maxNotes < 0 || graph.Notes.Count <= maxNotes) return 0;

        // Commitments outlive commentary: drop general chatter before anything the
        // meeting actually decided, owes, questioned, or flagged.
        var evictionOrder = graph.Notes.Values
            .OrderBy(n => n.Kind == NoteKind.General ? 0 : 1)
            .ThenBy(n => n.SourceTimestamp ?? DateTimeOffset.MinValue)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var evicted = 0;
        foreach (var note in evictionOrder)
        {
            if (graph.Notes.Count <= maxNotes) break;
            graph.RemoveNote(note.Id);
            evicted++;
        }
        return evicted;
    }

    private static int RemoveEmptyGroups(SceneGraph graph)
    {
        // A group whose members were all evicted would still paint an empty labelled
        // frame, which is exactly the stray-rectangle noise we are trying to remove.
        var groupsWithNodes = new HashSet<string>(
            graph.Nodes.Values.Where(n => n.GroupId is not null).Select(n => n.GroupId!),
            StringComparer.Ordinal);
        var children = graph.Groups.Values
            .Where(group => group.ParentGroupId is not null)
            .GroupBy(group => group.ParentGroupId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(child => child.Id).ToArray(),
                StringComparer.Ordinal);
        var contentMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        bool IsRemovable(SceneGroup group) =>
            group.LifecycleState != ElementLifecycleState.UserEdited;

        bool HasContent(string groupId)
        {
            if (contentMemo.TryGetValue(groupId, out var cached)) return cached;
            if (!visiting.Add(groupId)) return true;

            var hasContent = groupsWithNodes.Contains(groupId);
            if (!hasContent && children.TryGetValue(groupId, out var childIds))
            {
                hasContent = childIds.Any(childId =>
                    graph.Groups.TryGetValue(childId, out var child) &&
                    (!IsRemovable(child) || HasContent(childId)));
            }

            visiting.Remove(groupId);
            contentMemo[groupId] = hasContent;
            return hasContent;
        }

        var orphans = graph.Groups.Values
            .Where(group => IsRemovable(group) && !HasContent(group.Id))
            .Select(group => group.Id)
            .ToList();
        foreach (var id in orphans) graph.RemoveGroup(id);
        return orphans.Count;
    }
}
