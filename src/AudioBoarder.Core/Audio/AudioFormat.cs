namespace AudioBoarder.Core.Audio;

public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BytesPerSample => BitsPerSample / 8;
    public int BytesPerSecond => SampleRate * Channels * BytesPerSample;
    public static AudioFormat Mono16kPcm16 { get; } = new(16_000, 1, 16);
}
