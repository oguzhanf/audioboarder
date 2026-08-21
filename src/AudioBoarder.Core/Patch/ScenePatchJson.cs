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
        => Deserialize(json, out _);

    /// <summary>
    /// Deserializes a patch, reporting how much repair the model's output needed.
    /// </summary>
    public static ScenePatch Deserialize(string json, out ScenePatchParseInfo info)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("ScenePatch JSON is empty", nameof(json));

        // Repair the polymorphic discriminator first. System.Text.Json aborts the
        // whole document on the first unknown "op", so without this a single
        // invented name (e.g. "node_upsert" for "add_node") discards every
        // operation in the response and the diagram never updates.
        var repaired = ScenePatchOpNormalizer.Normalize(json, out var rewritten, out var dropped);
        info = new ScenePatchParseInfo(rewritten, dropped);

        var patch = JsonSerializer.Deserialize<ScenePatch>(repaired, Options);
        if (patch is null)
            throw new InvalidOperationException("ScenePatch JSON deserialized to null");
        return patch;
    }
}

/// <summary>
/// How much the model's raw output had to be corrected. Returned per-call rather
/// than held in static state so concurrent generations can't observe each other's
/// counts.
/// </summary>
public readonly record struct ScenePatchParseInfo(int RewrittenOps, int DroppedOps)
{
    public bool NeededRepair => RewrittenOps > 0 || DroppedOps > 0;
}
