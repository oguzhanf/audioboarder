using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Setup;

public static class WorkspaceSetupCommand
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var credentials = services.GetRequiredService<IAzureCredentialProvider>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        if (!await credentials.TryRestoreAsync(timeout.Token))
        {
            Console.WriteLine("Azure sign-in is required. Open AudioBoarder and connect the account first.");
            return 2;
        }
        if (!credentials.TryGetSignedInCredential(out var credential) || credential is null) return 2;
        var settings = services.GetRequiredService<IOptions<AudioBoarderSettings>>().Value;
        if (args.Contains("--check-resources", StringComparer.OrdinalIgnoreCase))
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var ready = await services.GetRequiredService<AutomaticWorkspaceSetup>()
                .HasRequiredResourcesAsync(credential, settings, checkTimeout.Token);
            Console.WriteLine($"Saved workspace resources: {(ready ? "ready" : "setup required")} in {elapsed.Elapsed.TotalSeconds:F1}s.");
            return ready ? 0 : 2;
        }
        if (args.Contains("--check-model", StringComparer.OrdinalIgnoreCase))
        {
            var options = services.GetRequiredService<IOptions<AudioBoarder.Services.LLM.AzureOpenAIOptions>>().Value;
            options.Credential = credential;
            var now = DateTimeOffset.UtcNow;
            var orchestrator = services.GetRequiredService<AudioBoarder.Services.DiagramOrchestrator>();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await orchestrator.GenerateAsync(null, mode: AudioBoarder.Core.LLM.GenerationMode.ContinuousExtraction,
                transcriptWindow: [new(Guid.NewGuid(), AudioBoarder.Core.Transcript.TranscriptSpeaker.Local,
                    "Customers enter through Azure Front Door. Front Door sends HTTPS requests to Azure App Service.", now, now.AddSeconds(5))],
                ct: timeout.Token);
            Console.WriteLine($"Synthetic pipeline probe: {orchestrator.Scene.Nodes.Count} nodes and {orchestrator.Scene.Edges.Count} edges in {elapsed.Elapsed.TotalSeconds:F1}s.");
            if (orchestrator.Scene.Nodes.Count < 2) throw new InvalidOperationException("The diagram pipeline did not produce the expected grounded components.");
            return 0;
        }
        var plan = await services.GetRequiredService<AutomaticWorkspaceSetup>()
            .InspectAsync(credential, settings, timeout.Token);
        Console.WriteLine(plan.Summary);
        if (!args.Contains("--apply", StringComparer.OrdinalIgnoreCase)) return 0;
        if (!args.Contains("--approve-azure", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Use --approve-azure only after approving resource creation, usage charges and own-account inference permissions.");
            return 2;
        }
        var result = await services.GetRequiredService<AutomaticSetupController>()
            .ConfigureLocalAsync(true, new Progress<string>(Console.WriteLine), timeout.Token);
        Console.WriteLine($"Workspace ready: streaming Speech in {result.SpeechResource.Region}; live model {result.Models.Chat.ModelName}.");
        return 0;
    }
}
