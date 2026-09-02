using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

public class SceneSummariserTests
{
    [Fact]
    public void IncludesSemanticFieldsAndBoundsNormalizesNotes()
    {
        var graph = new SceneGraph();
        var operations = new List<ScenePatchOperation>
        {
            new GroupOp("outer", "Outer", Array.Empty<string>(), Subtitle: "West\r\nEurope",
                BoundaryKind: BoundaryKind.CloudScope),
            new GroupOp("inner", "Inner", Array.Empty<string>(), "outer", "10.0.0.0/24",
                BoundaryKind.Network),
            new AddNode("a", NodeKind.Process, "Alpha", "inner", Description: "Does\twork"),
            new AddNode("b", NodeKind.DataStore, "Bravo"),
            new Connect("e1", "a", "b", EdgeKind.Flow, "writes\r\ndata", Step: 2,
                Protocol: "HTTPS", Payload: "Customer record",
                DataClassification: "Confidential", Authentication: "OAuth",
                InteractionMode: InteractionMode.Synchronous),
            new NoteUpsert("note0", NoteKind.ActionItem, new string('x', 500) + "\r\nprivate", "alice"),
        };
        for (var i = 1; i < 30; i++)
            operations.Add(new NoteUpsert($"note{i}", NoteKind.General, $"note {i}"));
        new ScenePatchApplier().Apply(graph, new ScenePatch(operations));
        graph.Nodes["a"].Locked = true;
        graph.TryMarkNodeUserEdited("a");

        var summary = SceneSummariser.Summarise(graph);

        summary.Should().Contain("locked=true");
        summary.Should().Contain("step=2");
        summary.Should().Contain("parent=outer");
        summary.Should().Contain("subtitle=\"10.0.0.0/24\"");
        summary.Should().Contain("owner=alice");
        summary.Should().Contain("West Europe");
        summary.Should().Contain("writes data");
        summary.Should().Contain("intent=SoftwareSystemArchitecture");
        summary.Should().NotContain("lifecycle=");
        summary.Should().Contain("boundary=Network");
        summary.Should().Contain("protocol=\"HTTPS\"");
        summary.Should().Contain("payload=\"Customer record\"");
        summary.Should().Contain("classification=\"Confidential\"");
        summary.Should().Contain("authentication=\"OAuth\"");
        summary.Should().Contain("mode=Synchronous");
        summary.Should().NotContain("\t");
        summary.Should().Contain("note(s) omitted");
        summary.Length.Should().BeLessThan(12_000);
    }
}
