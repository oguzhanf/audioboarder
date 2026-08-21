using System.Text;
using System.Text.RegularExpressions;

namespace AudioBoarder.Core.Transcript;

/// <summary>
/// Cleans hallucinated artefacts out of ASR output before it reaches the caption
/// pane and the diagram model.
///
/// <para>
/// Multilingual speech models fill ambiguous or near-silent audio with plausible
/// tokens from whatever script they drift into. Observed live from
/// <c>gpt-transcribe</c> with <c>language=en</c>: every sentence ended with
/// "囧。" — a CJK ideograph plus a fullwidth stop — appended to otherwise correct
/// English. Left in, these leak into node labels and the LLM prompt.
/// </para>
///
/// <para>
/// Stripping is only applied when the expected language uses Latin script, so a
/// genuinely Japanese or Chinese meeting is never mangled.
/// </para>
/// </summary>
public static class TranscriptTextCleaner
{
    // CJK ideographs + Hiragana + Katakana + Hangul + fullwidth/CJK punctuation.
    private static readonly Regex NonLatinScript = new(
        @"[\u3000-\u303F\u3040-\u309F\u30A0-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF\uFF00-\uFFEF]",
        RegexOptions.Compiled);

    private static readonly Regex BracketedAnnotation = new(
        @"\[[^\]]*\]|\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex CollapseSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Language codes whose transcripts should contain no CJK/Hangul script.</summary>
    private static readonly HashSet<string> LatinScriptLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "en-us", "en-gb", "de", "fr", "es", "it", "pt", "nl", "sv", "no", "da",
        "fi", "pl", "cs", "tr", "ro", "hu", "id", "ms", "vi", "af", "ca", "et", "hr",
        "lt", "lv", "sk", "sl", "sq", "sw", "tl", "cy",
    };

    public static bool ExpectsLatinScript(string? language)
        => !string.IsNullOrWhiteSpace(language) && LatinScriptLanguages.Contains(language.Trim());

    /// <summary>
    /// Removes bracketed noise annotations, and — when <paramref name="language"/>
    /// is Latin-script — hallucinated CJK/Hangul characters and fullwidth
    /// punctuation. Returns an empty string when nothing meaningful remains, so the
    /// caller can drop the segment entirely.
    /// </summary>
    public static string Clean(string? text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var s = BracketedAnnotation.Replace(text, " ");

        if (ExpectsLatinScript(language))
        {
            var stripped = NonLatinScript.Replace(s, " ");
            // If removing foreign script erased nearly everything, the segment was
            // hallucinated noise rather than speech with a stray character.
            var before = s.Count(char.IsLetterOrDigit);
            var after = stripped.Count(char.IsLetterOrDigit);
            if (before > 0 && after == 0) return string.Empty;
            s = stripped;
        }

        s = CollapseSpace.Replace(s, " ").Trim();

        // Tidy the space a removed character can leave in front of punctuation.
        s = Regex.Replace(s, @"\s+([,.;:!?])", "$1");
        s = s.Trim(' ', '\t', '\u00A0');

        // Nothing but punctuation left is noise, not speech.
        if (!s.Any(char.IsLetterOrDigit)) return string.Empty;
        return s;
    }
}
