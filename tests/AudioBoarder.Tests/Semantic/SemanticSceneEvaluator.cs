using System.Text;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Semantic;

public sealed class SemanticSceneEvaluator
{
    public SemanticEvaluation Evaluate(SemanticGoldenCase goldenCase, string? rawPatch = null)
    {
        var json = rawPatch ?? goldenCase.CapturedPatch.GetRawText();
        var patch = ScenePatchJson.Deserialize(json, out var parseInfo);
        var graph = new SceneGraph();
        var applyResult = new ScenePatchApplier().Apply(graph, patch);
        return EvaluateGraph(goldenCase, graph, parseInfo, applyResult);
    }

    private static SemanticEvaluation EvaluateGraph(
        SemanticGoldenCase goldenCase,
        SceneGraph graph,
        ScenePatchParseInfo parseInfo,
        ScenePatchResult applyResult)
    {
        var expected = goldenCase.Expected;
        var acceptedNodes = expected.RequiredNodes.Concat(expected.OptionalNodes).ToArray();
        var nodeMatches = MatchNodes(graph, acceptedNodes);
        var requiredNodeMatches = expected.RequiredNodes.Count(n => nodeMatches.ExpectedKeys.Contains(n.Key));
        var matchedNodes = nodeMatches.ActualToExpected.Count;
        var nodePrecision = Ratio(matchedNodes, graph.Nodes.Count);
        var nodeRecall = Ratio(requiredNodeMatches, expected.RequiredNodes.Count);
        var nodeKindAccuracy = Ratio(
            nodeMatches.ActualToExpected.Count(pair =>
                graph.Nodes[pair.Key].Kind == acceptedNodes.Single(n => n.Key == pair.Value).Kind),
            matchedNodes);

        var edgeResult = EvaluateEdges(graph, expected.Interactions, nodeMatches.ActualToExpected);
        var groupResult = EvaluateGroups(graph, expected.Groups, nodeMatches.ActualToExpected);
        var noteResult = EvaluateNotes(graph, expected.Notes);
        var forbidden = FindForbiddenFacts(
            graph, expected.ForbiddenNodes, expected.ForbiddenFacts, nodeMatches.ActualToExpected);
        var unmatchedFacts = nodeMatches.UnmatchedActualLabels
            .Concat(groupResult.UnmatchedActualLabels)
            .Concat(edgeResult.UnmatchedActualFacts)
            .Concat(noteResult.UnmatchedActualFacts)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new SemanticEvaluation(
            goldenCase.Id,
            new PrecisionRecallScore(nodePrecision, nodeRecall),
            nodeKindAccuracy,
            edgeResult.Score,
            edgeResult.DirectionAccuracy,
            edgeResult.KindAccuracy,
            edgeResult.LabelTermCoverage,
            edgeResult.StepAccuracy,
            edgeResult.ReversedEdges,
            groupResult.Score,
            groupResult.MembershipAccuracy,
            groupResult.NestingAccuracy,
            noteResult.Score,
            noteResult.KindAccuracy,
            noteResult.OwnerAccuracy,
            forbidden,
            unmatchedFacts,
            parseInfo.RewrittenOps,
            parseInfo.DroppedOps,
            applyResult.OperationsSkipped);
    }

    private static NodeMatchResult MatchNodes(SceneGraph graph, IReadOnlyList<NodeExpectation> expected)
    {
        var actualToExpected = new Dictionary<string, string>(StringComparer.Ordinal);
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = new List<string>();

        foreach (var node in graph.Nodes.Values)
        {
            var match = expected.FirstOrDefault(candidate =>
                Terms(candidate.Label, candidate.Aliases).Any(term => Same(term, node.Label)) &&
                !expectedKeys.Contains(candidate.Key));
            if (match is null)
            {
                unmatched.Add($"node:{node.Label}");
                continue;
            }

            actualToExpected[node.Id] = match.Key;
            expectedKeys.Add(match.Key);
        }

        return new NodeMatchResult(actualToExpected, expectedKeys, unmatched);
    }

