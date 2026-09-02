using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioBoarder.App.Updates;

/// <summary>
/// Minimal SemVer 2.0 value used by the updater. Unlike <see cref="Version"/>,
/// this preserves prerelease precedence, so preview.2 is newer than preview.1
/// and the stable release is newer than every preview of the same base version.
/// Build metadata is accepted but ignored for precedence.
/// </summary>
public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease = null) : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^(?:v)?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var match = Pattern.Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var patch))
            return false;

        var prerelease = match.Groups["pre"].Success
            ? match.Groups["pre"].Value
            : null;
        if (prerelease is not null && prerelease.Split('.').Any(IsInvalidNumericIdentifier))
            return false;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid semantic version.");

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;

            var leftNumeric = int.TryParse(left[i], NumberStyles.None,
                CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[i], NumberStyles.None,
                CultureInfo.InvariantCulture, out var rightNumber);

            int part;
            if (leftNumeric && rightNumeric)
                part = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric)
                part = -1;
            else if (rightNumeric)
                part = 1;
            else
                part = string.Compare(left[i], right[i], StringComparison.Ordinal);
            if (part != 0) return part;
        }
        return 0;
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public Version ToSystemVersion() => new(Major, Minor, Patch);

    public static SemanticVersion FromVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    private static bool IsInvalidNumericIdentifier(string identifier) =>
        identifier.Length > 1 &&
        identifier[0] == '0' &&
        identifier.All(char.IsDigit);
}
