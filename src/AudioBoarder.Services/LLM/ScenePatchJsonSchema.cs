namespace AudioBoarder.Services.LLM;

/// <summary>
/// JSON schema that mirrors <see cref="Core.Patch.ScenePatch"/>. Kept as a
/// hand-written constant so we can hand it to Azure OpenAI's strict JSON-schema
/// response format without dragging in a reflection-based generator.
/// </summary>
internal static class ScenePatchJsonSchema
{
    public static string Build() => Schema;

    private const string Schema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "operations": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "op": {
                "type": "string",
                "enum": [
                  "add_node", "update_node", "delete_node",
                  "connect", "disconnect", "relabel", "group", "ungroup",
                  "note_upsert", "note_delete",
                  "generate_image", "delete_image"
                ]
              },
              "id": { "type": "string" },
              "kind": { "type": "string" },
              "label": { "type": "string" },
              "icon": { "type": "string" },
              "description": { "type": "string" },
              "group_id": { "type": "string" },
              "from": { "type": "string" },
              "to": { "type": "string" },
              "node_ids": { "type": "array", "items": { "type": "string" } },
              "parent_group_id": { "type": "string" },
              "subtitle": { "type": "string" },
              "step": { "type": ["integer", "null"] },
              "text": { "type": "string" },
              "owner": { "type": "string" },
              "prompt": { "type": "string" },
              "attach_to_node_id": { "type": "string" },
              "position": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "kind": { "type": "string" },
                  "reference": { "type": "string" }
                },
                "required": ["kind", "reference"]
              },
              "source_timestamp": { "type": "string", "format": "date-time" }
            },
            "required": [
              "op", "id", "kind", "label", "icon", "description", "group_id", "from", "to",
              "node_ids", "parent_group_id", "subtitle", "step", "text", "owner", "prompt",
              "attach_to_node_id", "position", "source_timestamp"
            ]
          }
        }
      },
      "required": ["operations"]
    }
    """;
}
