using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Transcribes a captured audio chunk to one or more <see cref="TranscriptSegment"/>s.
/// Implementations may buffer chunks internally before emitting segments.
/// </summary>
public interface ITranscriptionService : IAsyncDisposable
{
    string Name { get; }
    bool IsReady { get; }

    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Feed an audio chunk. Returns any segments that became available; may be empty
    /// if the implementation is still accumulating audio.
    /// </summary>
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct);

    /// <summary>Flush buffered audio and emit pending segments. When
    /// <paramref name="force"/> is true, emit everything regardless of the
    /// silence/min-length heuristics (used on stop so the final utterance
    /// isn't lost).</summary>
    Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false);
}
