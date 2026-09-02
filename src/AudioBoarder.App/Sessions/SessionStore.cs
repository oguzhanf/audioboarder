using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBoarder.Core.Imaging;
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
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private long _latestSaveVersion;

    public SessionStore(ILogger<SessionStore>? logger = null)
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "sessions");
        Directory.CreateDirectory(_root);
        _logger = logger ?? NullLogger<SessionStore>.Instance;
    }

    public string RootDirectory => _root;

    public async Task<SessionSaveResult> SaveAsync(SceneGraph scene, CancellationToken ct = default)
    {
        var saveVersion = Interlocked.Increment(ref _latestSaveVersion);
        await _fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (saveVersion != Volatile.Read(ref _latestSaveVersion))
                return SessionSaveResult.Superseded;

            SessionPayload payload;
            lock (scene.SyncRoot)
            {
                payload = new SessionPayload
                {
                    SchemaVersion = SessionPayload.CurrentSchemaVersion,
                    SavedAt = DateTimeOffset.UtcNow,
                    Revision = scene.Revision,
                    IntentState = IntentStateRecord.From(scene.IntentState),
                    SuggestedIntentState = scene.SuggestedIntentState is null
                        ? null : IntentStateRecord.From(scene.SuggestedIntentState),
                    Nodes = scene.Nodes.Values.Select(n => new NodeRecord(n.Id, n.Kind.ToString(), n.Label,
                        n.X, n.Y, n.Width, n.Height, n.GroupId, n.Locked, n.Icon, n.Description, n.Sequence,
                        n.LifecycleState.ToString())).ToArray(),
                    Edges = scene.Edges.Values.Select(e => new EdgeRecord(
                        e.Id, e.FromNodeId, e.ToNodeId, e.Kind.ToString(), e.Label, e.Step,
                        e.Protocol, e.Payload, e.DataClassification, e.Authentication,
                        e.InteractionMode?.ToString(), e.LifecycleState.ToString())).ToArray(),
                    Groups = scene.Groups.Values.Select(g => new GroupRecord(
                        g.Id, g.Label, g.ParentGroupId, g.Subtitle,
                        g.BoundaryKind.ToString(), g.LifecycleState.ToString())).ToArray(),
                    Notes = scene.Notes.Values.Select(n => new NoteRecord(n.Id, n.Kind.ToString(), n.Text, n.Owner, n.SourceTimestamp)).ToArray(),
                    Images = scene.Images.Values.Select(i => new ImageRecord(
                        i.Id, i.Prompt, i.AttachedToNodeId, i.PngBytes, i.CreatedAt,
                        i.Status.ToString(), i.ErrorMessage, i.ModelName, i.Elapsed)).ToArray(),
                };
            }

            var path = Path.Combine(_root, "current.json");
            var tempPath = Path.Combine(_root, $"current.{saveVersion}.tmp");
            try
            {
                await using (var fs = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(fs, payload, JsonOpts, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                if (saveVersion == Volatile.Read(ref _latestSaveVersion))
                {
                    File.Move(tempPath, path, overwrite: true);
                    return SessionSaveResult.Saved;
                }
                return SessionSaveResult.Superseded;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Session save failed; category={Category}", SafeIoCategory(ex));
            throw;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<SessionPayload?> LoadLatestAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_root, "current.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.TryGetProperty("schemaVersion", out var versionElement)
                ? versionElement.GetInt32()
                : 0;
            if (version > SessionPayload.CurrentSchemaVersion || version < 0)
            {
                _logger.LogWarning(
                    "Prior session schema unsupported; version={Version} supported={Supported}",
                    version, SessionPayload.CurrentSchemaVersion);
                return null;
            }
            var payload = JsonSerializer.Deserialize<SessionPayload>(json, JsonOpts);
            if (payload is null) return null;
            return Migrate(payload, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not read prior session; category={Category}",
                ex is JsonException ? "invalid_session_json" : "session_read_failure");
            return null;
        }
    }

    public void Apply(SceneGraph scene, SessionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(payload);

        var groups = (payload.Groups ?? Array.Empty<GroupRecord>())
            .Where(g => IsValidId(g.Id))
            .GroupBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(g => g.Id, StringComparer.Ordinal);
        var restoredGroups = groups.Values.Select(g => new SceneGroup
        {
            Id = g.Id,
            Label = g.Label ?? string.Empty,
            Subtitle = g.Subtitle,
            ParentGroupId = IsValidId(g.ParentGroupId) &&
                            groups.ContainsKey(g.ParentGroupId!) &&
                            !WouldCreateGroupCycle(groups, g.Id, g.ParentGroupId!)
                ? g.ParentGroupId
                : null,
            BoundaryKind = ParseEnum(g.BoundaryKind, BoundaryKind.Generic),
            LifecycleState = ParseEnum(g.LifecycleState, ElementLifecycleState.Confirmed),
        }).ToArray();

        var nodes = (payload.Nodes ?? Array.Empty<NodeRecord>())
            .Where(n => IsValidId(n.Id))
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Select(n =>
            {
                if (!Enum.TryParse<NodeKind>(n.Kind, true, out var kind)) kind = NodeKind.Process;
                var validGeometry = IsValidGeometry(n);
                return new SceneNode
                {
                    Id = n.Id,
                    Kind = kind,
                    Label = n.Label ?? string.Empty,
                    Icon = n.Icon,
                    Description = n.Description,
                    X = validGeometry ? n.X : null,
                    Y = validGeometry ? n.Y : null,
                    Width = validGeometry ? n.Width : 140,
                    Height = validGeometry ? n.Height : 60,
                    GroupId = IsValidId(n.GroupId) && groups.ContainsKey(n.GroupId!) ? n.GroupId : null,
                    Locked = n.Locked,
                    Sequence = Math.Max(0, n.Sequence),
                    LifecycleState = ParseEnum(
                        n.LifecycleState,
                        n.Locked ? ElementLifecycleState.UserEdited : ElementLifecycleState.Confirmed),
                };
            })
            .ToArray();
        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var edges = (payload.Edges ?? Array.Empty<EdgeRecord>())
            .Where(e => IsValidId(e.Id) && IsValidId(e.FromId) && IsValidId(e.ToId))
            .Where(e => nodeIds.Contains(e.FromId) && nodeIds.Contains(e.ToId) &&
                        !string.Equals(e.FromId, e.ToId, StringComparison.Ordinal))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Select(e =>
            {
                if (!Enum.TryParse<EdgeKind>(e.Kind, true, out var kind)) kind = EdgeKind.Flow;
                return new SceneEdge
                {
                    Id = e.Id,
                    FromNodeId = e.FromId,
                    ToNodeId = e.ToId,
                    Kind = kind,
                    Label = e.Label,
                    Step = e.Step is > 0 ? e.Step : null,
                    Protocol = e.Protocol,
                    Payload = e.Payload,
                    DataClassification = e.DataClassification,
                    Authentication = e.Authentication,
                    InteractionMode = string.IsNullOrWhiteSpace(e.InteractionMode)
                        ? null
                        : Enum.TryParse<InteractionMode>(e.InteractionMode, true, out var interactionMode)
                            ? interactionMode
                            : null,
                    LifecycleState = ParseEnum(e.LifecycleState, ElementLifecycleState.Confirmed),
                };
            })
            .ToArray();

        var notes = (payload.Notes ?? Array.Empty<NoteRecord>())
            .Where(n => IsValidId(n.Id))
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Select(n =>
            {
                if (!Enum.TryParse<NoteKind>(n.Kind, true, out var kind)) kind = NoteKind.General;
                return new SceneNote
                {
                    Id = n.Id,
                    Kind = kind,
                    Text = n.Text ?? string.Empty,
                    Owner = n.Owner,
                    SourceTimestamp = n.SourceTimestamp,
                };
            })
            .ToArray();

        var images = (payload.Images ?? Array.Empty<ImageRecord>())
            .Where(i => IsValidId(i.Id))
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Select(i =>
            {
                if (!Enum.TryParse<ImageGenerationStatus>(i.Status, true, out var status))
                    status = ImageGenerationStatus.Failed;
                return new SceneImage
                {
                    Id = i.Id,
                    Prompt = i.Prompt ?? string.Empty,
                    AttachedToNodeId = IsValidId(i.AttachedToNodeId) &&
                                       nodeIds.Contains(i.AttachedToNodeId!)
                        ? i.AttachedToNodeId
                        : null,
                    PngBytes = i.PngBytes,
                    CreatedAt = i.CreatedAt,
                    Status = status,
                    ErrorMessage = i.ErrorMessage,
                    ModelName = i.ModelName,
                    Elapsed = i.Elapsed,
                };
            })
            .ToArray();

        scene.RestorePersistedState(
            nodes, edges, restoredGroups, notes, images, payload.Revision,
            ParseIntentState(payload.IntentState, payload.Revision),
            ParseIntentState(payload.SuggestedIntentState, payload.Revision));
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _latestSaveVersion);
        await _fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_root, "current.json");
            if (File.Exists(path)) File.Delete(path);
            foreach (var tempPath in Directory.EnumerateFiles(_root, "current.*.tmp"))
                File.Delete(tempPath);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static SessionPayload Migrate(SessionPayload payload, int sourceVersion)
    {
        if (sourceVersion <= 1)
        {
            payload.SchemaVersion = SessionPayload.CurrentSchemaVersion;
            payload.WasMigratedFromV0 = sourceVersion == 0;
            payload.Nodes ??= Array.Empty<NodeRecord>();
            payload.Edges ??= Array.Empty<EdgeRecord>();
            payload.Groups ??= Array.Empty<GroupRecord>();
            payload.Notes ??= Array.Empty<NoteRecord>();
            payload.Images ??= Array.Empty<ImageRecord>();
            payload.IntentState ??= IntentStateRecord.From(
                DiagramIntentState.Default with { AppliedRevision = Math.Max(0, payload.Revision) });
        }
        return payload;
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private static DiagramIntentState? ParseIntentState(IntentStateRecord? record, int revision)
    {
        if (record is null) return null;
        var intent = ParseEnum(record.AppliedIntent, DiagramIntent.SoftwareSystemArchitecture);
        var mode = ParseEnum(record.SelectionMode, DiagramIntentSelectionMode.Auto);
        var confidence = double.IsFinite(record.Confidence)
            ? Math.Clamp(record.Confidence, 0, 1)
            : 0;
        var reason = string.IsNullOrWhiteSpace(record.Reason)
            ? "Restored intent state"
            : record.Reason.Trim();
        if (reason.Length > 160) reason = reason[..160];
        return new DiagramIntentState(
            intent, mode, confidence, reason,
            Math.Max(0, record.AppliedRevision ?? revision));
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 128 &&
        !id.Any(char.IsControl);

    private static bool IsValidGeometry(NodeRecord node) =>
        (!node.X.HasValue || double.IsFinite(node.X.Value)) &&
        (!node.Y.HasValue || double.IsFinite(node.Y.Value)) &&
        node.X.HasValue == node.Y.HasValue &&
        double.IsFinite(node.Width) &&
        double.IsFinite(node.Height) &&
        node.Width > 0 &&
        node.Height > 0;

    private static bool WouldCreateGroupCycle(
        IReadOnlyDictionary<string, GroupRecord> groups,
        string groupId,
        string parentId)
    {
        var cursor = parentId;
        for (var guard = 0; guard < 128; guard++)
        {
            if (string.Equals(cursor, groupId, StringComparison.Ordinal)) return true;
            if (!groups.TryGetValue(cursor, out var parent) ||
                string.IsNullOrWhiteSpace(parent.ParentGroupId))
                return false;
            cursor = parent.ParentGroupId;
        }
        return true;
    }

    private static string SafeIoCategory(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access_denied",
        IOException => "io_failure",
        JsonException => "serialization_failure",
        OperationCanceledException => "cancelled",
        _ => "session_save_failure",
    };
}

public sealed class SessionPayload
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonIgnore]
    public bool WasMigratedFromV0 { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public int Revision { get; set; }
    public IntentStateRecord? IntentState { get; set; }
    public IntentStateRecord? SuggestedIntentState { get; set; }
    public NodeRecord[] Nodes { get; set; } = Array.Empty<NodeRecord>();
    public EdgeRecord[] Edges { get; set; } = Array.Empty<EdgeRecord>();
    public GroupRecord[] Groups { get; set; } = Array.Empty<GroupRecord>();
    public NoteRecord[] Notes { get; set; } = Array.Empty<NoteRecord>();
    public ImageRecord[] Images { get; set; } = Array.Empty<ImageRecord>();
}

public sealed record NodeRecord(string Id, string Kind, string Label,
    double? X, double? Y, double Width, double Height, string? GroupId, bool Locked,
    string? Icon = null, string? Description = null, long Sequence = 0,
    string? LifecycleState = null);
public sealed record EdgeRecord(
    string Id, string FromId, string ToId, string Kind, string? Label, int? Step = null,
    string? Protocol = null, string? Payload = null, string? DataClassification = null,
    string? Authentication = null, string? InteractionMode = null,
    string? LifecycleState = null);
public sealed record GroupRecord(
    string Id, string Label, string? ParentGroupId = null, string? Subtitle = null,
    string? BoundaryKind = null, string? LifecycleState = null);
public sealed record IntentStateRecord(
    string AppliedIntent,
    string SelectionMode,
    double Confidence,
    string Reason,
    int? AppliedRevision)
{
    public static IntentStateRecord From(DiagramIntentState state) => new(
        state.AppliedIntent.ToString(),
        state.SelectionMode.ToString(),
        state.Confidence,
        state.Reason,
        state.AppliedRevision);
}
public sealed record NoteRecord(string Id, string Kind, string Text, string? Owner, DateTimeOffset? SourceTimestamp);
public sealed record ImageRecord(
    string Id,
    string Prompt,
    string? AttachedToNodeId,
    byte[]? PngBytes,
    DateTimeOffset CreatedAt,
    string Status,
    string? ErrorMessage = null,
    string? ModelName = null,
    TimeSpan? Elapsed = null);

public enum SessionSaveResult
{
    Saved,
    Superseded,
}
