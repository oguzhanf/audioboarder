using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Patch;

/// <summary>
/// System.Text.Json aborts the entire document on the first unrecognised
/// polymorphic discriminator. Observed live: a model emitted
/// <c>"op": "node_upsert"</c> instead of <c>add_node</c>, the parse threw, and
/// every operation in that response was discarded — the board stayed at rev 0
/// while the transcript filled up.
/// </summary>
public class ScenePatchOpToleranceTests
{
    private static ScenePatch Parse(string ops) =>
        ScenePatchJson.Deserialize($"{{\"operations\":[{ops}]}}");

    [Fact]
    public void TheExactOpNameThatBrokeLiveGenerationNowParses()
    {
        var patch = Parse("""{"op":"node_upsert","id":"n1","kind":"technology","label":"Azure AI Foundry"}""");
        var add = patch.Operations.OfType<AddNode>().Single();
        add.Id.Should().Be("n1");
        add.Label.Should().Be("Azure AI Foundry");
    }

    [Theory]
    [InlineData("node_upsert")]
    [InlineData("upsert_node")]
    [InlineData("create_node")]
    [InlineData("new_node")]
    [InlineData("addNode")]
    [InlineData("add-node")]
    public void NodeCreationSynonymsMapToAddNode(string op)
    {
        Parse($$"""{"op":"{{op}}","id":"n1","kind":"process","label":"X"}""")
            .Operations.Single().Should().BeOfType<AddNode>();
    }

    [Theory]
    [InlineData("edge")]
    [InlineData("add_edge")]
    [InlineData("link")]
    [InlineData("create_edge")]
    public void EdgeSynonymsMapToConnect(string op)
    {
        Parse($$"""{"op":"{{op}}","id":"e1","from":"a","to":"b","kind":"flow","label":"calls"}""")
            .Operations.Single().Should().BeOfType<Connect>();
    }

    [Theory]
    [InlineData("add_note", typeof(NoteUpsert))]
    [InlineData("add_group", typeof(GroupOp))]
    [InlineData("remove_node", typeof(DeleteNode))]
    [InlineData("rename", typeof(Relabel))]
    [InlineData("clear", typeof(ClearScene))]
    public void OtherSynonymsMapToTheirCanonicalOp(string op, Type expected)
    {
        var json = op switch
        {
            "add_note" => """{"op":"add_note","id":"n1","kind":"decision","text":"ship it"}""",
            "add_group" => """{"op":"add_group","id":"g1","label":"System","node_ids":["a"]}""",
            "remove_node" => """{"op":"remove_node","id":"a"}""",
            "rename" => """{"op":"rename","id":"a","label":"New"}""",
            _ => """{"op":"clear"}""",
        };
        Parse(json).Operations.Single().Should().BeOfType(expected);
    }

    [Fact]
    public void OneUnmappableOpNoLongerDiscardsTheRestOfThePatch()
    {
        // This is the whole point: partial output must still move the diagram.
        var patch = ScenePatchJson.Deserialize("""
            {"operations":[
            {"op":"add_node","id":"a","kind":"technology","label":"Fabric"},
            {"op":"teleport_node","id":"zz"},
            {"op":"connect","id":"e1","from":"a","to":"b","kind":"flow","label":"feeds"}
            ]}
            """, out var info);

        patch.Operations.Should().HaveCount(2);
        patch.Operations.OfType<AddNode>().Should().ContainSingle();
        patch.Operations.OfType<Connect>().Should().ContainSingle();
        info.DroppedOps.Should().Be(1);
        info.NeededRepair.Should().BeTrue();
    }

    [Fact]
    public void CanonicalOpNamesArePassedThroughUnchanged()
    {
        ScenePatchJson.Deserialize(
            """{"operations":[{"op":"add_node","id":"a","kind":"actor","label":"User"}]}""",
            out var info)
            .Operations.Single().Should().BeOfType<AddNode>();

        info.RewrittenOps.Should().Be(0);
        info.DroppedOps.Should().Be(0);
        info.NeededRepair.Should().BeFalse();
    }

    [Fact]
    public void MalformedJsonStillThrowsRatherThanSilentlyReturningNothing()
    {
        var act = () => ScenePatchJson.Deserialize("{ not json");
        act.Should().Throw<Exception>();
    }
}
