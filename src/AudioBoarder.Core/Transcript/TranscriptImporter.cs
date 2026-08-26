using System.Text;
using System.Text.RegularExpressions;

namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Parses an exported meeting transcript into <see cref="TranscriptSegment"/>s so a
/// diagram can be produced without live audio.
/// <para>
/// Teams (and Zoom, Meet, and most recorders) export WebVTT, so that is the primary
/// format. SRT and plain text are handled too, because a pasted transcript is the
/// most common thing a user actually has to hand.
/// </para>
/// </summary>
public static class TranscriptImporter
{
    /// <summary>File-dialog filter covering everything <see cref="Parse"/> understands.</summary>
    public const string FileFilter =
        "Meeting transcripts (*.vtt;*.srt;*.txt;*.md)|*.vtt;*.srt;*.txt;*.md|" +
        "WebVTT from Teams (*.vtt)|*.vtt|SubRip (*.srt)|*.srt|Text (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*";

    // 00:00:12.340 --> 00:00:15.020   (WebVTT uses '.', SRT uses ',')
    private static readonly Regex CueTiming = new(
        @"^(?<start>\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3})",
        RegexOptions.Compiled);

    // Teams embeds the speaker as <v Priya Rao> (sometimes <v.local Priya Rao>).
    private static readonly Regex VoiceTag = new(
        @"^<v[^\s>]*\s+(?<name>[^>]+)>\s*(?<text>.*?)\s*(?:</v>)?$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SpeakerPrefix = new(
        @"^(?<name>[\p{L}][\p{L}\p{M}.'\- ]{1,48}?)\s*:\s+(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex InlineTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses transcript text. Cue timings are preserved when present; otherwise
    /// segments are spread over a synthetic timeline so downstream windowing (which
    /// is time-based) still behaves.
    /// </summary>
    /// <param name="content">Raw file contents.</param>
    /// <param name="localSpeaker">
    /// Name treated as the local participant, so the diagram can distinguish "you"
    /// from everyone else. Null treats every speaker as remote.
    /// </param>
    public static IReadOnlyList<TranscriptSegment> Parse(string? content, string? localSpeaker = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<TranscriptSegment>();

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = ParseCues(lines);
        if (cues.Count == 0) cues = ParsePlainText(lines);
        if (cues.Count == 0) return Array.Empty<TranscriptSegment>();

        // Anchor to a timeline ending "now" so the newest speech is the most recent,
        // matching how a live session fills the rolling buffer.
        var span = cues[^1].End;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMinutes(Math.Max(1, cues.Count * 0.1));
        var origin = DateTimeOffset.UtcNow - span;

        var segments = new List<TranscriptSegment>(cues.Count);
        foreach (var cue in cues)
        {
            var text = cue.Text.Trim();
            if (text.Length == 0) continue;
            var isLocal = localSpeaker is not null && cue.Speaker is not null &&
                          cue.Speaker.Contains(localSpeaker, StringComparison.OrdinalIgnoreCase);

            // Keep the speaker's name in the text: the model uses it to attribute
            // owners to action items, which is most of the value of a transcript.
            var body = cue.Speaker is null ? text : $"{cue.Speaker}: {text}";

            segments.Add(new TranscriptSegment(
                Guid.NewGuid(),
                isLocal ? TranscriptSpeaker.Local : TranscriptSpeaker.Remote,
                body,
                origin + cue.Start,
                origin + cue.End));
        }
        return segments;
    }

    private readonly record struct Cue(TimeSpan Start, TimeSpan End, string? Speaker, string Text);

    private static List<Cue> ParseCues(string[] lines)
    {
        var cues = new List<Cue>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = CueTiming.Match(lines[i].Trim());
            if (!m.Success) continue;

            if (!TryParseTimestamp(m.Groups["start"].Value, out var start) ||
                !TryParseTimestamp(m.Groups["end"].Value, out var end))
                continue;

            // Cue payload runs until the next blank line or the next cue timing.
            var body = new StringBuilder();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line)) { i = j; break; }
                if (CueTiming.IsMatch(line.Trim())) { i = j - 1; break; }
                if (body.Length > 0) body.Append(' ');
                body.Append(line.Trim());
                i = j;
            }

            var (speaker, text) = SplitSpeaker(body.ToString());
            if (text.Length > 0) cues.Add(new Cue(start, end, speaker, text));
        }
        return cues;
    }

    /// <summary>Plain text: one utterance per non-empty line, synthetic timings.</summary>
    private static List<Cue> ParsePlainText(string[] lines)
    {
        var cues = new List<Cue>();
        var cursor = TimeSpan.Zero;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Skip WebVTT/SRT scaffolding and markdown headings.
            if (line is "WEBVTT" || line.StartsWith("NOTE ", StringComparison.Ordinal)) continue;
            if (int.TryParse(line, out _)) continue;
            if (line.StartsWith('#')) continue;

            var (speaker, text) = SplitSpeaker(line);
            if (text.Length == 0) continue;

            // Roughly 3 words per second of speech — only the ordering matters.
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var duration = TimeSpan.FromSeconds(Math.Clamp(words / 3.0, 1.5, 30));
            cues.Add(new Cue(cursor, cursor + duration, speaker, text));
            cursor += duration;
        }
        return cues;
    }

    private static (string? Speaker, string Text) SplitSpeaker(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return (null, string.Empty);

        var voice = VoiceTag.Match(trimmed);
        if (voice.Success)
        {
            var name = Clean(voice.Groups["name"].Value);
            return (name.Length == 0 ? null : name,
                    Clean(InlineTag.Replace(voice.Groups["text"].Value, string.Empty)));
        }

        trimmed = InlineTag.Replace(trimmed, string.Empty).Trim();
        var prefix = SpeakerPrefix.Match(trimmed);
        if (prefix.Success)
        {
            var name = prefix.Groups["name"].Value.Trim();
            // A URL or a sentence fragment with digits is not a speaker name.
            if (!name.Contains("://", StringComparison.Ordinal) && !name.Any(char.IsDigit))
                return (Clean(name), Clean(prefix.Groups["text"].Value));
        }
        return (null, Clean(trimmed));
    }

    private static string Clean(string s) => Whitespace.Replace(s, " ").Trim();

    /// <summary>Accepts mm:ss.fff and hh:mm:ss.fff, with ',' or '.' before millis.</summary>
    private static bool TryParseTimestamp(string value, out TimeSpan result)
    {
        result = default;
        var parts = value.Replace(',', '.').Trim().Split(':');
        try
        {
            var secondsPart = parts[^1].Split('.');
            var seconds = int.Parse(secondsPart[0]);
            var millis = secondsPart.Length > 1
                ? int.Parse(secondsPart[1].PadRight(3, '0')[..3])
                : 0;
            var minutes = parts.Length >= 2 ? int.Parse(parts[^2]) : 0;
            var hours = parts.Length >= 3 ? int.Parse(parts[^3]) : 0;
            result = new TimeSpan(0, hours, minutes, seconds, millis);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
