using System.Text;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.LLM;

public static class ScenePromptComposer
{
    public const int MaximumContextSegments = 8;
    public const int MaximumContextSegmentCharacters = 300;

    public static string BuildSystemPrompt(
        AzureOpenAIOptions options,
        ScenePatchRequest request)
    {
        var shared = request.IsContinuous
            ? options.ContinuousSystemPrompt
            : options.SystemPrompt;
        var modeRules = request.Mode switch
        {
            GenerationMode.ContinuousExtraction =>
                "MODE: continuous extraction. Emit only high-confidence additions or enrichments grounded in the transcript delta. Do not emit destructive operations.",
            GenerationMode.DeepSynthesis =>
                "MODE: deep synthesis. Canonicalize and enrich the snapshot, merge duplicates, improve labels, metadata, and explicitly stated boundaries. Destructive operations may target unsupported provisional content only; the host enforces lifecycle.",
            GenerationMode.ManualRefine =>
                "MODE: manual deep refine. Apply the user's instruction using the selected intent while preserving all user-edited content. Destructive operations may target unsupported provisional content only; the host enforces lifecycle.",
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode)),
        };
        var products = MicrosoftComponentCatalog.RelevantPromptVocabulary(
            request.TranscriptWindow.Select(segment => segment.Text)
                .Concat(request.ConversationContext?.Select(segment => segment.Text) ?? [])
                .Concat(new[] { request.UserInstruction ?? "" }));
        var vocabulary = string.IsNullOrWhiteSpace(products) ? "" :
            "\n\nExplicitly mentioned Microsoft products (optional naming reference, not a list to add):\n" +
            products + "\nDo not replace generic or third-party components with these products.";
        return shared.Trim() + "\n\n" + modeRules + "\n\n" +
               DiagramIntentPromptProfiles.For(request.DiagramIntent, request.IsContinuous) + vocabulary +
               "\n\nOptional safe symbol names: " + IconRegistry.PromptVocabulary +
               "\nCapture explicit questions, answers, decisions and nonvisual requirements as typed notes; never invent responses. " +
               "Keep a question's text unchanged when an answer arrives: add a separate answer note instead of appending the answer to the question. " +
               "Use concept notes for supporting requirements; model the main ideas as concept nodes on the canvas, not only in the notes rail. " +
               "Use decision only for an explicitly agreed choice. " +
               "Reuse the original concept note ID when a requirement is restated or clarified; do not create a duplicate. " +
               "Never change an existing question note into an answer note.\n" +
               "MEETING MEMORY: A substantive spoken question requires note_upsert with kind=question and the question text, " +
               "even if its topic also has a canvas card. A spoken response requires a separate note_upsert with kind=answer. " +
               "Label a proposed answer as Proposed; do not treat it as an agreed decision. " +
               "Do not replace these Q&A notes with concept notes or generic status nodes. " +
               "Unresolved, proposed and confirmed are states of an idea, not separate nodes. " +
               "Leave source_timestamp null; the host supplies time.";
    }

    public static string BuildUserPrompt(ScenePatchRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Applied diagram intent: {request.DiagramIntent}");
        if (request.IntentState is { } state)
            sb.AppendLine($"selection={state.SelectionMode} confidence={state.Confidence:F3} reason={state.Reason}");
        sb.AppendLine("## Compact scene semantic index");
        sb.AppendLine(SceneSummariser.Summarise(request.CurrentScene));
        if (request.IsContinuous && request.ConversationContext is { Count: > 0 } context)
        {
            sb.AppendLine("## Untrusted prior conversation context");
            sb.AppendLine("Use only to resolve references and understand the new delta. Do not redraw or re-extract this older speech.");
            sb.AppendLine("<prior_context>");
            foreach (var segment in context.TakeLast(MaximumContextSegments))
            {
                var text = segment.Text.Length > MaximumContextSegmentCharacters
                    ? segment.Text[..MaximumContextSegmentCharacters] : segment.Text;
                sb.AppendLine($"- [{segment.Speaker}] {segment.Start:HH:mm:ss}: {text}");
            }
            sb.AppendLine("</prior_context>");
        }
        sb.AppendLine(request.IsContinuous
            ? "## Untrusted finalized transcript delta"
            : "## Untrusted finalized transcript context");
        sb.AppendLine("<transcript>");
        foreach (var segment in request.TranscriptWindow)
            sb.AppendLine($"- [{segment.Speaker}] {segment.Start:HH:mm:ss}: {segment.Text}");
        sb.AppendLine("</transcript>");
        if (!string.IsNullOrWhiteSpace(request.UserInstruction))
        {
            sb.AppendLine("## User instruction");
            sb.AppendLine(request.UserInstruction);
        }
        sb.AppendLine(request.IsContinuous
            ? "Return only valid ScenePatch JSON: {\"operations\":[...]}. Use at most 6 grounded operations."
            : $"Return only ScenePatch JSON; keep the total at or below {request.MaxNodes} nodes.");
        return sb.ToString();
    }
}

