using System.Text.Json.Nodes;
using AudioBoarder.Services.LLM;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.LLM;

public sealed class StructuredOutputSchemaTests
{
    [Fact]
    public void AzureStrictSchemaUsesAnyOfAndRequiresEveryDeclaredProperty()
    {
        var schema = ScenePatchJsonSchema.BuildForStructuredOutput();
        schema.Should().NotContain("\"oneOf\"").And.NotContain("\"const\"");
        Inspect(JsonNode.Parse(schema)!);
    }

    [Fact]
    public void ModelSymbolsAreConstrainedToTheBundledRegistryAndConceptsAreSupported()
    {
        var schema = JsonNode.Parse(ScenePatchJsonSchema.BuildForStructuredOutput())!;
        schema["$defs"]!["iconName"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>()).Should().BeEquivalentTo(IconRegistry.Names);
        schema["$defs"]!["nodeKind"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>()).Should().Contain("concept");
        var nodeOperations = schema["properties"]!["operations"]!["items"]!["anyOf"]!.AsArray()
            .Where(operation => operation?["properties"]?["icon"] is not null).ToArray();
        nodeOperations.Should().HaveCount(2);
        nodeOperations.Should().OnlyContain(operation =>
            operation!["properties"]!["icon"]!.ToJsonString().Contains("#/$defs/iconName"));
    }

    private static void Inspect(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
            {
                var required = obj["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                required.Should().BeEquivalentTo(properties.Select(p => p.Key));
                obj["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
            }
            foreach (var value in obj.Select(p => p.Value)) if (value is not null) Inspect(value);
        }
        else if (node is JsonArray array)
            foreach (var value in array) if (value is not null) Inspect(value);
    }
}
