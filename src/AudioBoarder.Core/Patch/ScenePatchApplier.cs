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
    public ScenePatchResult Apply(
        SceneGraph graph,
        ScenePatch patch,
        bool allowClear = true,
        ElementLifecycleState incomingLifecycle = ElementLifecycleState.Provisional)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(patch);

        lock (graph.SyncRoot)
        {
            var working = graph.Clone();
            var ctx = new ApplyContext(working);
            var applied = 0;
            var skipped = 0;

            var indexed = patch.Operations
                .Select((op, index) => new IndexedOperation(index, op))
                .ToArray();

            // Resolve dependencies by phase rather than trusting model output order.
            // Order within a phase remains stable for deterministic last-write wins.
            var phases = new[]
            {
                Ordered(indexed.Where(x => x.Operation is ClearScene)),
                Ordered(indexed.Where(x => x.Operation is GroupOp)),
                Ordered(indexed.Where(x => x.Operation is AddNode)),
                Ordered(indexed.Where(x => x.Operation is UpdateNode)),
            };

            foreach (var phase in phases)
            {
                foreach (var item in phase)
                {
                    if (item.Operation is ClearScene && !allowClear)
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        if (item.Operation is GroupOp group)
                            DefineGroup(working, group, item.Index, incomingLifecycle);
                        else
                            ApplyOne(working, item.Operation, item.Index, ctx, incomingLifecycle);
                        applied++;
                    }
                    catch (ScenePatchException) { skipped++; }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        skipped++;
                    }
                }
            }

            // All groups and nodes now exist, so membership and nesting references
            // are independent of where their declarations appeared in the patch.
            foreach (var item in Ordered(indexed.Where(x => x.Operation is GroupOp)))
                ApplyGroupContainment(working, (GroupOp)item.Operation, ctx);
            BreakGroupCycles(working);

            var remaining = indexed.Where(x =>
                x.Operation is not ClearScene and not GroupOp and not AddNode and not UpdateNode)
                .OrderBy(x => PhaseFor(x.Operation))
                .ThenBy(x => StableKey(x.Operation), StringComparer.Ordinal);
            foreach (var item in remaining)
            {
                try
                {
                    ApplyOne(working, item.Operation, item.Index, ctx, incomingLifecycle);
                    applied++;
                }
                catch (ScenePatchException) { skipped++; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    skipped++;
                }
            }
            graph.ReplaceWith(working);
            return new ScenePatchResult(applied, graph.Revision, skipped);
        }
    }

    private static int PhaseFor(ScenePatchOperation op) => op switch
    {
        Connect => 3,
        Relabel => 4,
        NoteUpsert or GenerateImage => 5,
        DeleteNode or Disconnect or UngroupOp or NoteDelete or DeleteImage => 6,
        _ => 5,
    };

    private static IOrderedEnumerable<IndexedOperation> Ordered(
        IEnumerable<IndexedOperation> operations) =>
        operations.OrderBy(x => StableKey(x.Operation), StringComparer.Ordinal);

    private static string StableKey(ScenePatchOperation op) => op switch
    {
        ClearScene => "clear",
        AddNode x => $"add:{x.Id}:{x.Label}:{x.Kind}:{x.GroupId}",
        UpdateNode x => $"update:{x.Id}:{x.Label}:{x.Kind}:{x.GroupId}",
        DeleteNode x => $"delete-node:{x.Id}",
        Connect x => $"connect:{x.Id}:{x.From}:{x.To}:{x.Kind}:{x.Label}:{x.Protocol}:{x.Payload}:{x.Authentication}:{x.InteractionMode}:{x.DataClassification}:{x.Step}",
        Disconnect x => $"disconnect:{x.Id}",
        Relabel x => $"relabel:{x.Id}:{x.Label}",
        GroupOp x => $"group:{x.Id}:{x.Label}:{x.ParentGroupId}:{x.Subtitle}:{x.BoundaryKind}:{string.Join(',', x.NodeIds ?? [])}",
        UngroupOp x => $"ungroup:{x.Id}",
        NoteUpsert x => $"note:{x.Id}:{x.Kind}:{x.Text}:{x.Owner}",
        NoteDelete x => $"note-delete:{x.Id}",
        GenerateImage x => $"image:{x.Id}:{x.AttachToNodeId}:{x.Prompt}",
        DeleteImage x => $"image-delete:{x.Id}",
        _ => op.GetType().FullName ?? op.GetType().Name,
    };

    private static void ApplyOne(
        SceneGraph graph,
        ScenePatchOperation op,
        int index,
        ApplyContext ctx,
        ElementLifecycleState incomingLifecycle)
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
                    if (existingById.LifecycleState == ElementLifecycleState.UserEdited)
                    {
                        ctx.MapAlias(add.Id, add.Id);
                        break;
                    }
                    if (label.Length > 0) existingById.Label = label;
                    existingById.Kind = add.Kind;
                    if (!string.IsNullOrWhiteSpace(add.Icon)) existingById.Icon = add.Icon;
                    if (!string.IsNullOrWhiteSpace(add.Description)) existingById.Description = CleanLabel(add.Description);
                    if (!string.IsNullOrWhiteSpace(add.GroupId) && graph.ContainsGroup(add.GroupId))
                        existingById.GroupId = add.GroupId;
                    existingById.LifecycleState = MergeLifecycle(
                        existingById.LifecycleState, incomingLifecycle);
                    graph.TouchNode(add.Id);
                    graph.NotifySemanticChanged();
                    ctx.MapAlias(add.Id, add.Id);
                    ctx.IndexLabel(key, add.Id);
                    break;
                }
                // 2) A node with the SAME label already exists → alias onto it.
                if (key.Length > 0 && ctx.TryResolveLabel(key, out var existingId)
                    && graph.Nodes.TryGetValue(existingId!, out var existingByLabel))
                {
                    if (existingByLabel.LifecycleState != ElementLifecycleState.UserEdited)
                    {
                        existingByLabel.Kind = add.Kind;
                        if (!string.IsNullOrWhiteSpace(add.Icon)) existingByLabel.Icon = add.Icon;
                        if (!string.IsNullOrWhiteSpace(add.Description)) existingByLabel.Description = CleanLabel(add.Description);
                        if (!string.IsNullOrWhiteSpace(add.GroupId) && graph.ContainsGroup(add.GroupId))
                            existingByLabel.GroupId = add.GroupId;
                        existingByLabel.LifecycleState = MergeLifecycle(
                            existingByLabel.LifecycleState, incomingLifecycle);
                        graph.TouchNode(existingId!);
                        graph.NotifySemanticChanged();
                    }
                    ctx.MapAlias(add.Id, existingId!);
                    break;
                }
                // 3) Genuinely new node.
                var addGroupId = add.GroupId is not null && graph.ContainsGroup(add.GroupId)
                    ? add.GroupId : null;
                graph.AddNode(new SceneNode
                {
                    Id = add.Id,
                    Kind = add.Kind,
                    Label = label,
                    GroupId = addGroupId,
                    Icon = string.IsNullOrWhiteSpace(add.Icon) ? null : add.Icon,
                    Description = string.IsNullOrWhiteSpace(add.Description) ? null : CleanLabel(add.Description),
                    LifecycleState = incomingLifecycle,
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
                if (nodeToUpdate.LifecycleState == ElementLifecycleState.UserEdited) break;
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
                nodeToUpdate.LifecycleState = MergeLifecycle(
                    nodeToUpdate.LifecycleState, incomingLifecycle);
                graph.TouchNode(id);
                graph.NotifySemanticChanged();
                break;
            }

            case DeleteNode del:
            {
                Validate.NonEmpty(index, "delete_node.id", del.Id);
                var id = ctx.Resolve(del.Id);
                if (!graph.Nodes.TryGetValue(id, out var nodeToDelete))
                    throw new ScenePatchException(index, "delete_node", $"node '{del.Id}' missing");
                // Check AFTER alias resolution: a patch can alias a fresh id onto an
                // existing node by label, so an id-only guard upstream is bypassable.
                // A node the user dragged is deliberate curation — never delete it.
                if (nodeToDelete.Locked ||
                    nodeToDelete.LifecycleState == ElementLifecycleState.UserEdited)
                    throw new ScenePatchException(index, "delete_node", $"node '{id}' is locked by the user");
                graph.RemoveNode(id);
                break;
            }

            case Connect conn:
            {
                Validate.NonEmpty(index, "connect.id", conn.Id);
                Validate.NonEmpty(index, "connect.from", conn.From);
                Validate.NonEmpty(index, "connect.to", conn.To);
                var from = ctx.Resolve(conn.From);
                var to = ctx.Resolve(conn.To);
                if (!graph.ContainsNode(from))
                    throw new ScenePatchException(index, "connect", $"from node '{conn.From}' missing");
                if (!graph.ContainsNode(to))
                    throw new ScenePatchException(index, "connect", $"to node '{conn.To}' missing");
                if (string.Equals(from, to, StringComparison.Ordinal))
                    throw new ScenePatchException(index, "connect", "self-loops not permitted");
                var label = CleanOptional(conn.Label);
                var protocol = CleanOptional(conn.Protocol);
                var payload = CleanOptional(conn.Payload);
                var classification = CleanOptional(conn.DataClassification);
                var authentication = CleanOptional(conn.Authentication);

                // Same id is an idempotent upsert, including endpoints. A deep pass
                // can therefore enrich the shallow edge created by a live pass.
                if (graph.Edges.TryGetValue(ctx.ResolveEdge(conn.Id), out var edgeById))
                {
                    if (edgeById.LifecycleState == ElementLifecycleState.UserEdited) break;
                    edgeById.FromNodeId = from;
                    edgeById.ToNodeId = to;
                    edgeById.Kind = conn.Kind;
                    edgeById.Label = label;
                    edgeById.Step = conn.Step is > 0 ? conn.Step : null;
                    edgeById.Protocol = protocol;
                    edgeById.Payload = payload;
                    edgeById.DataClassification = classification;
                    edgeById.Authentication = authentication;
                    edgeById.InteractionMode = conn.InteractionMode;
                    edgeById.LifecycleState = MergeLifecycle(
                        edgeById.LifecycleState, incomingLifecycle);
                    graph.TouchNode(from);
                    graph.TouchNode(to);
                    graph.NotifySemanticChanged();
                    break;
                }

                // Distinct same-direction interactions survive. Only an exact
                // semantic duplicate aliases to the existing edge.
                var duplicate = graph.Edges.Values.FirstOrDefault(e =>
                    string.Equals(e.FromNodeId, from, StringComparison.Ordinal) &&
                    string.Equals(e.ToNodeId, to, StringComparison.Ordinal) &&
                    SemanticValue(e.Label) == SemanticValue(label) &&
                    SemanticValue(e.Protocol) == SemanticValue(protocol) &&
                    SemanticValue(e.Payload) == SemanticValue(payload) &&
                    SemanticValue(e.Authentication) == SemanticValue(authentication) &&
                    e.InteractionMode == conn.InteractionMode &&
                    SemanticValue(e.DataClassification) == SemanticValue(classification));
                if (duplicate is not null)
                {
                    duplicate.LifecycleState = MergeLifecycle(
                        duplicate.LifecycleState, incomingLifecycle);
                    ctx.MapEdgeAlias(conn.Id, duplicate.Id);
                    break;
                }
                graph.AddEdge(new SceneEdge
                {
                    Id = conn.Id,
                    FromNodeId = from,
                    ToNodeId = to,
                    Kind = conn.Kind,
                    Label = label,
                    Step = conn.Step is > 0 ? conn.Step : null,
                    Protocol = protocol,
                    Payload = payload,
                    DataClassification = classification,
                    Authentication = authentication,
                    InteractionMode = conn.InteractionMode,
                    LifecycleState = incomingLifecycle,
                });
                ctx.MapEdgeAlias(conn.Id, conn.Id);
                // Both endpoints are part of the live discussion again.
                graph.TouchNode(from);
                graph.TouchNode(to);
                break;
            }

            case Disconnect disc:
                Validate.NonEmpty(index, "disconnect.id", disc.Id);
                var disconnectId = ctx.ResolveEdge(disc.Id);
                if (!graph.Edges.TryGetValue(disconnectId, out var edgeToDelete))
                    throw new ScenePatchException(index, "disconnect", $"edge '{disc.Id}' missing");
                if (edgeToDelete.LifecycleState == ElementLifecycleState.UserEdited)
                    throw new ScenePatchException(index, "disconnect", $"edge '{disconnectId}' is user edited");
                graph.RemoveEdge(disconnectId);
                break;

            case Relabel re:
            {
                Validate.NonEmpty(index, "relabel.id", re.Id);
                var id = ctx.Resolve(re.Id);
                if (graph.Nodes.TryGetValue(id, out var nodeRelabel))
                {
                    if (nodeRelabel.LifecycleState == ElementLifecycleState.UserEdited) break;
                    nodeRelabel.Label = CleanLabel(re.Label);
                    nodeRelabel.LifecycleState = MergeLifecycle(
                        nodeRelabel.LifecycleState, incomingLifecycle);
                    graph.NotifySemanticChanged();
                }
                else if (graph.Edges.TryGetValue(ctx.ResolveEdge(re.Id), out var edgeRelabel))
                {
                    if (edgeRelabel.LifecycleState == ElementLifecycleState.UserEdited) break;
                    edgeRelabel.Label = re.Label is null ? null : CleanLabel(re.Label);
                    edgeRelabel.LifecycleState = MergeLifecycle(
                        edgeRelabel.LifecycleState, incomingLifecycle);
                    graph.NotifySemanticChanged();
                }
                else if (graph.Groups.TryGetValue(re.Id, out var groupRelabel))
                {
                    if (groupRelabel.LifecycleState == ElementLifecycleState.UserEdited) break;
                    groupRelabel.Label = CleanLabel(re.Label);
                    groupRelabel.LifecycleState = MergeLifecycle(
                        groupRelabel.LifecycleState, incomingLifecycle);
                    graph.NotifySemanticChanged();
                }
                else
                    throw new ScenePatchException(index, "relabel", $"id '{re.Id}' not found in nodes/edges/groups");
                break;
            }

            case GroupOp g:
                DefineGroup(graph, g, index, incomingLifecycle);
                ApplyGroupContainment(graph, g, ctx);
                break;

            case UngroupOp ug:
                Validate.NonEmpty(index, "ungroup.id", ug.Id);
                if (!graph.Groups.TryGetValue(ug.Id, out var groupToDelete))
                    throw new ScenePatchException(index, "ungroup", $"group '{ug.Id}' missing");
                if (groupToDelete.LifecycleState == ElementLifecycleState.UserEdited)
                    throw new ScenePatchException(index, "ungroup", $"group '{ug.Id}' is user edited");
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
                    // The model is not asked for a timestamp, so stamp arrival time.
                    // Without this every note sorts equal and the rail shows no time
                    // at all — and eviction would fall back to lexicographic id order
                    // instead of dropping the oldest chatter first. Re-upserting the
                    // same id keeps its original time so a note doesn't look new.
                    SourceTimestamp = nu.SourceTimestamp
                        ?? (graph.Notes.TryGetValue(nu.Id, out var priorNote)
                                ? priorNote.SourceTimestamp
                                : null)
                        ?? DateTimeOffset.UtcNow,
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
        private readonly Dictionary<string, string> _edgeAlias = new(StringComparer.Ordinal);

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

        public void Reset()
        {
            _labelToId.Clear();
            _noteTextToId.Clear();
            _alias.Clear();
            _edgeAlias.Clear();
        }
        // Null-safe: the model sometimes omits a required id entirely, and a null
        // dictionary key throws ArgumentNullException rather than returning false.
        public string Resolve(string? id) =>
            id is null ? string.Empty : _alias.TryGetValue(id, out var real) ? real : id;
        public void MapAlias(string? from, string to) { if (!string.IsNullOrEmpty(from)) _alias[from] = to; }
        public string ResolveEdge(string? id) =>
            id is null ? string.Empty : _edgeAlias.TryGetValue(id, out var real) ? real : id;
        public void MapEdgeAlias(string? from, string to)
        {
            if (!string.IsNullOrEmpty(from)) _edgeAlias[from] = to;
        }
        public void IndexLabel(string? key, string id) { if (!string.IsNullOrEmpty(key)) _labelToId[key] = id; }
        public bool TryResolveLabel(string? key, out string? id)
        {
            if (string.IsNullOrEmpty(key)) { id = null; return false; }
            return _labelToId.TryGetValue(key, out id);
        }

        public void IndexNote(string? key, string id) { if (!string.IsNullOrEmpty(key)) _noteTextToId[key] = id; }
        public bool TryResolveNote(string? key, out string? id)
        {
            if (string.IsNullOrEmpty(key)) { id = null; return false; }
            return _noteTextToId.TryGetValue(key, out id);
        }
    }

    private static void DefineGroup(
        SceneGraph graph,
        GroupOp op,
        int index,
        ElementLifecycleState incomingLifecycle)
    {
        Validate.NonEmpty(index, "group.id", op.Id);
        if (!graph.Groups.TryGetValue(op.Id, out var group))
        {
            graph.AddGroup(new SceneGroup
            {
                Id = op.Id,
                Label = CleanLabel(op.Label),
                Subtitle = CleanOptional(op.Subtitle),
                BoundaryKind = op.BoundaryKind,
                LifecycleState = incomingLifecycle,
            });
            return;
        }
        if (group.LifecycleState == ElementLifecycleState.UserEdited) return;
        if (!string.IsNullOrWhiteSpace(op.Label)) group.Label = CleanLabel(op.Label);
        if (op.Subtitle is not null) group.Subtitle = CleanOptional(op.Subtitle);
        group.BoundaryKind = op.BoundaryKind;
        group.LifecycleState = MergeLifecycle(
            group.LifecycleState, incomingLifecycle);
        graph.NotifySemanticChanged();
    }

    /// <summary>
    /// Lifecycle is monotonic. A fast pass may reassert or enrich confirmed content,
    /// but it must never downgrade that content to provisional and make it eligible
    /// for automatic eviction or deep cleanup.
    /// </summary>
    private static ElementLifecycleState MergeLifecycle(
        ElementLifecycleState existing,
        ElementLifecycleState incoming) =>
        (ElementLifecycleState)Math.Max((int)existing, (int)incoming);

    private static void ApplyGroupContainment(SceneGraph graph, GroupOp op, ApplyContext ctx)
    {
        if (!graph.Groups.TryGetValue(op.Id, out var group)) return;
        if (group.LifecycleState != ElementLifecycleState.UserEdited)
        {
            if (string.IsNullOrWhiteSpace(op.ParentGroupId))
                group.ParentGroupId = null;
            else if (graph.ContainsGroup(op.ParentGroupId))
                group.ParentGroupId = op.ParentGroupId;
        }

        foreach (var nodeId in op.NodeIds ?? Array.Empty<string>())
        {
            var resolved = ctx.Resolve(nodeId);
            if (graph.Nodes.TryGetValue(resolved, out var node) &&
                node.LifecycleState != ElementLifecycleState.UserEdited)
            {
                node.GroupId = op.Id;
            }
        }
        graph.NotifySemanticChanged();
    }

    private static string? CleanOptional(string? value) =>
        value is null ? null : string.IsNullOrWhiteSpace(value) ? null : CleanLabel(value);

    private static string SemanticValue(string? value) => Normalize(value);

    private static void BreakGroupCycles(SceneGraph graph)
    {
        while (true)
        {
            List<string>? cycle = null;
            foreach (var start in graph.Groups.Keys.Order(StringComparer.Ordinal))
            {
                var path = new List<string>();
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                var cursor = start;
                while (graph.Groups.TryGetValue(cursor, out var group) &&
                       group.ParentGroupId is { } parent)
                {
                    if (positions.TryGetValue(cursor, out var cycleStart))
                    {
                        cycle = path.Skip(cycleStart).ToList();
                        break;
                    }
                    positions[cursor] = path.Count;
                    path.Add(cursor);
                    cursor = parent;
                }
                if (cycle is not null) break;
            }
            if (cycle is null || cycle.Count == 0) return;

            // Deterministic policy: detach the lexicographically smallest boundary
            // in the cycle, independent of patch operation order.
            var detach = cycle.Min(StringComparer.Ordinal)!;
            if (graph.Groups[detach].LifecycleState == ElementLifecycleState.UserEdited)
            {
                var editable = cycle
                    .Where(id => graph.Groups[id].LifecycleState != ElementLifecycleState.UserEdited)
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (editable is null) return;
                detach = editable;
            }
            graph.Groups[detach].ParentGroupId = null;
            graph.NotifySemanticChanged();
        }
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

    private sealed record IndexedOperation(int Index, ScenePatchOperation Operation);
}

public sealed record ScenePatchResult(int OperationsApplied, int Revision, int OperationsSkipped = 0);