    private static EdgeEvaluation EvaluateEdges(
        SceneGraph graph,
        IReadOnlyList<InteractionExpectation> expected,
        IReadOnlyDictionary<string, string> nodeKeys)
    {
        var unmatchedExpected = new HashSet<int>(Enumerable.Range(0, expected.Count));
        var matched = new List<(SceneEdge Actual, InteractionExpectation Expected)>();
        var reversed = 0;
        var unmatchedActual = new List<string>();

        foreach (var edge in graph.Edges.Values)
        {
            if (!nodeKeys.TryGetValue(edge.FromNodeId, out var from) ||
                !nodeKeys.TryGetValue(edge.ToNodeId, out var to))
            {
                unmatchedActual.Add($"edge:{edge.FromNodeId}->{edge.ToNodeId}:{edge.Label}");
                continue;
            }

            var exactIndex = unmatchedExpected.FirstOrDefault(
                index => expected[index].From == from && expected[index].To == to,
                -1);
            if (exactIndex >= 0)
            {
                matched.Add((edge, expected[exactIndex]));
                unmatchedExpected.Remove(exactIndex);
                continue;
            }

            if (expected.Any(e => e.From == to && e.To == from))
                reversed++;
            unmatchedActual.Add($"edge:{from}->{to}:{edge.Label}");
        }

        var labelTerms = matched.Sum(pair => pair.Expected.LabelTerms.Count);
        var coveredTerms = matched.Sum(pair => pair.Expected.LabelTerms.Count(
            term => Contains(pair.Actual.Label, term)));
        var stepped = matched.Count(pair => pair.Expected.Step.HasValue);
        var correctSteps = matched.Count(pair =>
            pair.Expected.Step.HasValue && pair.Actual.Step == pair.Expected.Step);

        return new EdgeEvaluation(
            new PrecisionRecallScore(Ratio(matched.Count, graph.Edges.Count), Ratio(matched.Count, expected.Count)),
            Ratio(matched.Count, expected.Count),
            Ratio(matched.Count(pair => pair.Actual.Kind == pair.Expected.Kind), matched.Count),
            Ratio(coveredTerms, labelTerms),
            Ratio(correctSteps, stepped),
            reversed,
            unmatchedActual);
    }

    private static GroupEvaluation EvaluateGroups(
        SceneGraph graph,
        IReadOnlyList<GroupExpectation> expected,
        IReadOnlyDictionary<string, string> nodeKeys)
    {
        var actualToExpected = new Dictionary<string, GroupExpectation>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = new List<string>();

        foreach (var group in graph.Groups.Values)
        {
            var match = expected.FirstOrDefault(candidate =>
                Terms(candidate.Label, candidate.Aliases).Any(term => Same(term, group.Label)) &&
                !usedKeys.Contains(candidate.Key));
            if (match is null)
            {
                unmatched.Add($"group:{group.Label}");
                continue;
            }

            actualToExpected[group.Id] = match;
            usedKeys.Add(match.Key);
        }

        var membershipChecks = expected.Sum(group => group.Members.Count);
        var membershipMatches = 0;
        var nestingChecks = expected.Count(group => group.Parent is not null);
        var nestingMatches = 0;
        foreach (var (actualGroupId, expectedGroup) in actualToExpected)
        {
            foreach (var member in expectedGroup.Members)
            {
                if (graph.Nodes.Values.Any(node =>
                    node.GroupId == actualGroupId &&
                    nodeKeys.TryGetValue(node.Id, out var key) &&
                    key == member))
                    membershipMatches++;
            }

            if (expectedGroup.Parent is null)
                continue;

            var actualParentId = graph.Groups[actualGroupId].ParentGroupId;
            if (actualParentId is not null &&
                actualToExpected.TryGetValue(actualParentId, out var parent) &&
                parent.Key == expectedGroup.Parent)
                nestingMatches++;
        }

        return new GroupEvaluation(
            new PrecisionRecallScore(
                Ratio(actualToExpected.Count, graph.Groups.Count),
                Ratio(actualToExpected.Count, expected.Count)),
            Ratio(membershipMatches, membershipChecks),
            Ratio(nestingMatches, nestingChecks),
            unmatched);
    }

    private static NoteEvaluation EvaluateNotes(SceneGraph graph, IReadOnlyList<NoteExpectation> expected)
    {
        var remaining = new HashSet<int>(Enumerable.Range(0, expected.Count));
        var matches = new List<(SceneNote Actual, NoteExpectation Expected)>();
        var unmatched = new List<string>();

        foreach (var note in graph.Notes.Values)
        {
            var matchIndex = remaining.FirstOrDefault(
                index => expected[index].TextTerms.All(term => Contains(note.Text, term)),
                -1);
            if (matchIndex < 0)
            {
                unmatched.Add($"note:{note.Text}");
                continue;
            }
            matches.Add((note, expected[matchIndex]));
            remaining.Remove(matchIndex);
        }

        var ownerChecks = matches.Count(pair => pair.Expected.Owner is not null);
        return new NoteEvaluation(
            new PrecisionRecallScore(Ratio(matches.Count, graph.Notes.Count), Ratio(matches.Count, expected.Count)),
            Ratio(matches.Count(pair => pair.Actual.Kind == pair.Expected.Kind), matches.Count),
            Ratio(matches.Count(pair =>
                pair.Expected.Owner is not null && Same(pair.Actual.Owner, pair.Expected.Owner)), ownerChecks),
            unmatched);
    }

