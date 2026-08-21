namespace AudioBoarder.Core.Audio;

/// <summary>
/// Binary speech/non-speech classifier. Implementations include a simple
/// RMS-energy detector (default) and Silero ONNX (opt-in).
/// </summary>
public interface IVoiceActivityDetector
{
    /// <summary>Returns true if the chunk likely contains speech.</summary>
    bool IsSpeech(AudioChunk chunk);
}
