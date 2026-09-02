using System.Text.Json.Nodes;

namespace AudioBoarder.Tests.Semantic;

public sealed class SemanticReleaseGateTests
{
    private readonly SemanticSceneEvaluator _evaluator = new();

    public static IEnumerable<object[]> Cases() =>
        SemanticFixtureLoader.Load().Cases.Select(testCase => new object[] { testCase });

    [Fact]
    public void Fixture_covers_all_intents_with_ten_cases_each()
    {
        var document = SemanticFixtureLoader.Load();

        document.SchemaVersion.Should().Be(1);
        document.Cases.Should().HaveCountGreaterThanOrEqualTo(60);
        document.Cases.GroupBy(testCase => testCase.Intent)
            .ToDictionary(group => group.Key, group => group.Count())
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["SoftwareSystemArchitecture"] = 10,
                ["SaaSMultiTenantArchitecture"] = 10,
                ["SecurityZeroTrustArchitecture"] = 10,
                ["CloudNetworkArchitecture"] = 10,
                ["IntegrationDataFlowArchitecture"] = 10,
                ["DiscussionSummary"] = 10,
            });
    }

    [Fact]
    public void Every_intent_covers_required_conversation_hazards()
    {
        var requiredVariants = new[]
        {
            "incomplete",
            "correction",
            "ambiguity",
            "repeated-names",
            "mixed-business-technical",
            "unsupported-fact",
            "prompt-injection",
        };

        foreach (var intentCases in SemanticFixtureLoader.Load().Cases.GroupBy(testCase => testCase.Intent))
        {
            foreach (var variant in requiredVariants)
                intentCases.Should().Contain(
                    testCase => testCase.Id.Contains(variant, StringComparison.Ordinal),
                    $"{intentCases.Key} must cover {variant}");

            intentCases.Should().OnlyContain(testCase =>
                testCase.Expected.RequiredNodes.Count > 0 &&
                testCase.Expected.OptionalNodes.Count > 0 &&
                testCase.Expected.ForbiddenNodes.Count > 0 &&
                testCase.Expected.Interactions.Count > 0 &&
                testCase.Expected.Groups.Count > 0);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Captured_responses_pass_offline_semantic_release_gate(SemanticGoldenCase testCase)
    {
        var result = _evaluator.Evaluate(testCase);

        result.Nodes.F1.Should().BeGreaterThanOrEqualTo(0.95, testCase.Id);
        result.NodeKindAccuracy.Should().BeGreaterThanOrEqualTo(0.95, testCase.Id);
        result.Edges.F1.Should().BeGreaterThanOrEqualTo(0.95, testCase.Id);
        result.EdgeDirectionAccuracy.Should().Be(1, testCase.Id);
        result.EdgeLabelTermCoverage.Should().BeGreaterThanOrEqualTo(0.90, testCase.Id);
        result.EdgeStepAccuracy.Should().Be(1, testCase.Id);
        result.Groups.F1.Should().BeGreaterThanOrEqualTo(0.95, testCase.Id);
        result.GroupMembershipAccuracy.Should().Be(1, testCase.Id);
        result.GroupNestingAccuracy.Should().Be(1, testCase.Id);
        result.Notes.F1.Should().BeGreaterThanOrEqualTo(0.90, testCase.Id);
        result.NoteKindAccuracy.Should().Be(1, testCase.Id);
        result.NoteOwnerAccuracy.Should().Be(1, testCase.Id);
        result.ForbiddenFacts.Should().BeEmpty(testCase.Id);
        result.HallucinatedFacts.Should().BeEmpty(testCase.Id);
        result.ParseRewrittenOperations.Should().Be(0, testCase.Id);
        result.ParseDroppedOperations.Should().Be(0, testCase.Id);
        result.ApplySkippedOperations.Should().Be(0, testCase.Id);
    }

    [Fact]
    public void Evaluator_catches_reversed_edges()
    {
        var testCase = SemanticFixtureLoader.Load().Cases.First();
        var patch = JsonNode.Parse(testCase.CapturedPatch.GetRawText())!.AsObject();
        var connection = patch["operations"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(operation => operation["op"]!.GetValue<string>() == "connect");
        var from = connection["from"]!.GetValue<string>();
        var to = connection["to"]!.GetValue<string>();
        connection["from"] = to;
        connection["to"] = from;

        var result = _evaluator.Evaluate(testCase, patch.ToJsonString());

        result.ReversedEdges.Should().Be(1);
        result.Edges.Recall.Should().BeLessThan(1);
        result.EdgeDirectionAccuracy.Should().BeLessThan(1);
    }

    [Fact]
    public void Evaluator_catches_missing_boundaries_and_membership()
    {
        var testCase = SemanticFixtureLoader.Load().Cases.First();
        var patch = JsonNode.Parse(testCase.CapturedPatch.GetRawText())!.AsObject();
        var operations = patch["operations"]!.AsArray();
        foreach (var group in operations
                     .Where(node => node!["op"]!.GetValue<string>() == "group")
                     .ToArray())
            operations.Remove(group);

        var result = _evaluator.Evaluate(testCase, patch.ToJsonString());

        result.Groups.Recall.Should().Be(0);
        result.GroupMembershipAccuracy.Should().BeLessThan(1);
    }

    [Fact]
    public void Evaluator_catches_forbidden_invented_facts()
    {
        var testCase = SemanticFixtureLoader.Load().Cases.First();
        var forbidden = testCase.Expected.ForbiddenNodes.First();
        var patch = JsonNode.Parse(testCase.CapturedPatch.GetRawText())!.AsObject();
        patch["operations"]!.AsArray().Add(new JsonObject
        {
            ["op"] = "add_node",
            ["id"] = "invented-forbidden-node",
            ["kind"] = "technology",
            ["label"] = forbidden.Label,
        });

        var result = _evaluator.Evaluate(testCase, patch.ToJsonString());

        result.ForbiddenFacts.Should().ContainSingle();
        result.HallucinatedFacts.Should().Contain(fact => fact.Contains(forbidden.Label));
        result.Nodes.Precision.Should().BeLessThan(1);
    }

    [Fact]
    public void Evaluator_reports_parser_repairs_and_applier_skips()
    {
        var testCase = SemanticFixtureLoader.Load().Cases.First();
        var raw = """
            {"operations":[
              {"op":"node_upsert","id":"only","kind":"actor","label":"Invented actor"},
              {"op":"teleport_node","id":"unknown"},
              {"op":"connect","id":"bad","from":"missing","to":"only","label":"calls"}
            ]}
            """;

        var result = _evaluator.Evaluate(testCase, raw);

        result.ParseRewrittenOperations.Should().Be(1);
        result.ParseDroppedOperations.Should().Be(1);
        result.ApplySkippedOperations.Should().Be(1);
    }
}
