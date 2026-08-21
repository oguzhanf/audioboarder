using AudioBoarder.Core.Rendering;
using AudioBoarder.Core.Scene;
using SkiaSharp;

namespace AudioBoarder.Services.Rendering;

/// <summary>
/// Modern "whiteboard" SkiaSharp renderer. Paints a <see cref="SceneGraph"/>
/// with soft card nodes (drop shadows, accent bars, kind colours), curved
/// connectors with label chips, and a subtle dot grid. No WPF dependencies.
/// </summary>
public sealed class SceneRenderer
{
    private readonly DiagramTheme _theme;
    private readonly SKTypeface _font;
    private readonly SKTypeface _fontSemibold;

    public SceneRenderer(DiagramTheme? theme = null)
    {
        _theme = theme ?? DiagramTheme.Light;
        _font = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        _fontSemibold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? _font;
    }

    public DiagramTheme Theme => _theme;

    public void Render(SKCanvas canvas, SceneGraph graph, int width, int height, bool drawBackground = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(graph);

        if (drawBackground) canvas.Clear(ColorParser.Of(_theme.Background));
        // Lock around structural enumeration so a background patch/layout can't
        // mutate the collections mid-draw (which throws and blanks the diagram).
        lock (graph.SyncRoot)
        {
            if (drawBackground) DrawDotGrid(canvas, graph, width, height);
            DrawGroups(canvas, graph);
            DrawEdges(canvas, graph);
            DrawNodes(canvas, graph);
        }
    }

    // ---- background ---------------------------------------------------------

    private void DrawDotGrid(SKCanvas canvas, SceneGraph graph, int width, int height)
    {
        const float step = 28f;
        float minX = 0, minY = 0, maxX = width, maxY = height;
        var positioned = graph.Nodes.Values.Where(n => n.X.HasValue && n.Y.HasValue).ToList();
        if (positioned.Count > 0)
        {
            minX = (float)positioned.Min(n => n.X!.Value - n.Width / 2) - 320;
            minY = (float)positioned.Min(n => n.Y!.Value - n.Height / 2) - 320;
            maxX = (float)positioned.Max(n => n.X!.Value + n.Width / 2) + 320;
            maxY = (float)positioned.Max(n => n.Y!.Value + n.Height / 2) + 320;
        }
        // Keep the dot count bounded.
        var cols = (maxX - minX) / step;
        var rows = (maxY - minY) / step;
        if (cols * rows > 12000) return;

        using var dot = new SKPaint
        {
            Color = ColorParser.Of(_theme.DotGrid),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        var startX = MathF.Floor(minX / step) * step;
        var startY = MathF.Floor(minY / step) * step;
        for (var x = startX; x <= maxX; x += step)
            for (var y = startY; y <= maxY; y += step)
                canvas.DrawCircle(x, y, 1.2f, dot);
    }

    // ---- groups -------------------------------------------------------------

    private void DrawGroups(SKCanvas canvas, SceneGraph graph)
    {
        if (graph.Groups.Count == 0) return;

        using var fill = new SKPaint
        {
            Color = ColorParser.Of(_theme.GroupFill),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var stroke = new SKPaint
        {
            Color = ColorParser.Of(_theme.GroupStroke),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };

        foreach (var group in graph.Groups.Values)
        {
            var children = graph.Nodes.Values
                .Where(n => n.GroupId == group.Id && n.X.HasValue && n.Y.HasValue).ToList();
            if (children.Count == 0) continue;
            var (x, y, w, h) = ComputeBoundingBox(children, padding: 26);
            var rect = SKRect.Create((float)x, (float)y, (float)w, (float)h);
            canvas.DrawRoundRect(rect, 18, 18, fill);
            canvas.DrawRoundRect(rect, 18, 18, stroke);

            if (!string.IsNullOrEmpty(group.Label))
            {
                using var pillText = new SKPaint
                {
                    Color = ColorParser.Of(_theme.ProcessAccent),
                    TextSize = 12, IsAntialias = true, Typeface = _fontSemibold,
                };
                var tw = pillText.MeasureText(group.Label);
                var pill = SKRect.Create(rect.Left + 12, rect.Top - 11, tw + 22, 22);
                using var pillBg = new SKPaint
                {
                    Color = ColorParser.Of(_theme.GroupLabelBg),
                    Style = SKPaintStyle.Fill, IsAntialias = true,
                };
                canvas.DrawRoundRect(pill, 11, 11, pillBg);
                canvas.DrawText(group.Label, pill.Left + 11, pill.MidY + 4, pillText);
            }
        }
    }

    private static (double X, double Y, double W, double H) ComputeBoundingBox(
        IEnumerable<SceneNode> nodes, double padding)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            if (!n.X.HasValue || !n.Y.HasValue) continue;
            minX = Math.Min(minX, n.X.Value - n.Width / 2);
            minY = Math.Min(minY, n.Y.Value - n.Height / 2);
            maxX = Math.Max(maxX, n.X.Value + n.Width / 2);
            maxY = Math.Max(maxY, n.Y.Value + n.Height / 2);
        }
        return (minX - padding, minY - padding, maxX - minX + 2 * padding, maxY - minY + 2 * padding);
    }

