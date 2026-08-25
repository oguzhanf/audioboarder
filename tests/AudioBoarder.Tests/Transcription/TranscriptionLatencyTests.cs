using AudioBoarder.Services.Transcription.Cloud;

namespace AudioBoarder.Tests.Transcription;

/// <summary>
/// Cover for the settings that determine how far behind the live transcript can
/// fall. A live meeting tool that silently drifts a minute behind is useless, and
/// these defaults are the thing that decides it.
/// </summary>
public class TranscriptionLatencyTests
{
    [Fact]
    public void WindowIsShortEnoughForALiveTranscript()
    {
        var options = new CloudTranscriptionOptions();

        // A continuous speaker never triggers the silence flush, so this window is
        // the floor on how long EVERY utterance waits before it is even sent.
        options.WindowSeconds.Should().BeLessThanOrEqualTo(5.0);
    }

    [Fact]
    public void RetryBackoffCannotStallTheTranscript()
    {
        var options = new CloudTranscriptionOptions();

        // The original 30s ceiling meant one failure produced a visible minute-long
        // gap while audio piled up behind it.
        options.MaxRetryBackoffSeconds.Should().BeLessThanOrEqualTo(3.0);
    }

    [Fact]
    public void BacklogIsBoundedSoAnOutageCannotSnowball()
    {
        var options = new CloudTranscriptionOptions();

        options.MaxBufferedSeconds.Should().BeGreaterThan(options.WindowSeconds);
        options.MaxBufferedSeconds.Should().BeLessThanOrEqualTo(30.0);
    }

    [Fact]
    public void SilenceFlushIsFastEnoughToFeelImmediate()
    {
        new CloudTranscriptionOptions().SilenceFlushMs.Should().BeLessThanOrEqualTo(500);
    }
}
