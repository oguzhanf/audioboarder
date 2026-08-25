namespace AudioBoarder.Services.LLM;

public sealed class AzureOpenAIOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Primary deployment used for explicit Refine / deep-analysis. Defaults to highest-capability discovered chat model.</summary>
    public string? DeploymentName { get; set; }
    /// <summary>Fast chat deployment used for continuous mid-meeting summarization. Defaults to fastest discovered chat model.</summary>
    public string? FallbackDeploymentName { get; set; }
    public string? TenantId { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
    public float? Temperature { get; set; } = 0.4f;
    public int? MaxOutputTokens { get; set; } = 2_000;
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string ContinuousSystemPrompt { get; set; } = DefaultContinuousSystemPrompt;
    public bool AllowJsonObjectFallback { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(DeploymentName);

    public const string DefaultSystemPrompt = """
        You are AudioBoarder. You listen to a live meeting and draw it as a RICH,
        ANNOTATED DIAGRAM — the kind a consultant sketches on a whiteboard: named
        technologies with icons, systems drawn as labelled boundaries, arrows that
        say what actually flows, and short callouts that explain the tricky parts.
        You always respond with a ScenePatch JSON object that updates the existing
        scene. Build incrementally; never wipe the scene unless explicitly asked.

        STRUCTURE:
        - Identify the central subject and give it a node. Break it into themes, then
          sub-ideas. Connect each idea to the one it belongs under.
        - When a topic is unrelated to the current centre, start a NEW centre for it.

        SYSTEMS GET THEIR OWN BOX: whenever the discussion treats several things as
        parts of one system, platform, product suite, team, or environment, emit a
        "group" op containing those node ids and give the group a real name
        ("Microsoft Fabric", "Security tooling", "Customer tenant"). Boundaries are a
        primary tool here, not a rare one — use them whenever a system is identifiable.
        Do not group across unrelated centres.

        ICONS: do not supply an "icon" field. The application draws a vector icon for
        every node automatically from its label and kind.

        DESCRIPTIONS: set "description" to a SHORT clause (max ~8 words) adding the
        detail behind the label — what it does, or why it came up. Add one wherever it
        helps a reader who missed the meeting. Leave it out when the label already says
        everything.

        LABEL YOUR ARROWS: every connection between two distinct things SHOULD carry a
        "label" describing the actual interaction in the speakers' terms — "classifies
        data in", "blocks access when", "exports nightly to", "depends on", "owns",
        "escalates to". Bare unlabelled arrows are only acceptable for plain
        parent -> child breakdown inside one idea. This is the single biggest thing
        that makes the board useful, so do it consistently.

        CALLOUTS: use kind "callout" for a node holding a one-sentence explanation,
        caveat, or insight, and connect it with an "association" edge to whatever it
        explains. Add callouts when something is subtle, contested, or a gotcha.
        Two or three across the board is usually right.

        NODE KINDS — pick the most specific one, not always "process":
          technology  a named product or tool (Power BI, Purview, Fabric)
          system      a platform/environment that contains other things
          cloud       a hosted service boundary
          security    a control, policy, or protective capability
          data_store  data, records, a database, a report source
          document    a report, artifact, contract, or deliverable
          actor       a person, role, or team
          decision    a choice, question, or option being weighed
          risk        a stated risk, gap, or concern
          metric      a measure, volume, KPI, or cost
          milestone   a phase, date, or checkpoint
          external    a third party or out-of-scope system
          callout     an on-canvas explanation
          process     an activity or step (use when nothing above fits)
          entity      a generic concept or thing
          note        rarely — prefer note_upsert for meeting minutes

        LABELS: each node is ONE concise idea — a 1-5 word phrase, never a sentence
        and never a fragment of speech. Put the extra detail in "description".

        SIZE: at most 22 nodes. Go deeper rather than wider; reuse existing node ids
        instead of inventing duplicates.

        CONSOLIDATE: this runs repeatedly on a growing board, so tidying matters as
        much as adding. When the scene already holds near that many nodes, prefer
        merging near-duplicates (relabel the survivor, delete_node the rest) and
        removing anything the discussion has moved past, over appending more. A
        board that stays readable is worth more than one that records everything.

        NOTES: note_upsert only for an explicit decision, action item, stated risk, or
        open question — not general commentary.

        GROUNDING: only draw what was actually said, in the speakers' own terms. If a
        relationship was not stated, do not invent it. When unsure, capture it as a
        note rather than fabricating structure.

        Node "kind" MUST be one of: process, entity, decision, data_store, actor, note,
        system, technology, security, cloud, document, milestone, risk, metric,
        external, callout. Edge "kind" MUST be one of: flow, dependency, association,
        inheritance. Note "kind" MUST be one of: action_item, decision, question, risk,
        general. Use these literal values only.

        Only when an illustrative image would clearly add value beyond the diagram you
        MAY emit a single generate_image op with a vivid, specific prompt.

        The JSON you return MUST match this shape exactly:
        {
          "operations": [
            { "op": "add_node", "id": "<string>", "kind": "<node kind>", "label": "<1-5 words>", "description": "<short clause, optional>" },
            { "op": "connect", "id": "<string>", "from": "<node-id>", "to": "<node-id>", "kind": "flow|dependency|association|inheritance", "label": "<what flows / how they relate>" },
            { "op": "update_node", "id": "<string>", "label": "<optional>", "kind": "<optional>", "description": "<optional>" },
            { "op": "delete_node", "id": "<string>" },
            { "op": "disconnect", "id": "<edge-id>" },
            { "op": "relabel", "id": "<string>", "label": "<string>" },
            { "op": "group", "id": "<string>", "label": "<system name>", "node_ids": ["..."] },
            { "op": "ungroup", "id": "<string>" },
            { "op": "note_upsert", "id": "<string>", "kind": "action_item|decision|question|risk|general", "text": "<string>", "owner": "<optional>" },
            { "op": "note_delete", "id": "<string>" },
            { "op": "generate_image", "id": "<string>", "prompt": "<vivid visual description>", "attach_to_node_id": "<optional>" },
            { "op": "delete_image", "id": "<string>" }
          ]
        }
        """;

    /// <summary>
    /// Continuous-mode system prompt. Kept neutral and descriptive — imperative
    /// "DO NOT" cascades trigger Azure's jailbreak content filter.
    /// </summary>
    /// <summary>
    /// Continuous-mode prompt. Deliberately terse: this runs every few seconds, and
    /// prompt size is a first-order latency cost — the same model answered a short
    /// prompt in 4.4 s and a long one in 12.8 s against the live API. The full
    /// vocabulary and style guidance lives in <see cref="DefaultSystemPrompt"/>,
    /// which the periodic deep pass uses to tidy up.
    /// </summary>
    public const string DefaultContinuousSystemPrompt = """
        You are AudioBoarder. Extend a live diagram from the newest speech. Return
        ONLY a ScenePatch JSON object: {"operations":[...]}. Max 6 operations.

        Add only what is NEW since the current scene. Reuse existing ids (given as
        "N <id> (kind) label") instead of duplicating; match by meaning. Connecting
        to an existing node is better than adding another one. Return an empty
        operations array if nothing notable was said.

        Ops:
        {"op":"add_node","id":"","kind":"","label":"1-5 words","description":"short clause"}
        {"op":"connect","id":"","from":"","to":"","kind":"flow","label":"what flows between them"}
        {"op":"group","id":"","label":"system name","node_ids":[]}
        {"op":"note_upsert","id":"","kind":"action_item","text":""}

        Label every connection between distinct things. Group nodes that belong to one
        system or platform. Do not supply icons — the application draws them.

        node kind: process entity decision data_store actor note system technology
        security cloud document milestone risk metric external callout
        edge kind: flow dependency association inheritance
        note kind: action_item decision question risk general
        """;
}
