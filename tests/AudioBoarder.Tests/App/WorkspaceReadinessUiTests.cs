using AudioBoarder.App.Health;
using AudioBoarder.App.ViewModels;

namespace AudioBoarder.Tests.App;

public sealed class WorkspaceReadinessUiTests
{
    [Theory]
    [InlineData(ComponentStatus.Unknown, false)]
    [InlineData(ComponentStatus.Checking, false)]
    [InlineData(ComponentStatus.Ready, false)]
    [InlineData(ComponentStatus.Degraded, false)]
    [InlineData(ComponentStatus.ActionRequired, true)]
    [InlineData(ComponentStatus.Failed, true)]
    public void SetupIsOfferedForMissingServicesNotForAConnectionInProgress(ComponentStatus status, bool expected)
    {
        MainViewModel.NeedsWorkspaceSetup([
            new HealthState(status, "Speech", "", DateTimeOffset.UtcNow, StartupHealthService.TranscriptionKey),
        ]).Should().Be(expected);
    }

    [Fact]
    public void MissingMicrophoneDoesNotAskTheUserToRecreateAzure()
    {
        MainViewModel.NeedsWorkspaceSetup([
            new HealthState(ComponentStatus.Failed, "Audio", "", DateTimeOffset.UtcNow, StartupHealthService.AudioKey),
        ]).Should().BeFalse();
    }
}
