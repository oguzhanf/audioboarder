namespace AudioBoarder.Services.Audio;

/// <summary>
/// Controls which physical streams are captured and where the neural VAD model
/// lives. Defaults to microphone-only so the app transcribes the user rather
/// than whatever audio happens to be playing on the machine.
/// </summary>
public sealed class AudioCaptureOptions
{
    public bool CaptureMicrophone { get; set; } = true;
    public bool CaptureLoopback { get; set; } = false;

    /// <summary>
    /// Path to a Silero <c>silero_vad.onnx</c>. When empty the pipeline looks
    /// for <c>Assets/silero_vad.onnx</c> next to the executable.
    /// </summary>
    public string? SileroModelPath { get; set; }

    /// <summary>Silero speech-probability threshold (0..1). Only used when Silero
    /// is explicitly opted in via AUDIOBOARDER_VAD=silero.</summary>
    public float VadThreshold { get; set; } = 0.45f;

    /// <summary>
    /// Energy-VAD RMS threshold (0..1) used to decide when an utterance STARTS and
    /// ENDS. It no longer decides which audio is kept, so the trade-off has inverted:
    /// over-triggering merely buffers a little extra audio (which helps the model),
    /// while under-triggering loses speech entirely. The old 0.012 default was tuned
    /// for the previous filter-everything behaviour and silently ate quiet
    /// microphones, so this is deliberately conservative.
    /// </summary>
    public float EnergyVadThresholdRms { get; set; } = 0.006f;

    /// <summary>Apply automatic gain control. OFF by default: AGC was found to
    /// over-amplify room noise to clipping, which defeats the energy VAD (every
    /// chunk reads as speech) and prevents pause detection. Only enable for
    /// genuinely faint microphones.</summary>
    public bool AutoGain { get; set; } = false;
}
