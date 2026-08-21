using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Tests.Transcription;

public class TranscriptBufferTests
{
    [Fact]
    public void Append_AndSnapshot_RoundTrip()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var buf = new TranscriptBuffer(TimeSpan.FromMinutes(1), () => clock);
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "hi", clock.AddSeconds(-10), clock.AddSeconds(-9)));
        buf.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public void EvictsOldSegments()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var now = clock;
        var buf = new TranscriptBuffer(TimeSpan.FromSeconds(5), () => now);
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "old",
            clock.AddSeconds(-30), clock.AddSeconds(-29)));
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "new",
            clock.AddSeconds(-1), clock));
        var snap = buf.Snapshot();
        snap.Should().HaveCount(1);
        snap[0].Text.Should().Be("new");
    }
}
