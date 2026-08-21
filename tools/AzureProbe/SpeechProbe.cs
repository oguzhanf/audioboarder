using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace AzureProbe;

public static class SpeechProbe
{
    public static async Task<int> RunAsync(string region, string resourceId, string? tenantId, string wavPath)
    {
        Console.WriteLine($"[speech] region={region}");
        Console.WriteLine($"[speech] resourceId={resourceId}");
        Console.WriteLine($"[speech] wav={wavPath}");

        var cred = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ExcludeInteractiveBrowserCredential = false,
            ExcludeAzurePowerShellCredential = true,
        });
        Console.WriteLine("[speech] acquiring AAD token…");
        var tk = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
        Console.WriteLine($"[speech] got token, expires {tk.ExpiresOn:HH:mm:ss}");

        var authToken = $"aad#{resourceId}#{tk.Token}";
        var speechConfig = SpeechConfig.FromAuthorizationToken(authToken, region);
        speechConfig.SpeechRecognitionLanguage = "en-US";

        using var audioConfig = AudioConfig.FromWavFileInput(wavPath);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var tcs = new TaskCompletionSource<int>();
        var got = 0;
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                got++;
                Console.WriteLine($"[speech] >> {e.Result.Text}");
            }
        };
        recognizer.SessionStopped += (_, _) => tcs.TrySetResult(got);
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                Console.WriteLine($"[speech] CANCEL code={e.ErrorCode} details={e.ErrorDetails}");
            tcs.TrySetResult(got);
        };

        await recognizer.StartContinuousRecognitionAsync();
        var result = await Task.WhenAny(tcs.Task, Task.Delay(30000));
        await recognizer.StopContinuousRecognitionAsync();
        Console.WriteLine($"[speech] segments={got}");
        return got > 0 ? 0 : 1;
    }
}
