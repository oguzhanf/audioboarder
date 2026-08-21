using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>
/// Serializer for the Excalidraw document model. Excalidraw expects camelCase
/// property names and omits null fields, so this is a separate options instance
/// from <c>ScenePatchJson</c> (which uses snake_case for the LLM DSL).
/// </summary>
public static class ExcalidrawJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        return opts;
    }

    public static string Serialize(ExcalidrawDocument document)
        => JsonSerializer.Serialize(document, Options);

    public static ExcalidrawDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Excalidraw JSON is empty", nameof(json));
        var doc = JsonSerializer.Deserialize<ExcalidrawDocument>(json, Options);
        if (doc is null)
            throw new InvalidOperationException("Excalidraw JSON deserialized to null");
        return doc;
    }
}
