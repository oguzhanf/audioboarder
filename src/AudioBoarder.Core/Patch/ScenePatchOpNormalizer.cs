using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioBoarder.Core.Patch;

/// <summary>
/// Normalises the polymorphic <c>op</c> discriminator before deserialisation.
///
/// <para>
/// System.Text.Json throws <see cref="JsonException"/> on the FIRST unrecognised
/// discriminator, which aborts the whole document — so one invented op name
/// silently discarded an entire scene patch and the diagram never moved. Observed
/// live: a model emitted <c>"op": "node_upsert"</c> instead of <c>add_node</c>
/// and every operation in that response was lost.
/// </para>
///
/// <para>
/// Enum values already get this treatment via <see cref="TolerantEnumConverterFactory"/>.
/// This closes the same gap for the operation discriminator: known synonyms are
/// rewritten, and anything still unrecognised is dropped so the remaining valid
/// operations survive.
/// </para>
/// </summary>
internal static class ScenePatchOpNormalizer
{
    /// <summary>Canonical discriminators declared by <see cref="ScenePatchOperation"/>.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "clear_scene", "add_node", "update_node", "delete_node", "connect", "disconnect",
        "relabel", "group", "ungroup", "note_upsert", "note_delete",
        "generate_image", "delete_image",
    };

    /// <summary>Plausible names models reach for, mapped to the canonical op.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_upsert"] = "add_node",
        ["upsert_node"] = "add_node",
        ["node"] = "add_node",
        ["add"] = "add_node",
        ["new_node"] = "add_node",
        ["create_node"] = "add_node",
        ["addnode"] = "add_node",

        ["update"] = "update_node",
        ["modify_node"] = "update_node",
        ["set_node"] = "update_node",
        ["node_update"] = "update_node",

        ["remove_node"] = "delete_node",
        ["del_node"] = "delete_node",
        ["node_delete"] = "delete_node",

        ["edge"] = "connect",
        ["add_edge"] = "connect",
        ["edge_upsert"] = "connect",
        ["link"] = "connect",
        ["connect_nodes"] = "connect",
        ["create_edge"] = "connect",

        ["remove_edge"] = "disconnect",
        ["delete_edge"] = "disconnect",
        ["unlink"] = "disconnect",

        ["rename"] = "relabel",
        ["set_label"] = "relabel",

        ["add_group"] = "group",
        ["group_nodes"] = "group",
        ["create_group"] = "group",
        ["group_upsert"] = "group",

        ["remove_group"] = "ungroup",
        ["delete_group"] = "ungroup",

        ["note"] = "note_upsert",
        ["add_note"] = "note_upsert",
        ["upsert_note"] = "note_upsert",
        ["create_note"] = "note_upsert",

        ["remove_note"] = "note_delete",
        ["delete_note"] = "note_delete",

        ["image"] = "generate_image",
        ["add_image"] = "generate_image",
        ["create_image"] = "generate_image",

        ["remove_image"] = "delete_image",

        ["clear"] = "clear_scene",
        ["reset"] = "clear_scene",
        ["clear_all"] = "clear_scene",
    };

    /// <summary>
    /// Rewrites recognised synonyms and removes operations that still cannot be
    /// mapped. Returns the original text unchanged when nothing needed fixing, so
    /// the common path costs one parse and no re-serialisation.
    /// </summary>
    public static string Normalize(string json, out int rewritten, out int dropped)
    {
        rewritten = 0;
        dropped = 0;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return json; }   // malformed: let the real deserializer report it

        if (root?["operations"] is not JsonArray ops) return json;

        var keep = new JsonArray();
        var changed = false;
        foreach (var node in ops.ToList())
        {
            if (node is not JsonObject obj) { dropped++; changed = true; continue; }

            var op = obj["op"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(op)) { dropped++; changed = true; continue; }

            if (Known.Contains(op))
            {
                keep.Add(obj.DeepClone());
                continue;
            }

            var normalized = Canonicalise(op);
            if (normalized is null) { dropped++; changed = true; continue; }

            obj["op"] = normalized;
            keep.Add(obj.DeepClone());
            rewritten++;
            changed = true;
        }

        if (!changed) return json;
        root!["operations"] = keep;
        return root.ToJsonString();
    }

    private static string? Canonicalise(string op)
    {
        if (Synonyms.TryGetValue(op, out var mapped)) return mapped;

        // Try again without separators so "addNode", "add-node" and "AddNode" all land.
        var compact = new string(op.Where(char.IsLetterOrDigit).ToArray());
        foreach (var (syn, target) in Synonyms)
            if (string.Equals(new string(syn.Where(char.IsLetterOrDigit).ToArray()), compact, StringComparison.OrdinalIgnoreCase))
                return target;
        foreach (var k in Known)
            if (string.Equals(new string(k.Where(char.IsLetterOrDigit).ToArray()), compact, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }
}
