using System.Security.Cryptography;
using System.Text;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.LLM;

internal static class MeetingNotePatchNormalizer
{
    public static ScenePatch Normalize(ScenePatch patch, SceneGraph scene)
    {
        var notes = scene.Notes.Values.ToList();
        var operations = new List<ScenePatchOperation>(patch.Operations.Count);
        foreach (var operation in patch.Operations)
        {
            if (operation is not NoteUpsert note)
            {
                operations.Add(operation);
                continue;
            }

            var text = (note.Text ?? string.Empty).Trim();
            var question = notes.FirstOrDefault(existing => existing.Kind == NoteKind.Question &&
                (existing.Id == note.Id || text.StartsWith(existing.Text.Trim() + " ", StringComparison.OrdinalIgnoreCase)));
            if (question is not null)
            {
                var original = question.Text.Trim();
                var appended = text.StartsWith(original + " ", StringComparison.OrdinalIgnoreCase)
                    ? text[original.Length..].Trim() : null;
                if (note.Kind == NoteKind.Answer ||
                    (note.Kind == NoteKind.Question && appended is { Length: > 0 } && !appended.Contains('?')))
                {
                    // Some models append an answer to its question despite the prompt.
                    // Keep the question and give only the supplied answer its own card.
                    text = appended ?? text;
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(question.Id + "\n" + text));
                    note = note with
                    {
                        Id = "answer-" + Convert.ToHexString(hash)[..24].ToLowerInvariant(),
                        Kind = NoteKind.Answer, Text = text,
                    };
                }
            }

            if (note.Kind == NoteKind.Concept)
            {
                var prior = notes.FirstOrDefault(existing => existing.Kind == NoteKind.Concept &&
                    existing.Id != note.Id && existing.Text.Trim().Length >= 24 &&
                    ExtendsText(text, existing.Text.Trim().TrimEnd('.')));
                if (prior is not null) note = note with { Id = prior.Id };
            }

            operations.Add(note with { Text = text, SourceTimestamp = null });
            notes.RemoveAll(existing => existing.Id == note.Id);
            notes.Add(new SceneNote { Id = note.Id, Kind = note.Kind, Text = text });
        }
        return new ScenePatch(operations);
    }

    private static bool ExtendsText(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length]) || char.IsPunctuation(text[prefix.Length]));
}
