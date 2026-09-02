namespace AudioBoarder.Services.LLM;

/// <summary>Strict operation-specific schema for the model-owned ScenePatch DSL.</summary>
public static class ScenePatchJsonSchema
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
                   "risk","metric","external","callout"]
        },
        "edgeKind": {
          "type": "string",
          "enum": ["flow","dependency","association","inheritance"]
        },
        "noteKind": {
          "type": "string",
          "enum": ["action_item","decision","question","risk","general"]
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
