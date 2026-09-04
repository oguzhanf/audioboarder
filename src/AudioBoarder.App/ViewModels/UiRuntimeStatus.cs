using AudioBoarder.App.Continuous;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;

namespace AudioBoarder.App.ViewModels;

public enum UiRuntimeState
{
    Initializing,
    Ready,
    Listening,
    CaptionsCurrent,
    Analyzing,
    DeepRefining,
    Current,
    Behind,
    RateLimited,
    Retrying,
    AudioGap,
    Degraded,
    Error,
}

public sealed record UiRuntimeStatus(
    UiRuntimeState State,
    string Label,
    string Details,
    bool IsWarning = false,
    bool IsError = false)
{
    public static UiRuntimeStatus Initializing(string details = "Checking components…") =>
        new(UiRuntimeState.Initializing, "Initializing", details);

    public static UiRuntimeStatus Ready(string details = "Ready to listen.") =>
        new(UiRuntimeState.Ready, "Ready", details);
}

public static class UiRuntimeStatusMapper
{
    public static UiRuntimeStatus Map(
        AudioPipelineDiagnostics audio,
        ContinuousRuntimeSnapshot generation,
        bool isListening,
        DateTimeOffset now,
        DateTimeOffset? latestCaptionTimestamp = null)
    {
        if (!isListening)
            return UiRuntimeStatus.Ready();

        if (audio.State == AudioPipelineRuntimeState.Faulted ||
            generation.Stage == GenerationRuntimeStage.Error)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Error,
                "Error",
                "Capture or diagram generation faulted. Pending work remains available for retry.",
                IsWarning: true,
                IsError: true);
        }

        if (string.Equals(audio.SafeErrorCode, "rate_limited", StringComparison.Ordinal))
        {
            var until = audio.RetryAt?.ToLocalTime().ToString("HH:mm:ss") ?? "the service allows";
            return new UiRuntimeStatus(
                UiRuntimeState.RateLimited,
                $"Rate limited until {until}",
                $"{audio.PendingBackendAudio.TotalSeconds:F0}s of audio buffered.",
                IsWarning: true);
        }

        var dropped = audio.DroppedBackendAudio;
        if (audio.DroppedBackendBytes > 0 || audio.ChannelDrops > 0 || dropped > TimeSpan.Zero)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.AudioGap,
                $"Audio gap {Math.Max(0, dropped.TotalSeconds):F0}s",
                "Some captured audio could not be retained while the transcription backend caught up.",
                IsWarning: true);
        }

        if (audio.State == AudioPipelineRuntimeState.Degraded &&
            audio.PendingBackendAudio > TimeSpan.Zero)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Retrying,
                "Retrying",
                $"{audio.PendingBackendAudio.TotalSeconds:F0}s of audio queued for transcription.",
                IsWarning: true);
        }

        if (audio.State == AudioPipelineRuntimeState.Degraded &&
            !string.IsNullOrWhiteSpace(audio.StatusMessage))
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Degraded,
                "Degraded",
                audio.StatusMessage,
                IsWarning: true);
        }

        if (generation.Stage == GenerationRuntimeStage.DeepSynthesizing)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.DeepRefining,
                "Deep refining",
                "Consolidating the current architecture.");
        }

        if (generation.Stage == GenerationRuntimeStage.Extracting)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Analyzing,
                $"Analyzing {Math.Max(1, generation.PendingSegments)} statements",
                "Applying the next safe incremental update.");
        }

        if (generation.Stage is GenerationRuntimeStage.Behind or GenerationRuntimeStage.Queued)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Behind,
                $"Behind {generation.PendingSegments} statements / {generation.Lag.TotalSeconds:F0}s",
                "Captions continue while diagram work is queued.",
                IsWarning: true);
        }

        if (generation.Stage == GenerationRuntimeStage.Degraded)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Degraded,
                "Degraded",
                "Diagram generation will retry without discarding pending statements.",
                IsWarning: true);
        }

        if (generation.Stage == GenerationRuntimeStage.Current && latestCaptionTimestamp.HasValue)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.Current,
                $"Current through {latestCaptionTimestamp.Value.ToLocalTime():HH:mm:ss}",
                "The live architecture canvas reflects processed captions.");
        }

        if (generation.PendingSegments == 0)
        {
            return new UiRuntimeStatus(
                UiRuntimeState.CaptionsCurrent,
                "Captions current",
                "Listening for the next statement.");
        }

        return new UiRuntimeStatus(
            UiRuntimeState.Listening,
            "Listening",
            $"Capturing audio at {now.ToLocalTime():HH:mm:ss}.");
    }
}
