using System.Text;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.LLM;

public static class ScenePromptComposer
{
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
        return shared.Trim() + "\n\n" + modeRules + "\n\n" +
               DiagramIntentPromptProfiles.For(request.DiagramIntent, request.IsContinuous);
    }

    public static string BuildUserPrompt(ScenePatchRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Applied diagram intent: {request.DiagramIntent}");
        if (request.IntentState is { } state)
            sb.AppendLine($"selection={state.SelectionMode} confidence={state.Confidence:F3} reason={state.Reason}");
        sb.AppendLine("## Compact scene semantic index");
        sb.AppendLine(SceneSummariser.Summarise(request.CurrentScene));
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
            ? "Return only the smallest grounded ScenePatch, maximum 6 operations."
            : $"Return only ScenePatch JSON; keep the total at or below {request.MaxNodes} nodes.");
        return sb.ToString();
    }
}

internal static class DiagramIntentPromptProfiles
{
    public static string For(DiagramIntent intent, bool compact) => intent switch
    {
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
            ? "INTENT: discussion summary. Organize grounded topics, decisions, actions, risks, questions, owners and milestones; this is the only intent eligible for later mind-map behavior."
            : """
              INTENT PROFILE — DISCUSSION SUMMARY
              Summarize grounded topics, actors/teams, decisions, options, actions,
              risks, questions, milestones and artifacts. Use notes for explicit
              commitments and concerns, and association/dependency edges for stated
              relationships. Do not force deployment or network structure. This is the
              only approved intent that may map to mind-map behavior in a later phase.
              """,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null),
    };
}
