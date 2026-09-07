using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AudioBoarder.App.Sessions;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;

namespace AudioBoarder.App.Demo;

public static class WhiteboardScenarioProbe
{
    private sealed record Scenario(string Id, string[] Turns, Action<SceneGraph> Inspect);

    public static async Task RunAsync(
        IScenePatchGenerator generator, ILayoutEngine layout, string outputDirectory, CancellationToken ct)
    {
        var scenarios = new Scenario[]
        {
            new("independent-thoughts",
            [
                "Three independent ideas for discussion: curiosity, the beauty of asymmetry, and the feeling of belonging. We are not claiming connections between them.",
            ], scene =>
            {
                Require(HasNode(scene, "curiosity") && HasNode(scene, "asymmetry") && HasNode(scene, "belonging"),
                    "The three main abstract ideas must be on the canvas, not only in notes.");
                Require(scene.Nodes.Count == 3 && scene.Edges.Count == 0,
                    "Independent thoughts must not acquire a hub or invented relationships.");
                Require(scene.Nodes.Values.Any(node => node.Kind == NodeKind.Concept), "Concept cards are missing.");
                RequireNoVendor(scene);
            }),
            new("learning-and-questions",
            [
                "Psychological safety lets people ask basic questions. Asking basic questions supports learning.",
                "What if asking those questions takes time away from shipping? That concern is unresolved.",
                "Our proposed answer is to reserve ten minutes during the weekly review for those questions. This is a proposal, not an agreed decision.",
            ], scene =>
            {
                Require(HasNode(scene, "safety") && HasNode(scene, "question") && HasNode(scene, "learning"),
                    "The conceptual model must include safety, questions and learning.");
                Require(scene.Edges.Count >= 2, "Spoken relationships between concepts are missing.");
                Require(scene.Notes.Values.Any(note => note.Kind == NoteKind.Question) &&
                    scene.Notes.Values.Any(note => note.Kind == NoteKind.Answer), "Keep the question and its spoken answer.");
                Require(!scene.Notes.Values.Any(note => note.Kind == NoteKind.Decision), "A proposal is not a decision.");
                RequireNoVendor(scene);
            }),
            new("related-perspectives",
            [
                "Optimism and realism are related perspectives that we want to compare. Neither causes or depends on the other.",
            ], scene =>
            {
                Require(HasNode(scene, "optimism") && HasNode(scene, "realism"), "Both perspectives must be represented.");
                Require(scene.Edges.Count > 0 && scene.Edges.Values.All(edge => edge.Kind == EdgeKind.Association),
                    "Related perspectives must not imply a causal or directional dependency.");
                RequireNoVendor(scene);
            }),
            new("vendor-neutral-system",
            [
                "The browser calls our API over HTTPS. The API runs on self-managed Kubernetes.",
                "That API writes to PostgreSQL and uses Redis as its cache. These are self-managed tools, not managed cloud services.",
            ], scene =>
            {
                Require(HasNode(scene, "browser") && HasNode(scene, "api") &&
                    HasNode(scene, "postgres") && HasNode(scene, "redis"), "Keep the actual vendor-neutral components.");
                Require(scene.Edges.Count >= 3, "The system's interactions are missing.");
                var api = scene.Nodes.Values.Single(node => node.Label.Contains("API", StringComparison.OrdinalIgnoreCase));
                var runtimes = scene.Nodes.Values.Where(node => node.Label.Contains("kubernetes", StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                var hostingEdge = scene.Edges.Values.Any(edge =>
                    edge.FromNodeId == api.Id && runtimes.Contains(edge.ToNodeId) ||
                    runtimes.Contains(edge.FromNodeId) && edge.ToNodeId == api.Id);
                var hostingGroup = api.GroupId is not null && scene.Groups.TryGetValue(api.GroupId, out var group) &&
                    group.Label.Contains("kubernetes", StringComparison.OrdinalIgnoreCase);
                var hostingDetail = api.Description?.Contains("kubernetes", StringComparison.OrdinalIgnoreCase) == true;
                Require(hostingEdge || hostingGroup || hostingDetail, "The explicitly stated hosting relationship is missing.");
                RequireNoVendor(scene);
                Require(scene.Nodes.Values.All(node => !ComponentIconVisuals.ForNode(node).IsOfficial),
                    "Generic and third-party systems must not acquire Azure artwork.");
            }),
            new("business-process",
            [
                "A request enters intake. Intake passes it to risk review. Risk review sends low-risk requests to approval.",
            ], scene =>
            {
                Require(HasNode(scene, "intake") && HasNode(scene, "review") && HasNode(scene, "approval"),
                    "Model the actual business steps.");
                Require(scene.Edges.Count >= 2, "The spoken process order is missing.");
                RequireNoVendor(scene);
            }),
            new("mixed-topic-evolution",
            [
                "Azure App Service sends queries to Azure SQL Database.",
                "A separate topic: curiosity. Another independent thought is belonging. These ideas are not connected to our application or to each other.",
                "Curiosity encourages asking questions. Those questions support learning. Belonging is still an independent thought.",
            ], scene =>
            {
                Require(HasNode(scene, "azure app service") && HasNode(scene, "azure sql"),
                    "A topic change must retain the architecture.");
                Require(HasNode(scene, "curiosity") && HasNode(scene, "belonging") && HasNode(scene, "learning"),
                    "The new abstract topics must coexist with the architecture.");
                var technology = scene.Nodes.Values.Where(node => node.Label.Contains("Azure", StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                Require(scene.Edges.Values.All(edge =>
                    technology.Contains(edge.FromNodeId) == technology.Contains(edge.ToNodeId)),
                    "A topic change must not invent a link to the earlier architecture.");
                var belonging = scene.Nodes.Values.Single(node => node.Label.Contains("belonging", StringComparison.OrdinalIgnoreCase));
                Require(scene.Edges.Values.All(edge => edge.FromNodeId != belonging.Id && edge.ToNodeId != belonging.Id),
                    "The explicitly independent thought must stay independent.");
            }),
        };

        var summaries = new List<object>();
        Directory.CreateDirectory(outputDirectory);
        foreach (var scenario in scenarios)
        {
            var folder = Path.Combine(outputDirectory, scenario.Id);
            var store = new SessionStore(folder);
            var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(10));
            await using var orchestrator = new DiagramOrchestrator(generator, layout, buffer);
            var previousNodes = new HashSet<string>(StringComparer.Ordinal);
            var elapsed = Stopwatch.StartNew();
            Console.WriteLine($"Modeling {scenario.Id}...");
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var index = 0; index < scenario.Turns.Length; index++)
            {
                var segment = new TranscriptSegment(Guid.NewGuid(), index % 2 == 0 ? TranscriptSpeaker.Local : TranscriptSpeaker.Remote,
                    scenario.Turns[index], start.AddSeconds(index * 10), start.AddSeconds(index * 10 + 5));
                buffer.Append(segment);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                var result = await orchestrator.GenerateAsync(null, mode: GenerationMode.ContinuousExtraction,
                    transcriptWindow: [segment], ct: requestTimeout.Token);
                Require(result.ApplyResult.OperationsSkipped == 0, "The model returned an invalid or unsupported patch.");
                Require(previousNodes.IsSubsetOf(orchestrator.Scene.Nodes.Keys), "A live update removed earlier ideas.");
                previousNodes.UnionWith(orchestrator.Scene.Nodes.Keys);
                await store.SaveAsync(orchestrator.Scene.Clone(), ct);
                await File.WriteAllTextAsync(Path.Combine(folder, $"turn-{index + 1}.json"),
                    SceneToCanvasJson.Serialize(orchestrator.Scene, orchestrator.Scene.Revision), ct);
            }
            scenario.Inspect(orchestrator.Scene);
            var scene = orchestrator.Scene;
            Require(scene.Nodes.Values.All(node => string.IsNullOrEmpty(node.Icon) || IconRegistry.Has(node.Icon)),
                "A generated symbol is not in the safe registry.");
            summaries.Add(new
            {
                scenario.Id, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                nodes = scene.Nodes.Values.Select(node => new { node.Id, kind = node.Kind.ToString(), node.Label }),
                edges = scene.Edges.Values.Select(edge => new
                {
                    source = scene.Nodes[edge.FromNodeId].Label, target = scene.Nodes[edge.ToNodeId].Label,
                    kind = edge.Kind.ToString(), edge.Label,
                }),
                notes = scene.Notes.Values.Select(note => new { kind = note.Kind.ToString(), note.Text }),
                groups = scene.Groups.Values.Select(group => new { group.Id, group.Label, group.ParentGroupId }),
            });
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "results.json"),
                JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"{scenario.Id}: {scene.Nodes.Count} nodes, {scene.Edges.Count} connections, {scene.Notes.Count} notes; {elapsed.Elapsed.TotalSeconds:F1}s.");
        }
    }

    private static bool HasNode(SceneGraph scene, string term) =>
        scene.Nodes.Values.Any(node => node.Label.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void RequireNoVendor(SceneGraph scene) =>
        Require(scene.Nodes.Values.All(node => !node.Label.Contains("Azure", StringComparison.OrdinalIgnoreCase) &&
            !node.Label.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)), "An unmentioned vendor was invented.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
