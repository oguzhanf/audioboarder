using System.Text.Json.Nodes;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.LLM;

/// <summary>Strict operation-specific schema for the model-owned ScenePatch DSL.</summary>
public static class ScenePatchJsonSchema
{
    public static string Build() => Schema;
    private static readonly string StructuredOutputSchema = MakeStructuredOutputSchema();
    public static string BuildForStructuredOutput() => StructuredOutputSchema;

    private static string MakeStructuredOutputSchema()
    {
        var schema = JsonNode.Parse(Schema)!;
        schema["$defs"]!["iconName"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(IconRegistry.Names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
        };
        foreach (var operation in schema["properties"]!["operations"]!["items"]!["oneOf"]!.AsArray())
        {
            if (operation?["properties"] is JsonObject properties && properties.ContainsKey("icon"))
                properties["icon"] = new JsonObject { ["$ref"] = "#/$defs/iconName" };
        }
        Normalize(schema);
        return schema.ToJsonString();
    }

    private static void Normalize(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child is not null) Normalize(child);
            return;
        }
        if (node is not JsonObject obj) return;
        var operation = obj["properties"]?["op"]?["const"]?.GetValue<string>();
        if (obj.Remove("oneOf", out var union)) obj["anyOf"] = union;
        if (obj.Remove("const", out var constant))
        {
            obj["type"] = "string";
            obj["enum"] = new JsonArray(constant);
        }
        if (obj["properties"] is JsonObject properties)
        {
            var required = (obj["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
            foreach (var property in properties.ToArray())
            {
                if (property.Value is null) continue;
                Normalize(property.Value);
                var nonNullableDefault = (operation == "connect" && property.Key == "kind") ||
                                         (operation == "group" && property.Key == "boundary_kind");
                if (!required.Contains(property.Key) && !nonNullableDefault)
                {
                    var original = property.Value.DeepClone();
                    properties[property.Key] = new JsonObject
                    {
                        ["anyOf"] = new JsonArray(original, new JsonObject { ["type"] = "null" }),
                    };
                }
            }
            obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        }
        foreach (var child in obj.Where(p => p.Key != "properties").Select(p => p.Value).ToArray())
            if (child is not null) Normalize(child);
    }

    private const string Schema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "operations": {
          "type": "array",
          "items": {
            "oneOf": [
              {
                "type": "object", "additionalProperties": false,
                "properties": { "op": { "const": "clear_scene" } },
                "required": ["op"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "add_node" },
                  "id": { "type": "string" },
                  "kind": { "$ref": "#/$defs/nodeKind" },
                  "label": { "type": "string" },
                  "group_id": { "type": "string" },
                  "position": { "$ref": "#/$defs/position" },
                  "icon": { "type": "string" },
                  "description": { "type": "string" }
                },
                "required": ["op", "id", "kind", "label"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "update_node" },
                  "id": { "type": "string" },
                  "kind": { "$ref": "#/$defs/nodeKind" },
                  "label": { "type": "string" },
                  "group_id": { "type": "string" },
                  "position": { "$ref": "#/$defs/position" },
                  "icon": { "type": "string" },
                  "description": { "type": "string" }
                },
                "required": ["op", "id"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "delete_node" },
                  "id": { "type": "string" }
                },
                "required": ["op", "id"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "connect" },
                  "id": { "type": "string" },
                  "from": { "type": "string" },
                  "to": { "type": "string" },
                  "kind": { "$ref": "#/$defs/edgeKind" },
                  "label": { "type": "string" },
                  "step": { "type": ["integer", "null"] },
                  "protocol": { "type": "string" },
                  "payload": { "type": "string" },
                  "data_classification": { "type": "string" },
                  "authentication": { "type": "string" },
                  "interaction_mode": { "$ref": "#/$defs/interactionMode" }
                },
                "required": ["op", "id", "from", "to"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "disconnect" },
                  "id": { "type": "string" }
                },
                "required": ["op", "id"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "relabel" },
                  "id": { "type": "string" },
                  "label": { "type": "string" }
                },
                "required": ["op", "id", "label"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "group" },
                  "id": { "type": "string" },
                  "label": { "type": "string" },
                  "node_ids": { "type": "array", "items": { "type": "string" } },
                  "parent_group_id": { "type": "string" },
                  "subtitle": { "type": "string" },
                  "boundary_kind": { "$ref": "#/$defs/boundaryKind" }
                },
                "required": ["op", "id", "label", "node_ids"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "ungroup" },
                  "id": { "type": "string" }
                },
                "required": ["op", "id"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "note_upsert" },
                  "id": { "type": "string" },
                  "kind": { "$ref": "#/$defs/noteKind" },
                  "text": { "type": "string" },
                  "owner": { "type": "string" },
                  "source_timestamp": { "type": "string", "format": "date-time" }
                },
                "required": ["op", "id", "kind", "text"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "note_delete" },
                  "id": { "type": "string" }
                },
                "required": ["op", "id"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "generate_image" },
                  "id": { "type": "string" },
                  "prompt": { "type": "string" },
                  "attach_to_node_id": { "type": "string" }
                },
                "required": ["op", "id", "prompt"]
              },
              {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "op": { "const": "delete_image" },
                  "id": { "type": "string" }
                },
                "required": ["op", "id"]
              }
            ]
          }
        }
      },
      "required": ["operations"],
      "$defs": {
        "nodeKind": {
          "type": "string",
          "enum": ["process","entity","decision","data_store","actor","note","system",
                   "technology","security","identity","cloud","document","milestone",
                   "risk","metric","external","callout","concept"]
        },
        "edgeKind": {
          "type": "string",
          "enum": ["flow","dependency","association","inheritance"]
        },
        "noteKind": {
          "type": "string",
          "enum": ["action_item","decision","question","risk","general","answer","concept"]
        },
        "boundaryKind": {
          "type": "string",
          "enum": ["generic","system","environment","tenant","network","trust_zone","cloud_scope","external"]
        },
        "interactionMode": {
          "type": "string",
          "enum": ["synchronous","asynchronous","batch","stream"]
        },
        "position": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["auto","above","below","left_of","right_of","near","inside_group"]
            },
            "reference": { "type": "string" }
          },
          "required": ["kind"]
        }
      }
    }
    """;
}
