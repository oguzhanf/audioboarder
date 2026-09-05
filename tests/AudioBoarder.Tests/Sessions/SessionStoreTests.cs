using System.IO;
using AudioBoarder.App.Sessions;
using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Sessions;

public class SessionStoreTests : IDisposable
{
    private readonly string _tempLocalAppData;
    public SessionStoreTests()
    {
        _tempLocalAppData = Path.Combine(Path.GetTempPath(), $"audioboarder-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempLocalAppData)) Directory.Delete(_tempLocalAppData, true); }
        catch { /* ignore */ }
    }

    [Fact]
    public void TestStoreUsesAnExplicitIsolatedRoot()
    {
        var store = new SessionStore(_tempLocalAppData);
        store.RootDirectory.Should().Be(_tempLocalAppData);
        store.RootDirectory.Should().NotBe(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioBoarder", "sessions"));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var store = new SessionStore(_tempLocalAppData);
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

        (await store.SaveAsync(scene)).Should().Be(SessionSaveResult.Saved);
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
        loaded.SchemaVersion.Should().Be(SessionPayload.CurrentSchemaVersion);
    }

    [Fact]
    public async Task LoadLatest_NoFile_ReturnsNull()
    {
        var store = new SessionStore(_tempLocalAppData);
        var result = await store.LoadLatestAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task Clear_RemovesFile()
    {
        var store = new SessionStore(_tempLocalAppData);
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
        var store = new SessionStore(_tempLocalAppData);
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

    [Fact]
    public async Task LoadLatest_MigratesUnversionedV0Payload()
    {
        var store = new SessionStore(_tempLocalAppData);
        var json = """
            {
              "savedAt": "2026-01-01T00:00:00Z",
              "revision": 1,
              "nodes": [
                { "id": "a", "kind": "Process", "label": "Alpha",
                  "x": 10, "y": 20, "width": 140, "height": 60,
                  "groupId": null, "locked": false }
              ],
              "edges": [],
              "groups": [],
              "notes": []
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(store.RootDirectory, "current.json"), json);

        var payload = await store.LoadLatestAsync();

        payload.Should().NotBeNull();
        payload!.SchemaVersion.Should().Be(SessionPayload.CurrentSchemaVersion);
        payload.WasMigratedFromV0.Should().BeTrue();
        payload.Nodes.Should().ContainSingle(n => n.Id == "a");
    }

    [Fact]
    public async Task V1RoundTrip_PreservesSemanticFieldsGeometryAndSequence()
    {
        var store = new SessionStore(_tempLocalAppData);
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new GroupOp("outer", "Outer", Array.Empty<string>(), Subtitle: "West Europe",
                BoundaryKind: BoundaryKind.CloudScope),
            new GroupOp("inner", "Inner", Array.Empty<string>(), ParentGroupId: "outer",
                Subtitle: "10.0.0.0/24", BoundaryKind: BoundaryKind.Network),
            new AddNode("a", NodeKind.Technology, "Alpha", "inner", Icon: "database", Description: "stores state"),
            new AddNode("b", NodeKind.External, "Bravo"),
            new Connect("e1", "a", "b", EdgeKind.Dependency, "calls", Step: 3,
                Protocol: "HTTPS", Payload: "Request", DataClassification: "Internal",
                Authentication: "managed identity", InteractionMode: InteractionMode.Synchronous),
            new NoteUpsert("n1", NoteKind.Decision, "Use Alpha", "owner",
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            new GenerateImage("img1", "Architecture thumbnail", "a"),
        }));
        scene.Nodes["a"].X = 12.5;
        scene.Nodes["a"].Y = 25.5;
        scene.Nodes["a"].Width = 222;
        scene.Nodes["a"].Height = 88;
        scene.Nodes["a"].Locked = true;
        scene.TryMarkNodeUserEdited("a");
        scene.TryMarkEdgeUserEdited("e1");
        scene.SetIntentState(new DiagramIntentState(
            DiagramIntent.CloudNetworkArchitecture,
            DiagramIntentSelectionMode.PinnedByUser,
            1,
            "Pinned by user",
            scene.Revision));
        scene.TryRestoreNodeSequence("a", 99);
        scene.Images["img1"].PngBytes = [1, 2, 3];
        scene.Images["img1"].Status = ImageGenerationStatus.Ready;
        scene.Images["img1"].ModelName = "image-model";

        await store.SaveAsync(scene);
        var payload = await store.LoadLatestAsync();
        var restored = new SceneGraph();
        store.Apply(restored, payload!);

        restored.Nodes["a"].Should().BeEquivalentTo(scene.Nodes["a"]);
        restored.Edges["e1"].Step.Should().Be(3);
        restored.Edges["e1"].Protocol.Should().Be("HTTPS");
        restored.Edges["e1"].Payload.Should().Be("Request");
        restored.Edges["e1"].DataClassification.Should().Be("Internal");
        restored.Edges["e1"].Authentication.Should().Be("managed identity");
        restored.Edges["e1"].InteractionMode.Should().Be(InteractionMode.Synchronous);
        restored.Edges["e1"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        restored.Groups["inner"].ParentGroupId.Should().Be("outer");
        restored.Groups["inner"].Subtitle.Should().Be("10.0.0.0/24");
        restored.Groups["inner"].BoundaryKind.Should().Be(BoundaryKind.Network);
        restored.IntentState.AppliedIntent.Should().Be(DiagramIntent.CloudNetworkArchitecture);
        restored.IntentState.SelectionMode.Should().Be(DiagramIntentSelectionMode.PinnedByUser);
        restored.Notes["n1"].Should().BeEquivalentTo(scene.Notes["n1"]);
        restored.Images["img1"].Should().BeEquivalentTo(scene.Images["img1"]);
    }

    [Fact]
    public async Task RoundTripPreservesUnlockedGeometryForUserEditedNode()
    {
        var store = new SessionStore(_tempLocalAppData);
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new AddNode("a", NodeKind.Process, "Alpha"),
        ]));
        scene.TryUpdateNodeGeometry("a", 900, 700, 220, 90, locked: true);
        scene.TryUpdateNodeGeometry("a", 900, 700, 220, 90, locked: false);

        await store.SaveAsync(scene);
        var restored = new SceneGraph();
        store.Apply(restored, (await store.LoadLatestAsync())!);

        restored.Nodes["a"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        restored.Nodes["a"].Locked.Should().BeFalse();
        restored.Nodes["a"].X.Should().Be(900);
        restored.Nodes["a"].Y.Should().Be(700);
    }

    [Fact]
    public async Task V1MigrationDefaultsNewSemanticFieldsSafely()
    {
        var store = new SessionStore(_tempLocalAppData);
        var json = """
            {
              "schemaVersion": 1,
              "savedAt": "2026-01-01T00:00:00Z",
              "revision": 4,
              "nodes": [
                { "id": "a", "kind": "Process", "label": "Alpha",
                  "x": null, "y": null, "width": 140, "height": 60,
                  "groupId": null, "locked": false }
              ],
              "edges": [],
              "groups": [],
              "notes": [],
              "images": []
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(store.RootDirectory, "current.json"), json);

        var payload = await store.LoadLatestAsync();
        var scene = new SceneGraph();
        store.Apply(scene, payload!);

        payload!.SchemaVersion.Should().Be(SessionPayload.CurrentSchemaVersion);
        payload.WasMigratedFromV0.Should().BeFalse();
        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.SoftwareSystemArchitecture);
        scene.IntentState.SelectionMode.Should().Be(DiagramIntentSelectionMode.Auto);
        scene.Nodes["a"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
    }

    [Fact]
    public async Task LoadLatest_RejectsFutureSchemaVersion()
    {
        var store = new SessionStore(_tempLocalAppData);
        await File.WriteAllTextAsync(
            Path.Combine(store.RootDirectory, "current.json"),
            """{"schemaVersion":999,"nodes":[],"edges":[],"groups":[],"notes":[]}""");

        (await store.LoadLatestAsync()).Should().BeNull();
    }

    [Fact]
    public void Apply_IgnoresMalformedGeometryAndInvalidIds()
    {
        var store = new SessionStore(_tempLocalAppData);
        var payload = new SessionPayload
        {
            Nodes =
            [
                new NodeRecord("valid", "Process", "Valid", 1, 2, -20, 0, null, true),
                new NodeRecord("bad\nid", "Process", "Invalid", 3, 4, 100, 50, null, false),
            ],
        };
        var scene = new SceneGraph();

        store.Apply(scene, payload);

        scene.Nodes.Should().ContainKey("valid").And.NotContainKey("bad\nid");
        scene.Nodes["valid"].Width.Should().Be(140);
        scene.Nodes["valid"].Height.Should().Be(60);
        scene.Nodes["valid"].Locked.Should().BeTrue();
    }

    [Fact]
    public void Apply_DoesNotDeduplicateDistinctPersistedIds()
    {
        var store = new SessionStore(_tempLocalAppData);
        var payload = new SessionPayload
        {
            Nodes =
            [
                new NodeRecord("a", "Process", "Same", null, null, 140, 60, null, false),
                new NodeRecord("b", "Process", "Same", null, null, 140, 60, null, false),
            ],
            Notes =
            [
                new NoteRecord("n1", "General", "Same note", null, null),
                new NoteRecord("n2", "General", "Same note", null, null),
            ],
        };
        var scene = new SceneGraph();

        store.Apply(scene, payload);

        scene.Nodes.Keys.Should().BeEquivalentTo("a", "b");
        scene.Notes.Keys.Should().BeEquivalentTo("n1", "n2");
    }
}
