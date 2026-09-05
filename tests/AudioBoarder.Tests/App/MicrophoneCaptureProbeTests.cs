using AudioBoarder.App.Health;
using AudioBoarder.App.HealthCheck;
using AudioBoarder.Core.Audio;

namespace AudioBoarder.Tests.App;

public sealed class MicrophoneCaptureProbeTests
{
    [Fact]
    public async Task LocalProbeMeasuresPcmAndStopsTheDevice()
    {
        var source = new ProbeSource();
        var result = await MicrophoneCaptureProbe.MeasureAsync(source, TimeSpan.FromMilliseconds(1), default);

        result.Chunks.Should().Be(1);
        result.Bytes.Should().Be(4);
        result.Peak.Should().Be(1);
        result.CaptureFailed.Should().BeFalse();
        source.IsRunning.Should().BeFalse();
    }

    [Theory]
    [InlineData(ComponentStatus.ActionRequired)]
    [InlineData(ComponentStatus.Failed)]
    [InlineData(ComponentStatus.RateLimited)]
    [InlineData(ComponentStatus.Checking)]
    [InlineData(ComponentStatus.Unknown)]
    public void IncompleteHealthMustNotReturnSuccess(ComponentStatus status) =>
        HealthCheckCommand.NeedsAttention(status).Should().BeTrue();

    private sealed class ProbeSource : IAudioCaptureSource
    {
        public AudioStreamRole Role => AudioStreamRole.Microphone;
        public AudioFormat OutputFormat => AudioFormat.Mono16kPcm16;
        public bool IsRunning { get; private set; }
        public event EventHandler<AudioChunk>? ChunkCaptured;
        public event EventHandler<AudioCaptureError>? CaptureFailed { add { } remove { } }
        public Task StartAsync(CancellationToken ct)
        {
            IsRunning = true;
            ChunkCaptured?.Invoke(this, new AudioChunk
            {
                Role = Role, Format = OutputFormat, CapturedAt = DateTimeOffset.UtcNow, Samples = new byte[] { 0, 128, 0, 64 },
            });
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
