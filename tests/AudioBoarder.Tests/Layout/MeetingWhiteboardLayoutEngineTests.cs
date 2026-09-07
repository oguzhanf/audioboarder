using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Layout;

public class MeetingWhiteboardLayoutEngineTests
{
    private readonly MeetingWhiteboardLayoutEngine _engine = new();

    [Theory]
    [InlineData(1200, 800)]
    [InlineData(800, 1200)]
    public void IsolatedConceptCardsUseCompactTwoDimensionalShelves(double width, double height)
    {
        var graph = Graph(Enumerable.Range(0, 36).Select(i => Node($"idea-{i:D2}")));
        var before = graph.Clone();

        var result = _engine.Apply(graph, new LayoutOptions(CanvasWidth: width, CanvasHeight: height));

        result.NodesPositioned.Should().Be(36);
        graph.Nodes.Values.Select(n => n.X).Distinct().Count().Should().BeGreaterThan(2);
        graph.Nodes.Values.Select(n => n.Y).Distinct().Count().Should().BeGreaterThan(2);
        (result.BoundsWidth / result.BoundsHeight).Should().BeInRange(0.5, 2.5);
        result.BoundsWidth.Should().BeLessThan(1800);
        result.BoundsHeight.Should().BeLessThan(1800);
        AssertNoOverlap(graph);
        AssertOnlyCoordinatesChanged(before, graph);
    }

    [Fact]
    public void DirectedProcessAndArchitectureIslandsCoexistWithMapsAndUnrelatedIdeas()
    {
        var graph = MixedMeeting();
        var before = graph.Clone();

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["start"].X.Should().BeLessThan(graph.Nodes["review"].X!.Value);
        graph.Nodes["review"].X.Should().BeLessThan(graph.Nodes["finish"].X!.Value);
        graph.Nodes["start"].Y.Should().Be(graph.Nodes["review"].Y);
        graph.Nodes["review"].Y.Should().Be(graph.Nodes["finish"].Y);
        graph.Nodes["client"].X.Should().BeLessThan(graph.Nodes["service"].X!.Value);
        graph.Nodes["service"].X.Should().BeLessThan(graph.Nodes["store"].X!.Value);
        var concept = graph.Nodes["concept"];
        new[] { "north", "south", "east", "west" }.Select(id => graph.Nodes[id].X!.Value)
            .Should().Contain(x => x < concept.X).And.Contain(x => x > concept.X);
        new[] { "north", "south", "east", "west" }.Select(id => graph.Nodes[id].Y!.Value)
            .Should().Contain(y => y < concept.Y).And.Contain(y => y > concept.Y);
        var chain = Bounds(graph, "start", "review", "finish");
        foreach (var idea in graph.Nodes.Values.Where(n => n.Id.StartsWith("idea-", StringComparison.Ordinal)))
            Intersects(chain, Bounds(graph, idea.Id)).Should().BeFalse();
        AssertNoOverlap(graph);
        AssertOnlyCoordinatesChanged(before, graph);
    }

    [Fact]
    public void AssociationEndpointsDoNotImplyDirectionOrInventAHigherLevelTopic()
    {
        var graph = Graph(new[] { Node("center"), Node("a"), Node("b"), Node("c"), Node("d") },
            new[] { Link("a", "center", "a"), Link("b", "center", "b"),
                Link("c", "center", "c"), Link("d", "center", "d") });
        var reversed = Graph(graph.Nodes.Values, graph.Edges.Values.Select(edge => new SceneEdge
        {
            Id = edge.Id, FromNodeId = edge.ToNodeId, ToNodeId = edge.FromNodeId, Kind = EdgeKind.Association,
        }));
        var before = graph.Clone();
        var originalNodes = graph.Nodes.Values.ToArray();
        var originalEdges = graph.Edges.Values.ToArray();

        _engine.Apply(graph, new LayoutOptions());
        _engine.Apply(reversed, new LayoutOptions());

        Coordinates(graph).Should().Equal(Coordinates(reversed));
        graph.Nodes.Should().HaveCount(5);
        graph.Edges.Should().HaveCount(4);
        foreach (var node in originalNodes) graph.Nodes[node.Id].Should().BeSameAs(node);
        foreach (var edge in originalEdges) graph.Edges[edge.Id].Should().BeSameAs(edge);
        AssertOnlyCoordinatesChanged(before, graph);
        AssertNoOverlap(graph);
    }

    [Fact]
    public void AssociationDoesNotReverseTheRanksOfADirectedChain()
    {
        var graph = Graph(new[] { Node("a"), Node("b"), Node("c") },
            new[] { Link("ab", "a", "b", EdgeKind.Flow), Link("bc", "b", "c", EdgeKind.Flow),
                Link("related", "c", "a") });

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["a"].X.Should().BeLessThan(graph.Nodes["b"].X!.Value);
        graph.Nodes["b"].X.Should().BeLessThan(graph.Nodes["c"].X!.Value);
        graph.Edges["related"].Kind.Should().Be(EdgeKind.Association);
        AssertNoOverlap(graph);
    }

