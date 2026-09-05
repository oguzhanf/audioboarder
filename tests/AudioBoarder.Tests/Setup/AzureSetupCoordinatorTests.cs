using AudioBoarder.App.Configuration;
using AudioBoarder.App.Setup;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Setup;

public sealed class AzureSetupCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"audioboarder-setup-{Guid.NewGuid():N}");

    [Fact]
    public async Task HealthyResourcesDoNotInterruptLogin()
    {
        var presenter = new Presenter();
        var coordinator = Create(AzureSetupViewModelTests.ReadyInventory(), presenter);

        await coordinator.EnsureConfiguredAsync();

        presenter.Count.Should().Be(0);
    }

    [Fact]
    public async Task MissingResourcesOpenWizardOnlyAfterSuccessfulLogin()
    {
        var presenter = new Presenter();
        var coordinator = Create(new SetupInventory(), presenter);
        await coordinator.EnsureConfiguredAsync();
        presenter.Count.Should().Be(1);

        var signedOut = new Presenter();
        await Create(new SetupInventory(), signedOut, signedIn: false).EnsureConfiguredAsync();
        signedOut.Count.Should().Be(0);
    }

    [Fact]
    public async Task CancelDoesNotSaveSettingsOrChangeRuntime()
    {
        var runtime = new AudioBoarderSettings();
        var coordinator = Create(new SetupInventory(), new Presenter(), runtime: runtime);

        await coordinator.EnsureConfiguredAsync();

        File.Exists(Path.Combine(_root, "settings.json")).Should().BeFalse();
        runtime.AzureOpenAI.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task SuccessfulSetupPersistsChoicesBeforeUpdatingRuntime()
    {
        var inventory = new SetupInventory();
        var presenter = new Presenter
        {
            Complete = async (vm, save, ct) =>
            {
                inventory.Accounts = AzureSetupViewModelTests.ReadyInventory().Accounts;
                await vm.RefreshAsync(ct);
                await save(vm.GetSelection(), ct);
            },
        };
        var runtime = new AudioBoarderSettings();
        var chat = new AzureOpenAIOptions();
        var coordinator = Create(inventory, presenter, runtime: runtime, chat: chat);

        await coordinator.EnsureConfiguredAsync();

        var saved = Settings().Load();
        saved.AzureOpenAI.DeploymentName.Should().Be("chat");
        saved.AzureOpenAI.AutoDiscover.Should().BeFalse();
        saved.ModelAccounts.Should().ContainSingle();
        runtime.AzureOpenAI.Endpoint.Should().Be(saved.AzureOpenAI.Endpoint);
        chat.DeploymentName.Should().Be("chat");
    }

    [Fact]
    public async Task SaveFailureLeavesRuntimeUnchanged()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "invalid JSON");
        var inventory = new SetupInventory();
        var presenter = new Presenter
        {
            Complete = async (vm, save, ct) =>
            {
                inventory.Accounts = AzureSetupViewModelTests.ReadyInventory().Accounts;
                await vm.RefreshAsync(ct);
                await save(vm.GetSelection(), ct);
            },
        };
        var runtime = new AudioBoarderSettings();
        var coordinator = Create(inventory, presenter, runtime: runtime);

        await coordinator.Invoking(c => c.EnsureConfiguredAsync())
            .Should().ThrowAsync<System.Text.Json.JsonException>();

        runtime.AzureOpenAI.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentPostLoginCallbacksCannotOpenDuplicateWizards()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presenter = new Presenter
        {
            Complete = async (_, _, _) =>
            {
                opened.SetResult();
                await closed.Task;
            },
        };
        var coordinator = Create(new SetupInventory(), presenter);
        var first = coordinator.EnsureConfiguredAsync();
        await opened.Task;
        await coordinator.EnsureConfiguredAsync();
        presenter.Count.Should().Be(1);
        closed.SetResult();
        await first;
    }

    private SettingsService Settings() => new(Path.Combine(_root, "defaults.json"), Path.Combine(_root, "settings.json"));

    private AzureSetupCoordinator Create(
        SetupInventory inventory,
        Presenter presenter,
        bool signedIn = true,
        AudioBoarderSettings? runtime = null,
        AzureOpenAIOptions? chat = null) =>
        new(new SetupCredentials { SignedIn = signedIn }, inventory, Settings(),
            Options.Create(runtime ?? new AudioBoarderSettings()),
            Options.Create(chat ?? new AzureOpenAIOptions()),
            Options.Create(new CloudTranscriptionOptions()),
            Options.Create(new ImageGeneratorOptions()), presenter);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Presenter : IAzureSetupPresenter
    {
        public int Count { get; private set; }
        public Func<AzureSetupViewModel, Func<AzureModelSelection, CancellationToken, Task>, CancellationToken, Task>? Complete { get; init; }
        public Task ShowAsync(AzureSetupViewModel viewModel, Func<AzureModelSelection, CancellationToken, Task> save, CancellationToken ct)
        {
            Count++;
            return Complete?.Invoke(viewModel, save, ct) ?? Task.CompletedTask;
        }
    }
}
