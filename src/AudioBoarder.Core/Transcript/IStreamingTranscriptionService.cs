namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Marker interface for transcription services that emit segments asynchronously
/// via their own event (e.g. true streaming recognizers like Azure Speech SDK).
/// The pipeline subscribes to <see cref="SegmentReady"/> in addition to calling
/// the standard <see cref="ITranscriptionService.TranscribeAsync"/> for audio push.
/// </summary>
public interface IStreamingTranscriptionService : ITranscriptionService
{
    /// <summary>Raised when the service finalises an utterance (committed text).</summary>
    event EventHandler<TranscriptSegment>? SegmentReady;

    /// <summary>Raised continuously with the in-progress hypothesis while the
    /// speaker is still talking (the "instant", Teams-style partial result).
    /// This text is provisional and is superseded by the next
    /// <see cref="SegmentReady"/> for the same utterance.</summary>
    event EventHandler<TranscriptSegment>? InterimReady;
}
