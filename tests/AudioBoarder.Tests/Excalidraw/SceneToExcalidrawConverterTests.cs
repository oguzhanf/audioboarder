using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Excalidraw;

public class SceneToExcalidrawConverterTests
{
    private readonly SceneToExcalidrawConverter _converter = new();
    private readonly ScenePatchApplier _applier = new();

    private SceneGraph BuildScene()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Client app"),
            new AddNode("b", NodeKind.Decision, "Authorized?"),
            new AddNode("c", NodeKind.DataStore, "User database"),
            new AddNode("d", NodeKind.Entity, "Order"),
            new Connect("e1", "a", "b", EdgeKind.Flow, "request"),
            new Connect("e2", "b", "c", EdgeKind.Dependency),
            new Connect("e3", "d", "c", EdgeKind.Inheritance),
        }));
        // Stand in for the layout engine: give every node a position.
        var i = 0;
        foreach (var n in graph.Nodes.Values)
        {
            n.X = 100 + i * 200;
            n.Y = 100 + i * 120;
            i++;
        }
        return graph;
    }

    private static ExcalidrawElement? Find(ExcalidrawDocument doc, string id)
        => doc.Elements.FirstOrDefault(e => e.Id == id);

    [Fact]
    public void ExplicitIconOverridesRegistryGlyph()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("t", NodeKind.Technology, "Power BI", Icon: "database"),
        }));
        graph.Nodes["t"].X = 10; graph.Nodes["t"].Y = 10;

        // A valid registry icon name wins over the label-derived default.
        graph.Nodes["t"].EffectiveIconName.Should().Be("database");
        // Icons are vector elements, never glyphs spliced into the label text.
        Find(_converter.Convert(graph), "t_label")!.OriginalText.Should().Be("Power BI");
    }

    [Fact]
    public void UnknownExplicitIconFallsBackToTheResolvedIcon()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            // An emoji (or any junk) must never reach the renderer.
            new AddNode("t", NodeKind.Technology, "Microsoft Purview", Icon: "\U0001F680"),
        }));

        graph.Nodes["t"].EffectiveIconName.Should().Be("search");
    }

    [Fact]
    public void KnownTechnologyGetsAutoGlyphWithoutExplicitIcon()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("t", NodeKind.Technology, "Microsoft Purview"),
        }));
        graph.Nodes["t"].X = 10; graph.Nodes["t"].Y = 10;

        var doc = _converter.Convert(graph);

        // The label carries text only …
        Find(doc, "t_label")!.OriginalText.Should().Be("Microsoft Purview");
        // … and the icon is a real image element backed by an SVG data URL.
        var icon = Find(doc, "t_icon");
        icon.Should().NotBeNull();
        icon!.Type.Should().Be("image");
        icon.FileId.Should().NotBeNullOrEmpty();
        doc.Files.Should().ContainKey(icon.FileId!);
    }

    [Fact]
    public void IconsContainNoEmoji()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Security, "Defender"),
            new AddNode("b", NodeKind.Actor, "Priya"),
            new AddNode("c", NodeKind.Risk, "Cloud capacity delay"),
        }));

        var doc = _converter.Convert(graph);

        foreach (var element in doc.Elements)
        {
            var text = (element.Text ?? string.Empty) + (element.OriginalText ?? string.Empty);
            text.Should().NotContainAny("\U0001F680", "\u26A0", "\U0001F6E1", "\U0001F464");
            foreach (var ch in text)
                char.IsSurrogate(ch).Should().BeFalse("emoji must not appear anywhere on the board");
        }
    }

    [Fact]
    public void DescriptionIsRenderedUnderLabelAndGrowsTheBox()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("d", NodeKind.Security, "DLP policy",
                Description: "blocks confidential exports"),
        }));
        graph.Nodes["d"].X = 200; graph.Nodes["d"].Y = 200;

        var doc = _converter.Convert(graph);
        Find(doc, "d_label")!.OriginalText.Should().Contain("blocks confidential exports");
        // Descriptions add a wrapped line, so the shape must grow beyond the default.
        Find(doc, "d")!.Width.Should().BeGreaterThan(140);
    }

    [Fact]
    public void NewKindsMapToDistinctShapes()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("sys", NodeKind.System, "Fabric"),
            new AddNode("risk", NodeKind.Risk, "Data leakage"),
            new AddNode("metric", NodeKind.Metric, "Daily volume"),
        }));
        foreach (var n in graph.Nodes.Values) { n.X = 50; n.Y = 50; }

        var doc = _converter.Convert(graph);
        Find(doc, "sys")!.Type.Should().Be("rectangle");
        Find(doc, "risk")!.Type.Should().Be("diamond");
        Find(doc, "metric")!.Type.Should().Be("ellipse");
        // A system boundary is drawn heavier than an ordinary node.
        Find(doc, "sys")!.StrokeWidth.Should().BeGreaterThan(Find(doc, "risk")!.StrokeWidth);
    }

    [Fact]
    public void EdgeLabelIsRenderedAndBoundToArrow()
    {
        var doc = _converter.Convert(BuildScene());
        var label = Find(doc, "e1_label")!;
        label.Text.Should().Be("request");
        label.ContainerId.Should().Be("e1");
        Find(doc, "e1")!.BoundElements.Should().Contain(b => b.Id == "e1_label");
    }

    [Fact]
    public void Document_HasExcalidrawHeader()
    {
        var doc = _converter.Convert(BuildScene());
        doc.Type.Should().Be("excalidraw");
        doc.Version.Should().Be(2);
        doc.Source.Should().Be("audioboarder");
        doc.AppState.ViewBackgroundColor.Should().Be("#ffffff");
    }

    [Fact]
    public void MapsNodeKindsToShapes()
    {
        var doc = _converter.Convert(BuildScene());
        Find(doc, "a")!.Type.Should().Be("rectangle");   // Process
        Find(doc, "a")!.Roundness.Should().NotBeNull();  // rounded
        Find(doc, "b")!.Type.Should().Be("diamond");     // Decision
        Find(doc, "c")!.Type.Should().Be("ellipse");     // DataStore
        Find(doc, "d")!.Type.Should().Be("rectangle");   // Entity
        Find(doc, "d")!.Roundness.Should().BeNull();     // sharp
    }

    [Fact]
    public void NodeShapeUsesTopLeftFromCenter()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
        }));
        var node = graph.Nodes["a"];
        // Generous explicit size so auto-sizing does not grow the box past it.
        node.X = 300; node.Y = 200; node.Width = 300; node.Height = 160;

        var shape = Find(_converter.Convert(graph), "a")!;
        shape.X.Should().Be(150);  // 300 - 300/2
        shape.Y.Should().Be(120);  // 200 - 160/2
        shape.Width.Should().Be(300);
        shape.Height.Should().Be(160);
    }

    [Fact]
    public void CreatesBoundTextLabelForNode()
    {
        var doc = _converter.Convert(BuildScene());
        var label = Find(doc, "a_label")!;
        label.Type.Should().Be("text");
        // The label carries text only; the kind icon is a separate vector element.
        label.Text.Should().Be("Client app");
        label.ContainerId.Should().Be("a");
        label.FontFamily.Should().Be(1); // Virgil hand-drawn
        label.StrokeColor.Should().Be(ExcalidrawPalette.Ink);

        Find(doc, "a")!.BoundElements.Should()
            .Contain(b => b.Id == "a_label" && b.Type == "text");
    }

    [Fact]
    public void CreatesBoundArrowsBetweenNodes()
    {
        var doc = _converter.Convert(BuildScene());
        var arrow = Find(doc, "e1")!;
        arrow.Type.Should().Be("arrow");
        arrow.StartBinding!.ElementId.Should().Be("a");
        arrow.EndBinding!.ElementId.Should().Be("b");
        arrow.EndArrowhead.Should().Be("arrow");
        // Stepped orthogonal route: start, two waypoints, end.
        arrow.Points.Should().HaveCount(4);
        // Every segment is axis-aligned — no free diagonals.
        for (var i = 1; i < arrow.Points!.Length; i++)
        {
            var dx = Math.Abs(arrow.Points[i][0] - arrow.Points[i - 1][0]);
            var dy = Math.Abs(arrow.Points[i][1] - arrow.Points[i - 1][1]);
            Math.Min(dx, dy).Should().Be(0, "each elbow segment must be horizontal or vertical");
        }
        // Excalidraw's own elbow mode is deliberately off: with bound endpoints it
        // never regenerates points, so it would render straight while hiding handles.
        arrow.Elbowed.Should().BeFalse();

        // The endpoints register the arrow on both nodes (so Excalidraw routes it).
        Find(doc, "a")!.BoundElements.Should().Contain(b => b.Id == "e1" && b.Type == "arrow");
        Find(doc, "b")!.BoundElements.Should().Contain(b => b.Id == "e1" && b.Type == "arrow");
    }

    [Fact]
    public void DependencyEdgesAreDashed()
    {
        var doc = _converter.Convert(BuildScene());
        Find(doc, "e2")!.StrokeStyle.Should().Be("dashed");
        Find(doc, "e1")!.StrokeStyle.Should().Be("solid");
    }

    [Fact]
    public void InheritanceEdgeUsesTriangleArrowhead()
    {
        var doc = _converter.Convert(BuildScene());
        Find(doc, "e3")!.EndArrowhead.Should().Be("triangle");
    }

    [Fact]
    public void EdgeLabelBecomesBoundArrowText()
    {
        var doc = _converter.Convert(BuildScene());
        var lbl = Find(doc, "e1_label")!;
        lbl.Type.Should().Be("text");
        lbl.Text.Should().Be("request");
        lbl.ContainerId.Should().Be("e1");
        Find(doc, "e1")!.BoundElements.Should().Contain(b => b.Id == "e1_label");
    }

    [Fact]
    public void GroupBecomesFrameThatOwnsItsMembers()
    {
        var graph = BuildScene();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new GroupOp("g1", "Backend", new[] { "b", "c" }),
        }));

        var doc = _converter.Convert(graph);
        var frame = Find(doc, "g1_frame")!;
        // A frame owns its children, so dragging the boundary moves the contents.
        // A plain background rectangle just slid out from under them.
        frame.Type.Should().Be("frame");
        frame.Name.Should().Be("Backend");

        Find(doc, "b")!.FrameId.Should().Be("g1_frame");
        Find(doc, "c")!.FrameId.Should().Be("g1_frame");
        // A node outside the group must not be captured by the frame.
        Find(doc, "a")!.FrameId.Should().BeNull();

        // The frame is drawn behind the nodes it contains.
        var frameIdx = doc.Elements.FindIndex(e => e.Id == "g1_frame");
        var nodeIdx = doc.Elements.FindIndex(e => e.Id == "b");
        frameIdx.Should().BeLessThan(nodeIdx);
    }

    [Fact]
    public void IncludesNotesAsStickyByDefault()
    {
        var graph = BuildScene();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("n1", NoteKind.ActionItem, "Send proposal", "Alex"),
        }));

        var doc = _converter.Convert(graph);
        Find(doc, "n1_note")!.Type.Should().Be("rectangle");
        var text = Find(doc, "n1_note_text")!;
        text.Text.Should().Contain("Send proposal");
        text.Text.Should().Contain("Alex");
        text.ContainerId.Should().Be("n1_note");
    }

    [Fact]
    public void NotesCanBeDisabled()
    {
        var graph = BuildScene();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new NoteUpsert("n1", NoteKind.Risk, "Latency risk"),
        }));

        var doc = _converter.Convert(graph, new ExcalidrawExportOptions { IncludeNotes = false });
        Find(doc, "n1_note").Should().BeNull();
    }

    [Fact]
    public void AllTextElementsHaveNonZeroDimensions()
    {
        var graph = BuildScene();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new GroupOp("g1", "Backend", new[] { "b", "c" }),
            new NoteUpsert("n1", NoteKind.Decision, "Use Postgres"),
        }));

        var doc = _converter.Convert(graph);
        var texts = doc.Elements.Where(e => e.Type == "text").ToList();
        texts.Should().NotBeEmpty();
        texts.Should().OnlyContain(t => t.Width > 0 && t.Height > 0);
    }

    [Fact]
    public void SeedsAreDeterministicAcrossConversions()
    {
        var a = _converter.Convert(BuildScene());
        var b = _converter.Convert(BuildScene());
        foreach (var ea in a.Elements)
        {
            var eb = b.Elements.First(x => x.Id == ea.Id);
            eb.Seed.Should().Be(ea.Seed);
            ea.Seed.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void UnpositionedNodesStillExportWithFiniteCoordinates()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
        }));
        // No positions assigned (X/Y remain null).

        var doc = _converter.Convert(graph);
        foreach (var id in new[] { "a", "b" })
        {
            var s = Find(doc, id)!;
            double.IsFinite(s.X).Should().BeTrue();
            double.IsFinite(s.Y).Should().BeTrue();
        }
    }

    [Fact]
    public void ProducesValidJsonThatRoundTrips()
    {
        var json = _converter.ConvertToJson(BuildScene());
        json.Should().Contain("\"type\": \"excalidraw\"");
        json.Should().Contain("\"version\": 2");

        var doc = ExcalidrawJson.Deserialize(json);
        doc.Elements.Should().NotBeEmpty();
        doc.Type.Should().Be("excalidraw");
    }

    [Fact]
    public void IconsAreDrawnInsideEveryShapeAndMoveWithIt()
    {
        // A diamond's bounding-box corner is OUTSIDE its outline, so a bounding-box
        // offset would leave the icon floating in empty canvas.
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("risk", NodeKind.Risk, "Cloud capacity delay"),
            new AddNode("dec", NodeKind.Decision, "Ship the Orion pilot"),
            new AddNode("ds", NodeKind.DataStore, "Documentation repository"),
            new AddNode("proc", NodeKind.Process, "Run replay test"),
        }));
        NodeSizer.ApplyTo(graph);
        foreach (var n in graph.Nodes.Values) { n.X = 500; n.Y = 500; }

        var doc = _converter.Convert(graph);

        foreach (var node in graph.Nodes.Values)
        {
            var icon = Find(doc, node.Id + "_icon")!;
            var shape = Find(doc, node.Id)!;

            foreach (var (px, py) in new[]
            {
                (icon.X, icon.Y),
                (icon.X + icon.Width, icon.Y),
                (icon.X, icon.Y + icon.Height),
                (icon.X + icon.Width, icon.Y + icon.Height),
            })
            {
                IsInsideShape(node, px, py, 500, 500).Should().BeTrue(
                    $"icon corner ({px:F0},{py:F0}) must lie inside the {node.Kind} outline");
            }

            // Grouped, not locked — otherwise dragging the node leaves the icon behind.
            icon.Locked.Should().BeFalse();
            icon.GroupIds.Should().NotBeEmpty();
            shape.GroupIds.Should().Equal(icon.GroupIds);
        }
    }

    private static bool IsInsideShape(SceneNode node, double px, double py, double cx, double cy)
    {
        double w = node.Width, h = node.Height;
        return node.Kind switch
        {
            NodeKind.Risk or NodeKind.Decision or NodeKind.Security or NodeKind.Milestone =>
                Math.Abs(px - cx) / (w / 2) + Math.Abs(py - cy) / (h / 2) <= 1.0,
            NodeKind.DataStore or NodeKind.Cloud or NodeKind.Metric =>
                Math.Pow((px - cx) / (w / 2), 2) + Math.Pow((py - cy) / (h / 2), 2) <= 1.0,
            _ => px >= cx - w / 2 && px <= cx + w / 2 && py >= cy - h / 2 && py <= cy + h / 2,
        };
    }
}
