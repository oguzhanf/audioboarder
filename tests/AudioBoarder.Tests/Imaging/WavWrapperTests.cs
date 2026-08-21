using AudioBoarder.Core.Audio;
using AudioBoarder.Services.Transcription.Cloud;

namespace AudioBoarder.Tests.Imaging;

public class WavWrapperTests
{
    [Fact]
    public void WrapWav_ProducesValidRiffHeader()
    {
        var pcm = new byte[320]; // 10ms at 16kHz mono PCM-16
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var s = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        var wav = OpenAITranscribeService.WrapWav(pcm, AudioFormat.Mono16kPcm16);

        wav.Length.Should().Be(44 + pcm.Length);
        // RIFF/WAVE/fmt /data markers
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        System.Text.Encoding.ASCII.GetString(wav, 12, 4).Should().Be("fmt ");
        System.Text.Encoding.ASCII.GetString(wav, 36, 4).Should().Be("data");
        // PCM format = 1
        BitConverter.ToInt16(wav, 20).Should().Be(1);
        // 16 kHz
        BitConverter.ToInt32(wav, 24).Should().Be(16_000);
        // Mono
        BitConverter.ToInt16(wav, 22).Should().Be(1);
        // 16-bit
        BitConverter.ToInt16(wav, 34).Should().Be(16);
        // payload length matches
        BitConverter.ToInt32(wav, 40).Should().Be(pcm.Length);
    }

    [Theory]
    [InlineData("Thanks for watching", true)]
    [InlineData("thanks for watching everyone!", true)]
    [InlineData("you", true)]
    [InlineData("Okay.", true)]
    [InlineData("um", true)]
    // Legitimate captions that merely contain a stop-word must NOT be dropped:
    [InlineData("are you", false)]
    [InlineData("see you tomorrow", false)]
    [InlineData("thank you for the detailed walkthrough", false)]
    [InlineData("the API calls the database", false)]
    [InlineData("Microsoft Azure cloud architecture diagram", false)]
    public void IsLikelyHallucination_Classifies(string text, bool expected)
    {
        OpenAITranscribeService.IsLikelyHallucination(text).Should().Be(expected);
    }
}
