using AudioBoarder.Core.Imaging;

namespace AudioBoarder.Core.Scene;

/// <summary>
/// In-memory scene the renderer draws. Mutable and not thread-safe;
/// callers serialize access via the dispatcher / a dedicated mutation thread.
/// </summary>
public sealed class SceneGraph
{
    private readonly Dictionary<string, SceneNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneNote> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneImage> _images = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, SceneNode> Nodes => _nodes;
    public IReadOnlyDictionary<string, SceneEdge> Edges => _edges;
    public IReadOnlyDictionary<string, SceneGroup> Groups => _groups;
    public IReadOnlyDictionary<string, SceneNote> Notes => _notes;
    public IReadOnlyDictionary<string, SceneImage> Images => _images;

    public int Revision { get; internal set; }
    public long GenerationEpoch { get; private set; }
    public DiagramIntentState IntentState { get; private set; } = DiagramIntentState.Default;
    public DiagramIntentState? SuggestedIntentState { get; private set; }

    private long _sequence;

    /// <summary>
    /// Synchronisation root. The diagram is mutated on a background thread
    /// (continuous diagrammer / refine) but rendered on the WPF UI thread.
    /// Both sides lock this object around structural access so the renderer
    /// never enumerates a collection that a patch is concurrently mutating
    /// (which throws "collection was modified" and drops the diagram).
    /// </summary>
    public object SyncRoot { get; } = new();

    internal void AddNode(SceneNode node)
    {
        node.Sequence = ++_sequence;
        _nodes[node.Id] = node;
        Revision++;
    }

    /// <summary>
    /// Marks a node as discussed again so it moves to the front of the recency order.
    /// </summary>
    internal void TouchNode(string id)
    {
        if (_nodes.TryGetValue(id, out var node)) node.Sequence = ++_sequence;
    }
    internal void RemoveNode(string id)
    {
        if (!_nodes.Remove(id)) return;
        var orphanEdges = _edges.Values
            .Where(e => e.FromNodeId == id || e.ToNodeId == id)
            .Select(e => e.Id)
            .ToList();
        foreach (var edgeId in orphanEdges) _edges.Remove(edgeId);
        // Detach images that referenced this node
        foreach (var img in _images.Values)
            if (img.AttachedToNodeId == id) img.AttachedToNodeId = null;
        Revision++;
    }

    internal void ReplaceWith(SceneGraph source)
    {
        _nodes.Clear();
        _edges.Clear();
        _groups.Clear();
        _notes.Clear();
        _images.Clear();
        foreach (var n in source._nodes.Values) _nodes[n.Id] = n;
        foreach (var e in source._edges.Values) _edges[e.Id] = e;
        foreach (var g in source._groups.Values) _groups[g.Id] = g;
        foreach (var n in source._notes.Values) _notes[n.Id] = n;
        foreach (var image in source._images.Values) _images[image.Id] = image;
        _sequence = Math.Max(_sequence, source._sequence);
        Revision = source.Revision;
        GenerationEpoch = source.GenerationEpoch;
        IntentState = source.IntentState;
        SuggestedIntentState = source.SuggestedIntentState;
    }

    internal void AddEdge(SceneEdge edge) { _edges[edge.Id] = edge; Revision++; }
    internal void NotifySemanticChanged() => Revision++;
    public void NotifyGeometryChanged()
    {
        lock (SyncRoot) Revision++;
    }
    internal void RemoveEdge(string id) { if (_edges.Remove(id)) Revision++; }
    internal void AddGroup(SceneGroup g) { _groups[g.Id] = g; Revision++; }
    internal void RemoveGroup(string id)
    {
        if (!_groups.Remove(id)) return;
        foreach (var n in _nodes.Values)
            if (n.GroupId == id) n.GroupId = null;
        foreach (var group in _groups.Values)
            if (group.ParentGroupId == id) group.ParentGroupId = null;
        Revision++;
    }
    internal void AddNote(SceneNote note) { _notes[note.Id] = note; Revision++; }
    internal void RemoveNote(string id) { if (_notes.Remove(id)) Revision++; }

