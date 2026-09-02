using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Intent;
using AudioBoarder.Tests.Semantic;

namespace AudioBoarder.Tests.Intent;

public class DiagramIntentDetectorTests
{
    private readonly DiagramIntentDetector _detector = new();

    [Fact]
    public void ClassifiesAllSixtySemanticFixtures_WithHighMacroF1()
    {
        var cases = SemanticFixtureLoader.Load().Cases;
        var results = cases.Select(c =>
        {
            var expected = Enum.Parse<DiagramIntent>(c.Intent);
            var actual = _detector.Detect(Segments(c.Transcript, 3))?.Intent;
            return (expected, actual);
        }).ToArray();

        var perClassF1 = Enum.GetValues<DiagramIntent>().Select(intent =>
        {
            var tp = results.Count(x => x.expected == intent && x.actual == intent);
            var fp = results.Count(x => x.expected != intent && x.actual == intent);
            var fn = results.Count(x => x.expected == intent && x.actual != intent);
            var precision = tp / (double)Math.Max(1, tp + fp);
            var recall = tp / (double)Math.Max(1, tp + fn);
            return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }).ToArray();

        perClassF1.Average().Should().BeGreaterThanOrEqualTo(0.90);
        results.Should().OnlyContain(x => x.actual.HasValue);
    }

    [Fact]
    public void RequiresThreeSegmentsOrTwentySeconds()
    {
        const string text = "Tenant context uses row level security in the shared tenant database.";

        _detector.Detect(Segments(text, 2, TimeSpan.FromSeconds(4))).Should().BeNull();

        var afterThree = _detector.Detect(Segments(text, 3, TimeSpan.FromSeconds(4)));
        afterThree.Should().NotBeNull();
        afterThree!.Intent.Should().Be(DiagramIntent.SaaSMultiTenantArchitecture);

        var afterTwentySeconds = _detector.Detect(Segments(text, 1, TimeSpan.FromSeconds(21)));
        afterTwentySeconds.Should().NotBeNull();
    }

    [Fact]
    public void MixedEvidenceExposesBoundedLowerConfidence()
    {
        var mixed = Segments(
            "Tenant context enters a virtual network through a private endpoint and then reaches the shared tenant database with row level security.",
            3);

        var detection = _detector.Detect(mixed);

        detection.Should().NotBeNull();
        detection!.Confidence.Should().BeInRange(0.45, 0.99);
        detection.Evidence.Should().StartWith("Matched ");
        detection.Evidence.Length.Should().BeLessThan(160);
    }

    [Fact]
    public void GenericConversationDoesNotMeetLexicalEvidenceThreshold()
    {
        var generic = Segments(
            "We should think carefully about this topic and meet again after everyone reviews the information.",
            3);

        _detector.Detect(generic).Should().BeNull();
    }

    [Fact]
    public void ShortEvidenceTermsDoNotMatchInsideUnrelatedWords()
    {
        var collisions = Segments(
            "Capital planning prevents eventual overruns while the team reviews recapitalization.",
            3);

        _detector.Detect(collisions).Should().BeNull(
            "api and event must match normalized token boundaries, not capital/prevents/eventual");
    }

    [Fact]
    public void MultiwordEvidencePhrasesStillMatchAcrossNormalizedPunctuation()
    {
        var detection = _detector.Detect(Segments(
            "We enforce zero-trust with a policy engine and conditional access for every managed device.",
            3));

        detection.Should().NotBeNull();
        detection!.Intent.Should().Be(DiagramIntent.SecurityZeroTrustArchitecture);
    }

