namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Thread-safe rolling buffer of transcript segments. Holds the most recent
/// segments inside <see cref="Window"/>. Older segments are dropped.
/// </summary>
public sealed class TranscriptBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<TranscriptSegment> _segments = new();
    private readonly Func<DateTimeOffset> _clock;

    public TimeSpan Window { get; }

    public TranscriptBuffer(TimeSpan window, Func<DateTimeOffset>? clock = null)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        Window = window;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Append(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_gate)
        {
            _segments.AddLast(segment);
            Evict();
        }
    }

    public IReadOnlyList<TranscriptSegment> Snapshot()
    {
        lock (_gate)
        {
            Evict();
            return _segments.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate) _segments.Clear();
    }

    private void Evict()
    {
        var cutoff = _clock() - Window;
        while (_segments.First is { } first && first.Value.End < cutoff)
            _segments.RemoveFirst();
    }
}
