using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Layout;

/// <summary>
/// Lays out each conversation island by its edges, then packs independent topics
/// compactly. Group frames are indivisible blocks at their parent's layout level.
/// </summary>
public sealed class MeetingWhiteboardLayoutEngine : ILayoutEngine
{
    public string Name => "MeetingWhiteboardLayoutEngine";

    public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (graph.Nodes.Count == 0) return new LayoutResult(0, 0, 0);

        return new Arrangement(graph, options).Apply();
    }

    private sealed class Arrangement
    {
        private readonly SceneGraph _graph;
        private readonly LayoutOptions _options;
        private readonly IReadOnlyDictionary<string, NodeGeometry> _geometry;
        private readonly List<Box> _fixedObstacles;
        private readonly double _gapX;
        private readonly double _gapY;
        private readonly double _padding;
        private readonly HashSet<string> _assigned = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visitedGroups = new(StringComparer.Ordinal);
        private readonly ArchitectureIntentLayoutEngine _flow = new(DiagramIntent.SoftwareSystemArchitecture);
        private readonly MindMapLayoutEngine _map = new();

        public Arrangement(SceneGraph graph, LayoutOptions options)
        {
            _graph = graph;
            _options = options;
            _geometry = LayoutSnapshot.Capture(graph).Nodes;
            _gapX = double.IsFinite(options.HorizontalSpacing) ? Math.Max(24, options.HorizontalSpacing) : 60;
            _gapY = double.IsFinite(options.VerticalSpacing) ? Math.Max(24, options.VerticalSpacing) : 80;
            _padding = double.IsFinite(options.Padding) ? Math.Max(0, options.Padding) : 40;
            _fixedObstacles = graph.Nodes.Values.Where(IsPinned).OrderBy(n => n.Id, StringComparer.Ordinal)
                .Select(n => NodeBox(n.Id, n.X!.Value, n.Y!.Value)).ToList();
        }

        public LayoutResult Apply()
        {
            var blocks = new List<Block>();
            foreach (var group in _graph.Groups.Values
                         .Where(g => string.IsNullOrEmpty(g.ParentGroupId) ||
                                     !_graph.Groups.ContainsKey(g.ParentGroupId))
                         .OrderBy(g => g.Id, StringComparer.Ordinal))
            {
                var block = BuildGroup(group.Id);
                if (block is not null) blocks.Add(block);
            }

            // Orphaned memberships (including invalid cyclic groups) must not lose nodes.
            blocks.AddRange(_graph.Nodes.Values.Where(n => !_assigned.Contains(n.Id))
                .OrderBy(n => n.Id, StringComparer.Ordinal).Select(NodeBlock));
            var arranged = ArrangeLevel(blocks, insideGroup: false);
            var positioned = 0;
            foreach (var node in _graph.Nodes.Values)
            {
                if (IsPinned(node)) continue;
                var point = arranged.Positions[node.Id];
                node.X = point.X;
                node.Y = point.Y;
                positioned++;
            }

            var snapshot = LayoutSnapshot.Capture(_graph);
            var bounds = Box.Enclose(snapshot.Nodes.Values
                .Select(n => new Box(n.Left, n.Top, n.Width, n.Height))
                .Concat(snapshot.Groups.Values.Select(g => new Box(g.Left, g.Top, g.Width, g.Height))));
            return new LayoutResult(positioned,
                bounds.Right - Math.Min(0, bounds.Left) + _padding,
                bounds.Bottom - Math.Min(0, bounds.Top) + _padding);
        }

        private bool IsPinned(SceneNode node) =>
            !_options.ReflowPinned && node.Locked &&
            node.X is { } x && double.IsFinite(x) &&
            node.Y is { } y && double.IsFinite(y);

        private Box NodeBox(string id, double x, double y)
        {
            var geometry = _geometry[id];
            return new Box(x - geometry.Width / 2, y - geometry.Height / 2, geometry.Width, geometry.Height);
        }

        private Block NodeBlock(SceneNode node)
        {
            _assigned.Add(node.Id);
            var pinned = IsPinned(node);
            var point = pinned ? new Point(node.X!.Value, node.Y!.Value) : new Point(0, 0);
            return new Block(new Dictionary<string, Point>(StringComparer.Ordinal) { [node.Id] = point },
                NodeBox(node.Id, point.X, point.Y), pinned, new HashSet<string>(StringComparer.Ordinal));
        }

        private Block? BuildGroup(string id)
        {
            if (!_visitedGroups.Add(id)) return null;
            var children = _graph.Nodes.Values.Where(n => n.GroupId == id)
                .OrderBy(n => n.Id, StringComparer.Ordinal).Select(NodeBlock).ToList();
            foreach (var child in _graph.Groups.Values.Where(g => g.ParentGroupId == id)
                         .OrderBy(g => g.Id, StringComparer.Ordinal))
            {
                var block = BuildGroup(child.Id);
                if (block is not null) children.Add(block);
            }
            if (children.Count == 0) return null;

            var result = ArrangeLevel(children, insideGroup: true);
            result.Groups.Add(id);
            var measured = NewGraph(result.Positions.Select(pair =>
            {
                var node = _graph.Nodes[pair.Key].Clone();
                node.X = pair.Value.X;
                node.Y = pair.Value.Y;
                return node;
            }), groups: result.Groups.Select(groupId => _graph.Groups[groupId]));
            var frame = LayoutSnapshot.Capture(measured).Groups[id];
            result.Bounds = new Box(frame.Left, frame.Top, frame.Width, frame.Height);
            if (result.Anchored) _fixedObstacles.Add(result.Bounds);
            return result;
        }

        private Block ArrangeLevel(List<Block> blocks, bool insideGroup)
        {
            var byId = blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            var projected = ProjectBlocks(blocks);
            var ids = byId.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var adjacency = ClusterPacker.BuildAdjacency(projected, ids);
            var islands = new List<Block>();
            foreach (var component in ClusterPacker.FindComponents(ids, adjacency))
            {
                var members = component.OrderBy(id => id, StringComparer.Ordinal).Select(id => byId[id]).ToList();
                var island = members.Count == 1 ? members[0] : ArrangeConnected(members, projected);
                islands.Add(island);
                // An anchored island cannot move later. Reserve its newly positioned
                // members as well as its pins before laying out the next island.
                if (island.Anchored) _fixedObstacles.Add(island.Bounds);
            }

            Pack(islands, insideGroup);
            return Merge(islands);
        }

        private SceneGraph ProjectBlocks(List<Block> blocks)
        {
            var owner = blocks.SelectMany(b => b.Positions.Keys.Select(id => (id, b.Id)))
                .ToDictionary(pair => pair.id, pair => pair.Id, StringComparer.Ordinal);
            var nodes = blocks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(block =>
            {
                var node = _graph.Nodes[block.Id].Clone();
                node.GroupId = null;
                node.Width = block.Bounds.Width;
                node.Height = block.Bounds.Height;
                node.Locked = false;
                node.X = null;
                node.Y = null;
                return node;
            });
            var edges = new List<SceneEdge>();
            foreach (var edge in _graph.Edges.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                if (!owner.TryGetValue(edge.FromNodeId, out var from) ||
                    !owner.TryGetValue(edge.ToNodeId, out var to) || from == to) continue;
                var copy = edge.Clone();
                copy.FromNodeId = from;
                copy.ToNodeId = to;
                edges.Add(copy);
            }
            // These representatives exist only in the helper's geometry graph. The
            // real nodes, edges, membership, lifecycle, and sizes are never replaced.
            return NewGraph(nodes, edges);
        }

        private Block ArrangeConnected(List<Block> blocks, SceneGraph projected)
        {
            var ids = blocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var edges = projected.Edges.Values
                .Where(e => ids.Contains(e.FromNodeId) && ids.Contains(e.ToNodeId)).ToArray();
            var directed = edges.Any(e => e.Kind != EdgeKind.Association);
            var helper = NewGraph(ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => projected.Nodes[id]),
                directed ? edges.Where(e => e.Kind != EdgeKind.Association) : edges);
            var options = _options with
            {
                Padding = 0, HorizontalSpacing = _gapX, VerticalSpacing = _gapY, ReflowPinned = true,
            };
            if (directed) _flow.Apply(helper, options);
            else _map.Apply(helper, options);

            var anchored = blocks.Where(b => b.Anchored).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
            var offset = new Point(0, 0);
            var occupied = new List<Box>();
            if (anchored.Length > 0)
            {
                var anchor = anchored[0];
                offset = new Point(anchor.Bounds.CenterX - helper.Nodes[anchor.Id].X!.Value,
                    anchor.Bounds.CenterY - helper.Nodes[anchor.Id].Y!.Value);
                occupied.AddRange(_fixedObstacles);
                occupied.AddRange(anchored.Select(b => b.Bounds));
            }

            foreach (var block in blocks.Where(b => !b.Anchored)
                         .OrderBy(b => helper.Nodes[b.Id].X).ThenBy(b => helper.Nodes[b.Id].Y)
                         .ThenBy(b => b.Id, StringComparer.Ordinal))
            {
                var node = helper.Nodes[block.Id];
                var candidate = new Box(node.X!.Value + offset.X - block.Bounds.Width / 2,
                    node.Y!.Value + offset.Y - block.Bounds.Height / 2, block.Bounds.Width, block.Bounds.Height);
                // Moving down clears variable-size map cards and pinned obstacles
                // without reversing the ranks of a directed flow.
                while (occupied.Any(box => Overlaps(candidate, box)))
                {
                    var bottom = occupied.Where(box => Overlaps(candidate, box)).Max(box => box.Bottom);
                    candidate = candidate with { Top = bottom + _gapY };
                }
                block.MoveTo(candidate.Left, candidate.Top);
                occupied.Add(block.Bounds);
            }
            return Merge(blocks);
        }

        private void Pack(List<Block> blocks, bool insideGroup)
        {
            var anchored = blocks.Where(b => b.Anchored).ToArray();
            var free = blocks.Where(b => !b.Anchored)
                .OrderByDescending(b => b.Bounds.Height).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
            if (free.Length == 0) return;

            var originX = insideGroup ? 0 : _padding;
            var originY = insideGroup ? 0 : _padding;
            var occupied = anchored.Select(b => b.Bounds).ToList();
            if (anchored.Length > 0 || !insideGroup) occupied.AddRange(_fixedObstacles);
            if (insideGroup && anchored.Length > 0)
            {
                originX = anchored.Min(b => b.Bounds.Left);
                originY = anchored.Min(b => b.Bounds.Top);
            }

            var aspect = _options.CanvasWidth / _options.CanvasHeight;
            aspect = double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.65, 2.2) : 1.5;
            var area = free.Sum(b => (b.Bounds.Width + _gapX) * (b.Bounds.Height + _gapY));
            var limit = Math.Max(free.Max(b => b.Bounds.Width), Math.Sqrt(area * aspect));
            var x = originX;
            var y = originY;
            var rowHeight = 0d;
            foreach (var block in free)
            {
                if (x > originX && x + block.Bounds.Width > originX + limit)
                {
                    x = originX;
                    y += rowHeight + _gapY;
                    rowHeight = 0;
                }
                var desired = new Box(x, y, block.Bounds.Width, block.Bounds.Height);
                var placed = FindSpace(desired, occupied, originX + limit);
                block.MoveTo(placed.Left, placed.Top);
                occupied.Add(block.Bounds);
                if (placed.Top > y)
                {
                    y = placed.Top;
                    rowHeight = 0;
                }
                x = placed.Right + _gapX;
                rowHeight = Math.Max(rowHeight, placed.Height);
            }
        }

        private Box FindSpace(Box desired, List<Box> occupied, double rightLimit)
        {
            if (!occupied.Any(box => Overlaps(desired, box))) return desired;
            var xs = occupied.Select(box => box.Right + _gapX).Append(desired.Left)
                .Where(x => x >= desired.Left && x + desired.Width <= Math.Max(rightLimit, desired.Right))
                .Distinct().ToArray();
            var ys = occupied.Select(box => box.Bottom + _gapY).Append(desired.Top)
                .Where(y => y >= desired.Top).Distinct().ToArray();
            var candidates = from x in xs
                             from y in ys
                             orderby (x - desired.Left) * (x - desired.Left) +
                                     (y - desired.Top) * (y - desired.Top), y, x
                             select desired with { Left = x, Top = y };
            return candidates.First(candidate => !occupied.Any(box => Overlaps(candidate, box)));
        }

        private bool Overlaps(Box a, Box b) =>
            a.Left < b.Right + _gapX && a.Right + _gapX > b.Left &&
            a.Top < b.Bottom + _gapY && a.Bottom + _gapY > b.Top;

        private static Block Merge(IEnumerable<Block> blocks)
        {
            var members = blocks.ToArray();
            return new Block(members.SelectMany(b => b.Positions)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Box.Enclose(members.Select(b => b.Bounds)), members.Any(b => b.Anchored),
                members.SelectMany(b => b.Groups).ToHashSet(StringComparer.Ordinal));
        }

        private static SceneGraph NewGraph(
            IEnumerable<SceneNode> nodes, IEnumerable<SceneEdge>? edges = null, IEnumerable<SceneGroup>? groups = null)
        {
            var graph = new SceneGraph();
            graph.RestorePersistedState(nodes, edges ?? [], groups ?? [], [], [], revision: 0);
            return graph;
        }
    }

    private readonly record struct Point(double X, double Y);

    private readonly record struct Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public double CenterX => Left + Width / 2;
        public double CenterY => Top + Height / 2;

        public static Box Enclose(IEnumerable<Box> boxes)
        {
            var items = boxes.ToArray();
            var left = items.Min(b => b.Left);
            var top = items.Min(b => b.Top);
            return new Box(left, top, items.Max(b => b.Right) - left, items.Max(b => b.Bottom) - top);
        }
    }

    private sealed class Block(
        Dictionary<string, Point> positions, Box bounds, bool anchored, HashSet<string> groups)
    {
        public string Id { get; } = positions.Keys.OrderBy(id => id, StringComparer.Ordinal).First();
        public Dictionary<string, Point> Positions { get; } = positions;
        public Box Bounds { get; set; } = bounds;
        public bool Anchored { get; } = anchored;
        public HashSet<string> Groups { get; } = groups;

        public void MoveTo(double left, double top)
        {
            var dx = left - Bounds.Left;
            var dy = top - Bounds.Top;
            foreach (var id in Positions.Keys.ToArray())
            {
                var point = Positions[id];
                Positions[id] = new Point(point.X + dx, point.Y + dy);
            }
            Bounds = Bounds with { Left = left, Top = top };
        }
    }
}
