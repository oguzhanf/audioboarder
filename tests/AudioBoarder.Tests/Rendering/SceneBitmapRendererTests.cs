using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;
using AudioBoarder.Services.Rendering;

namespace AudioBoarder.Tests.Rendering;

public class SceneBitmapRendererTests
{
    [Fact]
    public void RenderPng_ProducesNonTrivialBitmap()
    {
        var graph = new SceneGraph();
        new ScenePatchApplier().Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
            new AddNode("b", NodeKind.Decision, "?"),
            new AddNode("c", NodeKind.DataStore, "DB"),
            new Connect("e1", "a", "b", EdgeKind.Flow, "yes"),
            new Connect("e2", "b", "c", EdgeKind.Dependency, "writes"),
        }));
        new LayeredLayoutEngine().Apply(graph, new AudioBoarder.Core.Layout.LayoutOptions());

        var png = SceneBitmapRenderer.RenderPng(graph, 800, 600);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(1024);
        // PNG magic header
        png[0].Should().Be(0x89);
        png[1].Should().Be((byte)'P');
        png[2].Should().Be((byte)'N');
        png[3].Should().Be((byte)'G');
    }
}
