using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBoarder.Core.Patch;

public static class ScenePatchJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        // Tolerant enum handling: the LLM often emits synonyms ("database",
        // "service", "depends"). Mapping them to the nearest valid value keeps a
        // single odd field from discarding the whole scene patch.
        opts.Converters.Add(new TolerantEnumConverterFactory());
        return opts;
    }

    public static string Serialize(ScenePatch patch)
        => JsonSerializer.Serialize(patch, Options);

    public static ScenePatch Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("ScenePatch JSON is empty", nameof(json));
        var patch = JsonSerializer.Deserialize<ScenePatch>(json, Options);
        if (patch is null)
            throw new InvalidOperationException("ScenePatch JSON deserialized to null");
        return patch;
    }
}