    // ---- edges --------------------------------------------------------------

    private void DrawEdges(SKCanvas canvas, SceneGraph graph)
    {
        using var stroke = new SKPaint
        {
            Color = ColorParser.Of(_theme.EdgeStroke),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        using var labelText = new SKPaint
        {
            Color = ColorParser.Of(_theme.EdgeLabel),
            TextSize = 11, IsAntialias = true, Typeface = _font,
        };
        using var labelBg = new SKPaint
        {
            Color = ColorParser.Of(_theme.EdgeLabelBg),
            Style = SKPaintStyle.Fill, IsAntialias = true,
        };

        foreach (var edge in graph.Edges.Values)
        {
            if (!graph.Nodes.TryGetValue(edge.FromNodeId, out var from) ||
                !graph.Nodes.TryGetValue(edge.ToNodeId, out var to)) continue;
            if (!from.X.HasValue || !from.Y.HasValue || !to.X.HasValue || !to.Y.HasValue) continue;

            var (fx, fy) = ((float)from.X.Value, (float)from.Y.Value);
            var (tx, ty) = ((float)to.X.Value, (float)to.Y.Value);
            var (sx, sy) = TrimToBox(fx, fy, tx, ty, (float)from.Width, (float)from.Height);
            var (ex, ey) = TrimToBox(tx, ty, fx, fy, (float)to.Width, (float)to.Height);

            stroke.PathEffect?.Dispose();
            stroke.PathEffect = edge.Kind is EdgeKind.Dependency or EdgeKind.Association
                ? SKPathEffect.CreateDash(new float[] { 7, 5 }, 0)
                : null;

            // Smooth S-curve along the dominant axis for an organic, flowing feel.
            float c1x, c1y, c2x, c2y;
            if (MathF.Abs(ey - sy) >= MathF.Abs(ex - sx))
            {
                var midY = (sy + ey) / 2f;
                (c1x, c1y, c2x, c2y) = (sx, midY, ex, midY);
            }
            else
            {
                var midX = (sx + ex) / 2f;
                (c1x, c1y, c2x, c2y) = (midX, sy, midX, ey);
            }

            using var path = new SKPath();
            path.MoveTo(sx, sy);
            path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
            canvas.DrawPath(path, stroke);
            DrawArrowhead(canvas, c2x, c2y, ex, ey, stroke.Color);

            if (!string.IsNullOrEmpty(edge.Label))
            {
                // Position the chip on the curve midpoint (t=0.5 of the cubic).
                var (mx, my) = CubicPoint(sx, sy, c1x, c1y, c2x, c2y, ex, ey, 0.5f);
                var tw = labelText.MeasureText(edge.Label);
                var chip = SKRect.Create(mx - tw / 2 - 7, my - 9, tw + 14, 18);
                canvas.DrawRoundRect(chip, 9, 9, labelBg);
                var prevAlign = labelText.TextAlign;
                labelText.TextAlign = SKTextAlign.Center;
                canvas.DrawText(edge.Label, mx, my + 4, labelText);
                labelText.TextAlign = prevAlign;
            }
        }
    }

    private static (float X, float Y) CubicPoint(
        float p0x, float p0y, float p1x, float p1y, float p2x, float p2y, float p3x, float p3y, float t)
    {
        var u = 1 - t;
        var w0 = u * u * u; var w1 = 3 * u * u * t; var w2 = 3 * u * t * t; var w3 = t * t * t;
        return (w0 * p0x + w1 * p1x + w2 * p2x + w3 * p3x,
                w0 * p0y + w1 * p1y + w2 * p2y + w3 * p3y);
    }

    private static (float X, float Y) TrimToBox(float boxCx, float boxCy, float towardX, float towardY, float w, float h)
    {
        var dx = towardX - boxCx;
        var dy = towardY - boxCy;
        if (dx == 0 && dy == 0) return (boxCx, boxCy);
        var halfW = w / 2f + 4f;   // small gap so the line doesn't touch the card
        var halfH = h / 2f + 4f;
        var scaleX = dx == 0 ? float.MaxValue : halfW / MathF.Abs(dx);
        var scaleY = dy == 0 ? float.MaxValue : halfH / MathF.Abs(dy);
        var scale = MathF.Min(scaleX, scaleY);
        return (boxCx + dx * scale, boxCy + dy * scale);
    }

    private static void DrawArrowhead(SKCanvas canvas, float fromX, float fromY, float ex, float ey, SKColor color)
    {
        const float size = 10f;
        var angle = MathF.Atan2(ey - fromY, ex - fromX);
        var x1 = ex - size * MathF.Cos(angle - MathF.PI / 7);
        var y1 = ey - size * MathF.Sin(angle - MathF.PI / 7);
        var x2 = ex - size * MathF.Cos(angle + MathF.PI / 7);
        var y2 = ey - size * MathF.Sin(angle + MathF.PI / 7);

        using var path = new SKPath();
        path.MoveTo(ex, ey);
        path.LineTo(x1, y1);
        path.LineTo(x2, y2);
        path.Close();
        using var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(path, fill);
    }

    // ---- nodes --------------------------------------------------------------

    private void DrawNodes(SKCanvas canvas, SceneGraph graph)
    {
        using var text = new SKPaint
        {
            Color = ColorParser.Of(_theme.NodeText),
            TextSize = 13, IsAntialias = true, Typeface = _fontSemibold,
            TextAlign = SKTextAlign.Center,
            SubpixelText = true,
        };

        foreach (var node in graph.Nodes.Values)
        {
            if (!node.X.HasValue || !node.Y.HasValue) continue;
            var cx = (float)node.X.Value;
            var cy = (float)node.Y.Value;
            var w = (float)node.Width;
            var h = (float)node.Height;
            var rect = SKRect.Create(cx - w / 2, cy - h / 2, w, h);
            var accent = ColorParser.Of(AccentFor(node.Kind));
            var tint = Blend(accent, SKColors.White, 0.86f);

            switch (node.Kind)
            {
                case NodeKind.Decision:
                    DrawShadow(canvas, rect, 14, diamond: true);
                    DrawDiamond(canvas, rect, tint, accent);
                    break;
                case NodeKind.DataStore:
                    DrawShadow(canvas, rect, 10, diamond: false);
                    DrawCylinder(canvas, rect, tint, accent);
                    break;
                case NodeKind.Note:
                    DrawShadow(canvas, rect, 4, diamond: false);
                    DrawNoteShape(canvas, rect, Blend(accent, SKColors.White, 0.72f), accent);
                    break;
                case NodeKind.Actor:
                    DrawCard(canvas, rect, accent, tint, actor: true);
                    break;
                default: // Process, Entity
                    DrawCard(canvas, rect, accent, tint, actor: false);
                    break;
            }

            DrawLabel(canvas, node.Label, cx, cy, w - 24, text);
        }
    }

    private void DrawShadow(SKCanvas canvas, SKRect rect, float radius, bool diamond)
    {
        using var shadow = new SKPaint
        {
            Color = ColorParser.Of(_theme.NodeShadow),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7f),
        };
        var r = SKRect.Create(rect.Left, rect.Top + 4, rect.Width, rect.Height);
        if (diamond)
        {
            using var path = new SKPath();
            path.MoveTo(r.MidX, r.Top);
            path.LineTo(r.Right, r.MidY);
            path.LineTo(r.MidX, r.Bottom);
            path.LineTo(r.Left, r.MidY);
            path.Close();
            canvas.DrawPath(path, shadow);
        }
        else
        {
            canvas.DrawRoundRect(r, radius, radius, shadow);
        }
    }

