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

    [Fact]
    public void ReadAfter_UsesAppendSequenceWhenTimestampsAreIdentical()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var buf = new TranscriptBuffer(TimeSpan.FromMinutes(1), () => now);
        var start = buf.CurrentCursor;

        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "first", now, now));
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Remote, "second", now, now));

        var slice = buf.ReadAfter(start);
        slice.HasGap.Should().BeFalse();
        slice.Segments.Select(s => s.Text).Should().Equal("first", "second");
        slice.Through.Sequence.Should().Be(2);

        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "third", now, now));
        buf.ReadAfter(slice.Through).Segments.Select(s => s.Text).Should().Equal("third");
    }

    [Fact]
    public void ReadAfter_ReportsGapWhenRequestedDataWasEvicted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var buf = new TranscriptBuffer(TimeSpan.FromSeconds(5), () => now);
        var cursor = buf.CurrentCursor;
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "old",
            now.AddSeconds(-4), now.AddSeconds(-3)));

        now = now.AddSeconds(10);
        buf.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "new", now, now));

        var slice = buf.ReadAfter(cursor);
        slice.HasGap.Should().BeTrue();
        slice.FirstAvailable.Sequence.Should().Be(2);
        slice.Segments.Select(s => s.Text).Should().Equal("new");
    }
}
