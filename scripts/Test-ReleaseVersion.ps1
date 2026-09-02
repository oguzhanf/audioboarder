[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")

function Assert-Equal([string]$Expected, [string]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-VersionLess([string]$Lower, [string]$Higher, [string]$Message) {
    if ([Version]$Lower -ge [Version]$Higher) {
        throw "$Message Expected '$Lower' to be lower than '$Higher'."
    }
}

Assert-Equal "0.8.401" (ConvertTo-AudioBoarderMsiVersion "0.8.0-preview.1") "Preview mapping failed."
Assert-Equal "0.8.402" (ConvertTo-AudioBoarderMsiVersion "0.8.0-preview.2") "Preview sequence mapping failed."
Assert-Equal "0.8.1023" (ConvertTo-AudioBoarderMsiVersion "0.8.0") "Stable mapping failed."
Assert-Equal "255.255.65535" (ConvertTo-AudioBoarderMsiVersion "255.255.63") "Upper MSI limit mapping failed."

$ordered = @(
    "0.8.0-alpha.1",
    "0.8.0-alpha.199",
    "0.8.0-beta.1",
    "0.8.0-preview.1",
    "0.8.0-preview.2",
    "0.8.0-rc.1",
    "0.8.0",
    "0.8.1-alpha.1"
) | ForEach-Object { ConvertTo-AudioBoarderMsiVersion $_ }
for ($index = 1; $index -lt $ordered.Count; $index++) {
    Assert-VersionLess $ordered[$index - 1] $ordered[$index] "MSI ordering failed."
}

foreach ($invalid in @(
    "256.0.0",
    "0.256.0",
    "0.0.64",
    "0.8.0-preview.0",
    "0.8.0-preview.200",
    "0.8.0-nightly.1",
    "0.8.0-preview"
)) {
    try {
        ConvertTo-AudioBoarderMsiVersion $invalid | Out-Null
        throw "Invalid version '$invalid' was accepted."
    }
    catch {
        if ($_.Exception.Message -eq "Invalid version '$invalid' was accepted.") {
            throw
        }
    }
}

Write-Host "MSI version mapping contract passed." -ForegroundColor Green
