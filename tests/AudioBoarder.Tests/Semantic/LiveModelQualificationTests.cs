using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Semantic;

[Trait("Category", "LiveModel")]
public sealed class LiveModelQualificationTests
{
    [LiveModelFact]
    public async Task Configured_model_meets_semantic_thresholds()
    {
        var requiredVariables = new[]
        {
            "AUDIOBOARDER_LIVE_ENDPOINT",
            "AUDIOBOARDER_LIVE_DEPLOYMENT",
            "AUDIOBOARDER_LIVE_API_KEY",
        };
        var missing = requiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Live model qualification requires nonempty configuration: " +
                string.Join(", ", missing));
        }

        var options = Options.Create(new AzureOpenAIOptions
        {
            Endpoint = Environment.GetEnvironmentVariable("AUDIOBOARDER_LIVE_ENDPOINT"),
            DeploymentName = Environment.GetEnvironmentVariable("AUDIOBOARDER_LIVE_DEPLOYMENT"),
            ApiKey = Environment.GetEnvironmentVariable("AUDIOBOARDER_LIVE_API_KEY"),
            UseManagedIdentity = false,
            Temperature = 0,
        });
        var chat = new AzureOpenAIScenePatchGenerator(options);
        var responses = new AzureOpenAIResponsesGenerator(options);
        var generator = new SmartScenePatchGenerator(
            options, chat, responses, NullLogger<SmartScenePatchGenerator>.Instance);
        var evaluator = new SemanticSceneEvaluator();
        var cases = SelectCases();
        cases.Should().NotBeEmpty("AUDIOBOARDER_LIVE_CASES must select at least one known fixture ID.");
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var request = new ScenePatchRequest(
                new SceneGraph(),
                [new TranscriptSegment(Guid.Empty, TranscriptSpeaker.Remote, testCase.Transcript, start, start.AddMinutes(1))],
                MaxNodes: 60);
            var response = await generator.GenerateAsync(request, CancellationToken.None);
            var result = evaluator.Evaluate(
                testCase, response.RawJson ?? ScenePatchJson.Serialize(response.Patch));
            if (result.Nodes.F1 < 0.80 ||
                result.Edges.F1 < 0.75 ||
                result.Groups.Recall < 0.80 ||
                result.ForbiddenFacts.Count > 0 ||
                result.ParseDroppedOperations > 0)
            {
                failures.Add(
                    $"{testCase.Id}: nodes={result.Nodes.F1:F2}, edges={result.Edges.F1:F2}, " +
                    $"groups={result.Groups.Recall:F2}, forbidden={result.ForbiddenFacts.Count}, " +
                    $"dropped={result.ParseDroppedOperations}");
            }
        }

        failures.Should().BeEmpty("live qualification failures:\n" + string.Join('\n', failures));
    }

    private static IReadOnlyList<SemanticGoldenCase> SelectCases()
    {
        var all = SemanticFixtureLoader.Load().Cases;
        var requested = Environment.GetEnvironmentVariable("AUDIOBOARDER_LIVE_CASES");
        if (string.IsNullOrWhiteSpace(requested) || requested == "*")
            return all;

        var ids = requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(testCase => ids.Contains(testCase.Id)).ToArray();
    }
}

public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute()
    {
        var liveRequired = bool.TryParse(
            Environment.GetEnvironmentVariable("AUDIOBOARDER_LIVE_REQUIRED"),
            out var requiredValue) && requiredValue;
        var required = new[]
        {
            "AUDIOBOARDER_LIVE_ENDPOINT",
            "AUDIOBOARDER_LIVE_DEPLOYMENT",
            "AUDIOBOARDER_LIVE_API_KEY",
        };
        var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));
        if (missing.Any() && !liveRequired)
            Skip = "Live model qualification requires explicit AUDIOBOARDER_LIVE_* environment variables.";
    }
}
