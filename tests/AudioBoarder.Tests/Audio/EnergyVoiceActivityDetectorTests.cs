using AudioBoarder.Core.Audio;

namespace AudioBoarder.Tests.Audio;

public class EnergyVoiceActivityDetectorTests
{
    [Fact]
    public void SilentChunk_NotSpeech()
    {
        var det = new EnergyVoiceActivityDetector();
        var chunk = new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = new byte[1600],
        };
        det.IsSpeech(chunk).Should().BeFalse();
    }

    [Fact]
    public void LoudChunk_IsSpeech()
    {
        var det = new EnergyVoiceActivityDetector(thresholdRms: 0.05);
        var samples = Synthesise(amplitude: 25_000, samples: 800);
        var chunk = new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = samples,
        };
        det.IsSpeech(chunk).Should().BeTrue();
    }

    private static byte[] Synthesise(short amplitude, int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(amplitude * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }
}
