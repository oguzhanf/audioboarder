using System.IO;
using System.Text.Json;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.App.Sessions;

/// <summary>
/// Persists each successfully generated scene to <c>%LOCALAPPDATA%\AudioBoarder\sessions</c>
/// so the user can recover work after a crash or restart. Only scene-graph state is saved;
/// raw transcript is not written to disk unless the user explicitly exports it.
/// </summary>
public sealed class SessionStore
{
    private readonly string _root;
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ILogger<SessionStore>? logger = null)
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "sessions");
        Directory.CreateDirectory(_root);
        _logger = logger ?? NullLogger<SessionStore>.Instance;
    }

    public string RootDirectory => _root;

    public async Task SaveAsync(SceneGraph scene, CancellationToken ct = default)
    {
        try
        {
            var payload = new SessionPayload
            {
                SavedAt = DateTimeOffset.UtcNow,
                Revision = scene.Revision,
                Nodes = scene.Nodes.Values.Select(n => new NodeRecord(n.Id, n.Kind.ToString(), n.Label,
                    n.X, n.Y, n.Width, n.Height, n.GroupId, n.Locked, n.Icon, n.Description)).ToArray(),
                Edges = scene.Edges.Values.Select(e => new EdgeRecord(e.Id, e.FromNodeId, e.ToNodeId, e.Kind.ToString(), e.Label)).ToArray(),
                Groups = scene.Groups.Values.Select(g => new GroupRecord(g.Id, g.Label)).ToArray(),
                Notes = scene.Notes.Values.Select(n => new NoteRecord(n.Id, n.Kind.ToString(), n.Text, n.Owner, n.SourceTimestamp)).ToArray(),
            };
            var path = Path.Combine(_root, "current.json");
            await using var fs = File.Create(path);
            await JsonSerializer.SerializeAsync(fs, payload, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autosave failed");
        }
    }

    public async Task<SessionPayload?> LoadLatestAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_root, "current.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SessionPayload>(fs, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read prior session");
            return null;
        }
    }

    public void Apply(SceneGraph scene, SessionPayload payload)
    {
        var applier = new ScenePatchApplier();
        var ops = new List<ScenePatchOperation> { new ClearScene() };

        foreach (var g in payload.Groups)
            ops.Add(new GroupOp(g.Id, g.Label, Array.Empty<string>()));

        foreach (var n in payload.Nodes)
        {
            if (!Enum.TryParse<NodeKind>(n.Kind, true, out var kind)) kind = NodeKind.Process;
            ops.Add(new AddNode(n.Id, kind, n.Label, n.GroupId, Position: null, Icon: n.Icon, Description: n.Description));
        }
        foreach (var e in payload.Edges)
        {
            if (!Enum.TryParse<EdgeKind>(e.Kind, true, out var kind)) kind = EdgeKind.Flow;
            ops.Add(new Connect(e.Id, e.FromId, e.ToId, kind, e.Label));
        }
        foreach (var n in payload.Notes)
        {
            if (!Enum.TryParse<NoteKind>(n.Kind, true, out var kind)) kind = NoteKind.General;
            ops.Add(new NoteUpsert(n.Id, kind, n.Text, n.Owner, n.SourceTimestamp));
        }
        applier.Apply(scene, new ScenePatch(ops));

        foreach (var n in payload.Nodes)
        {
            if (scene.Nodes.TryGetValue(n.Id, out var live))
            {
                live.X = n.X;
                live.Y = n.Y;
                live.Width = n.Width;
                live.Height = n.Height;
                live.Locked = n.Locked;
            }
        }
    }

    public void Clear()
    {
        var path = Path.Combine(_root, "current.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

public sealed class SessionPayload
{
    public DateTimeOffset SavedAt { get; set; }
    public int Revision { get; set; }
    public NodeRecord[] Nodes { get; set; } = Array.Empty<NodeRecord>();
    public EdgeRecord[] Edges { get; set; } = Array.Empty<EdgeRecord>();
    public GroupRecord[] Groups { get; set; } = Array.Empty<GroupRecord>();
    public NoteRecord[] Notes { get; set; } = Array.Empty<NoteRecord>();
}

public sealed record NodeRecord(string Id, string Kind, string Label,
    double? X, double? Y, double Width, double Height, string? GroupId, bool Locked,
    string? Icon = null, string? Description = null);
public sealed record EdgeRecord(string Id, string FromId, string ToId, string Kind, string? Label);
public sealed record GroupRecord(string Id, string Label);
public sealed record NoteRecord(string Id, string Kind, string Text, string? Owner, DateTimeOffset? SourceTimestamp);
