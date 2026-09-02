[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = "Stop"
$localRoot = Join-Path $env:LOCALAPPDATA "AudioBoarder"
$identityRoot = Join-Path $env:LOCALAPPDATA ".IdentityService"
$targets = [System.Collections.Generic.List[string]]::new()

foreach ($path in @(
    (Join-Path $localRoot "sessions"),
    (Join-Path $localRoot "logs"),
    (Join-Path $localRoot "updates"),
    (Join-Path $localRoot "auth-record.json"),
    (Join-Path $localRoot "ui-state.json")
)) {
    if (Test-Path -LiteralPath $path) { $targets.Add($path) }
}
if (Test-Path -LiteralPath $identityRoot) {
    Get-ChildItem -LiteralPath $identityRoot -Force -ErrorAction SilentlyContinue |
        Where-Object Name -Like "AudioBoarder*" |
        ForEach-Object { $targets.Add($_.FullName) }
}

if ($targets.Count -eq 0) {
    Write-Host "No AudioBoarder local data was found."
    exit 0
}

Write-Host "The following AudioBoarder sessions, logs, updates, UI state, and app-specific auth cache will be deleted:"
$targets | ForEach-Object { Write-Host "  $_" }
if (!$Force) {
    $answer = Read-Host "Type DELETE to continue"
    if ($answer -cne "DELETE") {
        Write-Host "Cancelled; no files were changed."
        exit 1
    }
}

foreach ($path in $targets) {
    Remove-Item -LiteralPath $path -Recurse -Force
}
Write-Host "AudioBoarder local data deleted." -ForegroundColor Green
