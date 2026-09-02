using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>Tunables for <see cref="SceneToExcalidrawConverter"/>.</summary>
public sealed record ExcalidrawExportOptions
{
    /// <summary>0 = architect, 1 = artist (hand-drawn whiteboard), 2 = cartoonist.</summary>
    public int Roughness { get; init; } = 1;

    /// <summary>1 = Virgil (hand-drawn), 2 = Helvetica, 3 = Cascadia.</summary>
    public int FontFamily { get; init; } = 1;

    /// <summary>hachure | cross-hatch | solid. Solid reads cleaner for customer-facing diagrams.</summary>
    public string FillStyle { get; init; } = "solid";

    /// <summary>Append captured notes (decisions/action items/risks) as sticky notes beside the diagram.</summary>
    public bool IncludeNotes { get; init; } = true;

    /// <summary>Draw the Lucide vector icon for each node's kind/technology.</summary>
    public bool IncludeIcons { get; init; } = true;

    /// <summary>Route arrows as orthogonal elbows rather than free diagonals.</summary>
    public bool ElbowArrows { get; init; } = true;

    public string Background { get; init; } = "#ffffff";

    public static ExcalidrawExportOptions Default { get; } = new();
}

/// <summary>
/// Converts a <see cref="SceneGraph"/> into a real Excalidraw document. Nodes become
/// hand-drawn shapes with bound text labels; edges become arrows bound to their
/// endpoints (so Excalidraw routes and clips them to the shape borders); groups become
/// transparent labelled regions; notes become sticky notes. Seeds are derived
/// deterministically from element ids so the sketchy rendering stays stable across the
/// continuous diagrammer's frequent updates instead of wobbling every refresh.
/// </summary>
public sealed class SceneToExcalidrawConverter
{
    private const double NodeFontSize = 16;
    private const double GroupFontSize = 18;
    private const double NoteFontSize = 14;
    private const double LineHeight = 1.25;
    private const double BindingGap = 6;
    private const double IconSize = 18;
    private const double IconInset = 8;