    [Fact]
    public void NestedGroupsKeepDisconnectedMembersTogetherWithoutSwallowingOtherTopics()
    {
        var graph = GroupedMeeting();

        _engine.Apply(graph, new LayoutOptions());

        var snapshot = LayoutSnapshot.Capture(graph);
        Contains(Rect(snapshot.Groups["workshop"]), Rect(snapshot.Groups["plan"])).Should().BeTrue();
        Contains(Rect(snapshot.Groups["plan"]), Rect(snapshot.Groups["detail"])).Should().BeTrue();
        Contains(Rect(snapshot.Groups["workshop"]), Rect(snapshot.Groups["execution"])).Should().BeTrue();
        snapshot.Groups["plan"].Right.Should().BeLessThan(snapshot.Groups["execution"].Left);
        foreach (var group in snapshot.Groups.Values)
        foreach (var node in graph.Nodes.Values)
        {
            if (BelongsTo(graph, node, group.Id))
                Contains(Rect(group), Rect(snapshot.Nodes[node.Id])).Should().BeTrue($"{node.Id} belongs to {group.Id}");
            else
                Intersects(Rect(group), Rect(snapshot.Nodes[node.Id])).Should().BeFalse($"{node.Id} is outside {group.Id}");
        }
        AssertNoOverlap(graph);
    }

    [Fact]
    public void VariableSizeCardsAvoidAllPinnedObstaclesWithoutMovingOrResizingThem()
    {
        var cards = Enumerable.Range(0, 24)
            .Select(i => Node($"idea-{i:D2}", width: 140 + i % 4 * 90, height: 60 + i % 5 * 70));
        var graph = Graph(cards.Concat(new[]
        {
            Pin("pin-a", 240, 190, 400, 200),
            Pin("pin-b", 870, 590, 350, 220),
            Pin("pin-negative", -240, -150, 170, 80),
        }));
        var before = graph.Clone();

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(24);
        foreach (var pin in before.Nodes.Values.Where(n => n.Locked))
            graph.Nodes[pin.Id].Should().BeEquivalentTo(pin);
        AssertNoOverlap(graph);
        AssertOnlyCoordinatesChanged(before, graph);
    }

    [Theory]
    [InlineData(EdgeKind.Flow)]
    [InlineData(EdgeKind.Association)]
    public void ConnectedIslandsAvoidAPinAtTheirPreferredPosition(EdgeKind kind)
    {
        var graph = Graph(new[]
        {
            Pin("anchor", 260, 220),
            Node("wide", width: 520, height: 160),
            Node("tall", width: 190, height: 320),
            Node("small"),
        }, new[]
        {
            Link("wide-link", "anchor", "wide", kind),
            Link("tall-link", "anchor", "tall", kind),
            Link("small-link", "wide", "small", kind),
        });
        _engine.Apply(graph, new LayoutOptions());
        var preferred = graph.Nodes["wide"];
        graph = Graph(graph.Nodes.Values.Append(Pin("obstacle", preferred.X!.Value, preferred.Y!.Value, 520, 160)),
            graph.Edges.Values);
        var before = graph.Clone();

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["anchor"].Should().BeEquivalentTo(before.Nodes["anchor"]);
        graph.Nodes["obstacle"].Should().BeEquivalentTo(before.Nodes["obstacle"]);
        graph.Nodes["wide"].Y.Should().NotBe(preferred.Y);
        AssertNoOverlap(graph);
        AssertOnlyCoordinatesChanged(before, graph);
        var positions = Coordinates(graph);
        _engine.Apply(graph, new LayoutOptions());
        Coordinates(graph).Should().Equal(positions);
    }

    [Fact]
    public void SeparatelyAnchoredIslandsAlsoAvoidEachOthersUnpinnedMembers()
    {
        var graph = Graph(new[]
        {
            Pin("a-pin", 200, 180), Node("a-card", height: 300),
            Pin("b-pin", 200, 360), Node("b-card", height: 300),
        }, new[]
        {
            Link("a-flow", "a-pin", "a-card", EdgeKind.Flow),
            Link("b-flow", "b-pin", "b-card", EdgeKind.Flow),
        });
        var before = graph.Clone();

        _engine.Apply(graph, new LayoutOptions());

        AssertNoOverlap(graph);
        graph.Nodes["a-pin"].Should().BeEquivalentTo(before.Nodes["a-pin"]);
        graph.Nodes["b-pin"].Should().BeEquivalentTo(before.Nodes["b-pin"]);
        var positions = Coordinates(graph);
        _engine.Apply(graph, new LayoutOptions());
        Coordinates(graph).Should().Equal(positions);
    }

