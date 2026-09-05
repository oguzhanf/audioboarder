using AudioBoarder.App.Configuration;

namespace AudioBoarder.Tests.App;

public sealed class LocalDataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, $"local-data-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeclinedConfirmationDoesNotDeleteOrInvokeReset()
    {
        Directory.CreateDirectory(_root);
        var state = Path.Combine(_root, "ui-state.json");
        await File.WriteAllTextAsync(state, "{}");
        var resetCalled = false;
        var service = new LocalDataService(_root, _ =>
        {
            resetCalled = true;
            return Task.CompletedTask;
        });

        var deleted = await service.DeleteWithConfirmationAsync(new Confirmation(false));

        deleted.Should().BeFalse();
        resetCalled.Should().BeFalse();
        File.Exists(state).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmedDeletionUsesResetAndDeletesOnlyKnownLocalData()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "ui-state.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "auth-record-a1b2c3.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "keep.txt"), "keep");
        var resetCalled = false;
        var service = new LocalDataService(_root, _ =>
        {
            resetCalled = true;
            return Task.CompletedTask;
        });

        var deleted = await service.DeleteWithConfirmationAsync(new Confirmation(true));

        deleted.Should().BeTrue();
        resetCalled.Should().BeTrue();
        File.Exists(Path.Combine(_root, "ui-state.json")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "auth-record-a1b2c3.json")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "keep.txt")).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Confirmation(bool result) : ILocalDataDeletionConfirmation
    {
        public bool ConfirmDeleteLocalData() => result;
    }
}