    public ExcalidrawDocument Convert(SceneGraph graph, ExcalidrawExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= ExcalidrawExportOptions.Default;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var back = new List<ExcalidrawElement>();   // group regions (behind everything)
        var mid = new List<ExcalidrawElement>();     // arrows + arrow labels
        var front = new List<ExcalidrawElement>();   // node shapes + node labels
        var sticky = new List<ExcalidrawElement>();  // sidebar notes
        var files = new Dictionary<string, object>(StringComparer.Ordinal);

        lock (graph.SyncRoot)
        {
            var layout = LayoutSnapshot.Capture(graph);
            var geom = layout.Nodes.ToDictionary(
                pair => pair.Key,
                pair => new NodeBox(
                    pair.Value.CenterX,
                    pair.Value.CenterY,
                    pair.Value.Width,
                    pair.Value.Height),
                StringComparer.Ordinal);

            // Arrows first so we can register each arrow on the nodes it binds to.
            var boundByNode = new Dictionary<string, List<ExcalidrawBoundElement>>(StringComparer.Ordinal);
            foreach (var edge in graph.Edges.Values)
            {
                if (!geom.TryGetValue(edge.FromNodeId, out var from) ||
                    !geom.TryGetValue(edge.ToNodeId, out var to))
                    continue;

                var boundaryCrossing =
                    graph.IntentState.AppliedIntent == DiagramIntent.SecurityZeroTrustArchitecture &&
                    graph.Nodes.TryGetValue(edge.FromNodeId, out var fromNode) &&
                    graph.Nodes.TryGetValue(edge.ToNodeId, out var toNode) &&
                    !string.Equals(fromNode.GroupId, toNode.GroupId, StringComparison.Ordinal);
                var arrow = BuildArrow(edge, from, to, boundaryCrossing, options, now);
                mid.Add(arrow);

                var edgeText = ComposeEdgeText(edge);
                if (!string.IsNullOrWhiteSpace(edgeText))
                {
                    var lbl = BuildArrowLabel(arrow, edgeText, from, to, options, now);
                    arrow.BoundElements = new() { new ExcalidrawBoundElement { Id = lbl.Id, Type = "text" } };
                    mid.Add(lbl);
                }

                Register(boundByNode, edge.FromNodeId, arrow.Id);
                Register(boundByNode, edge.ToNodeId, arrow.Id);
            }

            // Node shapes + bound labels. Frames are computed first so each member
            // shape can carry its frameId: an Excalidraw frame moves its children,
            // whereas the plain background rectangle we used to emit was just a
            // sibling shape that slid out from under the nodes when dragged.
            var frameByNode = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in graph.Groups.Values
                         .OrderBy(g => layout.Groups[g.Id].Depth)
                         .ThenBy(g => g.Id, StringComparer.Ordinal))
            {
                var memberIds = graph.Nodes.Values
                    .Where(n => n.GroupId == group.Id && geom.ContainsKey(n.Id))
                    .Select(n => n.Id)
                    .ToList();
                foreach (var id in memberIds) frameByNode[id] = group.Id + "_frame";
                var frame = BuildGroupFrame(group, layout.Groups[group.Id], options, now);
                if (!string.IsNullOrWhiteSpace(group.ParentGroupId) &&
                    graph.Groups.ContainsKey(group.ParentGroupId))
                    frame.FrameId = group.ParentGroupId + "_frame";
                back.Add(frame);
            }

            foreach (var node in graph.Nodes.Values)
            {
                var box = geom[node.Id];
                var shape = BuildNodeShape(node, box, options, now);
                if (frameByNode.TryGetValue(node.Id, out var frameId)) shape.FrameId = frameId;

                var bound = boundByNode.TryGetValue(node.Id, out var arrows)
                    ? new List<ExcalidrawBoundElement>(arrows)
                    : new List<ExcalidrawBoundElement>();

                if (!string.IsNullOrWhiteSpace(node.Label))
                {
                    var text = BuildBoundLabel(node, box, options, now);
                    if (frameByNode.TryGetValue(node.Id, out var lblFrame)) text.FrameId = lblFrame;
                    bound.Add(new ExcalidrawBoundElement { Id = text.Id, Type = "text" });
                    front.Add(shape);
                    front.Add(text);
                }
                else
                {
                    front.Add(shape);
                }

                if (options.IncludeIcons)
                {
                    var icon = BuildNodeIcon(node, box, shape.StrokeColor, files, now);
                    if (frameByNode.TryGetValue(node.Id, out var iconFrame)) icon.FrameId = iconFrame;
                    // The shape must join the same group, otherwise dragging the node
                    // leaves the icon behind at the old coordinates.
                    shape.GroupIds = new List<string> { node.Id + "_grp" };
                    front.Add(icon);
                }

                if (bound.Count > 0) shape.BoundElements = bound;
            }

            if (options.IncludeNotes && graph.Notes.Count > 0)
                sticky.AddRange(BuildNotes(graph, geom, options, now));
        }

