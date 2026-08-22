using System.IO;
using AudioBoarder.App.Sessions;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Sessions;

public class SessionStoreTests : IDisposable
{
    private readonly string _tempLocalAppData;
    private readonly string _originalLocalAppData;

    public SessionStoreTests()
    {
        _originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        _tempLocalAppData = Path.Combine(Path.GetTempPath(), $"audioboarder-tests-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempLocalAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _originalLocalAppData);
        try { if (Directory.Exists(_tempLocalAppData)) Directory.Delete(_tempLocalAppData, true); }
        catch { /* ignore */ }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var store = new SessionStore();
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
            new AddNode("b", NodeKind.DataStore, "Bravo"),
            new Connect("e1", "a", "b", EdgeKind.Flow, "writes"),
            new NoteUpsert("n1", NoteKind.ActionItem, "Ship it", Owner: "alice"),
        }));
        scene.Nodes["a"].X = 100; scene.Nodes["a"].Y = 200;
        scene.Nodes["a"].Locked = true;

        await store.SaveAsync(scene);
        var loaded = await store.LoadLatestAsync();
        loaded.Should().NotBeNull();
        loaded!.Nodes.Should().HaveCount(2);
        loaded.Edges.Should().HaveCount(1);
        loaded.Notes.Should().HaveCount(1);

        var fresh = new SceneGraph();
        store.Apply(fresh, loaded);
        fresh.Nodes.Should().ContainKey("a");
        fresh.Nodes["a"].X.Should().Be(100);
        fresh.Nodes["a"].Locked.Should().BeTrue();
        fresh.Edges.Should().ContainKey("e1");
        fresh.Notes["n1"].Owner.Should().Be("alice");
    }

    [Fact]
    public async Task LoadLatest_NoFile_ReturnsNull()
    {
        var store = new SessionStore();
        var result = await store.LoadLatestAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task Clear_RemovesFile()
    {
        var store = new SessionStore();
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
        }));
        await store.SaveAsync(scene);
        (await store.LoadLatestAsync()).Should().NotBeNull();

        await store.ClearAsync();
        (await store.LoadLatestAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentSaves_PersistNewestSnapshotAsValidJson()
    {
        var store = new SessionStore();
        var older = new SceneGraph();
        var newer = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(older, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("old", NodeKind.Process, new string('x', 20_000)),
        }));
        applier.Apply(newer, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("new", NodeKind.Process, "Newest"),
        }));

        var first = store.SaveAsync(older);
        var second = store.SaveAsync(newer);
        await Task.WhenAll(first, second);

        var loaded = await store.LoadLatestAsync();
        loaded.Should().NotBeNull();
        loaded!.Nodes.Should().ContainSingle(n => n.Id == "new");
        Directory.EnumerateFiles(store.RootDirectory, "*.tmp").Should().BeEmpty();
    }
}
