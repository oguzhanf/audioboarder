using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Tests.Fakes;

public sealed class InMemoryScenePatchGenerator : IScenePatchGenerator
{
    private int _counter;

    public string Name => "InMemory(test)";

    public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = DateTimeOffset.UtcNow;
        var ops = new List<ScenePatchOperation>();

        var transcript = request.TranscriptWindow ?? Array.Empty<TranscriptSegment>();
        var firstRun = request.CurrentScene.Nodes.Count == 0;

        if (firstRun)
        {
            ops.Add(new AddNode("user", NodeKind.Actor, "User"));
            ops.Add(new AddNode("client", NodeKind.Process, "Client app"));
            ops.Add(new AddNode("api", NodeKind.Process, "API"));
            ops.Add(new AddNode("db", NodeKind.DataStore, "Database"));
            ops.Add(new Connect("e1", "user", "client", EdgeKind.Flow, "uses"));
            ops.Add(new Connect("e2", "client", "api", EdgeKind.Flow, "calls"));
            ops.Add(new Connect("e3", "api", "db", EdgeKind.Flow, "queries"));
        }

        var summary = BuildSummary(transcript, request.UserInstruction);
        if (!string.IsNullOrEmpty(summary))
        {
            var id = $"note-{++_counter}";
            ops.Add(new NoteUpsert(id, NoteKind.General, summary,
                SourceTimestamp: transcript.LastOrDefault()?.End));
        }

        var patch = new ScenePatch(ops);
        var elapsed = DateTimeOffset.UtcNow - start;
        return Task.FromResult(new ScenePatchResponse(patch, Name, elapsed,
            RawJson: ScenePatchJson.Serialize(patch)));
    }

    private static string BuildSummary(IReadOnlyList<TranscriptSegment> transcript, string? userInstruction)
    {
        var preview = string.Join(" | ", transcript.TakeLast(3).Select(s => $"[{s.Speaker}] {s.Text}"));
        if (!string.IsNullOrWhiteSpace(userInstruction))
            return $"{userInstruction.Trim()} :: {preview}";
        return string.IsNullOrEmpty(preview) ? "Test scene generated" : preview;
    }
}