    /// <summary>Modern card: white body, soft border, a coloured accent bar on
    /// the left, and (for actors) an accent "head" dot.</summary>
    private void DrawCard(SKCanvas canvas, SKRect rect, SKColor accent, SKColor tint, bool actor)
    {
        const float radius = 14f;
        DrawShadow(canvas, rect, radius, diamond: false);

        using var body = new SKPaint
        {
            Color = ColorParser.Of(_theme.NodeFill), Style = SKPaintStyle.Fill, IsAntialias = true,
        };
        using var border = new SKPaint
        {
            Color = ColorParser.Of(_theme.NodeBorder), Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f, IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, radius, radius, body);

        // Accent bar on the left edge (clipped to the rounded card).
        using (new SKAutoCanvasRestore(canvas))
        {
            using var clip = new SKPath();
            clip.AddRoundRect(rect, radius, radius);
            canvas.ClipPath(clip, antialias: true);
            using var bar = new SKPaint { Color = accent, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(SKRect.Create(rect.Left, rect.Top, 5f, rect.Height), bar);
            if (actor)
            {
                // a soft tinted header strip to hint "actor"
                using var head = new SKPaint { Color = tint, Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawCircle(rect.Left + 18, rect.Top + 15, 6, head);
                using var headRing = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawCircle(rect.Left + 18, rect.Top + 15, 6, headRing);
            }
        }
        canvas.DrawRoundRect(rect, radius, radius, border);
    }

    private string AccentFor(NodeKind kind) => kind switch
    {
        NodeKind.Decision => _theme.DecisionAccent,
        NodeKind.Entity => _theme.EntityAccent,
        NodeKind.DataStore => _theme.DataStoreAccent,
        NodeKind.Actor => _theme.ActorAccent,
        NodeKind.Note => _theme.NoteAccent,
        // Richer kinds reuse the closest existing theme accent so the classic
        // SkiaSharp view stays visually differentiated, not a wall of one colour.
        NodeKind.Risk or NodeKind.Security => _theme.DecisionAccent,
        NodeKind.Milestone or NodeKind.Metric => _theme.EntityAccent,
        NodeKind.Document or NodeKind.Cloud or NodeKind.System => _theme.DataStoreAccent,
        NodeKind.External => _theme.ActorAccent,
        NodeKind.Callout => _theme.NoteAccent,
        _ => _theme.ProcessAccent,
    };

    private static SKColor Blend(SKColor a, SKColor b, float t)
    {
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return new SKColor(L(a.Red, b.Red), L(a.Green, b.Green), L(a.Blue, b.Blue));
    }

    private static void DrawDiamond(SKCanvas canvas, SKRect rect, SKColor fill, SKColor accent)
    {
        using var f = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var s = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(rect.MidX, rect.Top);
        path.LineTo(rect.Right, rect.MidY);
        path.LineTo(rect.MidX, rect.Bottom);
        path.LineTo(rect.Left, rect.MidY);
        path.Close();
        canvas.DrawPath(path, f);
        canvas.DrawPath(path, s);
    }

    private static void DrawCylinder(SKCanvas canvas, SKRect rect, SKColor fill, SKColor accent)
    {
        using var f = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var s = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
        var eh = rect.Height * 0.22f;
        var top = SKRect.Create(rect.Left, rect.Top, rect.Width, eh);
        var bottom = SKRect.Create(rect.Left, rect.Bottom - eh, rect.Width, eh);
        var body = SKRect.Create(rect.Left, rect.Top + eh / 2, rect.Width, rect.Height - eh);
        canvas.DrawRect(body, f);
        canvas.DrawOval(bottom, f);
        canvas.DrawOval(top, f);
        canvas.DrawArc(bottom, 0, 180, false, s);
        canvas.DrawLine(rect.Left, rect.Top + eh / 2, rect.Left, rect.Bottom - eh / 2, s);
        canvas.DrawLine(rect.Right, rect.Top + eh / 2, rect.Right, rect.Bottom - eh / 2, s);
        canvas.DrawOval(top, s);
    }

    private static void DrawNoteShape(SKCanvas canvas, SKRect rect, SKColor fill, SKColor accent)
    {
        using var f = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var s = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        var fold = MathF.Min(rect.Width, rect.Height) * 0.22f;
        using var path = new SKPath();
        path.MoveTo(rect.Left, rect.Top);
        path.LineTo(rect.Right - fold, rect.Top);
        path.LineTo(rect.Right, rect.Top + fold);
        path.LineTo(rect.Right, rect.Bottom);
        path.LineTo(rect.Left, rect.Bottom);
        path.Close();
        canvas.DrawPath(path, f);
        canvas.DrawPath(path, s);
        canvas.DrawLine(rect.Right - fold, rect.Top, rect.Right - fold, rect.Top + fold, s);
        canvas.DrawLine(rect.Right - fold, rect.Top + fold, rect.Right, rect.Top + fold, s);
    }

    private void DrawLabel(SKCanvas canvas, string label, float cx, float cy, float maxWidth, SKPaint paint)
    {
        if (string.IsNullOrEmpty(label)) return;
        var lines = WrapText(label, maxWidth, paint);
        if (lines.Count > 3)
        {
            lines = lines.Take(3).ToList();
            lines[2] = Ellipsize(lines[2], maxWidth, paint);
        }
        var lineHeight = paint.TextSize + 3;
        var totalHeight = lineHeight * lines.Count;
        var startY = cy - totalHeight / 2 + paint.TextSize;
        for (var i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i], cx, startY + i * lineHeight, paint);
    }

    private static string Ellipsize(string s, float maxWidth, SKPaint paint)
    {
        if (paint.MeasureText(s) <= maxWidth) return s;
        while (s.Length > 1 && paint.MeasureText(s + "…") > maxWidth) s = s[..^1];
        return s + "…";
    }

    private static List<string> WrapText(string text, float maxWidth, SKPaint paint)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (paint.MeasureText(candidate) <= maxWidth) current = candidate;
            else { if (current.Length > 0) lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) lines.Add(text);
        return lines;
    }
}
