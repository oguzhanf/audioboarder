using System.Text.RegularExpressions;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Services.Intent;

public sealed record DiagramIntentDetection(
    DiagramIntent Intent,
    double Confidence,
    string Evidence);

/// <summary>Deterministic, local intent classifier for finalized transcript text.</summary>
public sealed class DiagramIntentDetector
{
    private static readonly IReadOnlyDictionary<DiagramIntent, EvidenceTerm[]> Profiles =
        new Dictionary<DiagramIntent, EvidenceTerm[]>
        {
            [DiagramIntent.SoftwareSystemArchitecture] =
            [
                new("modular monolith", 6), new("application tier", 4), new("data tier", 4),
                new("web app", 3), new("api", 2), new("database", 2), new("component", 1),
                new("service", 1), new("request", 1),
            ],
            [DiagramIntent.SaaSMultiTenantArchitecture] =
            [
                new("row level security", 7), new("tenant context", 6),
                new("shared tenant database", 6), new("tenant portal", 5),
                new("tenant api", 5), new("tenant", 3), new("noisy neighbor", 4),
                new("isolation", 2), new("control plane", 2),
            ],
            [DiagramIntent.SecurityZeroTrustArchitecture] =
            [
                new("zero trust", 8), new("policy engine", 7), new("identity provider", 6),
                new("managed device", 5), new("identity claims", 5),
                new("conditional access", 5), new("trust zone", 4),
                new("authenticates", 3), new("authorizes", 3), new("least privilege", 3),
            ],
            [DiagramIntent.CloudNetworkArchitecture] =
            [
                new("application gateway", 7), new("private endpoint", 7),
                new("private link", 6), new("virtual network", 6), new("vnet", 5),
                new("subnet", 5), new("network security group", 5), new("nsg", 4),
                new("address range", 3), new("cidr", 3), new("ingress", 2),
            ],
            [DiagramIntent.IntegrationDataFlowArchitecture] =
            [
                new("message queue", 7), new("webhook", 6), new("integration service", 6),
                new("api management", 5), new("enqueues", 5), new("batch loads", 5),
                new("crm", 4), new("event", 3), new("payload", 3),
                new("asynchronous", 3), new("stream", 2),
            ],
            [DiagramIntent.DiscussionSummary] =
            [
                new("project team", 5), new("agreed", 4), new("action item", 5),
                new("open question", 5), new("migration delay", 5),
                new("migration runbook", 5), new("launch", 3), new("milestone", 3),
                new("decision", 3), new("risk", 3), new("owner", 2),
            ],
        };

    public DiagramIntentDetection? Detect(IReadOnlyList<TranscriptSegment> finalizedTranscript)
    {
        ArgumentNullException.ThrowIfNull(finalizedTranscript);
        var usable = finalizedTranscript
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .OrderBy(s => s.Start)
            .ToArray();
        if (usable.Length == 0) return null;

        var elapsed = usable.Max(s => s.End) - usable.Min(s => s.Start);
        if (usable.Length < 3 && elapsed < TimeSpan.FromSeconds(20)) return null;

        var text = Normalize(string.Join(' ', usable.Select(s => s.Text)));
        var scores = new Dictionary<DiagramIntent, double>();
        var matched = new Dictionary<DiagramIntent, List<string>>();
        foreach (var (intent, terms) in Profiles)
        {
            double score = 0;
            var evidence = new List<string>();
            foreach (var term in terms)
            {
                var count = CountOccurrences(text, term.Text);
                if (count == 0) continue;
                score += term.Weight * Math.Min(count, 2);
                evidence.Add(term.Text);
            }

            // Structural evidence is intentionally weak; lexical domain evidence
            // must still be present before a diagram type is selected.
            if (intent == DiagramIntent.IntegrationDataFlowArchitecture &&
                Regex.IsMatch(text, @"\b(sends?|passes?|enqueues?|loads?|publishes?)\b.*\b(to|into)\b"))
                score += 1.5;
            if (intent == DiagramIntent.CloudNetworkArchitecture &&
                Regex.IsMatch(text, @"\b(inside|through|within)\b.*\b(network|subnet|endpoint)\b"))
                score += 1.5;
            if (intent == DiagramIntent.DiscussionSummary &&
                Regex.IsMatch(text, @"\b(decided|agreed|requires?|mitigated|owner)\b"))
                score += 1.5;

            scores[intent] = score;
            matched[intent] = evidence;
        }

        var ranked = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToArray();
        var winner = ranked[0];
        var runnerUp = ranked[1].Value;
        if (winner.Value < 4 || matched[winner.Key].Count == 0) return null;

        var separation = (winner.Value - runnerUp) / Math.Max(1, winner.Value);
        var coverage = Math.Min(1, matched[winner.Key].Count / 4d);
        var confidence = Math.Clamp(0.45 + separation * 0.35 + coverage * 0.2, 0, 0.99);
        var evidenceText = string.Join(", ", matched[winner.Key].Take(3));
        return new DiagramIntentDetection(
            winner.Key,
            Math.Round(confidence, 3),
            $"Matched {evidenceText}");
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static int CountOccurrences(string text, string term)
    {
        var normalizedTerm = Normalize(term);
        if (normalizedTerm.Length == 0) return 0;

        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(normalizedTerm, offset, StringComparison.Ordinal)) >= 0)
        {
            var beforeBoundary = offset == 0 || text[offset - 1] == ' ';
            var after = offset + normalizedTerm.Length;
            var afterBoundary = after == text.Length || text[after] == ' ';
            if (beforeBoundary && afterBoundary) count++;
            offset += normalizedTerm.Length;
        }
        return count;
    }

    private sealed record EvidenceTerm(string Text, double Weight);
}
