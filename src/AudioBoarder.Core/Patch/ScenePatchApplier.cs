using System.Text;
using System.Text.RegularExpressions;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Patch;

/// <summary>
/// Applies a <see cref="ScenePatch"/> to a <see cref="SceneGraph"/> on a
/// best-effort basis with de-duplication. The LLM frequently re-introduces a
/// concept it already drew under a fresh id; this applier recognises that by
/// normalised label and ALIASES the new id onto the existing node (updating it
/// in place and remapping any edges/groups in the same patch) instead of
/// creating a duplicate. Invalid operations are skipped rather than discarding
/// the whole patch.
/// </summary>
public sealed class ScenePatchApplier
{
    public ScenePatchResult Apply(SceneGraph graph, ScenePatch patch)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(patch);

        var ctx = new ApplyContext(graph);
        var ops = patch.Operations;
        var applied = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            try
            {
                ApplyOne(graph, ops[i], i, ctx);
                applied++;
            }
            catch (ScenePatchException)
            {
                // Skip this op, keep applying the rest.
            }
        }
        return new ScenePatchResult(applied, graph.Revision);
    }

    private static void ApplyOne(SceneGraph graph, ScenePatchOperation op, int index, ApplyContext ctx)
    {
        switch (op)
        {
            case ClearScene:
                graph.Clear();
                ctx.Reset();
                break;

            case AddNode add:
            {
                Validate.NonEmpty(index, "add_node.id", add.Id);
                var label = CleanLabel(add.Label);
                var key = Normalize(label);

                // 1) Same id already present → update in place.
                if (graph.Nodes.TryGetValue(add.Id, out var existingById))
                {
                    if (label.Length > 0) existingById.Label = label;
                    existingById.Kind = add.Kind;
                    if (!string.IsNullOrWhiteSpace(add.Icon)) existingById.Icon = add.Icon;
                    if (!string.IsNullOrWhiteSpace(add.Description)) existingById.Description = CleanLabel(add.Description);
                    ctx.MapAlias(add.Id, add.Id);
                    ctx.IndexLabel(key, add.Id);
                    break;
                }
                // 2) A node with the SAME label already exists → alias onto it.
                if (key.Length > 0 && ctx.TryResolveLabel(key, out var existingId)
                    && graph.Nodes.TryGetValue(existingId!, out var existingByLabel))
                {
                    existingByLabel.Kind = add.Kind;
                    if (!string.IsNullOrWhiteSpace(add.Icon)) existingByLabel.Icon = add.Icon;
                    if (!string.IsNullOrWhiteSpace(add.Description)) existingByLabel.Description = CleanLabel(add.Description);
                    ctx.MapAlias(add.Id, existingId!);
                    break;
                }
                // 3) Genuinely new node.
                var addGroupId = add.GroupId is not null && graph.ContainsGroup(add.GroupId) ? add.GroupId : null;
                graph.AddNode(new SceneNode
                {
                    Id = add.Id,
                    Kind = add.Kind,
                    Label = label,
                    GroupId = addGroupId,
                    Icon = string.IsNullOrWhiteSpace(add.Icon) ? null : add.Icon,
                    Description = string.IsNullOrWhiteSpace(add.Description) ? null : CleanLabel(add.Description),
                });
                ctx.MapAlias(add.Id, add.Id);
                ctx.IndexLabel(key, add.Id);
                break;
            }

            case UpdateNode upd:
            {
                Validate.NonEmpty(index, "update_node.id", upd.Id);
                var id = ctx.Resolve(upd.Id);
                if (!graph.Nodes.TryGetValue(id, out var nodeToUpdate))
                    throw new ScenePatchException(index, "update_node", $"node '{upd.Id}' missing");
                if (upd.Kind.HasValue) nodeToUpdate.Kind = upd.Kind.Value;
                if (upd.Label is not null) nodeToUpdate.Label = CleanLabel(upd.Label);
                if (upd.Icon is not null)
                    nodeToUpdate.Icon = upd.Icon.Length == 0 ? null : upd.Icon;
                if (upd.Description is not null)
                    nodeToUpdate.Description = upd.Description.Length == 0 ? null : CleanLabel(upd.Description);
                if (upd.GroupId is not null)
                {
                    if (upd.GroupId.Length == 0) nodeToUpdate.GroupId = null;
                    else if (!graph.ContainsGroup(upd.GroupId))
                        throw new ScenePatchException(index, "update_node", $"group '{upd.GroupId}' missing");
                    else nodeToUpdate.GroupId = upd.GroupId;
                }
                break;
            }

            case DeleteNode del:
            {
                Validate.NonEmpty(index, "delete_node.id", del.Id);
                var id = ctx.Resolve(del.Id);
                if (!graph.ContainsNode(id))
                    throw new ScenePatchException(index, "delete_node", $"node '{del.Id}' missing");
                graph.RemoveNode(id);
                break;
            }

            case Connect conn:
            {
                Validate.NonEmpty(index, "connect.id", conn.Id);
                var from = ctx.Resolve(conn.From);
                var to = ctx.Resolve(conn.To);
                if (!graph.ContainsNode(from))
                    throw new ScenePatchException(index, "connect", $"from node '{conn.From}' missing");
                if (!graph.ContainsNode(to))
                    throw new ScenePatchException(index, "connect", $"to node '{conn.To}' missing");
                if (string.Equals(from, to, StringComparison.Ordinal))
                    throw new ScenePatchException(index, "connect", "self-loops not permitted");
                // De-dup: skip if this id exists OR an edge already links these nodes
                // in the same direction (the model often re-asserts the same arrow).
                if (graph.ContainsEdge(conn.Id)) break;
                if (graph.Edges.Values.Any(e =>
                        string.Equals(e.FromNodeId, from, StringComparison.Ordinal) &&
                        string.Equals(e.ToNodeId, to, StringComparison.Ordinal)))
                    break;
                graph.AddEdge(new SceneEdge
                {
                    Id = conn.Id,
                    FromNodeId = from,
                    ToNodeId = to,
                    Kind = conn.Kind,
                    Label = conn.Label is null ? null : CleanLabel(conn.Label),
                });
                break;
            }

            case Disconnect disc:
                Validate.NonEmpty(index, "disconnect.id", disc.Id);
                if (!graph.ContainsEdge(disc.Id))
                    throw new ScenePatchException(index, "disconnect", $"edge '{disc.Id}' missing");
                graph.RemoveEdge(disc.Id);
                break;

            case Relabel re:
            {
                Validate.NonEmpty(index, "relabel.id", re.Id);
                var id = ctx.Resolve(re.Id);
                if (graph.Nodes.TryGetValue(id, out var nodeRelabel))
                    nodeRelabel.Label = CleanLabel(re.Label);
                else if (graph.Edges.TryGetValue(re.Id, out var edgeRelabel))
                    edgeRelabel.Label = re.Label is null ? null : CleanLabel(re.Label);
                else if (graph.Groups.TryGetValue(re.Id, out var groupRelabel))
                    groupRelabel.Label = CleanLabel(re.Label);
                else
                    throw new ScenePatchException(index, "relabel", $"id '{re.Id}' not found in nodes/edges/groups");
                break;
            }

            case GroupOp g:
                Validate.NonEmpty(index, "group.id", g.Id);
                if (graph.ContainsGroup(g.Id)) break;
                graph.AddGroup(new SceneGroup { Id = g.Id, Label = CleanLabel(g.Label) });
                foreach (var nid in g.NodeIds)
                {
                    var rid = ctx.Resolve(nid);
                    if (graph.ContainsNode(rid)) graph.Nodes[rid].GroupId = g.Id;
                }
                break;

            case UngroupOp ug:
                Validate.NonEmpty(index, "ungroup.id", ug.Id);
                if (!graph.ContainsGroup(ug.Id))
                    throw new ScenePatchException(index, "ungroup", $"group '{ug.Id}' missing");
                graph.RemoveGroup(ug.Id);
                break;

            case NoteUpsert nu:
            {
                Validate.NonEmpty(index, "note_upsert.id", nu.Id);
                var text = (nu.Text ?? string.Empty).Trim();
                // De-dup notes by normalised text so the same observation isn't
                // logged repeatedly under different ids.
                var noteKey = Normalize(text);
                if (noteKey.Length > 0 && ctx.TryResolveNote(noteKey, out var existingNoteId)
                    && !string.Equals(existingNoteId, nu.Id, StringComparison.Ordinal)
                    && graph.Notes.ContainsKey(existingNoteId!))
                    break;
                graph.AddNote(new SceneNote
                {
                    Id = nu.Id,
                    Kind = nu.Kind,
                    Text = text,
                    Owner = nu.Owner,
                    SourceTimestamp = nu.SourceTimestamp,
                });
                ctx.IndexNote(noteKey, nu.Id);
                break;
            }

            case NoteDelete nd:
                Validate.NonEmpty(index, "note_delete.id", nd.Id);
                if (!graph.ContainsNote(nd.Id))
                    throw new ScenePatchException(index, "note_delete", $"note '{nd.Id}' missing");
                graph.RemoveNote(nd.Id);
                break;

            case GenerateImage gi:
            {
                Validate.NonEmpty(index, "generate_image.id", gi.Id);
                Validate.NonEmpty(index, "generate_image.prompt", gi.Prompt);
                if (graph.ContainsImage(gi.Id))
                    throw new ScenePatchException(index, "generate_image", $"image '{gi.Id}' already exists");
                var attachId = gi.AttachToNodeId is null ? null : ctx.Resolve(gi.AttachToNodeId);
                var attachTo = attachId is not null && graph.ContainsNode(attachId) ? attachId : null;
                graph.AddImage(new Imaging.SceneImage
                {
                    Id = gi.Id,
                    Prompt = gi.Prompt,
                    AttachedToNodeId = attachTo,
                    Status = Imaging.ImageGenerationStatus.Pending,
                });
                break;
            }

            case DeleteImage di:
                Validate.NonEmpty(index, "delete_image.id", di.Id);
                if (!graph.ContainsImage(di.Id))
                    throw new ScenePatchException(index, "delete_image", $"image '{di.Id}' missing");
                graph.RemoveImage(di.Id);
                break;

            default:
                throw new ScenePatchException(index, op.GetType().Name, "unknown operation");
        }
    }

    /// <summary>Per-Apply bookkeeping for label/id de-duplication.</summary>
    private sealed class ApplyContext
    {
        private readonly Dictionary<string, string> _labelToId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _noteTextToId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);

        public ApplyContext(SceneGraph g)
        {
            foreach (var n in g.Nodes.Values)
            {
                var key = Normalize(n.Label);
                if (key.Length > 0) _labelToId[key] = n.Id;
            }
            foreach (var note in g.Notes.Values)
            {
                var key = Normalize(note.Text);
                if (key.Length > 0) _noteTextToId[key] = note.Id;
            }
        }

        public void Reset() { _labelToId.Clear(); _noteTextToId.Clear(); _alias.Clear(); }
        public string Resolve(string id) => _alias.TryGetValue(id, out var real) ? real : id;
        public void MapAlias(string from, string to) => _alias[from] = to;
        public void IndexLabel(string key, string id) { if (key.Length > 0) _labelToId[key] = id; }
        public bool TryResolveLabel(string key, out string? id) => _labelToId.TryGetValue(key, out id);
        public void IndexNote(string key, string id) { if (key.Length > 0) _noteTextToId[key] = id; }
        public bool TryResolveNote(string key, out string? id) => _noteTextToId.TryGetValue(key, out id);
    }

    /// <summary>Normalised key for fuzzy label/text identity: lower-cased,
    /// punctuation removed, whitespace collapsed.</summary>
    internal static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Strips JSON/markup artifacts (stray braces, quotes, backslashes, control
    /// chars) and collapses whitespace so a malformed model token like
    /// "Mashreq Users},{" can't render as a garbage node label. Caps length.
    /// </summary>
    internal static string CleanLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is '{' or '}' or '[' or ']' or '"' or '\\' or '`') continue;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        cleaned = cleaned.Trim(',', ';', ':', ' ').Trim();
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd();
        return cleaned;
    }

    private static class Validate
    {
        public static void NonEmpty(int index, string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ScenePatchException(index, field.Split('.')[0], $"{field} is required");
        }
    }
}

public sealed record ScenePatchResult(int OperationsApplied, int Revision);
