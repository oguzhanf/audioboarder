using System.IO;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Health;
using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.HealthCheck;

/// <summary>
/// Headless CLI command: <c>AudioBoarder.exe healthcheck [--llm] [--image]</c>.
/// Runs the three startup probes against the real services and exits with a
/// status code per failed component. Optional <c>--llm</c> adds an opt-in live
/// LLM probe; <c>--image</c> adds an opt-in live image-generation probe.
/// </summary>
public static class HealthCheckCommand
{
    public const int ExitOk = 0;
    public const int ExitAudio = 10;
    public const int ExitTranscription = 11;
    public const int ExitAzure = 12;
    public const int ExitLlmCall = 13;
    public const int ExitImageCall = 14;
    public const int ExitUnexpected = 99;

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter? output = null)
    {
        output ??= Console.Out;
        var withLlm = args.Any(a => string.Equals(a, "--llm", StringComparison.OrdinalIgnoreCase));
        var withImage = args.Any(a => string.Equals(a, "--image", StringComparison.OrdinalIgnoreCase));

        var health = services.GetRequiredService<StartupHealthService>();

        output.WriteLine("== AudioBoarder health check ==");
        await health.RunAllAsync().ConfigureAwait(false);

        var audio = health.GetState(StartupHealthService.AudioKey);
        var trans = health.GetState(StartupHealthService.TranscriptionKey);
        var llm = health.GetState(StartupHealthService.LlmKey);

        Print(output, audio);
        Print(output, trans);
        Print(output, llm);

        int code = ExitOk;
        if (audio.Status == ComponentStatus.Failed) code = Math.Max(code, ExitAudio);
        if (trans.Status == ComponentStatus.Failed) code = Math.Max(code, ExitTranscription);
        if (llm.Status == ComponentStatus.Failed) code = Math.Max(code, ExitAzure);

        if (withLlm && llm.Status == ComponentStatus.Ready)
        {
            try
            {
                output.WriteLine("");
                output.WriteLine("== Opt-in live LLM probe ==");
                var generator = services.GetRequiredService<IScenePatchGenerator>();
                var transcript = new[]
                {
                    new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, "We need to validate the meeting summarization end-to-end.", DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(-9)),
                    new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Remote, "Right. Confirm the deployment responds with a valid ScenePatch.", DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-4)),
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await generator.GenerateAsync(new ScenePatchRequest(new SceneGraph(), transcript), CancellationToken.None);
                sw.Stop();
                output.WriteLine($"  OK via {resp.ModelName} in {sw.ElapsedMilliseconds} ms; {resp.Patch.Operations.Count} ops");
            }
            catch (Exception ex)
            {
                output.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
                code = Math.Max(code, ExitLlmCall);
            }
        }

        if (withImage)
        {
            try
            {
                output.WriteLine("");
                output.WriteLine("== Opt-in live image probe ==");
                var settings = services.GetRequiredService<IOptions<AudioBoarderSettings>>().Value;
                if (!settings.ImageGeneration.Enabled)
                {
                    output.WriteLine("  SKIP: ImageGeneration.Enabled = false");
                }
                else
                {
                    var generator = services.GetRequiredService<IImageGenerator>();
                    if (!generator.IsConfigured)
                    {
                        output.WriteLine("  SKIP: no image deployment discovered (need MAI-Image-* or gpt-image-* in the Foundry resource)");
                    }
                    else
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var resp = await generator.GenerateAsync(new ImageGenerationRequest(
                            "A simple flowchart of three boxes labelled User, API, Database connected by arrows, on white background",
                            Width: 1024, Height: 1024), CancellationToken.None);
                        sw.Stop();
                        var outPath = Path.Combine(Path.GetTempPath(), $"audioboarder-healthcheck-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.png");
                        await File.WriteAllBytesAsync(outPath, resp.PngBytes);
                        output.WriteLine($"  OK via {resp.ModelName} in {sw.ElapsedMilliseconds} ms; {resp.PngBytes.Length} bytes -> {outPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
                code = Math.Max(code, ExitImageCall);
            }
        }

        output.WriteLine("");
        output.WriteLine($"Exit code: {code}");
        return code;
    }

    private static void Print(TextWriter w, HealthState s)
    {
        var icon = s.Status switch
        {
            ComponentStatus.Ready => "OK",
            ComponentStatus.Degraded => "WARN",
            ComponentStatus.Failed => "FAIL",
            ComponentStatus.Checking => "...",
            _ => "?",
        };
        w.WriteLine($"  [{icon}] {s.Title}: {s.Detail}");
    }
}
