using AudioBoarder.App.Health;

namespace AudioBoarder.Tests.Health;

public class HealthStateTests
{
    [Fact]
    public void IsReady_OnlyTrueWhenReady()
    {
        new HealthState(ComponentStatus.Ready, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeTrue();
        new HealthState(ComponentStatus.Degraded, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
        new HealthState(ComponentStatus.ActionRequired, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
        new HealthState(ComponentStatus.RateLimited, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
        new HealthState(ComponentStatus.Failed, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
        new HealthState(ComponentStatus.Checking, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
        new HealthState(ComponentStatus.Unknown, "x", "y", DateTimeOffset.UtcNow).IsReady.Should().BeFalse();
    }
}
