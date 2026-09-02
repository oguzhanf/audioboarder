using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Patch;

public class ScenePatchPermutationTests
{
    [Fact]
    public void DependencyOrderPermutationsProduceSameSemanticGraph()
    {
        ScenePatchOperation[] operations =
        [
            new Connect("flow", "client", "api", EdgeKind.Flow, "HTTPS request",
                Protocol: "HTTPS", Payload: "Order", Authentication: "OAuth 2.0",
                InteractionMode: InteractionMode.Synchronous),
            new GroupOp("subnet", "Application subnet", ["api"], "vnet",
                "10.0.1.0/24", BoundaryKind.Network),
            new AddNode("api", NodeKind.Process, "Orders API", "subnet"),
            new GroupOp("vnet", "Hub VNet", ["client"], BoundaryKind: BoundaryKind.Network),
            new AddNode("client", NodeKind.Actor, "Client", "vnet"),
        ];

        var snapshots = Permute(operations)
            .Select(permutation =>
            {
                var graph = new SceneGraph();
                new ScenePatchApplier().Apply(graph, new ScenePatch(permutation));
                return Snapshot(graph);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        snapshots.Should().ContainSingle();
    }

    [Fact]
    public void SameIdEnrichesEdge_AndDistinctSameDirectionInteractionsSurvive()
    {
        var graph = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(graph, new ScenePatch(
        [
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.DataStore, "B"),
            new Connect("e1", "a", "b", Label: "writes"),
        ]));

        applier.Apply(graph, new ScenePatch(
        [
            new Connect("e1", "a", "b", EdgeKind.Flow, "writes order",
                Step: 3, Protocol: "HTTPS", Payload: "OrderCreated",
                DataClassification: "Confidential", Authentication: "managed identity",
                InteractionMode: InteractionMode.Synchronous),
            new Connect("e2", "a", "b", EdgeKind.Flow, "reads status",
                Protocol: "HTTPS", Payload: "OrderStatus",
                InteractionMode: InteractionMode.Synchronous),
            new Connect("duplicate", "a", "b", EdgeKind.Dependency, " reads status ",
                Protocol: "https", Payload: "orderstatus",
                InteractionMode: InteractionMode.Synchronous),
        ]));

        graph.Edges.Should().HaveCount(2);
        graph.Edges["e1"].Protocol.Should().Be("HTTPS");
        graph.Edges["e1"].Payload.Should().Be("OrderCreated");
        graph.Edges["e1"].Authentication.Should().Be("managed identity");
        graph.Edges["e1"].DataClassification.Should().Be("Confidential");
        graph.Edges["e1"].Step.Should().Be(3);
        graph.Edges.Values.Should().ContainSingle(edge => edge.Label == "reads status");
    }

    [Fact]
    public void ModelPatchDoesNotOverwriteUserEditedElements()
    {
        var graph = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(graph, new ScenePatch(
        [
            new GroupOp("g", "Original group", [], BoundaryKind: BoundaryKind.System),
            new AddNode("a", NodeKind.Process, "Original node", "g"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e", "a", "b", Label: "original"),
        ]));
        graph.TryMarkNodeUserEdited("a");
        graph.TryMarkEdgeUserEdited("e");
        graph.TryMarkGroupUserEdited("g");

        applier.Apply(graph, new ScenePatch(
        [
            new AddNode("a", NodeKind.Risk, "Model node"),
            new Connect("e", "b", "a", EdgeKind.Dependency, "model edge"),
            new GroupOp("g", "Model group", ["b"], BoundaryKind: BoundaryKind.Network),
            new DeleteNode("a"),
            new Disconnect("e"),
            new UngroupOp("g"),
        ]));

        graph.Nodes["a"].Label.Should().Be("Original node");
        graph.Edges["e"].Label.Should().Be("original");
        graph.Edges["e"].FromNodeId.Should().Be("a");
        graph.Groups["g"].Label.Should().Be("Original group");
        graph.Groups["g"].BoundaryKind.Should().Be(BoundaryKind.System);
    }

    [Fact]
    public void HostControlsIncomingLifecycle_AndDeepPassCanConfirmElements()
    {
        var graph = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(graph, new ScenePatch(
        [
            new GroupOp("g", "System", []),
            new AddNode("a", NodeKind.Process, "A", "g"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e", "a", "b"),
        ]));
        graph.Nodes["a"].LifecycleState.Should().Be(ElementLifecycleState.Provisional);

        applier.Apply(graph, new ScenePatch(
        [
            new GroupOp("g", "System", ["a"]),
            new UpdateNode("a", Description: "validated"),
            new Connect("e", "a", "b", Label: "validated"),
        ]), incomingLifecycle: ElementLifecycleState.Confirmed);

        graph.Nodes["a"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
        graph.Edges["e"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
        graph.Groups["g"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
    }

    private static IEnumerable<ScenePatchOperation[]> Permute(ScenePatchOperation[] source)
    {
        if (source.Length == 1)
        {
            yield return source;
            yield break;
        }
        for (var i = 0; i < source.Length; i++)
        {
            var rest = source.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permute(rest))
                yield return [source[i], .. tail];
        }
    }

    private static string Snapshot(SceneGraph graph)
    {
        var nodes = graph.Nodes.Values.OrderBy(n => n.Id)
            .Select(n => $"N:{n.Id}:{n.Kind}:{n.Label}:{n.GroupId}:{n.LifecycleState}");
        var edges = graph.Edges.Values.OrderBy(e => e.Id)
            .Select(e => $"E:{e.Id}:{e.FromNodeId}:{e.ToNodeId}:{e.Kind}:{e.Label}:{e.Protocol}:{e.Payload}:{e.Authentication}:{e.InteractionMode}:{e.DataClassification}");
        var groups = graph.Groups.Values.OrderBy(g => g.Id)
            .Select(g => $"G:{g.Id}:{g.Label}:{g.ParentGroupId}:{g.Subtitle}:{g.BoundaryKind}:{g.LifecycleState}");
        return string.Join('|', nodes.Concat(edges).Concat(groups));
    }
}
