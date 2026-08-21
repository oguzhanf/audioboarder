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

    /// <summary>Energy-VAD RMS threshold (0..1). The reliable default gate: audio
    /// above this is treated as speech, below as silence. Tuned so normal speech
    /// passes while room silence does not.</summary>
    public float EnergyVadThresholdRms { get; set; } = 0.012f;

    /// <summary>Apply automatic gain control. OFF by default: AGC was found to
    /// over-amplify room noise to clipping, which defeats the energy VAD (every
    /// chunk reads as speech) and prevents pause detection. Only enable for
    /// genuinely faint microphones.</summary>
    public bool AutoGain { get; set; } = false;
}
