using AudioBoarder.Services.LLM;
using AzureProbe;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "llm")
{
    var ep = args[1];
    var dep = args[2];
    var tid = args.Length > 3 ? args[3] : null;
    // Optional 5th arg: a file with one transcript line per row, so the probe can be
    // run against a realistic meeting instead of the built-in toy example.
    var transcript = args.Length > 4 && File.Exists(args[4])
        ? await File.ReadAllTextAsync(args[4])
        : "Walk me through the order pipeline.\nThe client app talks to an API which writes to the database.\nWhat about retries?\nThere's a queue between them.";
    // 6th arg: "continuous" exercises the fast mid-meeting path (low reasoning
    // effort, incremental prompt) rather than the deep pass.
    var continuous = args.Length > 5 && string.Equals(args[5], "continuous", StringComparison.OrdinalIgnoreCase);
    return await LlmProbe.RunAsync(ep, dep, tid, transcript, continuous);
}

if (args.Length > 0 && args[0] == "asr")
{
    // asr <endpoint> <deployment> <wavPath> [vadThreshold] [chunkMs]
    var aEp = args[1];
    var aDep = args[2];
    var aWav = args[3];
    var aThr = args.Length > 4 && double.TryParse(args[4], out var t) ? t : 0.012;
    var aChunk = args.Length > 5 && int.TryParse(args[5], out var c) ? c : 30;
    return await AsrProbe.RunAsync(aEp, aDep, aWav, aThr, aChunk, null);
}

if (args.Length > 0 && args[0] == "speech")
{
    // speech <region> <resourceId> <tenant> <wavPath>
    var region = args[1];
    var resourceId = args[2];
    var tid = args[3];
    var wav = args[4];
    return await SpeechProbe.RunAsync(region, resourceId, tid, wav);
}

var tenantId = args.Length > 0 ? args[0] : null;
var subId    = args.Length > 1 ? args[1] : null;

if (string.IsNullOrWhiteSpace(subId))
{
    Console.WriteLine("Usage: AzureProbe <tenantId> <subscriptionId>");
    Console.WriteLine("       AzureProbe llm <endpoint> <deployment> [tenantId] [transcriptFile]");
    Console.WriteLine("       AzureProbe speech <region> <resourceId> <tenantId> <wavPath>");
    return 2;
}

Console.WriteLine($"[probe] Tenant={tenantId} Sub={subId}");
using var lf = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
var disc = new FoundryDiscovery(lf.CreateLogger<FoundryDiscovery>());

var sw = System.Diagnostics.Stopwatch.StartNew();
var result = await disc.DiscoverAsync(tenantId, subId);
sw.Stop();
Console.WriteLine($"[probe] Elapsed={sw.ElapsedMilliseconds}ms Success={result.Success}");
Console.WriteLine($"[probe] Endpoint={result.Endpoint}");
Console.WriteLine($"[probe] Deployment={result.DeploymentName}");
Console.WriteLine($"[probe] Fallback={result.FallbackDeploymentName}");
Console.WriteLine($"[probe] Message={result.Message}");
return result.Success ? 0 : 1;
