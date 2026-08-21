using AudioBoarder.Services.Transcription;
using FluentAssertions;
using Xunit;

namespace AudioBoarder.Tests.Transcription;

public class WhisperOutputCleanerTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("[ Silence ]", "")]
    [InlineData("(silence)", "")]
    [InlineData("[Music]", "")]
    [InlineData("[Applause]", "")]
    [InlineData("(typing)", "")]
    [InlineData(".", "")]
    [InlineData("...", "")]
    [InlineData(" - ", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void RemovesNoiseTokens(string? input, string expected)
    {
        WhisperTranscriptionService.CleanWhisperOutput(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Hello world", "Hello world")]
    [InlineData(" trimmed text  ", "trimmed text")]
    [InlineData("Real speech [BLANK_AUDIO]", "Real speech")]
    [InlineData("(noise) Question one", "Question one")]
    [InlineData("Some words [Music] more words", "Some words  more words")]
    public void PreservesRealSpeech(string input, string expected)
    {
        WhisperTranscriptionService.CleanWhisperOutput(input).Should().Be(expected);
    }
}
