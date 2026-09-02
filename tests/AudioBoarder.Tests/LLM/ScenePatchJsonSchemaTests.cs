using System.Text.Json;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

public class ScenePatchJsonSchemaTests
{
    [Fact]
    public void UsesOperationSpecificOneOf_WithAllSupportedOperations()
    {
        using var document = JsonDocument.Parse(ScenePatchJsonSchema.Build());
        var branches = document.RootElement
            .GetProperty("properties")
            .GetProperty("operations")
            .GetProperty("items")
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        var ops = branches.Select(branch =>
            branch.GetProperty("properties").GetProperty("op").GetProperty("const").GetString())
            .ToArray();

        ops.Should().BeEquivalentTo(
            "clear_scene", "add_node", "update_node", "delete_node",
            "connect", "disconnect", "relabel", "group", "ungroup",
            "note_upsert", "note_delete", "generate_image", "delete_image");
        var add = branches.Single(branch =>
            branch.GetProperty("properties").GetProperty("op").GetProperty("const").GetString() == "add_node");
        add.GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo("op", "id", "kind", "label");
        var connect = branches.Single(branch =>
            branch.GetProperty("properties").GetProperty("op").GetProperty("const").GetString() == "connect");
        connect.GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo("op", "id", "from", "to");
    }

    [Fact]
    public void SchemaContainsRealSemanticEnums_AndExcludesHostLifecycle()
    {
        var schema = ScenePatchJsonSchema.Build();

        schema.Should().Contain("\"identity\"");
        schema.Should().Contain("\"trust_zone\"");
        schema.Should().Contain("\"cloud_scope\"");
        schema.Should().Contain("\"asynchronous\"");
        schema.Should().Contain("\"data_classification\"");
        schema.Should().Contain("\"authentication\"");
        schema.Should().NotContain("lifecycle");
    }
}
