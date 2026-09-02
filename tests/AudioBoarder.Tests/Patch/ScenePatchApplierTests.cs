using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Patch;

public class ScenePatchApplierTests
{
    private readonly ScenePatchApplier _applier = new();

    [Fact]
    public void ContinuousReassertionNeverDowngradesConfirmedLifecycle()
    {
        var graph = new SceneGraph();
        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new GroupOp("g", "System", Array.Empty<string>()),
                new AddNode("a", NodeKind.System, "API", "g"),
                new AddNode("b", NodeKind.DataStore, "Database", "g"),
                new Connect("e", "a", "b", Label: "writes"),
            ]),
            incomingLifecycle: ElementLifecycleState.Confirmed);

        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new GroupOp("g", "System", ["a", "b"]),
                new AddNode("a", NodeKind.System, "API", "g"),
                new UpdateNode("b", Description: "stores records"),
                new Connect("e", "a", "b", Label: "writes records"),
            ]),
            incomingLifecycle: ElementLifecycleState.Provisional);

        graph.Nodes.Values.Should().OnlyContain(
            node => node.LifecycleState == ElementLifecycleState.Confirmed);
        graph.Edges.Values.Should().OnlyContain(
            edge => edge.LifecycleState == ElementLifecycleState.Confirmed);
        graph.Groups.Values.Should().OnlyContain(
            group => group.LifecycleState == ElementLifecycleState.Confirmed);
    }

    [Fact]
    public void GroupContainmentNeverChangesMemberLifecycle()
    {
        var graph = new SceneGraph();
        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new AddNode("confirmed", NodeKind.System, "Confirmed API"),
            ]),
            incomingLifecycle: ElementLifecycleState.Confirmed);
        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new GroupOp("provisional-group", "Provisional group", ["confirmed"]),
            ]),
            incomingLifecycle: ElementLifecycleState.Provisional);

        graph.Nodes["confirmed"].GroupId.Should().Be("provisional-group");
        graph.Nodes["confirmed"].LifecycleState.Should().Be(
            ElementLifecycleState.Confirmed);

        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new GroupOp("confirmed-group", "Confirmed group", Array.Empty<string>()),
            ]),
            incomingLifecycle: ElementLifecycleState.Confirmed);
        _applier.Apply(
            graph,
            new ScenePatch(
            [
                new AddNode("provisional", NodeKind.System, "Provisional worker"),
                new GroupOp("confirmed-group", "Confirmed group", ["provisional"]),
            ]),
            incomingLifecycle: ElementLifecycleState.Provisional);

        graph.Nodes["provisional"].GroupId.Should().Be("confirmed-group");
        graph.Nodes["provisional"].LifecycleState.Should().Be(
            ElementLifecycleState.Provisional);
    }

    [Fact]
    public void ApplyAddNode_AddsNode()
    {
        var graph = new SceneGraph();
        var patch = new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
        });
        _applier.Apply(graph, patch);
        graph.Nodes.Should().ContainKey("a");
        graph.Nodes["a"].Label.Should().Be("Alpha");
    }

    [Fact]
    public void ApplyAddNode_DuplicateUpdatesExisting()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new AddNode("a", NodeKind.Process, "A") }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new AddNode("a", NodeKind.Decision, "A2") }));
        graph.Nodes.Should().HaveCount(1);
        graph.Nodes["a"].Label.Should().Be("A2");
        graph.Nodes["a"].Kind.Should().Be(NodeKind.Decision);
    }

    [Fact]
    public void AddNode_WithMissingGroup_AddsNodeWithoutGroup()
    {
        // The exact failure that froze the live diagram: an add_node referencing
        // a group that doesn't exist must keep the node (sans group), not drop it.
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A", GroupId: "nonexistent"),
        }));
        graph.Nodes.Should().ContainKey("a");
        graph.Nodes["a"].GroupId.Should().BeNull();
    }

    [Fact]
    public void Connect_ToMissingNode_IsSkippedNotThrown()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new AddNode("a", NodeKind.Process, "A") }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new Connect("e1", "a", "b") }));
        graph.Edges.Should().NotContainKey("e1");
    }

    [Fact]
    public void Connect_SelfLoop_IsSkipped()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new AddNode("a", NodeKind.Process, "A") }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new Connect("e1", "a", "a") }));
        graph.Edges.Should().NotContainKey("e1");
    }

    [Fact]
    public void DeleteNode_RemovesNodeAndOrphanEdges()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b"),
        }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new DeleteNode("b") }));
        graph.Nodes.Should().NotContainKey("b");
        graph.Edges.Should().NotContainKey("e1");
    }

    [Fact]
    public void InvalidOperation_IsSkipped_ValidOpsStillApply()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new AddNode("a", NodeKind.Process, "A") }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "b", "missing"),
        }));
        graph.Nodes.Should().ContainKey("b");      // valid op applied
        graph.Edges.Should().NotContainKey("e1");  // invalid op skipped
        graph.Nodes.Should().ContainKey("a");
    }

    [Fact]
    public void AddNode_SanitizesGarbageLabelArtifacts()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Actor, "Mashreq Users},{"),
            new AddNode("b", NodeKind.Entity, "  Web   API  "),
        }));
        graph.Nodes["a"].Label.Should().Be("Mashreq Users");
        graph.Nodes["b"].Label.Should().Be("Web API");
    }

    [Fact]
    public void AddNode_DuplicateLabelNewId_AliasesOntoExisting_AndRemapsEdges()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("n1", NodeKind.Entity, "Web API"),
        }));
        // Next call: model re-introduces "Web API" under a NEW id and connects to it.
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("user", NodeKind.Actor, "User"),
            new AddNode("n2", NodeKind.Entity, "web api"),   // same concept, new id, diff case
            new Connect("e1", "user", "n2"),
        }));
        // No duplicate "Web API" node; edge remapped onto the original n1.
        graph.Nodes.Values.Count(n => ScenePatchApplier.Normalize(n.Label) == "web api").Should().Be(1);
        graph.Nodes.Should().ContainKey("n1");
        graph.Nodes.Should().NotContainKey("n2");
        graph.Edges.Values.Should().ContainSingle()
            .Which.ToNodeId.Should().Be("n1");
    }

    [Fact]
    public void Connect_DuplicateEdgeBetweenSameNodes_IsSkipped()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b"),
        }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new Connect("e2", "a", "b"), // same direction, different id
        }));
        graph.Edges.Should().HaveCount(1);
    }

    [Fact]
    public void NoteUpsert_DuplicateText_IsNotDuplicated()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("nt1", NoteKind.Risk, "Vendors are out of country"),
        }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("nt2", NoteKind.Risk, "vendors are out of country."),
        }));
        graph.Notes.Should().HaveCount(1);
    }

    [Fact]
    public void ClearScene_RemovesEverything()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b"),
            new NoteUpsert("n1", NoteKind.General, "hi"),
        }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[] { new ClearScene() }));
        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
        graph.Notes.Should().BeEmpty();
    }

    [Fact]
    public void GroupOp_AssignsNodeGroupIds()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new GroupOp("g1", "Backend", new[] { "a", "b" }),
        }));
        graph.Groups.Should().ContainKey("g1");
        graph.Nodes["a"].GroupId.Should().Be("g1");
        graph.Nodes["b"].GroupId.Should().Be("g1");
    }

    [Fact]
    public void Relabel_WorksOnNodesEdgesAndGroups()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b", EdgeKind.Flow, "old"),
            new GroupOp("g1", "old-group", new[] { "a" }),
            new Relabel("a", "Alpha"),
            new Relabel("e1", "new"),
            new Relabel("g1", "new-group"),
        }));
        graph.Nodes["a"].Label.Should().Be("Alpha");
        graph.Edges["e1"].Label.Should().Be("new");
        graph.Groups["g1"].Label.Should().Be("new-group");
    }

    [Fact]
    public void NoteUpsert_AddsAndUpdates()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("n1", NoteKind.ActionItem, "Buy milk"),
        }));
        graph.Notes["n1"].Text.Should().Be("Buy milk");
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("n1", NoteKind.Decision, "Buy bread"),
        }));
        graph.Notes["n1"].Text.Should().Be("Buy bread");
        graph.Notes["n1"].Kind.Should().Be(NoteKind.Decision);
    }
}
