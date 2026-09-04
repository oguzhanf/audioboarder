using System.Text.Json.Nodes;
using AudioBoarder.App.Configuration;

namespace AudioBoarder.Tests.App;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, $"settings-service-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultWritableSettingsPathIsUnderLocalApplicationData()
    {
        var service = new SettingsService();

        service.LocalSettingsPath.Should().StartWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        service.LocalSettingsPath.Should().EndWith(
            Path.Combine("AudioBoarder", "appsettings.Local.json"));
        service.LocalSettingsPath.Should().NotStartWith(AppContext.BaseDirectory);
    }

    [Fact]
    public async Task SaveAtomicallyMergesKnownFieldsAndPreservesUnknownValues()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var local = Path.Combine(_root, "appsettings.Local.json");
        await File.WriteAllTextAsync(defaults,
            """{"AudioBoarder":{"Theme":"Light","Sessions":{"AutoSave":true}}}""");
        await File.WriteAllTextAsync(local,
            """{"AudioBoarder":{"Theme":"Dark","FutureFeature":{"Mode":"keep-me"}}}""");
        var service = new SettingsService(defaults, local);
        var settings = service.Load();

        settings.Theme = "System";
        settings.Sessions.AutoSave = false;
        await service.SaveAsync(settings, new SettingsSecrets(null, null));

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(local))!;
        saved["AudioBoarder"]!["Theme"]!.GetValue<string>().Should().Be("System");
        saved["AudioBoarder"]!["Sessions"]!["AutoSave"]!.GetValue<bool>().Should().BeFalse();
        saved["AudioBoarder"]!["FutureFeature"]!["Mode"]!.GetValue<string>().Should().Be("keep-me");
        Directory.EnumerateFiles(_root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task BlankSecretMeansPreserveExistingSecret()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var local = Path.Combine(_root, "appsettings.Local.json");
        await File.WriteAllTextAsync(defaults, """{"AudioBoarder":{}}""");
        await File.WriteAllTextAsync(local,
            """
            {"AudioBoarder":{"AzureOpenAI":{"ApiKey":"existing-openai"},"AzureSpeech":{"ApiKey":"existing-speech"}}}
            """);
        var service = new SettingsService(defaults, local);

        await service.SaveAsync(
            service.Load(),
            new SettingsSecrets("", "new-speech"));

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(local))!;
        saved["AudioBoarder"]!["AzureOpenAI"]!["ApiKey"]!.GetValue<string>()
            .Should().Be("existing-openai");
        saved["AudioBoarder"]!["AzureSpeech"]!["ApiKey"]!.GetValue<string>()
            .Should().Be("new-speech");
    }

    [Fact]
    public async Task ExplicitClearRemovesSavedSecrets()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var local = Path.Combine(_root, "appsettings.Local.json");
        await File.WriteAllTextAsync(defaults, """{"AudioBoarder":{}}""");
        await File.WriteAllTextAsync(local,
            """
            {"AudioBoarder":{"AzureOpenAI":{"ApiKey":"old-openai"},"AzureSpeech":{"ApiKey":"old-speech"}}}
            """);
        var service = new SettingsService(defaults, local);

        await service.SaveAsync(
            service.Load(),
            new SettingsSecrets(null, null, true, true));

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(local))!;
        saved["AudioBoarder"]!["AzureOpenAI"]!.AsObject()
            .ContainsKey("ApiKey").Should().BeTrue();
        saved["AudioBoarder"]!["AzureOpenAI"]!["ApiKey"].Should().BeNull();
        saved["AudioBoarder"]!["AzureSpeech"]!.AsObject()
            .ContainsKey("ApiKey").Should().BeTrue();
        saved["AudioBoarder"]!["AzureSpeech"]!["ApiKey"].Should().BeNull();
    }

    [Fact]
    public async Task ExplicitClearOverridesSecretInheritedFromPortableLayer()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var portable = Path.Combine(_root, "portable.Local.json");
        var user = Path.Combine(_root, "user.Local.json");
        await File.WriteAllTextAsync(defaults, """{"AudioBoarder":{}}""");
        await File.WriteAllTextAsync(portable,
            """{"AudioBoarder":{"AzureOpenAI":{"ApiKey":"portable-secret"}}}""");
        var service = new SettingsService(defaults, portable, user);

        await service.SaveAsync(
            service.Load(),
            new SettingsSecrets(null, null, ClearAzureOpenAIApiKey: true));

        service.Load().AzureOpenAI.ApiKey.Should().BeNull();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(user))!;
        saved["AudioBoarder"]!["AzureOpenAI"]!.AsObject()
            .ContainsKey("ApiKey").Should().BeTrue();
    }

    [Fact]
    public async Task EditableModelIncludesPortableLayerBeforeUserOverrides()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var portable = Path.Combine(_root, "portable.Local.json");
        var user = Path.Combine(_root, "user.Local.json");
        await File.WriteAllTextAsync(defaults,
            """{"AudioBoarder":{"Theme":"Light","AzureOpenAI":{"DeploymentName":"default"}}}""");
        await File.WriteAllTextAsync(portable,
            """{"AudioBoarder":{"Theme":"Dark","AzureOpenAI":{"DeploymentName":"portable"}}}""");
        await File.WriteAllTextAsync(user,
            """{"AudioBoarder":{"Theme":"System"}}""");
        var service = new SettingsService(defaults, portable, user);

        var settings = service.Load();

        settings.Theme.Should().Be("System");
        settings.AzureOpenAI.DeploymentName.Should().Be("portable",
            "saving user settings must not mask the supported executable-adjacent layer");
    }

    [Fact]
    public async Task InvalidExistingLocalJsonIsNotOverwritten()
    {
        Directory.CreateDirectory(_root);
        var defaults = Path.Combine(_root, "appsettings.json");
        var local = Path.Combine(_root, "appsettings.Local.json");
        await File.WriteAllTextAsync(defaults, """{"AudioBoarder":{}}""");
        await File.WriteAllTextAsync(local, "{ invalid");
        var service = new SettingsService(defaults, local);

        var save = () => service.SaveAsync(
            service.Load(), new SettingsSecrets(null, null));

        await save.Should().ThrowAsync<System.Text.Json.JsonException>();
        (await File.ReadAllTextAsync(local)).Should().Be("{ invalid");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
