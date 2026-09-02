using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Patch;

public class ScenePatchJsonTests
{
    [Theory]
    [InlineData("system", NodeKind.System)]
    [InlineData("external", NodeKind.External)]
    [InlineData("technology", NodeKind.Technology)]
    [InlineData("security", NodeKind.Security)]
    [InlineData("cloud", NodeKind.Cloud)]
    [InlineData("callout", NodeKind.Callout)]
    [InlineData("risk", NodeKind.Risk)]
    [InlineData("metric", NodeKind.Metric)]
    [InlineData("document", NodeKind.Document)]
    [InlineData("milestone", NodeKind.Milestone)]
    [InlineData("data_store", NodeKind.DataStore)]
    public void RealEnumMemberIsNeverShadowedByALegacySynonym(string jsonKind, NodeKind expected)
    {
        // "system" used to be a synonym onto Entity and "external" onto Actor. Once they
        // became real kinds the synonyms silently downgraded them, so a system boundary
        // was drawn as a plain entity box.
        var json = $"{{\"operations\":[{{\"op\":\"add_node\",\"id\":\"n1\",\"kind\":\"{jsonKind}\",\"label\":\"X\"}}]}}";
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations.OfType<AddNode>().Single().Kind.Should().Be(expected);
    }

    [Fact]
    public void IconAndDescriptionSurviveRoundTrip()
    {
        var original = new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("t", NodeKind.Technology, "Power BI",
                Icon: "\U0001F4CA", Description: "reporting layer"),
        });

        var add = ScenePatchJson.Deserialize(ScenePatchJson.Serialize(original))
            .Operations.OfType<AddNode>().Single();
        add.Icon.Should().Be("\U0001F4CA");
        add.Description.Should().Be("reporting layer");
    }

    [Fact]
    public void RoundTrip_AllOperationKinds()
    {
        var original = new ScenePatch(new ScenePatchOperation[]
        {
            new ClearScene(),
            new AddNode("a", NodeKind.Process, "Alpha"),
            new AddNode("b", NodeKind.Decision, "?", Position: new PositionHint(PositionHintKind.Below, "a")),
            new Connect("e1", "a", "b", EdgeKind.Flow, "yes", 1,
                "HTTPS", "request", "internal", "OAuth", InteractionMode.Synchronous),
            new GroupOp("g1", "g", new[] { "a", "b" },
                BoundaryKind: BoundaryKind.Environment),
            new NoteUpsert("n1", NoteKind.ActionItem, "Ship it",
                Owner: "team", SourceTimestamp: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
        });

        var json = ScenePatchJson.Serialize(original);
        var roundTripped = ScenePatchJson.Deserialize(json);

        roundTripped.Operations.Should().HaveCount(original.Operations.Count);
        roundTripped.Operations[1].Should().BeOfType<AddNode>()
            .Which.Label.Should().Be("Alpha");
        roundTripped.Operations[3].Should().BeOfType<Connect>()
            .Which.From.Should().Be("a");
        var connection = (Connect)roundTripped.Operations[3];
        connection.Protocol.Should().Be("HTTPS");
        connection.InteractionMode.Should().Be(InteractionMode.Synchronous);
        ((GroupOp)roundTripped.Operations[4]).BoundaryKind.Should().Be(BoundaryKind.Environment);
    }

    [Fact]
    public void Deserialize_HandlesLlmStyleJson()
    {
        const string json = """
        {
          "operations": [
            { "op": "add_node", "id": "u", "kind": "actor", "label": "User" },
            { "op": "add_node", "id": "api", "kind": "process", "label": "API" },
            { "op": "connect", "id": "e1", "from": "u", "to": "api", "kind": "flow", "label": "request" }
          ]
        }
        """;
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations.Should().HaveCount(3);
        patch.Operations[0].Should().BeOfType<AddNode>().Which.Kind.Should().Be(NodeKind.Actor);
        patch.Operations[2].Should().BeOfType<Connect>().Which.Kind.Should().Be(EdgeKind.Flow);
    }

    [Fact]
    public void Deserialize_EmptyJson_Throws()
    {
        var act = () => ScenePatchJson.Deserialize("");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("database", NodeKind.DataStore)]
    [InlineData("service", NodeKind.Entity)]
    [InlineData("user", NodeKind.Actor)]
    [InlineData("condition", NodeKind.Decision)]
    [InlineData("task", NodeKind.Process)]
    [InlineData("Data Store", NodeKind.DataStore)]
    [InlineData("DATA_STORE", NodeKind.DataStore)]
    public void Deserialize_MapsNodeKindSynonyms(string kind, NodeKind expected)
    {
        var json = $$"""
        { "operations": [ { "op": "add_node", "id": "x", "kind": "{{kind}}", "label": "X" } ] }
        """;
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations[0].Should().BeOfType<AddNode>().Which.Kind.Should().Be(expected);
    }

    [Fact]
    public void Deserialize_UnknownNodeKind_FallsBackToDefault_WithoutDiscardingPatch()
    {
        // A single unrecognised enum value must NOT blow up the whole patch
        // (this is exactly what froze the live diagram before).
        const string json = """
        {
          "operations": [
            { "op": "add_node", "id": "a", "kind": "quantum_widget", "label": "Mystery" },
            { "op": "add_node", "id": "b", "kind": "actor", "label": "User" }
          ]
        }
        """;
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations.Should().HaveCount(2);
        patch.Operations[0].Should().BeOfType<AddNode>().Which.Kind.Should().Be(NodeKind.Entity);
        patch.Operations[1].Should().BeOfType<AddNode>().Which.Kind.Should().Be(NodeKind.Actor);
    }

    [Theory]
    [InlineData("depends", EdgeKind.Dependency)]
    [InlineData("calls", EdgeKind.Flow)]
    [InlineData("extends", EdgeKind.Inheritance)]
    [InlineData("relates", EdgeKind.Association)]
    [InlineData("nonsense", EdgeKind.Flow)]
    public void Deserialize_MapsEdgeKindSynonyms(string kind, EdgeKind expected)
    {
        var json = $$"""
        { "operations": [ { "op": "connect", "id": "e", "from": "a", "to": "b", "kind": "{{kind}}" } ] }
        """;
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations[0].Should().BeOfType<Connect>().Which.Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("todo", NoteKind.ActionItem)]
    [InlineData("blocker", NoteKind.Risk)]
    [InlineData("open_question", NoteKind.Question)]
    [InlineData("whatever", NoteKind.General)]
    public void Deserialize_MapsNoteKindSynonyms(string kind, NoteKind expected)
    {
        var json = $$"""
        { "operations": [ { "op": "note_upsert", "id": "n", "kind": "{{kind}}", "text": "hi" } ] }
        """;
        var patch = ScenePatchJson.Deserialize(json);
        patch.Operations[0].Should().BeOfType<NoteUpsert>().Which.Kind.Should().Be(expected);
    }

    [Fact]
    public void Serialize_StillEmitsSnakeCaseEnums()
    {
        var patch = new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.DataStore, "DB"),
        });
        var json = ScenePatchJson.Serialize(patch);
        json.Should().Contain("\"data_store\"");
    }

    [Theory]
    [InlineData("async", InteractionMode.Asynchronous)]
    [InlineData("scheduled", InteractionMode.Batch)]
    [InlineData("streaming", InteractionMode.Stream)]
    public void Deserialize_ToleratesInteractionModeSynonyms(string value, InteractionMode expected)
    {
        var json = $$"""
        {"operations":[{"op":"connect","id":"e","from":"a","to":"b","interaction_mode":"{{value}}"}]}
        """;
        ((Connect)ScenePatchJson.Deserialize(json).Operations.Single())
            .InteractionMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("vnet", BoundaryKind.Network)]
    [InlineData("subscription", BoundaryKind.CloudScope)]
    [InlineData("security zone", BoundaryKind.TrustZone)]
    public void Deserialize_ToleratesBoundaryKindSynonyms(string value, BoundaryKind expected)
    {
        var json = $$"""
        {"operations":[{"op":"group","id":"g","label":"G","node_ids":[],"boundary_kind":"{{value}}"}]}
        """;
        ((GroupOp)ScenePatchJson.Deserialize(json).Operations.Single())
            .BoundaryKind.Should().Be(expected);
    }
}
