using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Tests.Transcript;

/// <summary>
/// Cover for importing an exported meeting transcript. This is the path that
/// produces a diagram without audio — and, in practice, the only way to get a
/// Teams meeting onto the board, since Teams exposes no live caption API.
/// </summary>
public class TranscriptImporterTests
{
    private const string TeamsVtt = """
        WEBVTT

        00:00:01.120 --> 00:00:05.400
        <v Maya Chen>Let's lock the Orion pilot for September 18.</v>

        00:00:05.900 --> 00:00:09.250
        <v John Patel>I'll close the high-severity security findings first.</v>

        00:00:09.800 --> 00:00:13.000
        <v Priya Rao>Cloud capacity might slip in week two, that's the main risk.</v>
        """;

    [Fact]
    public void ParsesTeamsWebVttWithSpeakersAndTimings()
    {
        var segments = TranscriptImporter.Parse(TeamsVtt);

        segments.Should().HaveCount(3);
        // The speaker stays in the text: it is what lets the model attribute owners.
        segments[0].Text.Should().StartWith("Maya Chen:");
        segments[0].Text.Should().Contain("September 18");
        segments[1].Text.Should().StartWith("John Patel:");
        segments[2].Text.Should().StartWith("Priya Rao:");

        // Cue timings are preserved, so the 4.28s first cue keeps its real duration.
        segments[0].Duration.Should().BeCloseTo(TimeSpan.FromSeconds(4.28), TimeSpan.FromMilliseconds(50));
        segments[0].Start.Should().BeBefore(segments[1].Start);
    }

    [Fact]
    public void StripsVoiceTagMarkupFromTheText()
    {
        var segments = TranscriptImporter.Parse(TeamsVtt);

        foreach (var s in segments)
        {
            s.Text.Should().NotContain("<v");
            s.Text.Should().NotContain("</v>");
        }
    }

    [Fact]
    public void ParsesSrtWithCommaMilliseconds()
    {
        const string srt = """
            1
            00:00:02,000 --> 00:00:06,500
            Maya: We ship the pilot on the eighteenth.

            2
            00:00:07,000 --> 00:00:10,000
            John: Security review closes first.
            """;

        var segments = TranscriptImporter.Parse(srt);

        segments.Should().HaveCount(2);
        segments[0].Text.Should().StartWith("Maya:");
        segments[1].Text.Should().StartWith("John:");
    }

    [Fact]
    public void FallsBackToPlainTextWhenThereAreNoCues()
    {
        const string plain = """
            Maya: We should ship the Orion pilot on September 18.
            John: I'll close the security findings first.
            Priya: Cloud capacity is the risk I'm watching.
            """;

        var segments = TranscriptImporter.Parse(plain);

        segments.Should().HaveCount(3);
        segments[0].Text.Should().StartWith("Maya:");
        // Synthetic timings must still advance so time-based windowing works.
        segments[0].Start.Should().BeBefore(segments[2].Start);
    }

    [Fact]
    public void PlainTextWithoutSpeakersStillImports()
    {
        var segments = TranscriptImporter.Parse(
            "We agreed to ship on the eighteenth.\nSecurity review is the blocker.");

        segments.Should().HaveCount(2);
        segments[0].Text.Should().Be("We agreed to ship on the eighteenth.");
    }

    [Fact]
    public void MarksTheNamedLocalSpeakerAsLocal()
    {
        var segments = TranscriptImporter.Parse(TeamsVtt, localSpeaker: "Priya");

        segments.Where(s => s.Speaker == TranscriptSpeaker.Local)
            .Should().ContainSingle(s => s.Text.StartsWith("Priya Rao:"));
        segments.Count(s => s.Speaker == TranscriptSpeaker.Remote).Should().Be(2);
    }

    [Fact]
    public void TranscriptEndsAtRoughlyNowSoRecencyWindowingWorks()
    {
        var segments = TranscriptImporter.Parse(TeamsVtt);

        // The rolling buffer selects by recency; an import anchored in the past
        // would be filtered straight back out.
        segments[^1].End.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("WEBVTT\n\n")]
    public void EmptyOrHeaderOnlyInputYieldsNothing(string? content)
    {
        TranscriptImporter.Parse(content).Should().BeEmpty();
    }

    [Fact]
    public void DoesNotMistakeAUrlForASpeakerName()
    {
        var segments = TranscriptImporter.Parse("Check the doc at https://example.com/spec for detail.");

        segments.Should().ContainSingle();
        segments[0].Text.Should().Contain("https://example.com/spec");
    }
}