    private static IReadOnlyList<string> FindForbiddenFacts(
        SceneGraph graph,
        IReadOnlyList<ForbiddenNodeExpectation> forbiddenNodes,
        IReadOnlyList<ForbiddenFactExpectation> forbidden,
        IReadOnlyDictionary<string, string> nodeKeys)
    {
        var found = forbiddenNodes
            .Where(candidate => graph.Nodes.Values.Any(node =>
                Terms(candidate.Label, candidate.Aliases).Any(term => Contains(node.Label, term))))
            .Select(candidate => $"node:{candidate.Label}")
            .ToList();
        foreach (var fact in forbidden)
        {
            var present = fact.Kind.ToLowerInvariant() switch
            {
                "node" => graph.Nodes.Values.Any(n => Contains(n.Label, fact.Term)),
                "group" => graph.Groups.Values.Any(g => Contains(g.Label, fact.Term)),
                "note" => graph.Notes.Values.Any(n => Contains(n.Text, fact.Term)),
                "edge" => graph.Edges.Values.Any(e =>
                    Contains(e.Label, fact.Term) &&
                    (fact.From is null ||
                     nodeKeys.TryGetValue(e.FromNodeId, out var from) && from == fact.From) &&
                    (fact.To is null ||
                     nodeKeys.TryGetValue(e.ToNodeId, out var to) && to == fact.To)),
                _ => throw new InvalidOperationException($"Unknown forbidden fact kind '{fact.Kind}'."),
            };
            if (present)
                found.Add($"{fact.Kind}:{fact.Term}");
        }
        return found;
    }

    private static IEnumerable<string> Terms(string label, IReadOnlyList<string> aliases)
        => aliases.Prepend(label);

    internal static bool Same(string? left, string? right) => Normalize(left) == Normalize(right);

    internal static bool Contains(string? text, string? term)
    {
        var normalizedTerm = Normalize(term);
        return normalizedTerm.Length > 0 && Normalize(text).Contains(normalizedTerm, StringComparison.Ordinal);
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        return string.Join(' ', builder.ToString().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static double Ratio(int numerator, int denominator)
        => denominator == 0 ? 1d : (double)numerator / denominator;

    private sealed record NodeMatchResult(
        IReadOnlyDictionary<string, string> ActualToExpected,
        IReadOnlySet<string> ExpectedKeys,
        IReadOnlyList<string> UnmatchedActualLabels);

    private sealed record EdgeEvaluation(
        PrecisionRecallScore Score,
        double DirectionAccuracy,
        double KindAccuracy,
        double LabelTermCoverage,
        double StepAccuracy,
        int ReversedEdges,
        IReadOnlyList<string> UnmatchedActualFacts);

    private sealed record GroupEvaluation(
        PrecisionRecallScore Score,
        double MembershipAccuracy,
        double NestingAccuracy,
        IReadOnlyList<string> UnmatchedActualLabels);

    private sealed record NoteEvaluation(
        PrecisionRecallScore Score,
        double KindAccuracy,
        double OwnerAccuracy,
        IReadOnlyList<string> UnmatchedActualFacts);
}

public sealed record PrecisionRecallScore(double Precision, double Recall)
{
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed record SemanticEvaluation(
    string CaseId,
    PrecisionRecallScore Nodes,
    double NodeKindAccuracy,
    PrecisionRecallScore Edges,
    double EdgeDirectionAccuracy,
    double EdgeKindAccuracy,
    double EdgeLabelTermCoverage,
    double EdgeStepAccuracy,
    int ReversedEdges,
    PrecisionRecallScore Groups,
    double GroupMembershipAccuracy,
    double GroupNestingAccuracy,
    PrecisionRecallScore Notes,
    double NoteKindAccuracy,
    double NoteOwnerAccuracy,
    IReadOnlyList<string> ForbiddenFacts,
    IReadOnlyList<string> HallucinatedFacts,
    int ParseRewrittenOperations,
    int ParseDroppedOperations,
    int ApplySkippedOperations);
