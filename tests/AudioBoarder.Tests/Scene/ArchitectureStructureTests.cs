using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

/// <summary>
/// Cover for the structure an architecture diagram needs: nested boundaries and a
/// numbered request path. Both follow the Azure Architecture Center conventions —
/// containers nest (subscription > vnet > subnet) and dataflows are numbered so a
/// reader can follow them in order.
/// </summary>
public class ArchitectureStructureTests
{
    private readonly ScenePatchApplier _applier = new();

    [Fact]
    public void ContainersNestViaParentGroupId()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("app", NodeKind.Technology, "App Service"),
            new GroupOp("sub", "Production subscription", Array.Empty<string>()),
            new GroupOp("vnet", "Hub VNet", Array.Empty<string>(), ParentGroupId: "sub", Subtitle: "10.1.0.0/16"),
            new GroupOp("snet", "App subnet", new[] { "app" }, ParentGroupId: "vnet", Subtitle: "10.1.1.0/24"),
        }));

        graph.Groups["vnet"].ParentGroupId.Should().Be("sub");
        graph.Groups["snet"].ParentGroupId.Should().Be("vnet");
        graph.Groups["vnet"].Subtitle.Should().Be("10.1.0.0/16");
        graph.Nodes["app"].GroupId.Should().Be("snet");
    }

    [Fact]
    public void AContainerCannotBecomeItsOwnAncestor()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new GroupOp("a", "A", Array.Empty<string>()),
            new GroupOp("b", "B", Array.Empty<string>(), ParentGroupId: "a"),
            // Closing the loop would make layout recurse forever.
            new GroupOp("a", "A", Array.Empty<string>(), ParentGroupId: "b"),
        }));

        graph.Groups["a"].ParentGroupId.Should().BeNull();
        graph.Groups["b"].ParentGroupId.Should().Be("a");
    }

    [Fact]
    public void AGroupCanGainMembersOnALaterPass()
    {
        // Boundaries are discovered incrementally as people talk, so re-emitting a
        // group must add to it rather than being ignored as a duplicate.
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("gw", NodeKind.Technology, "Application Gateway"),
            new GroupOp("vnet", "Hub VNet", new[] { "gw" }),
        }));
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("app", NodeKind.Technology, "App Service"),
            new GroupOp("vnet", "Hub VNet", new[] { "app" }),
        }));

        graph.Groups.Should().ContainSingle();
        graph.Nodes.Values.Where(n => n.GroupId == "vnet")
            .Select(n => n.Id).Should().BeEquivalentTo(new[] { "gw", "app" });
    }

    [Fact]
    public void ConnectionsCarryNumberedSteps()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("user", NodeKind.Actor, "User"),
            new AddNode("fd", NodeKind.Technology, "Azure Front Door"),
            new AddNode("app", NodeKind.Technology, "App Service"),
            new Connect("e1", "user", "fd", EdgeKind.Flow, "HTTPS request", Step: 1),
            new Connect("e2", "fd", "app", EdgeKind.Flow, "routes to origin", Step: 2),
        }));

        graph.Edges["e1"].Step.Should().Be(1);
        graph.Edges["e2"].Step.Should().Be(2);
    }

    [Fact]
    public void StructuralEdgesHaveNoStepNumber()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Technology, "Key Vault"),
            new AddNode("b", NodeKind.Technology, "App Service"),
            new Connect("e", "b", "a", EdgeKind.Dependency, "reads secrets"),
        }));

        graph.Edges["e"].Step.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NonPositiveStepNumbersAreTreatedAsUnnumbered(int step)
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Technology, "A"),
            new AddNode("b", NodeKind.Technology, "B"),
            new Connect("e", "a", "b", EdgeKind.Flow, null, Step: step),
        }));

        graph.Edges["e"].Step.Should().BeNull("a badge numbered 0 or negative is meaningless");
    }

    [Fact]
    public void NestingAndStepsSurviveACloneForAutosave()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("app", NodeKind.Technology, "App Service"),
            new AddNode("db", NodeKind.DataStore, "Azure SQL Database"),
            new GroupOp("vnet", "Hub VNet", Array.Empty<string>(), Subtitle: "10.0.0.0/16"),
            new GroupOp("snet", "App subnet", new[] { "app" }, ParentGroupId: "vnet"),
            new Connect("e", "app", "db", EdgeKind.Flow, "queries", Step: 3),
        }));

        var clone = graph.Clone();

        clone.Groups["snet"].ParentGroupId.Should().Be("vnet");
        clone.Groups["vnet"].Subtitle.Should().Be("10.0.0.0/16");
        clone.Edges["e"].Step.Should().Be(3);
    }
}