    internal void AddImage(SceneImage img) { _images[img.Id] = img; Revision++; }
    internal void RemoveImage(string id) { if (_images.Remove(id)) Revision++; }
    /// <summary>Public to allow async image-generation tasks to update a previously-added placeholder.</summary>
    public void NotifyImageUpdated(string id)
    {
        lock (SyncRoot)
        {
            if (_images.ContainsKey(id)) Revision++;
        }
    }

    internal void Clear()
    {
        _nodes.Clear();
        _edges.Clear();
        _groups.Clear();
        _notes.Clear();
        _images.Clear();
        SuggestedIntentState = null;
        GenerationEpoch++;
        Revision++;
    }

    public bool ContainsNode(string id) => _nodes.ContainsKey(id);
    public bool ContainsEdge(string id) => _edges.ContainsKey(id);
    public bool ContainsGroup(string id) => _groups.ContainsKey(id);
    public bool ContainsNote(string id) => _notes.ContainsKey(id);
    public bool ContainsImage(string id) => _images.ContainsKey(id);

    public bool TryAddUserNode(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.Id) ||
            string.IsNullOrWhiteSpace(node.Label) ||
            !node.X.HasValue || !node.Y.HasValue ||
            !double.IsFinite(node.X.Value) || !double.IsFinite(node.Y.Value) ||
            !double.IsFinite(node.Width) || !double.IsFinite(node.Height) ||
            node.Width <= 0 || node.Height <= 0)
            return false;

        lock (SyncRoot)
        {
            if (_nodes.ContainsKey(node.Id)) return false;
            node.Locked = true;
            node.LifecycleState = ElementLifecycleState.UserEdited;
            AddNode(node);
            return true;
        }
    }

    public bool TryUpdateNodeGeometry(
        string id, double x, double y, double width, double height, bool locked)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0)
            return false;

        lock (SyncRoot)
        {
            if (!_nodes.TryGetValue(id, out var node)) return false;
            node.X = x;
            node.Y = y;
            node.Width = width;
            node.Height = height;
            node.Locked = locked;
            if (locked) node.LifecycleState = ElementLifecycleState.UserEdited;
            Revision++;
            return true;
        }
    }

    public bool TryMarkNodeUserEdited(string id)
    {
        lock (SyncRoot)
        {
            if (!_nodes.TryGetValue(id, out var node)) return false;
            node.LifecycleState = ElementLifecycleState.UserEdited;
            Revision++;
            return true;
        }
    }

    public bool TryMarkEdgeUserEdited(string id)
    {
        lock (SyncRoot)
        {
            if (!_edges.TryGetValue(id, out var edge)) return false;
            edge.LifecycleState = ElementLifecycleState.UserEdited;
            Revision++;
            return true;
        }
    }

    public bool TryMarkGroupUserEdited(string id)
    {
        lock (SyncRoot)
        {
            if (!_groups.TryGetValue(id, out var group)) return false;
            group.LifecycleState = ElementLifecycleState.UserEdited;
            Revision++;
            return true;
        }
    }

    public IReadOnlyList<ElementLifecycleChange> PromoteProvisionalElements(
        IEnumerable<string> nodeIds,
        IEnumerable<string> edgeIds,
        IEnumerable<string> groupIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(edgeIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        lock (SyncRoot)
        {
            var changes = new List<ElementLifecycleChange>();
            foreach (var id in nodeIds.Distinct(StringComparer.Ordinal))
            {
                if (_nodes.TryGetValue(id, out var node) &&
                    node.LifecycleState == ElementLifecycleState.Provisional)
                {
                    node.LifecycleState = ElementLifecycleState.Confirmed;
                    changes.Add(new ElementLifecycleChange("node", id,
                        ElementLifecycleState.Provisional, ElementLifecycleState.Confirmed));
                }
            }
            foreach (var id in edgeIds.Distinct(StringComparer.Ordinal))
            {
                if (_edges.TryGetValue(id, out var edge) &&
                    edge.LifecycleState == ElementLifecycleState.Provisional)
                {
                    edge.LifecycleState = ElementLifecycleState.Confirmed;
                    changes.Add(new ElementLifecycleChange("edge", id,
                        ElementLifecycleState.Provisional, ElementLifecycleState.Confirmed));
                }
            }
            foreach (var id in groupIds.Distinct(StringComparer.Ordinal))
            {
                if (_groups.TryGetValue(id, out var group) &&
                    group.LifecycleState == ElementLifecycleState.Provisional)
                {
                    group.LifecycleState = ElementLifecycleState.Confirmed;
                    changes.Add(new ElementLifecycleChange("group", id,
                        ElementLifecycleState.Provisional, ElementLifecycleState.Confirmed));
                }
            }
            if (changes.Count > 0) Revision++;
            return changes;
        }
    }

    public void SetIntentState(DiagramIntentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (SyncRoot)
        {
            if (IntentState == state) return;
            var appliedIntentChanged = IntentState.AppliedIntent != state.AppliedIntent;
            IntentState = state;
            if (appliedIntentChanged) GenerationEpoch++;
            Revision++;
        }
    }

    public void SetSuggestedIntentState(DiagramIntentState? state)
    {
        lock (SyncRoot)
        {
            if (SuggestedIntentState == state) return;
            SuggestedIntentState = state;
            Revision++;
        }
    }

    /// <summary>
    /// Restores a persisted recency stamp without allowing the graph's monotonic
    /// sequence to move backwards.
    /// </summary>
    public bool TryRestoreNodeSequence(string id, long sequence)
    {
        if (sequence < 0) return false;
        lock (SyncRoot)
        {
            if (!_nodes.TryGetValue(id, out var node)) return false;
            node.Sequence = sequence;
            _sequence = Math.Max(_sequence, sequence);
            return true;
        }
    }

    /// <summary>
    /// Replaces the graph with an already validated persistence snapshot. This
    /// intentionally bypasses model-patch de-duplication so distinct saved IDs
    /// and notes are restored without semantic loss.
    /// </summary>
    public void RestorePersistedState(
        IEnumerable<SceneNode> nodes,
        IEnumerable<SceneEdge> edges,
        IEnumerable<SceneGroup> groups,
        IEnumerable<SceneNote> notes,
        IEnumerable<SceneImage> images,
        int revision,
        DiagramIntentState? intentState = null,
        DiagramIntentState? suggestedIntentState = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(images);
        lock (SyncRoot)
        {
            var nextGenerationEpoch = GenerationEpoch + 1;
            _nodes.Clear();
            _edges.Clear();
            _groups.Clear();
            _notes.Clear();
            _images.Clear();
            foreach (var group in groups) _groups[group.Id] = group.Clone();
            foreach (var node in nodes) _nodes[node.Id] = node.Clone();
            foreach (var edge in edges) _edges[edge.Id] = edge.Clone();
            foreach (var note in notes) _notes[note.Id] = note.Clone();
            foreach (var image in images) _images[image.Id] = image.Clone();
            _sequence = _nodes.Values.Select(n => n.Sequence).DefaultIfEmpty().Max();
            Revision = Math.Max(0, revision);
            GenerationEpoch = nextGenerationEpoch;
            IntentState = intentState ?? DiagramIntentState.Default with { AppliedRevision = Math.Max(0, revision) };
            SuggestedIntentState = suggestedIntentState;
        }
    }

    public SceneGraph Clone()
    {
        lock (SyncRoot)
        {
            var copy = new SceneGraph
            {
                Revision = Revision,
                GenerationEpoch = GenerationEpoch,
                _sequence = _sequence,
            };
            copy.IntentState = IntentState;
            copy.SuggestedIntentState = SuggestedIntentState;
            foreach (var n in _nodes.Values) copy._nodes[n.Id] = n.Clone();
            foreach (var e in _edges.Values) copy._edges[e.Id] = e.Clone();
            foreach (var g in _groups.Values) copy._groups[g.Id] = g.Clone();
            foreach (var n in _notes.Values) copy._notes[n.Id] = n.Clone();
            foreach (var img in _images.Values) copy._images[img.Id] = img.Clone();
            return copy;
        }
    }
}
