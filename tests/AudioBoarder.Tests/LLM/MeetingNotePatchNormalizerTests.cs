using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

public sealed class MeetingNotePatchNormalizerTests
{
    [Theory]
    [InlineData(NoteKind.Question, "What if the worker stops? Orders remain on the queue.")]
    [InlineData(NoteKind.Answer, "Orders remain on the queue.")]
    public void SpokenAnswerDoesNotReplaceItsQuestion(NoteKind kind, string text)
    {
        var scene = new SceneGraph();
        scene.AddNote(new SceneNote { Id = "question", Kind = NoteKind.Question, Text = "What if the worker stops?" });
        var patch = MeetingNotePatchNormalizer.Normalize(new ScenePatch([new NoteUpsert("question", kind, text)]), scene);
        new ScenePatchApplier().Apply(scene, patch);

        scene.Notes["question"].Text.Should().Be("What if the worker stops?");
        scene.Notes.Values.Should().ContainSingle(note =>
            note.Kind == NoteKind.Answer && note.Text == "Orders remain on the queue.");
        new ScenePatchApplier().Apply(scene, patch);
        scene.Notes.Count.Should().Be(2);
    }

    [Fact]
    public void FollowupQuestionIsNotMisclassifiedAsAnAnswer()
    {
        var scene = new SceneGraph();
        scene.AddNote(new SceneNote { Id = "q", Kind = NoteKind.Question, Text = "What if the worker stops?" });
        var patch = new ScenePatch([new NoteUpsert("q", NoteKind.Question,
            "What if the worker stops? Who gets the alert?")]);

        MeetingNotePatchNormalizer.Normalize(patch, scene).Should().BeEquivalentTo(patch);
    }

    [Fact]
    public void RestatedRequirementEnrichesItsOriginalCard()
    {
        var scene = new SceneGraph();
        scene.AddNote(new SceneNote { Id = "requirement", Kind = NoteKind.Concept, Text = "Recovery time under fifteen minutes." });
        var patch = MeetingNotePatchNormalizer.Normalize(new ScenePatch([new NoteUpsert("duplicate", NoteKind.Concept,
            "Recovery time under fifteen minutes, confirmed with operations.")]), scene);
        new ScenePatchApplier().Apply(scene, patch);

        scene.Notes.Should().ContainSingle();
        scene.Notes["requirement"].Text.Should().Contain("confirmed with operations");
    }
}