    [Fact]
    public void ManualPinWins_AndPopulatedSceneUsesSuggestionContract()
    {
        var coordinator = new DiagramIntentCoordinator(_detector);
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new AddNode("existing", NodeKind.Process, "Existing component"),
        ]));
        var tenantTranscript = Segments(
            "Tenant context flows through the tenant portal to the tenant API and shared tenant database with row level security.",
            3);

        coordinator.Evaluate(scene, tenantTranscript);
        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.SoftwareSystemArchitecture);
        scene.SuggestedIntentState!.AppliedIntent.Should().Be(DiagramIntent.SaaSMultiTenantArchitecture);

        coordinator.RejectSuggestion(scene).Should().BeTrue();
        scene.SuggestedIntentState.Should().BeNull();

        coordinator.Evaluate(scene, tenantTranscript);
        coordinator.ApplySuggestion(scene).Should().BeTrue();
        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.SaaSMultiTenantArchitecture);

        coordinator.Pin(scene, DiagramIntent.DiscussionSummary);
        coordinator.Evaluate(scene, tenantTranscript).Should().BeNull();
        scene.IntentState.SelectionMode.Should().Be(DiagramIntentSelectionMode.PinnedByUser);
        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.DiscussionSummary);
        scene.SuggestedIntentState.Should().BeNull();
    }

    [Fact]
    public void ReturningToAutoClearsPinAndSuggestionWithoutInvisibleSwitch()
    {
        var coordinator = new DiagramIntentCoordinator(_detector);
        var scene = new SceneGraph();
        coordinator.Pin(scene, DiagramIntent.SecurityZeroTrustArchitecture);

        coordinator.UseAuto(scene);

        scene.IntentState.SelectionMode.Should().Be(DiagramIntentSelectionMode.Auto);
        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.SecurityZeroTrustArchitecture);
        scene.IntentState.Confidence.Should().Be(0);
        scene.SuggestedIntentState.Should().BeNull();
    }

    [Fact]
    public void GenerationEpochChangesOnlyWhenAppliedIntentChanges()
    {
        var coordinator = new DiagramIntentCoordinator(_detector);
        var scene = new SceneGraph();
        var initialEpoch = scene.GenerationEpoch;
        var tenantTranscript = Segments(
            "Tenant context flows through the tenant portal to the tenant API and shared tenant database with row level security.",
            3);

        coordinator.Evaluate(scene, tenantTranscript);
        scene.GenerationEpoch.Should().Be(initialEpoch + 1);

        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new AddNode("existing", NodeKind.Process, "Existing"),
        ]));
        var beforeSuggestion = scene.GenerationEpoch;
        var networkTranscript = Segments(
            "The application gateway enters a virtual network through a private endpoint in the subnet.",
            3);
        coordinator.Evaluate(scene, networkTranscript);
        scene.GenerationEpoch.Should().Be(beforeSuggestion,
            "recording a suggestion does not change the applied intent");

        coordinator.ApplySuggestion(scene).Should().BeTrue();
        scene.GenerationEpoch.Should().Be(beforeSuggestion + 1);

        var beforeModeOnlyChange = scene.GenerationEpoch;
        coordinator.Pin(scene, DiagramIntent.CloudNetworkArchitecture);
        coordinator.UseAuto(scene);
        scene.GenerationEpoch.Should().Be(beforeModeOnlyChange,
            "pin/auto mode changes do not invalidate generation when the applied intent is unchanged");
    }

    private static IReadOnlyList<TranscriptSegment> Segments(
        string text,
        int count,
        TimeSpan? totalDuration = null)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var duration = totalDuration ?? TimeSpan.FromSeconds(count);
        var list = new List<TranscriptSegment>();
        for (var i = 0; i < count; i++)
        {
            var from = i * words.Length / count;
            var to = (i + 1) * words.Length / count;
            var segmentStart = start + TimeSpan.FromTicks(duration.Ticks * i / count);
            var segmentEnd = i == count - 1
                ? start + duration
                : start + TimeSpan.FromTicks(duration.Ticks * (i + 1) / count);
            list.Add(new TranscriptSegment(
                Guid.NewGuid(),
                TranscriptSpeaker.Remote,
                string.Join(' ', words[from..to]),
                segmentStart,
                segmentEnd));
        }
        return list;
    }
}
