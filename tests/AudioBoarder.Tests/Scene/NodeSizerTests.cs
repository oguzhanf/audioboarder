using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

/// <summary>
/// Regression cover for text spilling outside its shape: node boxes used to be a
/// fixed 140x60 regardless of content.
/// </summary>
public class NodeSizerTests
{
    private const double Advance = 0.58;

    [Fact]
    public void LongLabelGetsATallerOrWiderBoxThanAShortOne()
    {
        var (shortW, shortH) = NodeSizer.Measure("API", null, hasIcon: true);
        var (longW, longH) = NodeSizer.Measure(
            "Customer documentation first draft due September 10", null, hasIcon: true);

        (longW * longH).Should().BeGreaterThan(shortW * shortH);
    }

    [Fact]
    public void BoxIsAlwaysWideEnoughForItsLongestWord()
    {
        var (width, _) = NodeSizer.Measure("Internationalisation", null, hasIcon: true);

        var wordWidth = "Internationalisation".Length * NodeSizer.LabelFontSize * Advance;
        var usable = width - NodeSizer.IconBand;
        usable.Should().BeGreaterThan(wordWidth * 0.75,
            "a box narrower than its longest unbreakable word will clip that word");
    }

    [Fact]
    public void TextAlwaysFitsInsideTheMeasuredBox()
    {
        var cases = new (string Label, string? Description)[]
        {
            ("API", null),
            ("Release checklist due September 11", null),
            ("Customer documentation first draft due September 10", null),
            ("Owner extraction errors reduce accuracy across multiple named speakers", null),
            // Descriptions render in the SAME bound text block as the label, so they
            // must be measured at the same font size or the shape under-sizes.
            ("Cloud capacity delay", "Capacity may slip in week two"),
            ("DLP policy", "blocks confidential exports from the production tenant"),
        };

        // Every kind, so sloped shapes (diamond/ellipse) are covered too.
        foreach (var kind in Enum.GetValues<NodeKind>())
        {
            foreach (var (label, description) in cases)
            {
                var (width, height) = NodeSizer.Measure(label, description, hasIcon: true, kind: kind);
                var ratio = NodeSizer.InteriorRatioFor(kind);

                // Wrap exactly as SceneToExcalidrawConverter.BuildBoundLabel does.
                var textWidth = Math.Max(20, width * ratio - NodeSizer.IconBand - 12);
                var composed = description is null ? label : label + "\n" + description;
                var lines = NodeSizer.CountLines(composed, textWidth, NodeSizer.LabelFontSize);
                var needed = lines * NodeSizer.LabelFontSize * NodeSizer.LineHeight;

                (height * ratio).Should().BeGreaterThanOrEqualTo(needed,
                    $"'{composed}' as {kind} needs {lines} line(s) and must not overflow its shape");
            }
        }
    }

    [Fact]
    public void SlopedShapesGetABiggerBoxThanRectanglesForTheSameText()
    {
        const string label = "Security review closes all high-severity findings";
        var (rectW, rectH) = NodeSizer.Measure(label, null, hasIcon: true, kind: NodeKind.Process);
        var (diamondW, diamondH) = NodeSizer.Measure(label, null, hasIcon: true, kind: NodeKind.Risk);

        (diamondW * diamondH).Should().BeGreaterThan(rectW * rectH,
            "a diamond's inscribed rectangle is about half its bounding box");
    }

    [Fact]
    public void DescriptionAddsHeight()
    {
        var (_, plain) = NodeSizer.Measure("DLP policy", null, hasIcon: true);
        var (_, described) = NodeSizer.Measure(
            "DLP policy", "blocks confidential exports from the tenant", hasIcon: true);

        described.Should().BeGreaterThan(plain);
    }

    [Fact]
    public void ApplyToSizesEveryUnlockedNodeAndLeavesPinnedOnesAlone()
    {
        var graph = new SceneGraph();
        var applier = new Core.Patch.ScenePatchApplier();
        applier.Apply(graph, new Core.Patch.ScenePatch(new Core.Patch.ScenePatchOperation[]
        {
            new Core.Patch.AddNode("a", NodeKind.Process, "A very long label that must wrap onto lines"),
            new Core.Patch.AddNode("b", NodeKind.Process, "B"),
        }));
        graph.Nodes["b"].Width = 999;
        graph.Nodes["b"].Height = 999;
        graph.Nodes["b"].Locked = true;

        NodeSizer.ApplyTo(graph);

        graph.Nodes["a"].Width.Should().BeGreaterThan(0);
        graph.Nodes["a"].Height.Should().BeGreaterThan(0);
        graph.Nodes["b"].Width.Should().Be(999, "a pinned node keeps the size the user gave it");
    }

    [Fact]
    public void WidthIsClampedSoOneLabelCannotDominateTheBoard()
    {
        var (width, _) = NodeSizer.Measure(new string('x', 400), null, hasIcon: true);

        width.Should().BeLessThanOrEqualTo(600);
    }

    [Fact]
    public void ASingleUnbreakableTokenIsHardBrokenNotOverflowed()
    {
        const string token = "Azure-Synapse-Analytics-Workspace-Provisioning-Pipeline";
        var (width, height) = NodeSizer.Measure(token, null, hasIcon: true);

        var textWidth = width - NodeSizer.IconBand - 12;
        var lines = NodeSizer.CountLines(token, textWidth, NodeSizer.LabelFontSize);

        lines.Should().BeGreaterThan(1, "a token wider than the line must be hard-broken");
        height.Should().BeGreaterThanOrEqualTo(lines * NodeSizer.LabelFontSize * NodeSizer.LineHeight);
    }

    [Theory]
    [InlineData("Rapid prototyping", "api")]
    [InlineData("Staging environment", "tag")]
    [InlineData("Keyboard shortcuts", "key")]
    public void IconMatchingRequiresWholeWords(string label, string wrongPhrase)
    {
        // Substring matching gave visibly wrong icons for these labels.
        var resolved = IconRegistry.Resolve(label, NodeKind.Process);

        resolved.Should().Be("cog", $"'{label}' must not match the '{wrongPhrase}' phrase");
    }

    [Fact]
    public void IconMatchingStillFindsRealTechnologies()
    {
        IconRegistry.Resolve("Microsoft Purview", NodeKind.Technology).Should().Be("search");
        IconRegistry.Resolve("Power BI dashboard", NodeKind.Technology).Should().Be("bar-chart");
        IconRegistry.Resolve("Payments API", NodeKind.Technology).Should().Be("plug");
    }
}
