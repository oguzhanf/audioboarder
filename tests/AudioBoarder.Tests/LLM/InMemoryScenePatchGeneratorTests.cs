using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Tests.Fakes;

namespace AudioBoarder.Tests.LLM;

public class InMemoryScenePatchGeneratorTests
{
    [Fact]
    public async Task FirstGeneration_ProducesBootstrapScene()
    {
        var gen = new InMemoryScenePatchGenerator();
        var transcript = new[] { new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "hello", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        var req = new ScenePatchRequest(new SceneGraph(), transcript);

        var resp = await gen.GenerateAsync(req, CancellationToken.None);

        resp.Patch.Operations.Should().NotBeEmpty();
        resp.Patch.Operations.OfType<AudioBoarder.Core.Patch.AddNode>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RawJson_IsValidScenePatch()
    {
        var gen = new InMemoryScenePatchGenerator();
        var req = new ScenePatchRequest(new SceneGraph(), Array.Empty<TranscriptSegment>());
        var resp = await gen.GenerateAsync(req, CancellationToken.None);

        var parsed = AudioBoarder.Core.Patch.ScenePatchJson.Deserialize(resp.RawJson!);
        parsed.Operations.Count.Should().Be(resp.Patch.Operations.Count);
    }
}
