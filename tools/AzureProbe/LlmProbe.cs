using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureProbe;

public static class LlmProbe
{
    public static async Task<int> RunAsync(string endpoint, string deployment, string? tenantId, string transcript,
        bool continuous = false)
    {
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        var opts = new AzureOpenAIOptions
        {
            Endpoint = endpoint,
            DeploymentName = deployment,
            TenantId = tenantId,
            UseManagedIdentity = true,
            Temperature = null,
        };

        var chat = new AzureOpenAIScenePatchGenerator(
            Options.Create(opts),
            lf.CreateLogger<AzureOpenAIScenePatchGenerator>());
        var responses = new AzureOpenAIResponsesGenerator(
            Options.Create(opts),
            httpClient: null,
            lf.CreateLogger<AzureOpenAIResponsesGenerator>());
        var smart = new SmartScenePatchGenerator(
            Options.Create(opts),
            chat,
            responses,
            lf.CreateLogger<SmartScenePatchGenerator>());

        var fakeTranscript = transcript.Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select((line, i) => new TranscriptSegment(Guid.NewGuid(),
                i % 2 == 0 ? TranscriptSpeaker.Local : TranscriptSpeaker.Remote,
                line.Trim(), DateTimeOffset.UtcNow.AddSeconds(-30 + i), DateTimeOffset.UtcNow.AddSeconds(-29 + i)))
            .ToList();

        var req = new ScenePatchRequest(
            new SceneGraph(),
            fakeTranscript,
            Mode: continuous ? GenerationMode.ContinuousExtraction : GenerationMode.DeepSynthesis);
        Console.WriteLine($"[llm] Calling Smart({endpoint} / {deployment}) continuous={continuous}…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var resp = await smart.GenerateAsync(req, CancellationToken.None);
            sw.Stop();
            Console.WriteLine($"[llm] OK via {resp.ModelName}; elapsed={sw.ElapsedMilliseconds}ms ops={resp.Patch.Operations.Count}");
            foreach (var op in resp.Patch.Operations)
                Console.WriteLine($"  - {op}");
            Console.WriteLine();
            Console.WriteLine($"--- Raw JSON ({resp.RawJson?.Length} bytes) ---");
            Console.WriteLine(resp.RawJson?.Substring(0, Math.Min(4000, resp.RawJson.Length)));
            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"[llm] FAIL elapsed={sw.ElapsedMilliseconds}ms: {ex.Message}");
            return 1;
        }
    }
}