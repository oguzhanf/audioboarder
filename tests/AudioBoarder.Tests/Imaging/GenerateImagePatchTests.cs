using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Imaging;

public class GenerateImagePatchTests
{
    [Fact]
    public void GenerateImage_AddsPendingSceneImage()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
            new GenerateImage("img-1", "A futuristic dashboard", AttachToNodeId: "a"),
        }));
        scene.Images.Should().ContainKey("img-1");
        scene.Images["img-1"].Status.Should().Be(ImageGenerationStatus.Pending);
        scene.Images["img-1"].Prompt.Should().Be("A futuristic dashboard");
        scene.Images["img-1"].AttachedToNodeId.Should().Be("a");
    }

    [Fact]
    public void GenerateImage_MissingAttachNode_AddsImageUnattached()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new GenerateImage("img-1", "test", AttachToNodeId: "missing"),
        }));
        scene.Images.Should().ContainKey("img-1");
        scene.Images["img-1"].AttachedToNodeId.Should().BeNull();
    }

    [Fact]
    public void DeleteNode_DetachesImagesNotDeletesThem()
    {
        var scene = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new GenerateImage("img-1", "test", AttachToNodeId: "a"),
        }));
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new DeleteNode("a"),
        }));
        scene.Nodes.Should().NotContainKey("a");
        scene.Images.Should().ContainKey("img-1");
        scene.Images["img-1"].AttachedToNodeId.Should().BeNull();
    }

    [Fact]
    public void DeleteImage_RemovesImage()
    {
        var scene = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new GenerateImage("img-1", "test"),
        }));
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new DeleteImage("img-1"),
        }));
        scene.Images.Should().BeEmpty();
    }
}
