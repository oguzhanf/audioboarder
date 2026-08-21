using AudioBoarder.Core.Audio;

namespace AudioBoarder.Core.Audio;

/// <summary>
/// Simple energy-based VAD that needs zero external dependencies. Reliable
/// fallback used when a Silero ONNX model isn't configured.
/// Compares the RMS amplitude of a chunk against a noise floor.
/// </summary>
public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly double _thresholdRms;

    public EnergyVoiceActivityDetector(double thresholdRms = 0.015)
    {
        if (thresholdRms <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdRms));
        _thresholdRms = thresholdRms;
    }

    public bool IsSpeech(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Format.BitsPerSample != 16)
            throw new NotSupportedException("EnergyVoiceActivityDetector only supports 16-bit PCM");

        var span = chunk.Samples.Span;
        if (span.Length < 2) return false;

        long sumSquares = 0;
        var count = 0;
        for (var i = 0; i + 1 < span.Length; i += 2)
        {
            var sample = (short)(span[i] | (span[i + 1] << 8));
            sumSquares += sample * sample;
            count++;
        }
        if (count == 0) return false;

        var rms = Math.Sqrt(sumSquares / (double)count) / short.MaxValue;
        return rms >= _thresholdRms;
    }
}
