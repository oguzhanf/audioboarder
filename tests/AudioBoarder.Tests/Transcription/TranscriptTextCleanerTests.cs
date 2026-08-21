using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Tests.Transcription;

/// <summary>
/// Multilingual ASR fills ambiguous audio with tokens from whatever script it
/// drifts into. Observed live from gpt-transcribe with language=en: every sentence
/// came back ending in "囧。" — a CJK ideograph plus a fullwidth stop — appended to
/// otherwise correct English, which then leaked into node labels and the prompt.
/// </summary>
public class TranscriptTextCleanerTests
{
    [Fact]
    public void StripsTheExactArtefactSeenLive()
    {
        TranscriptTextCleaner.Clean(
            "Today we are talking about an application that runs on Azure. 囧。", "en")
            .Should().Be("Today we are talking about an application that runs on Azure.");
    }

    [Theory]
    [InlineData("Protected by Web Application Firewall. 囧。")]
    [InlineData("Risk managed by CSPM。")]
    [InlineData("A user logs on ありがとう")]
    [InlineData("Traffic towards the application 한국어")]
    public void RemovesForeignScriptWhenEnglishIsExpected(string input)
    {
        var cleaned = TranscriptTextCleaner.Clean(input, "en");
        cleaned.Should().NotBeNullOrWhiteSpace();
        cleaned.Should().MatchRegex("^[\\x20-\\x7E]+$", "only Latin text should remain");
    }

    [Fact]
    public void DropsSegmentsThatWereEntirelyHallucinatedScript()
    {
        TranscriptTextCleaner.Clean("囧。ありがとう", "en").Should().BeEmpty();
    }

    [Fact]
    public void LeavesGenuineNonLatinTranscriptsAloneWhenThatLanguageIsExpected()
    {
        // A Japanese meeting must not be mangled.
        const string jp = "これはテストです";
        TranscriptTextCleaner.Clean(jp, "ja").Should().Be(jp);
    }

    [Fact]
    public void TidiesSpaceLeftBeforePunctuation()
    {
        TranscriptTextCleaner.Clean("Runs on Azure 囧 .", "en").Should().Be("Runs on Azure.");
    }

    [Fact]
    public void StripsBracketedNoiseAnnotations()
    {
        TranscriptTextCleaner.Clean("[BLANK_AUDIO] Hello there (typing)", "en")
            .Should().Be("Hello there");
    }

    [Fact]
    public void ReturnsEmptyForPunctuationOnlyOutput()
    {
        TranscriptTextCleaner.Clean("...", "en").Should().BeEmpty();
        TranscriptTextCleaner.Clean("   ", "en").Should().BeEmpty();
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("de", true)]
    [InlineData("tr", true)]
    [InlineData("ja", false)]
    [InlineData("zh", false)]
    [InlineData("ko", false)]
    public void KnowsWhichLanguagesExpectLatinScript(string lang, bool expected)
    {
        TranscriptTextCleaner.ExpectsLatinScript(lang).Should().Be(expected);
    }
}
