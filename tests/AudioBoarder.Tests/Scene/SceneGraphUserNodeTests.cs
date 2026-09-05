using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

public sealed class SceneGraphUserNodeTests
{
    [Fact]
    public void DroppedNodeIsPinnedAndProtectedAsUserEdited()
    {
        var graph = new SceneGraph();
        var node = new SceneNode
        {
            Id = "user-azure-openai-1",
            Label = "Azure OpenAI Service",
            Kind = NodeKind.Technology,
            X = 120,
            Y = 240,
        };

        graph.TryAddUserNode(node).Should().BeTrue();

        graph.Nodes[node.Id].Locked.Should().BeTrue();
        graph.Nodes[node.Id].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        graph.Revision.Should().Be(1);
    }

    [Fact]
    public void InvalidOrDuplicateDroppedNodeIsRejected()
    {
        var graph = new SceneGraph();
        var node = new SceneNode { Id = "same", Label = "SQL Server", X = 1, Y = 2 };

        graph.TryAddUserNode(node).Should().BeTrue();
        graph.TryAddUserNode(node.Clone()).Should().BeFalse();
        graph.TryAddUserNode(new SceneNode { Id = "bad", Label = "", X = 1, Y = 2 })
            .Should().BeFalse();
    }
}
