function ConvertTo-AudioBoarderMsiVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    $normalized = $Version.Trim()
    if ($normalized.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $match = [Regex]::Match(
        $normalized,
        '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<label>alpha|beta|preview|rc)\.(?<sequence>\d+))?$',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (!$match.Success) {
        throw "Version must be <major>.<minor>.<patch> or use a supported prerelease suffix: alpha.N, beta.N, preview.N, or rc.N."
    }

    $major = [int]$match.Groups["major"].Value
    $minor = [int]$match.Groups["minor"].Value
    $patch = [int]$match.Groups["patch"].Value
    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 63) {
        throw "MSI version limits are major <= 255, minor <= 255, and SemVer patch <= 63."
    }

    if (!$match.Groups["label"].Success) {
        $ordinal = 1023
    }
    else {
        $sequence = [int]$match.Groups["sequence"].Value
        if ($sequence -lt 1 -or $sequence -gt 199) {
            throw "Prerelease sequence must be between 1 and 199."
        }

        $offset = switch ($match.Groups["label"].Value.ToLowerInvariant()) {
            "alpha" { 0 }
            "beta" { 200 }
            "preview" { 400 }
            "rc" { 600 }
            default { throw "Unsupported prerelease label." }
        }
        $ordinal = $offset + $sequence
    }

    "$major.$minor.$(($patch * 1024) + $ordinal)"
}
