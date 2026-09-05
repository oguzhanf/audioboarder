[CmdletBinding()]
param(
    [switch]$IncludeHistory,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Test-IsCredentialPlaceholder([string]$Value, [string]$FilePath = "") {
    $candidate = $Value.Trim().Trim('"', "'")
    # These exact values are historical HTTP/settings test fixtures, not credentials.
    # Keep the exception file-scoped so production assignments remain findings.
    $fixturePath = $FilePath.Replace('\', '/')
    if ($fixturePath -eq "tests/AudioBoarder.Tests/App/SettingsServiceTests.cs" -and
        @("existing-openai", "existing-speech", "old-openai", "old-speech", "portable-secret") -ccontains $candidate) {
        return $true
    }
    if ($fixturePath -eq "tests/AudioBoarder.Tests/Transcription/CloudTranscriptionReliabilityTests.cs" -and
        @("expired-key", "rejected-key", "stale-key", "current-key") -ccontains $candidate) {
        return $true
    }
    if ([string]::IsNullOrWhiteSpace($candidate) -or
        $candidate -match '^(?i:null|none|nil)$') {
        return $true
    }
    if ($candidate.StartsWith('${{', [StringComparison]::Ordinal) -or
        $candidate -match '^(\$\{.*\}|\$env:.*|<.*>|__.*__)$') {
        return $true
    }
    if ($candidate -match '^(?:Environment\.GetEnvironmentVariable\(|[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*;?$)') {
        return $true
    }
    if ($candidate -match '^(?i:test-key|api-key|token|secret|not-a-secret|example|sample|placeholder|changeme|replace[-_]?me|your[-_].*|fake[-_].*|dummy[-_].*|sentinel[-_].*|0+)$') {
        return $true
    }
    $false
}

function Find-CredentialAssignments([string]$Text, [string]$Label, [string]$FilePath = $Label) {
    $findings = [System.Collections.Generic.List[string]]::new()
    $quotedCredentialName =
        '(?:[A-Za-z0-9_-]*(?:Api[_-]?Key|(?:Api|Access|Auth|Bearer|Refresh|Client)[_-]?Token|Client[_-]?Secret))'
    $unquotedCredentialName =
        '(?:[A-Za-z0-9_-]*(?:Api[_-]?Key|Access[_-]?Token|Auth[_-]?Token|Bearer[_-]?Token|Client[_-]?Secret)|[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*_Token)'
    $quotedAssignment =
        "(?im)[`"']?$quotedCredentialName[`"']?\s*[:=]\s*[`"'](?<value>[^`"'\r\n]*)[`"']"
    $unquotedAssignment =
        "(?im)^\s*[+-]?\s*(?:export\s+|\`$env:)?$unquotedCredentialName\s*[:=]\s*(?![`"'])(?<value>[^\s#]+)"

    foreach ($pattern in @($quotedAssignment, $unquotedAssignment)) {
        foreach ($match in [Regex]::Matches($Text, $pattern)) {
            $value = $match.Groups["value"].Value
            if (!(Test-IsCredentialPlaceholder $value $FilePath)) {
                $findings.Add("${Label}: non-placeholder API key/token/client secret")
            }
        }
    }
    $findings
}

function Invoke-ScannerSelfTest {
    $fixtures = @(
        @{ Text = '{ "ApiKey": "sk-live-1234567890abcdef" }'; Detect = $true },
        @{ Text = 'ClientSecret = "actual-client-secret-123456"'; Detect = $true },
        @{ Text = 'AccessToken=ghp_1234567890abcdefghijklmnopqrstuvwxyz'; Detect = $true },
        @{ Text = 'OPENAI_API_KEY=live-key-1234567890'; Detect = $true },
        @{ Text = 'GITHUB_TOKEN=live-token-1234567890'; Detect = $true },
        @{ Text = '{ "ApiKey": "" }'; Detect = $false },
        @{ Text = 'ApiKey = "test-key"'; Detect = $false },
        @{ Text = 'AUDIOBOARDER_LIVE_API_KEY: ${{ secrets.AUDIOBOARDER_LIVE_API_KEY }}'; Detect = $false },
        @{ Text = 'client_secret = <set-in-secret-store>'; Detect = $false },
        @{ Text = 'ApiKey = "expired-key"'; Path = 'tests/AudioBoarder.Tests/Transcription/CloudTranscriptionReliabilityTests.cs'; Detect = $false },
        @{ Text = 'ApiKey = "expired-key"'; Path = 'src/Production.cs'; Detect = $true },
        @{ Text = 'ApiKey = "actual-secret-1234"'; Path = 'tests/AudioBoarder.Tests/Transcription/CloudTranscriptionReliabilityTests.cs'; Detect = $true }
    )
    foreach ($fixture in $fixtures) {
        $actual = @(Find-CredentialAssignments $fixture.Text "fixture" ([string]$fixture.Path)).Count
        if (($actual -gt 0) -ne $fixture.Detect) {
            throw "Secret scanner self-test failed for fixture: $($fixture.Text)"
        }
    }
    Write-Host "Repository secret scanner self-test passed." -ForegroundColor Green
}

if ($SelfTest) {
    Invoke-ScannerSelfTest
    return
}

Push-Location $root
try {
    $tracked = @(git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed." }

    $findings = [System.Collections.Generic.List[string]]::new()
    $sensitiveJsonSetting =
        '(?i)"(TenantId|SubscriptionId|ResourceId)"\s*:\s*"(?<value>[^"]+)"'
    $resourceId = '(?i)/subscriptions/[0-9a-f-]{36}/resourceGroups/'
    $guid = '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b'
    $privateMaterial =
        '(?i)(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|AccountKey=|SharedAccessSignature=)'
    $iconHashes = @{}
    $iconManifest = Join-Path $root "src\AudioBoarder.Core\Assets\AzureIcons\SHA256SUMS.txt"
    if (Test-Path -LiteralPath $iconManifest) {
        foreach ($line in Get-Content -LiteralPath $iconManifest) {
            if ($line -notmatch '^(?<hash>[0-9a-f]{64}) \*(?<name>[a-z0-9-]+\.svg)$') {
                throw "Invalid architecture icon manifest."
            }
            $iconHashes[$Matches.name] = $Matches.hash
        }
    }

    foreach ($relative in $tracked) {
        $path = Join-Path $root $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try {
            $text = [IO.File]::ReadAllText($path)
        }
        catch {
            continue
        }
        foreach ($match in [Regex]::Matches($text, $sensitiveJsonSetting)) {
            $value = $match.Groups["value"].Value
            if (![string]::IsNullOrWhiteSpace($value) -and
                $value -ne "00000000-0000-0000-0000-000000000000") {
                $findings.Add("${relative}: non-empty sensitive identifier setting")
            }
        }
        if ($relative -ne "scripts/scan-repository.ps1") {
            foreach ($finding in Find-CredentialAssignments $text $relative) {
                $findings.Add($finding)
            }
        }
        if ($text -match $resourceId) { $findings.Add("${relative}: Azure resource ID") }
        if ($relative -ne "scripts/scan-repository.ps1" -and $text -match $privateMaterial) {
            $findings.Add("${relative}: credential-like material")
        }
        if ($text -match $guid) {
            $allowedInstallerGuid =
                $relative -eq "installer/Package.wxs" -and
                $text -match 'UpgradeCode="9F67C607-0E72-4EA2-8F36-26EB4251CA78"'
            $structuralGuidFile =
                $relative -eq "AudioBoarder.sln" -or
                $relative -eq "src/AudioBoarder.App/app.manifest" -or
                $relative -eq "src/AudioBoarder.App/Updates/UpdateIntegrity.cs" -or
                $relative -eq "scripts/scan-repository.ps1"
            # Microsoft's unchanged SVGs use GUIDs as drawing element/gradient IDs.
            # Only byte-identical assets pinned in the architecture-icon manifest qualify.
            $iconName = [IO.Path]::GetFileName($relative)
            $pinnedIcon = $relative.StartsWith("src/AudioBoarder.Core/Assets/AzureIcons/") -and
                $iconHashes.ContainsKey($iconName) -and
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ieq $iconHashes[$iconName]
            $onlyZeroPlaceholder = [Regex]::Matches($text, $guid) |
                ForEach-Object Value |
                Where-Object { $_ -ne "00000000-0000-0000-0000-000000000000" } |
                Measure-Object |
                Select-Object -ExpandProperty Count
            if (!$allowedInstallerGuid -and !$structuralGuidFile -and !$pinnedIcon -and $onlyZeroPlaceholder -gt 0) {
                $findings.Add("${relative}: unexpected literal GUID")
            }
        }
    }

    foreach ($output in $tracked | Where-Object { $_ -match '(^|/)(bin|obj)/' }) {
        $findings.Add("${output}: tracked build output")
    }

    if ($IncludeHistory) {
        $history = git --no-pager log --all -p --format= -- . `
            ":(exclude)src/AudioBoarder.App/Assets/web/assets/*" `
            ":(exclude)scripts/scan-repository.ps1"
        if ($LASTEXITCODE -ne 0) { throw "git history scan failed." }
        $historyText = $history -join "`n"
        if ($historyText -match $resourceId) { $findings.Add("git history: Azure resource ID") }
        if ($historyText -match $privateMaterial) {
            $findings.Add("git history: credential-like material")
        }
        $historyPath = ""
        $chunk = [Text.StringBuilder]::new()
        foreach ($line in $history) {
            if ($line -match '^diff --git a/(.+) b/(.+)$') {
                foreach ($finding in Find-CredentialAssignments $chunk.ToString() "git history: $historyPath" $historyPath) {
                    $findings.Add($finding)
                }
                $chunk.Clear() | Out-Null
                $historyPath = $Matches[2]
            }
            $chunk.AppendLine($line) | Out-Null
        }
        foreach ($finding in Find-CredentialAssignments $chunk.ToString() "git history: $historyPath" $historyPath) {
            $findings.Add($finding)
        }
    }

    if ($findings.Count -gt 0) {
        $findings | Sort-Object -Unique | ForEach-Object {
            Write-Host "PRIVACY SCAN: $_" -ForegroundColor Red
        }
        throw "Repository privacy/secret scan failed with $($findings.Count) finding(s)."
    }
    Write-Host "Repository privacy/secret scan passed ($($tracked.Count) versioned/working-tree files)." -ForegroundColor Green
}
finally {
    Pop-Location
}
