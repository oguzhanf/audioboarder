namespace AudioBoarder.Core.Transcript;

public enum TranscriptionRuntimeState
{
    Healthy,
    Degraded,
    RateLimited,
    Retrying,
    Backlogged,
    AudioDropped,
    Fatal,
}

/// <summary>Public, payload-free diagnostics suitable for logs and UI status.</summary>
public sealed record TranscriptionDiagnostics(
    TranscriptionRuntimeState State,
    TimeSpan PendingDuration,
    DateTimeOffset? RetryAt = null,
    TimeSpan DroppedDuration = default,
    long DroppedBytes = 0,
    string? SafeErrorCode = null,
    string? StatusMessage = null)
{
    public static TranscriptionDiagnostics Healthy { get; } =
        new(TranscriptionRuntimeState.Healthy, TimeSpan.Zero);
}

public interface ITranscriptionDiagnosticsSource
{
    TranscriptionDiagnostics Diagnostics { get; }
    event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;
}

public enum AudioPipelineRuntimeState
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Faulted,
}

/// <summary>Aggregate capture and backend state without transcript or server payloads.</summary>
public sealed record AudioPipelineDiagnostics(
    AudioPipelineRuntimeState State,
    long ChannelDrops,
    TimeSpan PendingBackendAudio,
    TimeSpan DroppedBackendAudio,
    long DroppedBackendBytes,
    DateTimeOffset? RetryAt = null,
    string? SafeErrorCode = null,
    string? StatusMessage = null)
{
    public static AudioPipelineDiagnostics Stopped { get; } =
        new(AudioPipelineRuntimeState.Stopped, 0, TimeSpan.Zero, TimeSpan.Zero, 0);
}
