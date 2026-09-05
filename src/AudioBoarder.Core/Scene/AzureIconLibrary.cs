using System.Text.RegularExpressions;

namespace AudioBoarder.Core.Scene;

/// <summary>
/// Optional index over Microsoft's official Azure architecture icons.
/// <para>
/// This optional user-supplied set extends or overrides the curated artwork
/// embedded by <see cref="ComponentIconVisuals"/>. Microsoft's icons are used
/// only in architectural diagrams and their library previews, under the terms at
/// <see href="https://learn.microsoft.com/azure/architecture/icons/"/>.
/// </para>
/// <para>
/// Microsoft's guidance also requires that icons are never cropped, flipped,
/// rotated or recoloured, so official icons are emitted verbatim.
/// </para>
/// </summary>
public sealed class AzureIconLibrary
{
    /// <summary>Files look like <c>10021-icon-service-Function-Apps.svg</c>.</summary>
    private static readonly Regex FileName = new(
        @"^(?:\d+-)?icon-service-(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, string> _byName = new(StringComparer.Ordinal);

    private AzureIconLibrary(Dictionary<string, string> byName, string root)
    {
        _byName = byName;
        Root = root;
    }

    public string Root { get; }
    public int Count => _byName.Count;

    /// <summary>An empty library; every lookup misses and callers fall back.</summary>
    public static AzureIconLibrary Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal), string.Empty);

    /// <summary>
    /// Indexes every <c>.svg</c> under <paramref name="folder"/>. Returns
    /// <see cref="Empty"/> when the folder is missing or unreadable, so a bad path
    /// in configuration degrades to the bundled icons rather than failing startup.
    /// </summary>
    public static AzureIconLibrary Load(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Empty;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.svg", SearchOption.AllDirectories))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var match = FileName.Match(stem);
                var raw = match.Success ? match.Groups["name"].Value : stem;

                // "Function-Apps" and "Azure-Function-Apps" should both be findable.
                var key = Normalize(raw.Replace('-', ' ').Replace('_', ' '));
                if (key.Length == 0) continue;
                map.TryAdd(key, path);

                var stripped = StripAzurePrefix(key);
                if (stripped != key) map.TryAdd(stripped, path);
            }
        }
        catch (IOException)
        {
            return Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Empty;
        }

        return map.Count == 0 ? Empty : new AzureIconLibrary(map, folder);
    }

    /// <summary>
    /// Finds the official icon for a node label, or null when the set has nothing
    /// for it. Matching is exact on the normalised name, then longest-prefix, so
    /// "Azure SQL Database (primary)" still resolves to the SQL Database icon.
    /// </summary>
    public string? FindPath(string? label)
    {
        if (_byName.Count == 0 || string.IsNullOrWhiteSpace(label)) return null;

        var key = Normalize(label);
        if (key.Length == 0) return null;
        if (_byName.TryGetValue(key, out var exact)) return exact;

        var stripped = StripAzurePrefix(key);
        if (stripped != key && _byName.TryGetValue(stripped, out var viaStrip)) return viaStrip;

        // Longest service name contained in the label wins, so a more specific icon
        // is preferred over a generic one ("sql managed instance" over "sql").
        string? best = null;
        var bestLength = 0;
        foreach (var (name, path) in _byName)
        {
            if (name.Length <= bestLength || name.Length < 4) continue;
            if (ContainsWord(key, name))
            {
                best = path;
                bestLength = name.Length;
            }
        }
        return best;
    }

    /// <summary>Reads the icon's SVG markup, or null when it cannot be read.</summary>
    public string? ReadSvg(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool ContainsWord(string haystack, string needle)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return false;
        var beforeOk = i == 0 || haystack[i - 1] == ' ';
        var end = i + needle.Length;
        var afterOk = end >= haystack.Length || haystack[end] == ' ';
        return beforeOk && afterOk;
    }

    private static string StripAzurePrefix(string key) =>
        key.StartsWith("azure ", StringComparison.Ordinal) ? key[6..] : key;

    /// <summary>Lower-cased, punctuation removed, whitespace collapsed.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) || c is '-' or '_') sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
