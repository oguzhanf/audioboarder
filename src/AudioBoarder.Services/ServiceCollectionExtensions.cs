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
using AudioBoarder.Services.Intent;
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
        services.AddSingleton<DiagramIntentDetector>();
        services.AddSingleton<DiagramIntentCoordinator>();
        services.AddSingleton<TranscriptBuffer>(_ => new TranscriptBuffer(TimeSpan.FromMinutes(5)));
        services.AddSingleton<DiagramTheme>(_ => DiagramTheme.Light);
        services.AddSingleton<ILayoutEngine, IntentLayoutEngine>();
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
                    lf?.CreateLogger("VAD")?.LogInformation("Using Silero neural VAD (opt-in)");
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

        // Build a fresh ordered candidate list for every health/listen attempt so
        // discovery changes and recovered credentials take effect immediately.
        // Selection initializes each candidate before capture starts: cloud first,
        // then configured Azure Speech, then local Whisper. It never switches an
        // active utterance between services.
        services.AddSingleton<ITranscriptionServiceSelector>(sp =>
        {
            IReadOnlyList<TranscriptionCandidate> BuildCandidates()
            {
                var cloud = sp.GetRequiredService<IOptions<CloudTranscriptionOptions>>().Value;
                var speech = sp.GetRequiredService<IOptions<AzureSpeechSettings>>().Value;
                var backend = (cloud.Backend ?? "auto").Trim().ToLowerInvariant();
                var candidates = new List<TranscriptionCandidate>();

                if (backend is "local" or "whisper")
                {
                    candidates.Add(new(
                        TranscriptionBackendKind.LocalWhisper,
                        sp.GetRequiredService<WhisperTranscriptionService>()));
                    return candidates;
                }

                if (backend == "speech")
                {
                    candidates.Add(new(
                        TranscriptionBackendKind.AzureSpeech,
                        sp.GetRequiredService<AzureSpeechStreamingService>()));
                }
                else if (backend is "cloud" or "openai")
                {
                    candidates.Add(new(
                        TranscriptionBackendKind.Cloud,
                        ResolveCloud(sp, cloud)));
                    if (speech.IsConfigured)
                        candidates.Add(new(
                            TranscriptionBackendKind.AzureSpeech,
                            sp.GetRequiredService<AzureSpeechStreamingService>()));
                }
                else
                {
                    if (cloud.IsConfigured)
                        candidates.Add(new(
                            TranscriptionBackendKind.Cloud,
                            ResolveCloud(sp, cloud)));
                    if (speech.IsConfigured)
                        candidates.Add(new(
                            TranscriptionBackendKind.AzureSpeech,
                            sp.GetRequiredService<AzureSpeechStreamingService>()));
                }

                candidates.Add(new(
                    TranscriptionBackendKind.LocalWhisper,
                    sp.GetRequiredService<WhisperTranscriptionService>()));
                return candidates;
            }

            return new TranscriptionServiceSelector(
                BuildCandidates,
                sp.GetService<ILoggerFactory>()?.CreateLogger<TranscriptionServiceSelector>());
        });

        static ITranscriptionService ResolveCloud(IServiceProvider sp, CloudTranscriptionOptions cloud)
            => cloud.IsMaiModel
                ? sp.GetRequiredService<MaiTranscribeService>()
                : sp.GetRequiredService<OpenAITranscribeService>();
        // NOTE: We intentionally do NOT register ITranscriptionService as a DI singleton.
        // The selector must re-evaluate live discovery/auth state for every session.

        services.AddSingleton<OpenAIImageGenerator>();
        services.AddSingleton<MaiImageGenerator>();
        services.AddSingleton<IImageGenerator, SmartImageGenerator>();

        services.AddSingleton<AudioDeviceService>();
        services.AddSingleton<AudioPipeline>(sp =>
        {
            var selector = sp.GetRequiredService<ITranscriptionServiceSelector>();
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

            return new AudioPipeline(sources, selector, vad, buffer,
                loggerFactory?.CreateLogger<AudioPipeline>());
        });

        services.AddSingleton<DiagramOrchestrator>(sp => new DiagramOrchestrator(
            sp.GetRequiredService<IScenePatchGenerator>(),
            sp.GetRequiredService<ILayoutEngine>(),
            sp.GetRequiredService<TranscriptBuffer>(),
            sp.GetRequiredService<SceneGraph>(),
            sp.GetService<IImageGenerator>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<DiagramOrchestrator>(),
            sp.GetService<SceneBudget>(),
            sp.GetRequiredService<DiagramIntentCoordinator>()));

        services.AddSingleton<FoundryDiscovery>();
        services.AddSingleton<IFoundryDiscovery>(sp => sp.GetRequiredService<FoundryDiscovery>());
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
