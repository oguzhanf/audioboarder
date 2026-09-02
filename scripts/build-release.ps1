[CmdletBinding()]
param(
    [string]$Version = "0.8.0",
    [switch]$Prerelease,
    [switch]$Unsigned,
    [switch]$StagingForSigning,
    [switch]$DryRun,
    [string]$AllowedSignerCertificateSha256 = $env:AUDIOBOARDER_ALLOWED_SIGNER_CERT_SHA256,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $root
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

function Get-SignTool {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) { throw "Windows SDK signtool.exe (x64) was not found." }
    $candidate.FullName
}

function Get-AllowedSignerCertificateHashes([string]$Allowlist) {
    $hashes = @($Allowlist -split '[,;]' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        ForEach-Object {
            if ($_.StartsWith("sha256:", [StringComparison]::OrdinalIgnoreCase)) {
                $_.Substring(7)
            } else { $_ }
        })
    if ($hashes.Count -eq 0 -or
        @($hashes | Where-Object { $_ -notmatch '^[0-9A-Fa-f]{64}$' }).Count -gt 0) {
        throw "Allowed signer certificate identity must be a comma/semicolon-separated allowlist of exact SHA-256 certificate hashes."
    }
    @($hashes | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
}

function Get-CertificateSha256([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($sha256.ComputeHash($Certificate.RawData)).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Sign-Files(
    [string[]]$Paths,
    [string]$SignTool,
    [string]$PfxPath,
    [string]$Password,
    [string[]]$AllowedSignerHashes) {
    foreach ($path in $Paths | Sort-Object -Unique) {
        Invoke-Checked {
            & $SignTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $PfxPath /p $Password $path
        } "Sign $(Split-Path -Leaf $path)"
        Invoke-Checked { & $SignTool verify /pa /all $path } "Verify signature $(Split-Path -Leaf $path)"
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne "Valid" -or $null -eq $signature.SignerCertificate) {
            throw "Authenticode validation failed for $path."
        }
        $signerHash = Get-CertificateSha256 $signature.SignerCertificate
        if ($AllowedSignerHashes -inotcontains $signerHash) {
            throw "Signer certificate SHA-256 identity mismatch for $path."
        }
    }
}

try {
    if ($Version.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $Version = $Version.Substring(1)
    }
    $msiVersion = ConvertTo-AudioBoarderMsiVersion $Version
    if (!$Prerelease -and $Version.Contains("-")) {
        throw "A version with a prerelease identifier requires -Prerelease."
    }
    if ($StagingForSigning -and !$Unsigned) {
        throw "-StagingForSigning requires -Unsigned because this phase must not receive signing credentials."
    }
    if (!$Prerelease -and $Unsigned -and !$StagingForSigning) {
        throw "Production/non-prerelease releases cannot be unsigned."
    }
    if ($Prerelease -and $Unsigned -and !$StagingForSigning -and !$Version.Contains("-")) {
        throw "Unsigned prerelease artifacts must carry a prerelease identifier in Version."
    }
    if ($StagingForSigning -and [string]::IsNullOrWhiteSpace($AllowedSignerCertificateSha256)) {
        throw "Signing staging requires AllowedSignerCertificateSha256 so the unsigned binaries embed the final update trust identity."
    }
    if (!$Unsigned -and (
        [string]::IsNullOrWhiteSpace($env:AUDIOBOARDER_SIGNING_PFX_BASE64) -or
        [string]::IsNullOrWhiteSpace($env:AUDIOBOARDER_SIGNING_PFX_PASSWORD) -or
        [string]::IsNullOrWhiteSpace($AllowedSignerCertificateSha256))) {
        throw "Signed release requires AUDIOBOARDER_SIGNING_PFX_BASE64, AUDIOBOARDER_SIGNING_PFX_PASSWORD, and AllowedSignerCertificateSha256."
    }
    $allowedSignerHashes = if ($Unsigned -and !$StagingForSigning) {
        @()
    } else {
        @(Get-AllowedSignerCertificateHashes $AllowedSignerCertificateSha256)
    }

    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw "Could not determine the exact source commit."
    }
    $dirty = [bool](git status --porcelain)
    if (!$Prerelease -and $dirty) {
        throw "Production release builds require a clean working tree."
    }

    $buildStartedAtUtc = [DateTimeOffset]::UtcNow
    $suffix = if ($StagingForSigning) {
        "-unsigned-staging"
    } elseif ($Unsigned) {
        "-unsigned"
    } else {
        ""
    }
    $stem = "AudioBoarder-v$Version-win-x64"
    $releaseDirectory = Join-Path $root "$OutputRoot\release\v$Version"
    $stagingDirectory = Join-Path $root "$OutputRoot\staging\v$Version"
    $msiPublish = Join-Path $stagingDirectory "msi"
    $portablePublish = Join-Path $stagingDirectory "portable"
    $webDirectory = Join-Path $root "src\AudioBoarder.App\web"
    $dotnetPackages = Join-Path $stagingDirectory "dotnet-packages.json"
    $sbomName = "AudioBoarder-v$Version.spdx.json"
    $sbomPath = Join-Path $releaseDirectory $sbomName
    $noticesPath = Join-Path $releaseDirectory "THIRD-PARTY-NOTICES.txt"
    $metadataPath = Join-Path $releaseDirectory "RELEASE-METADATA.json"
    $pfxPath = Join-Path $stagingDirectory "signing-certificate.pfx"

    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $releaseDirectory, $msiPublish, $portablePublish -Force | Out-Null

    Invoke-Checked {
        & "$root\scripts\Test-ReleaseVersion.ps1"
    } "Test SemVer to MSI ProductVersion mapping"
    Invoke-Checked {
        & "$root\scripts\scan-repository.ps1" -SelfTest
    } "Test repository secret scanner fixtures"
    Invoke-Checked { dotnet restore AudioBoarder.sln } "Restore .NET dependencies"
    Push-Location $webDirectory
    try {
        Invoke-Checked { npm ci } "Install exact web dependencies"
        Invoke-Checked { npm run build } "Build custom SVG canvas"

        $preview = Start-Process -FilePath (Get-Command node).Source `
            -ArgumentList @("node_modules\vite\bin\vite.js", "preview", "--host", "127.0.0.1", "--port", "5566", "--strictPort") `
            -WorkingDirectory $webDirectory -PassThru
        try {
            $ready = $false
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                try {
                    Invoke-WebRequest -Uri "http://127.0.0.1:5566/" -UseBasicParsing -TimeoutSec 2 | Out-Null
                    $ready = $true
                    break
                }
                catch { Start-Sleep -Milliseconds 500 }
            }
            if (!$ready) { throw "Vite preview did not become ready." }
            Invoke-Checked {
                node verify.cjs "http://127.0.0.1:5566/" "" (Join-Path $stagingDirectory "svg-verify.png")
            } "Verify SVG canvas in headless Edge"
        }
        finally {
            if (!$preview.HasExited) { Stop-Process -Id $preview.Id -Force }
        }
    }
    finally {
        Pop-Location
    }

    $commonProperties = @(
        "-p:Version=$Version",
        "-p:SourceRevisionId=$commit",
        "-p:UpdateAllowedSignerCertificateSha256=$AllowedSignerCertificateSha256"
    )
    Invoke-Checked {
        dotnet build AudioBoarder.sln -c Release --no-restore @commonProperties
    } "Build solution"
    Invoke-Checked {
        dotnet test AudioBoarder.sln -c Release --no-build --filter "Category!=LiveModel" `
            --logger "trx;LogFileName=offline-release.trx"
    } "Run full offline .NET suite"
    Invoke-Checked {
        dotnet test tests\AudioBoarder.Tests\AudioBoarder.Tests.csproj -c Release --no-build `
            --filter "FullyQualifiedName~SemanticReleaseGateTests"
    } "Run offline semantic gates"
    Invoke-Checked {
        dotnet test tests\AudioBoarder.Tests\AudioBoarder.Tests.csproj -c Release --no-build `
            --filter "FullyQualifiedName~SessionStoreTests"
    } "Run schema migration compatibility gates"
    Invoke-Checked {
        & "$root\scripts\scan-repository.ps1" -IncludeHistory
    } "Scan repository and history for secrets and private resource identifiers"

    Invoke-Checked {
        dotnet list AudioBoarder.sln package --include-transitive --format json |
            Set-Content -LiteralPath $dotnetPackages -Encoding utf8NoBOM
    } "Resolve dependency inventory"
    & "$root\scripts\New-Sbom.ps1" `
        -DotNetPackagesJson $dotnetPackages `
        -PackageLockJson (Join-Path $webDirectory "package-lock.json") `
        -Version $Version -Commit $commit -OutputPath $sbomPath -NoticesPath $noticesPath

    Invoke-Checked {
        dotnet publish src\AudioBoarder.App\AudioBoarder.App.csproj -c Release -r win-x64 `
            --self-contained true -o $msiPublish @commonProperties -p:PortableBuild=false
    } "Publish MSI application payload"
    Invoke-Checked {
        dotnet publish src\AudioBoarder.App\AudioBoarder.App.csproj -c Release -r win-x64 `
            --self-contained true -o $portablePublish @commonProperties -p:PortableBuild=true
    } "Publish portable application payload"

    foreach ($publishDirectory in @($msiPublish, $portablePublish)) {
        Copy-Item -LiteralPath "$root\LICENSE" -Destination (Join-Path $publishDirectory "LICENSE")
        Copy-Item -LiteralPath $sbomPath -Destination (Join-Path $publishDirectory $sbomName)
        Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $publishDirectory "THIRD-PARTY-NOTICES.txt")
    }

    $signTool = $null
    $signingPassword = $env:AUDIOBOARDER_SIGNING_PFX_PASSWORD
    if (!$Unsigned) {
        [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:AUDIOBOARDER_SIGNING_PFX_BASE64))
        $signTool = Get-SignTool
        $binaries = @(
            Get-ChildItem -LiteralPath $msiPublish, $portablePublish -Recurse -File -Include *.exe, *.dll |
                Select-Object -ExpandProperty FullName
        )
        Sign-Files $binaries $signTool $pfxPath $signingPassword $allowedSignerHashes
    }

    $msiPublishedExe = Join-Path $msiPublish "AudioBoarder.exe"
    $publishedExeInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($msiPublishedExe)
    if ([string]::IsNullOrWhiteSpace($publishedExeInfo.ProductVersion) -or
        !$publishedExeInfo.ProductVersion.StartsWith($Version, [StringComparison]::OrdinalIgnoreCase) -or
        !$publishedExeInfo.ProductVersion.Contains($commit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published executable ProductVersion does not contain the requested version and exact source commit."
    }
    $metadata = [ordered]@{
        product = "AudioBoarder"
        version = $Version
        msiVersion = $msiVersion
        sourceCommit = $commit
        sourceTreeDirty = $dirty
        prerelease = [bool]$Prerelease
        signed = !$Unsigned
        signingPending = [bool]$StagingForSigning
        allowedSignerCertificateSha256 = if ($Unsigned -and !$StagingForSigning) {
            $null
        } else {
            $allowedSignerHashes
        }
        runtime = "win-x64"
        selfContained = $true
        dryRun = [bool]$DryRun
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        publishExecutableFileVersion = $publishedExeInfo.FileVersion
        publishExecutableProductVersion = $publishedExeInfo.ProductVersion
        publishExecutableSha256 = (Get-FileHash -LiteralPath $msiPublishedExe -Algorithm SHA256).Hash.ToLowerInvariant()
        publishExecutableLastWriteTimeUtc = (Get-Item -LiteralPath $msiPublishedExe).LastWriteTimeUtc.ToString("O")
    }
    $metadata | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM
    foreach ($publishDirectory in @($msiPublish, $portablePublish)) {
        Copy-Item -LiteralPath $metadataPath -Destination (Join-Path $publishDirectory "RELEASE-METADATA.json")
    }

    $msiOutputName = "$stem$suffix"
    $installerReleaseDirectory = Join-Path $root "installer\bin\x64\Release"
    $installerObjReleaseDirectory = Join-Path $root "installer\obj\x64\Release"
    Remove-Item -LiteralPath $installerReleaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installerObjReleaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $installerBuildStartedAtUtc = [DateTimeOffset]::UtcNow
    Invoke-Checked {
        dotnet build installer\AudioBoarder.Installer.wixproj -c Release `
            -p:Version=$Version -p:MsiVersion=$msiVersion -p:MsiOutputName=$msiOutputName `
            -p:PublishDir=$msiPublish
    } "Build WiX MSI"
    $builtMsiPath = Join-Path $installerReleaseDirectory "$msiOutputName.msi"
    if (!(Test-Path -LiteralPath $builtMsiPath -PathType Leaf)) {
        throw "WiX build did not produce the exact expected Release/x64 output $builtMsiPath."
    }
    $builtMsi = Get-Item -LiteralPath $builtMsiPath
    if ($builtMsi.LastWriteTimeUtc -lt $installerBuildStartedAtUtc.UtcDateTime.AddSeconds(-2)) {
        throw "WiX output predates this installer build and may be stale."
    }
    $msiPath = Join-Path $releaseDirectory "$msiOutputName.msi"
    Copy-Item -LiteralPath $builtMsi.FullName -Destination $msiPath
    if (!$Unsigned) {
        Sign-Files @($msiPath) $signTool $pfxPath $signingPassword $allowedSignerHashes
    }

    $zipPath = Join-Path $releaseDirectory "$stem-portable$suffix.zip"
    Compress-Archive -Path (Join-Path $portablePublish "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $checksumFiles = Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object Name -ne "SHA256SUMS.txt" |
        Sort-Object Name
    $checksumFiles | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $releaseDirectory "SHA256SUMS.txt") -Encoding ascii

    & "$root\scripts\Test-ReleaseArtifacts.ps1" `
        -ArtifactDirectory $releaseDirectory -Version $Version `
        -MsiVersion $msiVersion -MsiPublishDirectory $msiPublish `
        -BuildStartedAtUtc $buildStartedAtUtc `
        -Prerelease:$Prerelease -Unsigned:$Unsigned `
        -StagingForSigning:$StagingForSigning `
        -AllowedSignerCertificateSha256 $AllowedSignerCertificateSha256
    Write-Host "Release build complete: $releaseDirectory" -ForegroundColor Green
}
finally {
    if ($pfxPath -and (Test-Path -LiteralPath $pfxPath)) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
    Pop-Location
}
