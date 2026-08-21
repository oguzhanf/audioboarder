namespace AudioBoarder.Core.Transcript;

public enum TranscriptSpeaker
{
    Local,
    Remote,
}

public sealed record TranscriptSegment(
    Guid Id,
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Start,
    DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}