        var doc = new ExcalidrawDocument
        {
            AppState = new ExcalidrawAppState { ViewBackgroundColor = options.Background, GridSize = null },
        };
        doc.Elements.AddRange(back);
        doc.Elements.AddRange(mid);
        doc.Elements.AddRange(front);
        doc.Elements.AddRange(sticky);
        foreach (var kv in files) doc.Files[kv.Key] = kv.Value;
        return doc;
    }

    /// <summary>Serialize a scene straight to <c>.excalidraw</c> JSON text.</summary>
    public string ConvertToJson(SceneGraph graph, ExcalidrawExportOptions? options = null)
        => ExcalidrawJson.Serialize(Convert(graph, options));

    // ---- geometry -----------------------------------------------------------

    private readonly record struct NodeBox(double Cx, double Cy, double W, double H)
    {
        public double Left => Cx - W / 2;
        public double Top => Cy - H / 2;
    }

    // ---- nodes --------------------------------------------------------------

    private ExcalidrawElement BuildNodeShape(SceneNode node, NodeBox box, ExcalidrawExportOptions o, long now)
    {
        var colors = ExcalidrawPalette.For(node.Kind);
        var (type, rounded) = ShapeFor(node.Kind);
        return new ExcalidrawElement
        {
            Id = node.Id,
            Type = type,
            X = box.Left,
            Y = box.Top,
            Width = box.W,
            Height = box.H,
            StrokeColor = colors.Stroke,
            BackgroundColor = colors.Fill,
            FillStyle = o.FillStyle,
            StrokeWidth = StrokeWidthFor(node.Kind),
            StrokeStyle = StrokeStyleFor(node.Kind),
            Roughness = o.Roughness,
            Roundness = rounded ? new ExcalidrawRoundness { Type = 3 } : null,
            Seed = Seed(node.Id),
            VersionNonce = Seed(node.Id, 1),
            Updated = now,
            Locked = node.Locked,
        };
    }

    private static (string Type, bool Rounded) ShapeFor(NodeKind kind) => kind switch
    {
        NodeKind.Process => ("rectangle", true),
        NodeKind.Entity => ("rectangle", false),
        NodeKind.Decision => ("diamond", false),
        NodeKind.DataStore => ("ellipse", false),
        NodeKind.Actor => ("rectangle", true),
        NodeKind.Note => ("rectangle", false),
        NodeKind.System => ("rectangle", false),
        NodeKind.Technology => ("rectangle", true),
        NodeKind.Security => ("diamond", false),
        NodeKind.Identity => ("rectangle", true),
        NodeKind.Cloud => ("ellipse", false),
        NodeKind.Document => ("rectangle", false),
        NodeKind.Milestone => ("diamond", false),
        NodeKind.Risk => ("diamond", false),
        NodeKind.Metric => ("ellipse", false),
        NodeKind.External => ("rectangle", true),
        NodeKind.Callout => ("rectangle", true),
        _ => ("rectangle", true),
    };

    /// <summary>
    /// Stroke weight per kind. System/Cloud boundaries get a heavier border so they
    /// read as containers; callouts get a hairline so they recede behind real nodes.
    /// </summary>
    private static double StrokeWidthFor(NodeKind kind) => kind switch
    {
        NodeKind.System or NodeKind.Cloud => 3,
        NodeKind.Callout => 1,
        _ => 2,
    };

    private static string StrokeStyleFor(NodeKind kind) => kind switch
    {
        NodeKind.External or NodeKind.Callout => "dashed",
        _ => "solid",
    };

    /// <summary>
    /// Text rendered inside a node: the label, plus an optional smaller description
    /// line. The kind icon is a real vector element drawn beside the text, never a
    /// glyph spliced into the string.
    /// </summary>
    private static string ComposeNodeText(SceneNode node)
        => string.IsNullOrWhiteSpace(node.Description)
            ? node.Label
            : $"{node.Label}\n{node.Description}";

    /// <summary>
    /// Builds the vector icon shown inside a node as an Excalidraw image element
    /// backed by an inline SVG data URL, and registers the blob in the document's
    /// file map. Kept unbound and locked so it never becomes a drag target.
    /// </summary>
    private static ExcalidrawElement BuildNodeIcon(
        SceneNode node, NodeBox box, string strokeColor, Dictionary<string, object> files, long now)
    {
        var iconName = node.EffectiveIconName;
        var fileId = $"icon_{iconName}_{Sanitize(strokeColor)}";
        if (!files.ContainsKey(fileId))
        {
            files[fileId] = new ExcalidrawFile
            {
                Id = fileId,
                MimeType = "image/svg+xml",
                DataURL = IconRegistry.RenderDataUrl(iconName, strokeColor, IconSize),
                Created = now,
            };
        }

        // Anchor to the shape's INSCRIBED rectangle, not its bounding box: the
        // top-left corner of a diamond's bounds is outside the drawn outline, so a
        // bounding-box offset would leave the icon floating in empty canvas.
        var ratio = NodeSizer.InteriorRatioFor(node.Kind);
        var interiorLeft = box.Cx - box.W * ratio / 2;
        var interiorTop = box.Cy - box.H * ratio / 2;

        return new ExcalidrawElement
        {
            Id = node.Id + "_icon",
            Type = "image",
            FileId = fileId,
            Status = "saved",
            X = interiorLeft + IconInset,
            Y = interiorTop + IconInset,
            Width = IconSize,
            Height = IconSize,
            StrokeColor = "transparent",
            BackgroundColor = "transparent",
            FillStyle = "solid",
            StrokeWidth = 1,
            Roughness = 0,
            Scale = new double[] { 1, 1 },
            // Grouped with its shape (not locked) so dragging the node carries the
            // icon with it. A locked element is excluded from selection and would be
            // left behind at the old coordinates.
            GroupIds = new List<string> { node.Id + "_grp" },
            Seed = Seed(node.Id, 4),
            VersionNonce = Seed(node.Id, 5),
            Updated = now,
        };
    }

    private static string Sanitize(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private ExcalidrawElement BuildBoundLabel(SceneNode node, NodeBox box, ExcalidrawExportOptions o, long now)
    {
        // Constrain the text to the shape's usable interior — a diamond's sloped
        // sides mean its inscribed rectangle is far smaller than its bounding box.
        var interior = NodeSizer.InteriorRatioFor(node.Kind);
        var width = Math.Max(20, box.W * interior - NodeSizer.IconBand - 12);
        var composed = ComposeNodeText(node);
        var (wrapped, lines) = WrapToWidth(composed, width, NodeFontSize);
        var height = lines * NodeFontSize * LineHeight;
        return new ExcalidrawElement
        {
            Id = node.Id + "_label",
            Type = "text",
            X = box.Cx - width / 2 + NodeSizer.IconBand / 2,
            Y = box.Cy - height / 2,
            Width = width,
            Height = height,
            StrokeColor = ExcalidrawPalette.Ink,
            BackgroundColor = "transparent",
            FillStyle = o.FillStyle,
            StrokeWidth = 1,
            Roughness = o.Roughness,
            Text = wrapped,
            OriginalText = composed,
            FontSize = NodeFontSize,
            FontFamily = o.FontFamily,
            TextAlign = "center",
            VerticalAlign = "middle",
            ContainerId = node.Id,
            LineHeight = LineHeight,
            AutoResize = true,
            Seed = Seed(node.Id, 2),
            VersionNonce = Seed(node.Id, 3),
            Updated = now,
        };
    }

    // ---- edges --------------------------------------------------------------

    private ExcalidrawElement BuildArrow(
        SceneEdge edge,
        NodeBox from,
        NodeBox to,
        bool boundaryCrossing,
        ExcalidrawExportOptions o,
        long now)
    {
        var dx = to.Cx - from.Cx;
        var dy = to.Cy - from.Cy;
        var dashed = edge.Kind is EdgeKind.Dependency or EdgeKind.Association;
        var points = o.ElbowArrows ? OrthogonalPoints(dx, dy) : new[] { new[] { 0.0, 0.0 }, new[] { dx, dy } };
        return new ExcalidrawElement
        {
            Id = edge.Id,
            Type = "arrow",
            X = from.Cx,
            Y = from.Cy,
            Width = Math.Abs(dx),
            Height = Math.Abs(dy),
            StrokeColor = ExcalidrawPalette.Edge,
            BackgroundColor = "transparent",
            FillStyle = o.FillStyle,
            StrokeWidth = boundaryCrossing ? 3 : 2,
            StrokeStyle = dashed ? "dashed" : "solid",
            Roughness = o.Roughness,
            Points = points,
            LastCommittedPoint = null,
            StartBinding = new ExcalidrawBinding { ElementId = edge.FromNodeId, Focus = 0, Gap = BindingGap },
            EndBinding = new ExcalidrawBinding { ElementId = edge.ToNodeId, Focus = 0, Gap = BindingGap },
            StartArrowhead = null,
            EndArrowhead = edge.Kind == EdgeKind.Inheritance ? "triangle" : "arrow",
            // Deliberately NOT Excalidraw's `elbowed` mode: with bound endpoints it
            // never regenerates the intermediate points, so the arrow still draws as a
            // straight diagonal while disabling its transform handles. We emit the
            // orthogonal waypoints ourselves and let roundness soften the corners.
            Elbowed = false,
            Roundness = new ExcalidrawRoundness { Type = 2 },
            Seed = Seed(edge.Id),
            VersionNonce = Seed(edge.Id, 1),
            Updated = now,
        };
    }

    /// <summary>
    /// Builds a stepped route between two nodes. Layered layout stacks nodes in ranks,
    /// so a vertical-first dogleg follows the flow; near-straight runs stay straight
    /// rather than gaining a pointless jog.
    /// </summary>
    private static double[][] OrthogonalPoints(double dx, double dy)
    {
        const double Straight = 12;
        if (Math.Abs(dx) < Straight || Math.Abs(dy) < Straight)
            return new[] { new[] { 0.0, 0.0 }, new[] { dx, dy } };

        return Math.Abs(dy) >= Math.Abs(dx)
            ? new[]                                   // vertical flow: down, across, down
            {
                new[] { 0.0, 0.0 },
                new[] { 0.0, dy / 2 },
                new[] { dx, dy / 2 },
                new[] { dx, dy },
            }
            : new[]                                   // horizontal flow: across, down, across
            {
                new[] { 0.0, 0.0 },
                new[] { dx / 2, 0.0 },
                new[] { dx / 2, dy },
                new[] { dx, dy },
            };
    }

    private ExcalidrawElement BuildArrowLabel(ExcalidrawElement arrow, string label, NodeBox from, NodeBox to,
        ExcalidrawExportOptions o, long now)
    {
        var width = Math.Max(20, label.Length * NoteFontSize * 0.6);
        var height = NoteFontSize * LineHeight;
        return new ExcalidrawElement
        {
            Id = arrow.Id + "_label",
            Type = "text",
            X = (from.Cx + to.Cx) / 2 - width / 2,
            Y = (from.Cy + to.Cy) / 2 - height / 2,
            Width = width,
            Height = height,
            StrokeColor = ExcalidrawPalette.Ink,
            BackgroundColor = "transparent",
            FillStyle = o.FillStyle,
            StrokeWidth = 1,
            Roughness = o.Roughness,
            Text = label,
            OriginalText = label,
            FontSize = NoteFontSize,
            FontFamily = o.FontFamily,
            TextAlign = "center",
            VerticalAlign = "middle",
            ContainerId = arrow.Id,
            LineHeight = LineHeight,
            AutoResize = true,
            Seed = Seed(arrow.Id, 2),
            VersionNonce = Seed(arrow.Id, 3),
            Updated = now,
        };
    }

    private static string ComposeEdgeText(SceneEdge edge)
    {
        var heading = new List<string>();
        if (edge.Step is > 0) heading.Add($"Step {edge.Step}");
        if (!string.IsNullOrWhiteSpace(edge.Label)) heading.Add(edge.Label.Trim());

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(edge.Protocol)) metadata.Add(edge.Protocol.Trim());
        if (!string.IsNullOrWhiteSpace(edge.Payload)) metadata.Add(edge.Payload.Trim());
        if (!string.IsNullOrWhiteSpace(edge.Authentication)) metadata.Add($"auth: {edge.Authentication.Trim()}");
        if (!string.IsNullOrWhiteSpace(edge.DataClassification))
            metadata.Add($"class: {edge.DataClassification.Trim()}");
        if (edge.InteractionMode.HasValue) metadata.Add(ToDisplay(edge.InteractionMode.Value.ToString()));

        return string.Join("\n", new[]
        {
            string.Join(" · ", heading),
            string.Join(" · ", metadata),
        }.Where(line => line.Length > 0));
    }

    private static string ToDisplay(string value)
    {
        var chars = new List<char>(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i])) chars.Add(' ');
            chars.Add(char.ToLowerInvariant(value[i]));
        }
        return new string(chars.ToArray());
    }

    // ---- groups -------------------------------------------------------------

    /// <summary>
    /// Builds a real Excalidraw <c>frame</c> for a system boundary.
    /// <para>
    /// Frames own their children: dragging one moves everything inside it, and the
    /// frame renders its own name. The previous implementation emitted a plain
    /// background rectangle plus a floating text label, which merely sat behind the
    /// nodes — dragging the boundary slid the box out from under its own contents.
    /// </para>
    /// </summary>
    private ExcalidrawElement BuildGroupFrame(SceneGroup group, GroupBounds bounds,
        ExcalidrawExportOptions o, long now)
    {
        var nameParts = new List<string>
        {
            string.IsNullOrWhiteSpace(group.Label) ? "Group" : group.Label,
        };
        if (!string.IsNullOrWhiteSpace(group.Subtitle)) nameParts.Add(group.Subtitle);
        if (group.BoundaryKind != BoundaryKind.Generic)
            nameParts.Add(ToDisplay(group.BoundaryKind.ToString()));

        return new ExcalidrawElement
        {
            Id = group.Id + "_frame",
            Type = "frame",
            Name = string.Join(" — ", nameParts),
            X = bounds.Left,
            Y = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            StrokeColor = "#5c7cfa",
            BackgroundColor = "transparent",
            FillStyle = "solid",
            StrokeWidth = 2,
            StrokeStyle = "solid",
            // Frames are chrome, not sketch: roughness 0 keeps the boundary crisp
            // against the hand-drawn shapes inside it.
            Roughness = 0,
            Roundness = null,
            Seed = Seed(group.Id),
            VersionNonce = Seed(group.Id, 1),
            Updated = now,
        };
    }

    // ---- notes --------------------------------------------------------------

    private IEnumerable<ExcalidrawElement> BuildNotes(SceneGraph graph, Dictionary<string, NodeBox> geom,
        ExcalidrawExportOptions o, long now)
    {
        // Stack sticky notes in a column to the right of the diagram bounds.
        double columnX = 120, top = 140;
        if (geom.Count > 0)
        {
            columnX = geom.Values.Max(g => g.Left + g.W) + 80;
            top = geom.Values.Min(g => g.Top);
        }

        const double width = 240;
        var y = top;
        foreach (var note in graph.Notes.Values.OrderByDescending(n => n.SourceTimestamp ?? DateTimeOffset.MinValue))
        {
            var colors = ExcalidrawPalette.ForNote(note.Kind);
            var body = FormatNote(note);
            var (wrapped, lines) = WrapToWidth(body, width - 20, NoteFontSize);
            var height = Math.Max(60, lines * NoteFontSize * LineHeight + 24);

            yield return new ExcalidrawElement
            {
                Id = note.Id + "_note",
                Type = "rectangle",
                X = columnX,
                Y = y,
                Width = width,
                Height = height,
                StrokeColor = colors.Stroke,
                BackgroundColor = colors.Fill,
                FillStyle = o.FillStyle,
                StrokeWidth = 1.5,
                Roughness = o.Roughness,
                Roundness = new ExcalidrawRoundness { Type = 3 },
                Seed = Seed(note.Id),
                VersionNonce = Seed(note.Id, 1),
                Updated = now,
                BoundElements = new() { new ExcalidrawBoundElement { Id = note.Id + "_note_text", Type = "text" } },
            };
            yield return new ExcalidrawElement
            {
                Id = note.Id + "_note_text",
                Type = "text",
                X = columnX + 10,
                Y = y + 10,
                Width = width - 20,
                Height = height - 20,
                StrokeColor = ExcalidrawPalette.Ink,
                BackgroundColor = "transparent",
                FillStyle = o.FillStyle,
                StrokeWidth = 1,
                Roughness = o.Roughness,
                Text = wrapped,
                OriginalText = body,
                FontSize = NoteFontSize,
                FontFamily = o.FontFamily,
                TextAlign = "left",
                VerticalAlign = "middle",
                ContainerId = note.Id + "_note",
                LineHeight = LineHeight,
                AutoResize = true,
                Seed = Seed(note.Id, 2),
                VersionNonce = Seed(note.Id, 3),
                Updated = now,
            };
            y += height + 20;
        }
    }

    private static string FormatNote(SceneNote note)
    {
        var header = note.Kind.ToString();
        var owner = string.IsNullOrWhiteSpace(note.Owner) ? "" : $"\n— {note.Owner}";
        return $"[{header}] {note.Text}{owner}";
    }

    // ---- helpers ------------------------------------------------------------

    private static void Register(Dictionary<string, List<ExcalidrawBoundElement>> map, string nodeId, string arrowId)
    {
        if (!map.TryGetValue(nodeId, out var list))
            map[nodeId] = list = new List<ExcalidrawBoundElement>();
        list.Add(new ExcalidrawBoundElement { Id = arrowId, Type = "arrow" });
    }

    /// <summary>
    /// Wraps text to an approximate character width for the given font size and inserts
    /// hard newlines. Excalidraw renders the stored <c>text</c> verbatim (it does not
    /// re-wrap bound text on load), so labels must be pre-wrapped to avoid clipping.
    /// </summary>
    private static (string Wrapped, int Lines) WrapToWidth(string text, double width, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, 1);
        var charsPerLine = Math.Max(1, (int)(width / (fontSize * 0.52)));
        var outLines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // Hard-break a token that cannot fit on a line by itself, so a long
                // unbroken identifier cannot run outside the shape.
                if (word.Length > charsPerLine)
                {
                    if (line.Length > 0) { outLines.Add(line); line = string.Empty; }
                    var rest = word;
                    while (rest.Length > charsPerLine)
                    {
                        outLines.Add(rest[..charsPerLine]);
                        rest = rest[charsPerLine..];
                    }
                    line = rest;
                    continue;
                }

                var candidate = line.Length == 0 ? word : line + " " + word;
                if (candidate.Length > charsPerLine && line.Length > 0)
                {
                    outLines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            outLines.Add(line);
        }
        if (outLines.Count == 0) return (text, 1);
        return (string.Join("\n", outLines), Math.Max(1, outLines.Count));
    }

    /// <summary>Deterministic positive seed from an element id (FNV-1a) so the
    /// hand-drawn rendering is stable across continuous diagram refreshes.</summary>
    private static int Seed(string id, int salt = 0)
    {
        unchecked
        {
            var h = 2166136261u ^ (uint)salt;
            foreach (var c in id)
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)(h & 0x7fffffff);
        }
    }
}
