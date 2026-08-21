namespace AudioBoarder.Core.Audio;

/// <summary>
/// Captures one logical audio stream (mic or loopback) and emits
/// <see cref="AudioChunk"/>s via the <see cref="ChunkCaptured"/> event.
/// Implementations are expected to negotiate the device's native format
/// and convert/resample to <see cref="AudioFormat.Mono16kPcm16"/>.
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    AudioStreamRole Role { get; }
    AudioFormat OutputFormat { get; }
    bool IsRunning { get; }

    event EventHandler<AudioChunk>? ChunkCaptured;
    event EventHandler<AudioCaptureError>? CaptureFailed;

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed record AudioCaptureError(AudioStreamRole Role, string Message, Exception? Cause);
