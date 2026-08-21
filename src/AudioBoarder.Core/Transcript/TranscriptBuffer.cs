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

    /// <summary>
    /// The most recent <paramref name="window"/> of segments, always keeping at
    /// least <paramref name="minSegments"/> so a pass is never handed nothing.
    /// <para>
    /// Continuous passes only need what was just said — the current scene already
    /// carries everything said earlier. Re-sending the full rolling window on every
    /// tick made the prompt grow for the whole meeting: measured against the live
    /// Responses API, the same model answered a short prompt in 4.4 s and the full
    /// app prompt in 12.8 s, so this input is a first-order latency cost.
    /// </para>
    /// </summary>
    public IReadOnlyList<TranscriptSegment> SnapshotRecent(TimeSpan window, int minSegments = 3)
    {
        if (window <= TimeSpan.Zero) return Snapshot();
        lock (_gate)
        {
            Evict();
            var cutoff = _clock() - window;
            var recent = _segments.Where(s => s.End >= cutoff).ToArray();
            if (recent.Length >= minSegments) return recent;
            // Too few in the window (a long pause): fall back to the last N overall.
            return _segments.Reverse().Take(minSegments).Reverse().ToArray();
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