    [Fact]
    public void PinnedNestedGroupRemainsAnObstacleForUnrelatedCards()
    {
        var graph = GroupedMeeting();
        var pinned = graph.Nodes["detail-a"];
        pinned.X = 700;
        pinned.Y = 500;
        pinned.Locked = true;

        _engine.Apply(graph, new LayoutOptions());

        pinned.X.Should().Be(700);
        pinned.Y.Should().Be(500);
        var snapshot = LayoutSnapshot.Capture(graph);
        var outside = snapshot.Nodes["unrelated"];
        Intersects(Rect(snapshot.Groups["workshop"]), Rect(outside)).Should().BeFalse();
        Intersects(Rect(snapshot.Groups["plan"]), Rect(snapshot.Nodes["root-card"])).Should().BeFalse();
        AssertNoOverlap(graph);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReflowAllMovesPinsOnlyWhenExplicitlyRequestedAndNeverUnlocksThem(bool reflowPinned)
    {
        var graph = Graph(new[] { Pin("manual", 9000, 8000), Node("idea") },
            new[] { Link("related", "manual", "idea") });

        _engine.Apply(graph, new LayoutOptions(ReflowPinned: reflowPinned));

        graph.Nodes["manual"].Locked.Should().BeTrue();
        if (reflowPinned)
        {
            graph.Nodes["manual"].X.Should().NotBe(9000);
            graph.Nodes["manual"].Y.Should().NotBe(8000);
        }
        else
        {
            graph.Nodes["manual"].X.Should().Be(9000);
            graph.Nodes["manual"].Y.Should().Be(8000);
        }
        AssertNoOverlap(graph);
    }

    [Fact]
    public void RepeatedLayoutIsStableIncludingNestedFramesAndNodeSizes()
    {
        var graph = GroupedMeeting();
        var before = graph.Clone();
        _engine.Apply(graph, new LayoutOptions());
        var expected = LayoutSnapshot.Capture(graph);

        for (var repeat = 0; repeat < 5; repeat++)
        {
            _engine.Apply(graph, new LayoutOptions());
            LayoutSnapshot.Capture(graph).Should().BeEquivalentTo(expected);
        }
        AssertOnlyCoordinatesChanged(before, graph);
    }

    [Fact]
    public void GeometryDoesNotDependOnNodeEdgeOrGroupInsertionOrder()
    {
        var first = GroupedMeeting();
        var second = Graph(first.Nodes.Values.Reverse(), first.Edges.Values.Reverse(), first.Groups.Values.Reverse());

        _engine.Apply(first, new LayoutOptions());
        _engine.Apply(second, new LayoutOptions());

        LayoutSnapshot.Capture(first).Should().BeEquivalentTo(LayoutSnapshot.Capture(second));
    }

    [Fact]
    public void EmptyGraphAndLockedNodeWithoutCoordinatesAreHandled()
    {
        _engine.Apply(new SceneGraph(), new LayoutOptions()).Should().Be(new LayoutResult(0, 0, 0));
        var node = Node("unlocated");
        node.Locked = true;
        var graph = Graph(new[] { node });

        _engine.Apply(graph, new LayoutOptions()).NodesPositioned.Should().Be(1);

        graph.Nodes[node.Id].Locked.Should().BeTrue();
        graph.Nodes[node.Id].X.Should().NotBeNull();
        graph.Nodes[node.Id].Y.Should().NotBeNull();
    }

    private static SceneGraph MixedMeeting() => Graph(new[]
    {
        Node("start", NodeKind.Process), Node("review", NodeKind.Decision), Node("finish", NodeKind.Process),
        Node("client", NodeKind.Actor), Node("service", NodeKind.Technology), Node("store", NodeKind.DataStore),
        Node("concept"), Node("north"), Node("south"), Node("east"), Node("west"),
    }.Concat(Enumerable.Range(0, 8).Select(i => Node($"idea-{i}"))), new[]
    {
        Link("start-review", "start", "review", EdgeKind.Flow),
        Link("review-finish", "review", "finish", EdgeKind.Flow),
        Link("client-service", "client", "service", EdgeKind.Dependency),
        Link("service-store", "service", "store", EdgeKind.Dependency),
        Link("concept-north", "concept", "north"), Link("concept-south", "concept", "south"),
        Link("concept-east", "concept", "east"), Link("concept-west", "concept", "west"),
    });

    private static SceneGraph GroupedMeeting() => Graph(new[]
    {
        Node("root-card", group: "workshop"), Node("plan-a", group: "plan"), Node("plan-b", group: "plan"),
        Node("detail-a", width: 280, height: 170, group: "detail"),
        Node("execute-a", NodeKind.Process, group: "execution"), Node("execute-b", group: "execution"),
        Node("unrelated"), Node("other-a", group: "other"), Node("other-b", group: "other"),
    }, new[]
    {
        Link("handoff", "plan-a", "execute-a", EdgeKind.Flow),
        Link("context", "unrelated", "plan-b"),
    }, new[]
    {
        new SceneGroup { Id = "workshop", Label = "Workshop" },
        new SceneGroup { Id = "plan", Label = "Plan", ParentGroupId = "workshop" },
        new SceneGroup { Id = "detail", Label = "Detail", ParentGroupId = "plan" },
        new SceneGroup { Id = "execution", Label = "Execution", ParentGroupId = "workshop" },
        new SceneGroup { Id = "other", Label = "Separate topic" },
    });

    private static SceneGraph Graph(IEnumerable<SceneNode> nodes, IEnumerable<SceneEdge>? edges = null,
        IEnumerable<SceneGroup>? groups = null)
    {
        var graph = new SceneGraph();
        graph.RestorePersistedState(nodes, edges ?? [], groups ?? [], [], [], revision: 17);
        return graph;
    }

    private static SceneNode Node(string id, NodeKind kind = NodeKind.Concept, double width = 140, double height = 60,
        string? group = null) =>
        new() { Id = id, Label = id, Kind = kind, Width = width, Height = height, GroupId = group };

    private static SceneNode Pin(string id, double x, double y, double width = 140, double height = 60) =>
        new()
        {
            Id = id, Label = id, Kind = NodeKind.Concept, X = x, Y = y, Width = width, Height = height,
            Locked = true, LifecycleState = ElementLifecycleState.UserEdited,
        };

    private static SceneEdge Link(string id, string from, string to, EdgeKind kind = EdgeKind.Association) =>
        new() { Id = id, FromNodeId = from, ToNodeId = to, Kind = kind };

    private static (string Id, double? X, double? Y, double Width, double Height)[] Coordinates(SceneGraph graph) =>
        graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => (n.Id, n.X, n.Y, n.Width, n.Height)).ToArray();

