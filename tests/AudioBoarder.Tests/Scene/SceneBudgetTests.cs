using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Scene;

/// <summary>
/// Regression cover for the "rev 416 hairball" failure: a long meeting grew the
/// board without bound and scattered group members so their frames overlapped.
/// </summary>
public class SceneBudgetTests
{
    private readonly ScenePatchApplier _applier = new();

    private SceneGraph BuildGraph(int nodeCount, string? groupId = null)
    {
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < nodeCount; i++)
            ops.Add(new AddNode($"n{i:D3}", NodeKind.Process, $"Node {i}", groupId));
        if (groupId is not null)
            ops.Add(new GroupOp(groupId, "Group", Enumerable.Range(0, nodeCount).Select(i => $"n{i:D3}").ToArray()));
        _applier.Apply(graph, new ScenePatch(ops));
        return graph;
    }

    [Fact]
    public void Enforce_TrimsNodesDownToBudget()
    {
        var graph = BuildGraph(80);

        var result = SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 20));

        graph.Nodes.Count.Should().Be(20);
        result.NodesEvicted.Should().Be(60);
    }

    [Fact]
    public void Enforce_NeverEvictsNodesTheUserPinned()
    {
        var graph = BuildGraph(60);
        // Pin more nodes than the budget allows, oldest-first — these are the exact
        // nodes eviction would otherwise take.
        foreach (var node in graph.Nodes.Values.OrderBy(n => n.Sequence).Take(25))
            node.Locked = true;

        SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 20));

        graph.Nodes.Values.Count(n => n.Locked).Should().Be(25);
        graph.Nodes.Values.Should().OnlyContain(n => n.Locked);
    }

    [Fact]
    public void Enforce_KeepsWellConnectedHubsOverStrays()
    {
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation> { new AddNode("hub", NodeKind.Process, "Hub") };
        for (var i = 0; i < 30; i++) ops.Add(new AddNode($"leaf{i:D2}", NodeKind.Entity, $"Leaf {i}"));
        for (var i = 0; i < 5; i++) ops.Add(new Connect($"e{i}", "hub", $"leaf{i:D2}"));
        _applier.Apply(graph, new ScenePatch(ops));

        SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 6));

        graph.Nodes.Should().ContainKey("hub");
    }

    [Fact]
    public void Enforce_DropsGeneralChatterBeforeCommitments()
    {
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < 20; i++)
            ops.Add(new NoteUpsert($"g{i:D2}", NoteKind.General, $"chatter {i}"));
        ops.Add(new NoteUpsert("a1", NoteKind.ActionItem, "Ship the installer"));
        ops.Add(new NoteUpsert("r1", NoteKind.Risk, "Unsigned binary"));
        _applier.Apply(graph, new ScenePatch(ops));

        SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNotes: 5));

        graph.Notes.Count.Should().Be(5);
        graph.Notes.Should().ContainKey("a1");
        graph.Notes.Should().ContainKey("r1");
    }

    [Fact]
    public void Enforce_RemovesGroupsLeftEmptyByEviction()
    {
        var graph = BuildGraph(40, groupId: "grp");

        var result = SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 0));

        graph.Groups.Should().BeEmpty();
        result.GroupsRemoved.Should().Be(1);
    }

    [Fact]
    public void Enforce_KeepsAFreshlyCreatedGroupWhenNothingWasEvicted()
    {
        // A group whose member ids did not resolve yet must survive: the applier never
        // re-populates an existing group, so reaping it here would churn permanently.
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("n0", NodeKind.Process, "Node"),
            new GroupOp("fresh", "Fresh group", new[] { "not-yet-created" }),
        }));

        var result = SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 42));

        graph.Groups.Should().ContainKey("fresh");
        result.GroupsRemoved.Should().Be(0);
    }

    [Fact]
    public void Enforce_EvictsLeastRecentlyDiscussedNodeFirst()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("stale", NodeKind.Process, "Mentioned once"),
            new AddNode("active", NodeKind.Process, "Discussed throughout"),
            new AddNode("filler", NodeKind.Process, "Filler"),
        }));

        // "active" comes up again later in the meeting.
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new UpdateNode("active", Label: "Discussed throughout, again"),
        }));

        SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 1));

        graph.Nodes.Should().ContainKey("active");
        graph.Nodes.Should().NotContainKey("stale");
    }

    [Fact]
    public void Apply_StampsNoteTimestampsSoTheRailCanOrderThem()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("a1", NoteKind.ActionItem, "Ship the installer"),
        }));

        graph.Notes["a1"].SourceTimestamp.Should().NotBeNull();
    }

    [Fact]
    public void Apply_RefusesToDeleteALockedNodeEvenViaLabelAlias()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("pinned", NodeKind.Process, "Pinned idea"),
        }));
        graph.Nodes["pinned"].Locked = true;

        // Alias a fresh id onto the locked node by matching its label, then delete it.
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("alias", NodeKind.Process, "Pinned idea"),
            new DeleteNode("alias"),
        }));

        graph.Nodes.Should().ContainKey("pinned");
    }

    [Fact]
    public void Layout_KeepsGroupFramesFromOverlapping()
    {
        // Two groups, cross-linked so a purely connectivity-driven layout would
        // interleave their members and make the frames intersect.
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < 6; i++) ops.Add(new AddNode($"a{i}", NodeKind.Process, $"A{i}", "ga"));
        for (var i = 0; i < 6; i++) ops.Add(new AddNode($"b{i}", NodeKind.Process, $"B{i}", "gb"));
        ops.Add(new GroupOp("ga", "Group A", Enumerable.Range(0, 6).Select(i => $"a{i}").ToArray()));
        ops.Add(new GroupOp("gb", "Group B", Enumerable.Range(0, 6).Select(i => $"b{i}").ToArray()));
        for (var i = 1; i < 6; i++) ops.Add(new Connect($"ea{i}", "a0", $"a{i}"));
        for (var i = 1; i < 6; i++) ops.Add(new Connect($"eb{i}", "b0", $"b{i}"));
        ops.Add(new Connect("cross1", "a0", "b0"));
        ops.Add(new Connect("cross2", "a3", "b4"));
        _applier.Apply(graph, new ScenePatch(ops));

        new MindMapLayoutEngine().Apply(graph, new LayoutOptions());

        var a = FrameBounds(graph, "ga");
        var b = FrameBounds(graph, "gb");
        Overlaps(a, b).Should().BeFalse(
            "group frames are drawn from member bounds, so overlapping members produce overlapping boundaries");
    }

    [Fact]
    public void Layout_PositionsEveryMemberOfAnUnconnectedGroup()
    {
        // A group whose members share no edges must still be fully positioned.
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < 5; i++) ops.Add(new AddNode($"s{i}", NodeKind.Entity, $"S{i}", "solo"));
        ops.Add(new GroupOp("solo", "Solo", Enumerable.Range(0, 5).Select(i => $"s{i}").ToArray()));
        _applier.Apply(graph, new ScenePatch(ops));

        new MindMapLayoutEngine().Apply(graph, new LayoutOptions());

        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);
    }

    [Fact]
    public void Layout_KeepsFramesDisjointWhenGroupMembersShareNoEdges()
    {
        // The degenerate case eviction actually manufactures: removing a hub strips
        // its edges, leaving former neighbours as isolated singletons that still share
        // a GroupId. Clustering per connected component would scatter them across the
        // canvas while the single frame stretched over all of them.
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < 4; i++) ops.Add(new AddNode($"a{i}", NodeKind.Process, $"A{i}", "ga"));
        for (var i = 0; i < 4; i++) ops.Add(new AddNode($"b{i}", NodeKind.Process, $"B{i}", "gb"));
        ops.Add(new GroupOp("ga", "Group A", Enumerable.Range(0, 4).Select(i => $"a{i}").ToArray()));
        ops.Add(new GroupOp("gb", "Group B", Enumerable.Range(0, 4).Select(i => $"b{i}").ToArray()));
        _applier.Apply(graph, new ScenePatch(ops));

        new MindMapLayoutEngine().Apply(graph, new LayoutOptions());

        Overlaps(FrameBounds(graph, "ga"), FrameBounds(graph, "gb")).Should().BeFalse();
    }

    [Fact]
    public void Layout_KeepsFramesDisjointAfterEvictionSplitsAGroup()
    {
        var graph = new SceneGraph();
        var ops = new List<ScenePatchOperation>();
        for (var i = 0; i < 6; i++) ops.Add(new AddNode($"a{i}", NodeKind.Process, $"A{i}", "ga"));
        for (var i = 0; i < 6; i++) ops.Add(new AddNode($"b{i}", NodeKind.Process, $"B{i}", "gb"));
        ops.Add(new GroupOp("ga", "Group A", Enumerable.Range(0, 6).Select(i => $"a{i}").ToArray()));
        ops.Add(new GroupOp("gb", "Group B", Enumerable.Range(0, 6).Select(i => $"b{i}").ToArray()));
        for (var i = 1; i < 6; i++) ops.Add(new Connect($"ea{i}", "a0", $"a{i}"));
        for (var i = 1; i < 6; i++) ops.Add(new Connect($"eb{i}", "b0", $"b{i}"));
        _applier.Apply(graph, new ScenePatch(ops));

        // Trim hard enough that hubs go and the survivors are disconnected singletons.
        SceneBudgetEnforcer.Enforce(graph, new SceneBudget(MaxNodes: 6));
        new MindMapLayoutEngine().Apply(graph, new LayoutOptions());

        if (graph.Groups.ContainsKey("ga") && graph.Groups.ContainsKey("gb"))
            Overlaps(FrameBounds(graph, "ga"), FrameBounds(graph, "gb")).Should().BeFalse();
        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);
    }

    [Fact]
    public void Layout_HandlesDegenerateScenes()
    {
        var single = new SceneGraph();
        _applier.Apply(single, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("only", NodeKind.Process, "Only node"),
        }));
        new MindMapLayoutEngine().Apply(single, new LayoutOptions());
        single.Nodes["only"].X.Should().NotBeNull();

        var allLocked = new SceneGraph();
        _applier.Apply(allLocked, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
        }));
        foreach (var n in allLocked.Nodes.Values) { n.X = 10; n.Y = 20; n.Locked = true; }
        var result = new MindMapLayoutEngine().Apply(allLocked, new LayoutOptions());
        result.NodesPositioned.Should().Be(0);
        allLocked.Nodes.Values.Should().OnlyContain(n => n.X == 10 && n.Y == 20);

        var empty = new SceneGraph();
        new MindMapLayoutEngine().Apply(empty, new LayoutOptions()).NodesPositioned.Should().Be(0);
    }

    // Mirrors SceneToExcalidrawConverter.BuildGroupFrame: bounds of members + padding.
    private static (double L, double T, double R, double B) FrameBounds(SceneGraph graph, string groupId)
    {
        const double pad = 34;
        var members = graph.Nodes.Values.Where(n => n.GroupId == groupId).ToList();
        return (members.Min(m => m.X!.Value - m.Width / 2) - pad,
                members.Min(m => m.Y!.Value - m.Height / 2) - pad,
                members.Max(m => m.X!.Value + m.Width / 2) + pad,
                members.Max(m => m.Y!.Value + m.Height / 2) + pad);
    }

    private static bool Overlaps(
        (double L, double T, double R, double B) a, (double L, double T, double R, double B) b)
        => a.L < b.R && b.L < a.R && a.T < b.B && b.T < a.B;
}
