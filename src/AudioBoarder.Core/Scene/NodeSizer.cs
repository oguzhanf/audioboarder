namespace AudioBoarder.Core.Scene;

/// <summary>
/// Computes the box a node needs so its text always fits inside the shape.
/// <para>
/// Node sizes used to be fixed (140×60) regardless of content, so any label longer
/// than a couple of words spilled outside its rectangle. Sizing is done here, in the
/// scene, rather than in the renderer so the layout engine reserves the correct
/// footprint too — otherwise shapes are laid out to one size and drawn at another,
/// and they overlap.
/// </para>
/// </summary>
public static class NodeSizer
{
    public const double LabelFontSize = 16;
    public const double LineHeight = 1.25;

    /// <summary>Horizontal room reserved for the kind icon rendered inside the shape.</summary>
    public const double IconBand = 26;

    private const double HorizontalPadding = 18;
    private const double VerticalPadding = 16;
    private const double MinWidth = 150;
    private const double MaxWidth = 260;
    private const double MinHeight = 56;

    /// <summary>
    /// Average glyph advance as a fraction of font size. Excalidraw's hand-drawn face
    /// is narrower than a monospace cell; this is deliberately generous so estimation
    /// error grows the box rather than clipping the text.
    /// </summary>
    private const double AdvanceRatio = 0.58;

    /// <summary>
    /// Sizes every unlocked node to fit its own text. Locked nodes keep the size the
    /// user gave them.
    /// </summary>
    public static void ApplyTo(SceneGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        lock (graph.SyncRoot)
        {
            foreach (var node in graph.Nodes.Values)
            {
                if (node.Locked) continue;
                var (w, h) = Measure(node.Label, node.Description, hasIcon: true, kind: node.Kind);
                node.Width = w;
                node.Height = h;
            }
        }
    }

    /// <summary>
    /// How much of a shape's bounding box the text can actually occupy.
    /// A diamond's inscribed rectangle is half its bounding box, and an ellipse's is
    /// 1/sqrt(2) per axis — so identical text needs a visibly larger box in those
    /// shapes or it spills out of the sloped sides.
    /// </summary>
    private static double InteriorRatio(NodeKind kind) => ShapeOf(kind) switch
    {
        NodeShape.Diamond => 0.52,
        NodeShape.Ellipse => 0.72,
        _ => 1.0,
    };

    /// <summary>
    /// Fraction of a shape's bounding box its text may occupy. Renderers use this so
    /// the wrap width they apply matches the width this class sized the box for.
    /// </summary>
    public static double InteriorRatioFor(NodeKind kind) => InteriorRatio(kind);

    private enum NodeShape { Rectangle, Diamond, Ellipse }

    /// <summary>Mirrors the shape mapping used by the Excalidraw converter.</summary>
    private static NodeShape ShapeOf(NodeKind kind) => kind switch
    {
        NodeKind.Decision or NodeKind.Security or NodeKind.Milestone or NodeKind.Risk
            => NodeShape.Diamond,
        NodeKind.DataStore or NodeKind.Cloud or NodeKind.Metric
            => NodeShape.Ellipse,
        _ => NodeShape.Rectangle,
    };

    /// <summary>Measures the box required to hold a label and optional description.</summary>
    public static (double Width, double Height) Measure(
        string? label, string? description, bool hasIcon, NodeKind kind = NodeKind.Process)
    {
        var iconRoom = hasIcon ? IconBand : 0;
        var ratio = InteriorRatio(kind);

        // Choose a width first: wide enough for the longest word, then grown toward
        // a pleasing aspect ratio, then clamped so one long label cannot dominate.
        var labelWidth = LongestWordWidth(label, LabelFontSize);
        var descWidth = LongestWordWidth(description, LabelFontSize);
        var naturalWidth = Math.Max(
            TextWidth(label, LabelFontSize),
            TextWidth(description, LabelFontSize));

        // Wrapping a long line to roughly two rows reads better than one long strip.
        var target = naturalWidth > MaxWidth - HorizontalPadding - iconRoom
            ? Math.Sqrt(naturalWidth * LabelFontSize * LineHeight * 2.2)
            : naturalWidth;

        var contentWidth = Math.Max(Math.Max(labelWidth, descWidth), target);
        // Inflate so the TEXT fits the shape's usable interior, not just its bounds.
        var width = Math.Clamp(
            (contentWidth + HorizontalPadding + iconRoom) / ratio, MinWidth, MaxWidth / ratio);

        var textWidth = Math.Max(20, width * ratio - HorizontalPadding - iconRoom);
        // The renderer wraps label and description as ONE bound text block at
        // LabelFontSize. Measuring the description at a smaller size here would
        // under-size the box and the text would spill out of the shape.
        var labelLines = CountLines(label, textWidth, LabelFontSize);
        var descLines = CountLines(description, textWidth, LabelFontSize);

        var textHeight = VerticalPadding
                         + (labelLines + descLines) * LabelFontSize * LineHeight;
        var height = textHeight / ratio;

        return (Math.Round(width), Math.Round(Math.Max(MinHeight, height)));
    }

    /// <summary>Number of wrapped lines the text needs at the given width.</summary>
    public static int CountLines(string? text, double width, double fontSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var charsPerLine = Math.Max(1, (int)(width / (fontSize * AdvanceRatio)));
        var lines = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            var current = 0;
            var lineCount = 1;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // A token wider than the line gets hard-broken, exactly as the
                // renderer does — otherwise it silently overflows the shape.
                if (word.Length > charsPerLine)
                {
                    if (current > 0) { lineCount++; current = 0; }
                    lineCount += (word.Length - 1) / charsPerLine;
                    current = word.Length % charsPerLine;
                    if (current == 0) current = charsPerLine;
                    continue;
                }

                var candidate = current == 0 ? word.Length : current + 1 + word.Length;
                if (candidate > charsPerLine && current > 0)
                {
                    lineCount++;
                    current = word.Length;
                }
                else
                {
                    current = candidate;
                }
            }
            lines += lineCount;
        }
        return Math.Max(1, lines);
    }

    private static double TextWidth(string? text, double fontSize)
        => string.IsNullOrWhiteSpace(text) ? 0 : text.Length * fontSize * AdvanceRatio;

    /// <summary>A box can never be narrower than its longest unbreakable word.</summary>
    private static double LongestWordWidth(string? text, double fontSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var longest = 0;
        foreach (var word in text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            longest = Math.Max(longest, word.Length);
        return longest * fontSize * AdvanceRatio;
    }
}
