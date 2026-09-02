using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Layout;

public class IntentLayoutEngineTests
{
    [Fact]
    public void ArchitectureIntentsNeverResolveToMindMap()
    {
        var resolver = new IntentLayoutEngine();
        foreach (var intent in Enum.GetValues<DiagramIntent>()
                     .Where(x => x != DiagramIntent.DiscussionSummary))
            resolver.ResolveEngine(intent).Should().NotBeOfType<MindMapLayoutEngine>();
    }

    [Fact]
    public void DiscussionSummaryUsesMindMap()
    {
        new IntentLayoutEngine().ResolveEngine(DiagramIntent.DiscussionSummary)
            .Should().BeOfType<MindMapLayoutEngine>();
    }

    [Theory]
    [InlineData(DiagramIntent.SoftwareSystemArchitecture)]
    [InlineData(DiagramIntent.SaaSMultiTenantArchitecture)]
    [InlineData(DiagramIntent.SecurityZeroTrustArchitecture)]
    [InlineData(DiagramIntent.CloudNetworkArchitecture)]
    [InlineData(DiagramIntent.IntegrationDataFlowArchitecture)]
    public void ArchitectureGeometryIsDeterministic(DiagramIntent intent)
    {
        var first = Build(intent);
        var second = Build(intent);
        var engine = new IntentLayoutEngine();

        engine.Apply(first, new LayoutOptions());
        engine.Apply(second, new LayoutOptions());

        first.Nodes.Keys.OrderBy(x => x).Select(id => (id, first.Nodes[id].X, first.Nodes[id].Y))
            .Should().Equal(second.Nodes.Keys.OrderBy(x => x)
                .Select(id => (id, second.Nodes[id].X, second.Nodes[id].Y)));
    }

    [Fact]
    public void ReflowUnpinnedTreatsLockedNodesAsFixedObstacles()
    {
        var graph = Build(DiagramIntent.SoftwareSystemArchitecture);
        graph.TryUpdateNodeGeometry("gateway", 300, 200, 180, 70, locked: true);
        var engine = new IntentLayoutEngine();

        engine.Apply(graph, new LayoutOptions());

        graph.Nodes["gateway"].X.Should().Be(300);
        graph.Nodes["gateway"].Y.Should().Be(200);
        graph.Nodes["gateway"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        graph.Nodes.Values.Where(n => n.Id != "gateway").Should().OnlyContain(n =>
            !Overlaps(n, graph.Nodes["gateway"]));
    }

    [Fact]
    public void ReflowAllCanMovePinnedNodesExplicitly()
    {
        var graph = Build(DiagramIntent.SoftwareSystemArchitecture);
        graph.TryUpdateNodeGeometry("gateway", 9000, 9000, 180, 70, locked: true);

        new IntentLayoutEngine().Apply(graph, new LayoutOptions(ReflowPinned: true));

        graph.Nodes["gateway"].X.Should().NotBe(9000);
        graph.Nodes["gateway"].Y.Should().NotBe(9000);
        graph.Nodes["gateway"].Locked.Should().BeTrue();
    }

    [Fact]
    public void ReflowUnpinnedMovesUnlockedUserEditedNode()
    {
        var graph = Build(DiagramIntent.SoftwareSystemArchitecture);
        graph.TryUpdateNodeGeometry("gateway", 9000, 9000, 180, 70, locked: true);
        graph.TryUpdateNodeGeometry("gateway", 9000, 9000, 180, 70, locked: false);
        graph.Nodes["gateway"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);

        new IntentLayoutEngine().Apply(graph, new LayoutOptions());

        graph.Nodes["gateway"].Locked.Should().BeFalse();
        graph.Nodes["gateway"].X.Should().NotBe(9000);
        graph.Nodes["gateway"].Y.Should().NotBe(9000);
    }

    [Fact]
    public void NestedBoundsContainNodesAndChildGroups()
    {
        var graph = Build(DiagramIntent.CloudNetworkArchitecture);
        new IntentLayoutEngine().Apply(graph, new LayoutOptions());

        var snapshot = LayoutSnapshot.Capture(graph);
        var outer = snapshot.Groups["cloud"];
        var inner = snapshot.Groups["network"];
        var database = snapshot.Nodes["database"];

        outer.Left.Should().BeLessThanOrEqualTo(inner.Left);
        outer.Top.Should().BeLessThanOrEqualTo(inner.Top);
        outer.Right.Should().BeGreaterThanOrEqualTo(inner.Right);
        outer.Bottom.Should().BeGreaterThanOrEqualTo(inner.Bottom);
        inner.Left.Should().BeLessThanOrEqualTo(database.Left);
        inner.Right.Should().BeGreaterThanOrEqualTo(database.Right);
    }

    [Fact]
    public void ReleaseFixtureNodesDoNotOverlap()
    {
        var graph = Build(DiagramIntent.IntegrationDataFlowArchitecture);
        new IntentLayoutEngine().Apply(graph, new LayoutOptions());
        var nodes = graph.Nodes.Values.OrderBy(n => n.Id).ToArray();
        for (var i = 0; i < nodes.Length; i++)
        for (var j = i + 1; j < nodes.Length; j++)
            Overlaps(nodes[i], nodes[j]).Should().BeFalse(
                $"{nodes[i].Id} and {nodes[j].Id} should not overlap");
    }

    private static SceneGraph Build(DiagramIntent intent)
    {
        var graph = new SceneGraph();
        new ScenePatchApplier().Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("client", NodeKind.Actor, "Client"),
            new AddNode("gateway", NodeKind.Security, "Gateway"),
            new AddNode("worker", NodeKind.Process, "Worker"),
            new AddNode("database", NodeKind.DataStore, "Database"),
            new Connect("e1", "client", "gateway", Step: 1),
            new Connect("e2", "gateway", "worker", Step: 2),
            new Connect("e3", "worker", "database", Step: 3),
            new GroupOp("cloud", "Azure cloud", Array.Empty<string>(), null, null,
                BoundaryKind.CloudScope),
            new GroupOp("network", "Application network", new[] { "gateway", "worker", "database" },
                "cloud", null, BoundaryKind.Network),
        }));
        NodeSizer.ApplyTo(graph);
        graph.SetIntentState(new DiagramIntentState(
            intent, DiagramIntentSelectionMode.PinnedByUser, 1, "test", graph.Revision));
        return graph;
    }

    private static bool Overlaps(SceneNode a, SceneNode b) =>
        Math.Abs(a.X!.Value - b.X!.Value) < (a.Width + b.Width) / 2 &&
        Math.Abs(a.Y!.Value - b.Y!.Value) < (a.Height + b.Height) / 2;
}