    private static bool BelongsTo(SceneGraph graph, SceneNode node, string groupId)
    {
        var parent = node.GroupId;
        while (parent is not null && graph.Groups.TryGetValue(parent, out var group))
        {
            if (parent == groupId) return true;
            parent = group.ParentGroupId;
        }
        return false;
    }

    private static void AssertOnlyCoordinatesChanged(SceneGraph before, SceneGraph after)
    {
        after.Nodes.Values.Should().BeEquivalentTo(before.Nodes.Values,
            options => options.Excluding(n => n.X).Excluding(n => n.Y));
        after.Edges.Should().BeEquivalentTo(before.Edges);
        after.Groups.Should().BeEquivalentTo(before.Groups);
        after.Notes.Should().BeEquivalentTo(before.Notes);
        after.Images.Should().BeEquivalentTo(before.Images);
        after.IntentState.Should().Be(before.IntentState);
        after.SuggestedIntentState.Should().Be(before.SuggestedIntentState);
        after.Revision.Should().Be(before.Revision);
        after.GenerationEpoch.Should().Be(before.GenerationEpoch);
    }

    private static void AssertNoOverlap(SceneGraph graph)
    {
        var nodes = LayoutSnapshot.Capture(graph).Nodes.Values.ToArray();
        foreach (var node in graph.Nodes.Values)
        {
            node.X.Should().NotBeNull();
            node.Y.Should().NotBeNull();
            double.IsFinite(node.X!.Value).Should().BeTrue();
            double.IsFinite(node.Y!.Value).Should().BeTrue();
        }
        for (var i = 0; i < nodes.Length; i++)
        for (var j = i + 1; j < nodes.Length; j++)
            Intersects(Rect(nodes[i]), Rect(nodes[j])).Should().BeFalse($"{nodes[i].Id} and {nodes[j].Id} must not overlap");
    }

    private static (double Left, double Top, double Right, double Bottom) Bounds(SceneGraph graph, params string[] ids)
    {
        var nodes = LayoutSnapshot.Capture(graph).Nodes;
        return (ids.Min(id => nodes[id].Left), ids.Min(id => nodes[id].Top),
            ids.Max(id => nodes[id].Right), ids.Max(id => nodes[id].Bottom));
    }

    private static (double Left, double Top, double Right, double Bottom) Rect(NodeGeometry n) =>
        (n.Left, n.Top, n.Right, n.Bottom);

    private static (double Left, double Top, double Right, double Bottom) Rect(GroupBounds g) =>
        (g.Left, g.Top, g.Right, g.Bottom);

    private static bool Intersects((double Left, double Top, double Right, double Bottom) a,
        (double Left, double Top, double Right, double Bottom) b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool Contains((double Left, double Top, double Right, double Bottom) outer,
        (double Left, double Top, double Right, double Bottom) inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;
}
