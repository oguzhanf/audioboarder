using AudioBoarder.Core.Audio;

namespace AudioBoarder.Core.Audio;

/// <summary>
/// VAD that accepts every chunk. Use this when the downstream transcription
/// service handles silence internally (Whisper, gpt-4o-transcribe, MAI-Transcribe-1
/// all do). Keeps the pipeline simple and avoids dropping borderline-soft speech.
/// </summary>
public sealed class PassThroughVoiceActivityDetector : IVoiceActivityDetector
{
    public bool IsSpeech(AudioChunk chunk) => true;
}
