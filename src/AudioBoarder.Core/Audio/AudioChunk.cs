namespace AudioBoarder.Core.Audio;

/// <summary>
/// A short, contiguous chunk of audio samples from one stream. Carries its own
/// timestamp so downstream consumers can correlate without sharing a clock.
/// </summary>
public sealed class AudioChunk
{
    public required AudioStreamRole Role { get; init; }
    public required AudioFormat Format { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>16-bit signed PCM samples, interleaved if multi-channel.</summary>
    public required ReadOnlyMemory<byte> Samples { get; init; }

    public TimeSpan Duration => Format.BytesPerSecond == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Samples.Length / (double)Format.BytesPerSecond);
}
