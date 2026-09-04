using AudioBoarder.App.Continuous;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;

namespace AudioBoarder.Tests.App;

public class UiRuntimeStatusTests
{
    [Fact]
    public void BackendFallbackMapsToVisibleDegradedStatus()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Degraded,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                SafeErrorCode: "authentication_required",
                StatusMessage: "cloud authentication required, using Azure Speech"),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.Degraded);
        status.Label.Should().Be("Degraded");
        status.Details.Should().Be("cloud authentication required, using Azure Speech");
    }

    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RateLimitIsExplicitAndNeverGreen()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Degraded,
                0,
                TimeSpan.FromSeconds(8),
                TimeSpan.Zero,
                0,
                Now.AddSeconds(30),
                "rate_limited"),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.RateLimited);
        status.Label.Should().StartWith("Rate limited until");
        status.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void BacklogMapsToRetrying()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Degraded,
                0,
                TimeSpan.FromSeconds(12),
                TimeSpan.Zero,
                0),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.Retrying);
        status.Details.Should().Contain("12s");
    }

    [Fact]
    public void DroppedAudioMapsToVisibleGap()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Degraded,
                2,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(4),
                64000),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.AudioGap);
        status.Label.Should().Be("Audio gap 4s");
        status.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void BehindIncludesStatementCountAndLag()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Running, 0, TimeSpan.Zero, TimeSpan.Zero, 0),
            Snapshot(GenerationRuntimeStage.Behind, pending: 5, lag: TimeSpan.FromSeconds(9)),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.Behind);
        status.Label.Should().Be("Behind 5 statements / 9s");
    }

    [Fact]
    public void CurrentIncludesActualCaptionTimestamp()
    {
        var through = Now.AddSeconds(-2);
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Running, 0, TimeSpan.Zero, TimeSpan.Zero, 0),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now,
            through);

        status.State.Should().Be(UiRuntimeState.Current);
        status.Label.Should().Contain(through.ToLocalTime().ToString("HH:mm:ss"));
    }

    [Fact]
    public void FaultMapsToError()
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Faulted, 0, TimeSpan.Zero, TimeSpan.Zero, 0),
            Snapshot(GenerationRuntimeStage.Current),
            true,
            Now);

        status.State.Should().Be(UiRuntimeState.Error);
        status.IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData(GenerationRuntimeStage.Extracting, UiRuntimeState.Analyzing)]
    [InlineData(GenerationRuntimeStage.DeepSynthesizing, UiRuntimeState.DeepRefining)]
    [InlineData(GenerationRuntimeStage.Degraded, UiRuntimeState.Degraded)]
    public void GenerationStagesHaveExplicitTokens(
        GenerationRuntimeStage stage,
        UiRuntimeState expected)
    {
        var status = UiRuntimeStatusMapper.Map(
            new AudioPipelineDiagnostics(
                AudioPipelineRuntimeState.Running, 0, TimeSpan.Zero, TimeSpan.Zero, 0),
            Snapshot(stage, pending: 3),
            true,
            Now);

        status.State.Should().Be(expected);
    }

    private static ContinuousRuntimeSnapshot Snapshot(
        GenerationRuntimeStage stage,
        int pending = 0,
        TimeSpan? lag = null) =>
        new(
            stage,
            null,
            default,
            default,
            default,
            null,
            pending,
            lag ?? TimeSpan.Zero,
            stage == GenerationRuntimeStage.Extracting,
            stage == GenerationRuntimeStage.DeepSynthesizing,
            Now,
            null);
}
