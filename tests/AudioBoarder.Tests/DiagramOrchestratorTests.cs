using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Tests.Fakes;

namespace AudioBoarder.Tests;

public class DiagramOrchestratorTests
{
    [Fact]
    public async Task LayoutFailureRaisesTerminalFailureEvent()
    {
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        var orchestrator = new DiagramOrchestrator(
            new InMemoryScenePatchGenerator(),
            new ThrowingLayout(),
            buffer,
            new SceneGraph());
        DiagramGenerationFailed? failure = null;
        orchestrator.GenerationFailed += (_, value) => failure = value;

        var act = () => orchestrator.GenerateAsync(null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Should().NotBeNull();
        failure!.Error.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class ThrowingLayout : ILayoutEngine
    {
        public string Name => "throwing";
        public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
            => throw new InvalidOperationException("layout failed");
    }
}