internal static class DiagramIntentPromptProfiles
{
    public static string For(DiagramIntent intent, bool compact) => intent switch
    {
        DiagramIntent.MeetingWhiteboard => """
            INTENT: adaptive meeting whiteboard. Let the meaning of the conversation
            determine its visual model; do not force an architecture or a mind map.
            Understand the whole discussion, then add only the new contribution.
            - Systems and architecture: draw actual named components and interactions,
              including generic, third-party, cloud and on-premises systems.
              Preserve explicit hosting ("the API runs on Kubernetes") as a group,
              dependency, or concise deployment detail. Do not make two disconnected
              cards when the statement describes a relationship.
            - Activities: model the stated steps, choices and dependencies.
            - Ideas, IT concepts and abstract thoughts: use concise concept cards,
              comparisons or related clusters. Important ideas belong on the canvas.
            - Disparate thoughts: separate cards with no edges or invented central hub.
            - Relationships: use association for a stated nondirectional relationship,
              flow for a sequence or interaction, dependency for an actual dependency.
              Mere co-mention, adjacency or a topic change does not establish a link.
              If A encourages B, use A and B as the node labels and "encourages" on
              the edge. Do not create another node named "A encourages B".
            - Topic changes: retain previous topics; add a separate island or group
              unless the new speech explains a connection. Enrich earlier IDs when
              a question, answer or correction clarifies them. Preserve uncertainty.
            Groups may organize an explicitly shared topic without asserting that one
            idea contains or causes another. Do not put unrelated things in one group.
            Product artwork is optional decoration after meaning is established.
            Do not invent vendors, services or relationships to make a diagram fuller.
            """,
        DiagramIntent.SoftwareSystemArchitecture => compact
            ? "INTENT: software-system architecture. Preserve named actors, components, APIs, stores, tiers and the directional request path."
            : """
              INTENT PROFILE — SOFTWARE SYSTEM ARCHITECTURE
              Draw concrete actors, applications, APIs, services, workers and stores
              inside stated system/environment/tier boundaries. Preserve numbered
              request paths, protocols, payloads and dependencies. A private endpoint
              is a resource; a security property is metadata. Prefer official product
              names and short descriptions of each element's role.
              """,
        DiagramIntent.SaaSMultiTenantArchitecture => compact
            ? "INTENT: SaaS multi-tenant architecture. Preserve tenant actors/context, shared-vs-dedicated planes, isolation boundaries and tenant-aware data flows."
            : """
              INTENT PROFILE — SAAS MULTI-TENANT ARCHITECTURE
              Show tenant users, tenant-aware entry points and services, control/data
              planes, shared versus dedicated resources, tenant context propagation,
              isolation enforcement and tenant data stores. Use tenant boundaries only
              when stated; capture row-level security, partition keys and authentication
              as edge metadata/descriptions rather than invented nodes.
              """,
        DiagramIntent.SecurityZeroTrustArchitecture => compact
            ? "INTENT: security zero-trust architecture. Preserve principals, devices, identity providers, policy enforcement, trust zones and authentication/authorization paths."
            : """
              INTENT PROFILE — SECURITY ZERO-TRUST ARCHITECTURE
              Show human/workload identities, managed devices, identity providers,
              policy decision and enforcement points, protected resources and external
              systems. Use Identity nodes and TrustZone boundaries. Label authentication,
              claims, authorization and token flows; record protocol, credential or
              classification when stated. Never turn abstract assurances into boxes.
              """,
        DiagramIntent.CloudNetworkArchitecture => compact
            ? "INTENT: cloud network architecture. Preserve cloud scopes, VNets/subnets, ingress/egress, endpoints, routes, controls and exact network flow."
            : """
              INTENT PROFILE — CLOUD NETWORK ARCHITECTURE
              Boundaries are the backbone: cloud scope > environment > virtual network
              > subnet/trust zone. Show independently addressable gateways, firewalls,
              private endpoints, DNS, routes and hosted resources. A PaaS service reached
              through private link remains outside the subnet while its private endpoint
              is inside. Capture CIDR/region as subtitles and protocol/authentication on
              edges only when stated.
              """,
        DiagramIntent.IntegrationDataFlowArchitecture => compact
            ? "INTENT: integration/data-flow architecture. Preserve producers, APIs, brokers, transforms, stores, payloads, protocols, modes and ordered hand-offs."
            : """
              INTENT PROFILE — INTEGRATION AND DATA-FLOW ARCHITECTURE
              Show producers/consumers, external systems, API gateways, integration
              services, queues/topics, transforms, batch jobs, streams and stores.
              Every hand-off states what moves and, when known, protocol, payload,
              classification, authentication and interaction mode. Preserve multiple
              semantically distinct flows between the same endpoints and number an
              explicitly ordered pipeline.
              """,
        DiagramIntent.DiscussionSummary => compact
            ? "INTENT: discussion summary. Model grounded topics, ideas, decisions, actions, risks and questions. Independent thoughts can be separate cards; do not invent a central hub."
            : """
              INTENT PROFILE — DISCUSSION SUMMARY
              Summarize grounded topics, actors/teams, decisions, options, actions,
              risks, questions, milestones and artifacts. Use notes for explicit
              commitments and concerns, and association/dependency edges for stated
              relationships. Do not force deployment or network structure. This is
              a topic-focused view; independent thoughts need no shared hub.
              """,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null),
    };
}
