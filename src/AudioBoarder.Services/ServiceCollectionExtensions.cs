using System.IO;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Rendering;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Audio;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.Layout;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Rendering;
using AudioBoarder.Services.Transcription;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services;

/// <summary>
/// Production DI registration. Wires the real audio capture, transcription,
/// LLM, image, layout, and rendering stack.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioBoarder(this IServiceCollection services)
    {
        services.AddSingleton<SceneGraph>();
        services.AddSingleton<TranscriptBuffer>(_ => new TranscriptBuffer(TimeSpan.FromMinutes(5)));
        services.AddSingleton<DiagramTheme>(_ => DiagramTheme.Light);
        services.AddSingleton<ILayoutEngine, LayeredGroupLayoutEngine>();
        services.AddSingleton<SceneRenderer>(sp => new SceneRenderer(sp.GetService<DiagramTheme>()));

        // Energy VAD is the reliable DEFAULT speech gate. The Silero ONNX model
        // proved broken on this capture stack (it classified loud, clear speech
        // as 0% speech — maxProb ~0.002 — gating out everything and producing no
        // transcripts). Silero is therefore opt-in via AUDIOBOARDER_VAD=silero.
        services.AddSingleton<IVoiceActivityDetector>(sp =>
        {
            var capture = sp.GetService<IOptions<AudioCaptureOptions>>()?.Value ?? new AudioCaptureOptions();
            var lf = sp.GetService<ILoggerFactory>();
            if (string.Equals(Environment.GetEnvironmentVariable("AUDIOBOARDER_VAD"), "silero", StringComparison.OrdinalIgnoreCase))
            {
                var modelPath = ResolveSileroModelPath(capture.SileroModelPath);
                var silero = SileroVoiceActivityDetector.TryCreate(modelPath, capture.VadThreshold,
                    logger: lf?.CreateLogger<SileroVoiceActivityDetector>());
                if (silero is not null)
                {
                    lf?.CreateLogger("VAD")?.LogInformation("Using Silero neural VAD (opt-in) from {Path}", modelPath);
                    return silero;
                }
                lf?.CreateLogger("VAD")?.LogWarning("Silero opt-in requested but model not found; using energy VAD");
            }
            lf?.CreateLogger("VAD")?.LogInformation("Using energy VAD (RMS threshold {T})", capture.EnergyVadThresholdRms);
            return new EnergyVoiceActivityDetector(capture.EnergyVadThresholdRms);
        });

        services.AddSingleton<AzureOpenAIScenePatchGenerator>();
        services.AddSingleton<AzureOpenAIResponsesGenerator>();
        services.AddSingleton<IScenePatchGenerator, SmartScenePatchGenerator>();

        // Register all transcription implementations. Selection happens lazily at Listen
        // time (via the factory below) so the choice can react to discovery results that
        // populate CloudTranscriptionOptions AFTER the container is built.
        services.AddSingleton<WhisperTranscriptionService>(sp =>
        {
            var opts = sp.GetService<WhisperOptions>()
                       ?? sp.GetService<IOptions<WhisperOptions>>()?.Value
                       ?? new WhisperOptions();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<WhisperTranscriptionService>();
            return new WhisperTranscriptionService(opts, logger);
        });
        services.AddSingleton<OpenAITranscribeService>();
        services.AddSingleton<MaiTranscribeService>();
        services.AddSingleton<AzureSpeechStreamingService>();

        // Always-on lazy selector. AudioPipeline holds a factory and resolves at Listen time.
        // StartupHealthService also resolves via this factory so it can re-evaluate after
        // discovery populates CloudTranscriptionOptions.
        //
        // Priority for "auto": Azure Speech STREAMING first when it is configured.
        // Streaming emits partial hypotheses in ~200-300ms ("words appear as you
        // talk"); gpt-4o-transcribe is a BATCH API that cannot emit partials at all,
        // so it always costs a full buffer window plus a round trip. Accuracy is
        // worth less than latency in a live meeting tool. Explicit Backend="cloud"
        // or "openai" still forces the LLM transcriber.
        services.AddSingleton<Func<ITranscriptionService>>(sp => () =>
        {
            var cloud = sp.GetRequiredService<IOptions<CloudTranscriptionOptions>>().Value;
            var speech = sp.GetRequiredService<IOptions<AzureSpeechSettings>>().Value;
            var backend = (cloud.Backend ?? "auto").Trim().ToLowerInvariant();

            if (backend == "local" || backend == "whisper")
                return sp.GetRequiredService<WhisperTranscriptionService>();

            if (backend == "speech" && speech.IsConfigured)
                return sp.GetRequiredService<AzureSpeechStreamingService>();

            if (backend is "cloud" or "openai" && cloud.IsConfigured)
                return ResolveCloud(sp, cloud);

            // auto: streaming wins when available.
            if (speech.IsConfigured)
                return sp.GetRequiredService<AzureSpeechStreamingService>();

            if (cloud.IsConfigured)
                return ResolveCloud(sp, cloud);

            return sp.GetRequiredService<WhisperTranscriptionService>();
        });

        static ITranscriptionService ResolveCloud(IServiceProvider sp, CloudTranscriptionOptions cloud)
            => cloud.DeploymentName!.StartsWith("MAI-", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<MaiTranscribeService>()
                : sp.GetRequiredService<OpenAITranscribeService>();
        // NOTE: We intentionally do NOT register ITranscriptionService as a DI singleton.
        // Doing so cached the first resolution (Whisper, picked before discovery had a
        // chance to populate CloudTranscriptionOptions) and froze the app on the local
        // backend forever. Consumers must use Func<ITranscriptionService> so the choice
        // tracks live options.

        services.AddSingleton<OpenAIImageGenerator>();
        services.AddSingleton<MaiImageGenerator>();
        services.AddSingleton<IImageGenerator, SmartImageGenerator>();

        services.AddSingleton<AudioDeviceService>();
        services.AddSingleton<AudioPipeline>(sp =>
        {
            var factory = sp.GetRequiredService<Func<ITranscriptionService>>();
            var vad = sp.GetRequiredService<IVoiceActivityDetector>();
            var buffer = sp.GetRequiredService<TranscriptBuffer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var capture = sp.GetService<IOptions<AudioCaptureOptions>>()?.Value ?? new AudioCaptureOptions();
            var devices = sp.GetRequiredService<AudioDeviceService>();

            var sources = new List<IAudioCaptureSource>();
            if (capture.CaptureMicrophone)
                sources.Add(new WasapiAudioCaptureSource(AudioStreamRole.Microphone, devices,
                    loggerFactory?.CreateLogger<WasapiAudioCaptureSource>(), capture.AutoGain));
            if (capture.CaptureLoopback)
                sources.Add(new WasapiAudioCaptureSource(AudioStreamRole.Loopback, devices,
                    loggerFactory?.CreateLogger<WasapiAudioCaptureSource>(), capture.AutoGain));
            if (sources.Count == 0) // never leave the pipeline deaf
                sources.Add(new WasapiAudioCaptureSource(AudioStreamRole.Microphone, devices,
                    loggerFactory?.CreateLogger<WasapiAudioCaptureSource>(), capture.AutoGain));

            return new AudioPipeline(sources, factory, vad, buffer,
                loggerFactory?.CreateLogger<AudioPipeline>());
        });

        services.AddSingleton<DiagramOrchestrator>(sp => new DiagramOrchestrator(
            sp.GetRequiredService<IScenePatchGenerator>(),
            sp.GetRequiredService<ILayoutEngine>(),
            sp.GetRequiredService<TranscriptBuffer>(),
            sp.GetRequiredService<SceneGraph>(),
            sp.GetService<IImageGenerator>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<DiagramOrchestrator>(),
            sp.GetService<SceneBudget>()));

        services.AddSingleton<FoundryDiscovery>();
        return services;
    }

    /// <summary>
    /// Resolves the Silero ONNX model path. Honours an explicit configured path,
    /// then an env override, then the bundled <c>Assets/silero_vad.onnx</c> that
    /// ships next to the executable.
    /// </summary>
    private static string? ResolveSileroModelPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var env = Environment.GetEnvironmentVariable("AUDIOBOARDER_SILERO_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        return File.Exists(bundled) ? bundled : configured;
    }
}
