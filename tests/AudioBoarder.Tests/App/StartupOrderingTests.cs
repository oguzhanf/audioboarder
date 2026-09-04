namespace AudioBoarder.Tests.App;

public class StartupOrderingTests
{
    [Fact]
    public async Task UpdateStartsFirstWithoutBlockingHealthAndStartsHealthOnlyOnce()
    {
        var order = new List<string>();
        var updateGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var healthStarts = 0;

        var startup = AudioBoarder.App.App.StartIndependentStartupTasksAsync(
            () =>
            {
                order.Add("update");
                return updateGate.Task;
            },
            () =>
            {
                order.Add("health");
                healthStarts++;
                return Task.CompletedTask;
            });

        order.Should().Equal("update", "health");
        healthStarts.Should().Be(1);
        startup.IsCompleted.Should().BeFalse("the update check is still running");

        updateGate.SetResult();
        await startup;

        healthStarts.Should().Be(1, "update completion must not start health a second time");
    }
}
