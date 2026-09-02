namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Stable position in a <see cref="TranscriptBuffer"/>. Sequence zero means
/// "before the first segment"; values increase once for every append.
/// </summary>
public readonly record struct TranscriptCursor(long Sequence)
{
    public static TranscriptCursor Beginning { get; } = new(0);
}

/// <summary>
/// Segments appended after <see cref="RequestedAfter"/> through
/// <see cref="Through"/>. <see cref="HasGap"/> is true when some requested
/// segments had already been evicted before the read.
/// </summary>
public sealed record TranscriptSlice(
    TranscriptCursor RequestedAfter,
    TranscriptCursor FirstAvailable,
    TranscriptCursor Through,
    IReadOnlyList<TranscriptSegment> Segments,
    bool HasGap);
