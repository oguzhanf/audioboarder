[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$MsiVersion,
    [Parameter(Mandatory)][string]$MsiPublishDirectory,
    [Parameter(Mandatory)][DateTimeOffset]$BuildStartedAtUtc,
    [switch]$Prerelease,
    [switch]$Unsigned,
    [switch]$StagingForSigning,
    [string]$AllowedSignerCertificateSha256 = ""
)

$ErrorActionPreference = "Stop"
$artifactDirectory = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$msiPublishDirectory = (Resolve-Path -LiteralPath $MsiPublishDirectory).Path

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

function Get-MsiQueryRows([string]$Path, [string]$Query, [int]$Columns) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember(
        "OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
    $view = $database.GetType().InvokeMember(
        "OpenView", "InvokeMethod", $null, $database, @($Query))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $rows = @()
    while ($record = $view.GetType().InvokeMember(
            "Fetch", "InvokeMethod", $null, $view, $null)) {
        $values = @()
        for ($column = 1; $column -le $Columns; $column++) {
            $values += $record.GetType().InvokeMember(
                "StringData", "GetProperty", $null, $record, $column)
        }
        $rows += ,$values
    }
    $rows
}
$suffix = if ($StagingForSigning) {
    "-unsigned-staging"
} elseif ($Unsigned) {
    "-unsigned"
} else {
    ""
}
$stem = "AudioBoarder-v$Version-win-x64"
$msiName = "$stem$suffix.msi"
$zipName = "$stem-portable$suffix.zip"
$sbomName = "AudioBoarder-v$Version.spdx.json"
$expected = @(
    $msiName,
    $zipName,
    "SHA256SUMS.txt",
    $sbomName,
    "THIRD-PARTY-NOTICES.txt",
    "RELEASE-METADATA.json"
)

foreach ($name in $expected) {
    $path = Join-Path $artifactDirectory $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing release artifact: $name"
    }
    if ((Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Release artifact is empty: $name"
    }
}

$metadata = Get-Content -LiteralPath (Join-Path $artifactDirectory "RELEASE-METADATA.json") -Raw |
    ConvertFrom-Json
if ($metadata.version -ne $Version) { throw "Metadata version mismatch." }
if ($metadata.msiVersion -ne $MsiVersion) { throw "Metadata MSI version mismatch." }
if ([string]::IsNullOrWhiteSpace($metadata.sourceCommit) -or $metadata.sourceCommit.Length -ne 40) {
    throw "Metadata does not contain an exact 40-character source commit."
}
if ([bool]$metadata.prerelease -ne [bool]$Prerelease) { throw "Metadata prerelease state mismatch." }
if ([bool]$metadata.signed -eq [bool]$Unsigned) { throw "Metadata signature state mismatch." }
if ([bool]$metadata.signingPending -ne [bool]$StagingForSigning) {
    throw "Metadata signing-pending state mismatch."
}

$msiPath = Join-Path $artifactDirectory $msiName
$msiItem = Get-Item -LiteralPath $msiPath
if ($msiItem.LastWriteTimeUtc -lt $BuildStartedAtUtc.UtcDateTime.AddSeconds(-2)) {
    throw "Release MSI predates this release build."
}
$publishExe = Join-Path $msiPublishDirectory "AudioBoarder.exe"
if (!(Test-Path -LiteralPath $publishExe -PathType Leaf)) {
    throw "MSI publish directory does not contain AudioBoarder.exe."
}
$publishExeInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishExe)
$publishExeHash = (Get-FileHash -LiteralPath $publishExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($metadata.publishExecutableFileVersion -ne $publishExeInfo.FileVersion -or
    $metadata.publishExecutableProductVersion -ne $publishExeInfo.ProductVersion -or
    $metadata.publishExecutableSha256 -ne $publishExeHash) {
    throw "Release metadata does not match the exact published executable used for the MSI."
}
if (!$publishExeInfo.ProductVersion.StartsWith($Version, [StringComparison]::OrdinalIgnoreCase) -or
    !$publishExeInfo.ProductVersion.Contains(
        $metadata.sourceCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published executable does not embed the requested version and source commit."
}

$productVersionRows = @(Get-MsiQueryRows $msiPath `
    'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''' 1)
if ($productVersionRows.Count -ne 1 -or $productVersionRows[0][0] -ne $MsiVersion) {
    throw "MSI ProductVersion does not match the mapped MSI version."
}
$fileRows = @(Get-MsiQueryRows $msiPath 'SELECT `FileName`, `Version` FROM `File`' 2 |
    Where-Object { $_[0] -like "*AudioBoarder.exe*" })
if ($fileRows.Count -ne 1 -or $fileRows[0][1] -ne $publishExeInfo.FileVersion) {
    throw "MSI File table does not match the exact Release publish executable FileVersion."
}

$sbom = Get-Content -LiteralPath (Join-Path $artifactDirectory $sbomName) -Raw | ConvertFrom-Json
if ($sbom.spdxVersion -ne "SPDX-2.3") { throw "SBOM is not SPDX 2.3." }
$rootPackage = $sbom.packages | Where-Object name -eq "AudioBoarder" | Select-Object -First 1
if ($null -eq $rootPackage -or $rootPackage.versionInfo -ne $Version) {
    throw "SBOM version mismatch."
}

$checksumPath = Join-Path $artifactDirectory "SHA256SUMS.txt"
$checksumLines = Get-Content -LiteralPath $checksumPath
foreach ($name in $expected | Where-Object { $_ -ne "SHA256SUMS.txt" }) {
    $line = $checksumLines | Where-Object { $_ -match "\s+\*?$([Regex]::Escape($name))$" }
    if ($null -eq $line) { throw "No checksum entry for $name." }
    $expectedHash = ($line -split "\s+")[0]
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $artifactDirectory $name) -Algorithm SHA256).Hash
    if ($actualHash -ine $expectedHash) { throw "Checksum mismatch for $name." }
}

$inspectDirectory = Join-Path $artifactDirectory ".inspect"
if (Test-Path -LiteralPath $inspectDirectory) {
    Remove-Item -LiteralPath $inspectDirectory -Recurse -Force
}
try {
    Expand-Archive -LiteralPath (Join-Path $artifactDirectory $zipName) -DestinationPath $inspectDirectory
    $portableExe = Join-Path $inspectDirectory "AudioBoarder.exe"
    foreach ($requiredRelative in @(
        "AudioBoarder.exe",
        "appsettings.json",
        "Assets\web\index.html",
        "LICENSE",
        "THIRD-PARTY-NOTICES.txt",
        $sbomName,
        "RELEASE-METADATA.json"
    )) {
        if (!(Test-Path -LiteralPath (Join-Path $inspectDirectory $requiredRelative))) {
            throw "Portable archive is missing $requiredRelative."
        }
    }

    $msiSignature = Get-AuthenticodeSignature -LiteralPath $msiPath
    $exeSignature = Get-AuthenticodeSignature -LiteralPath $portableExe
    if ($Unsigned) {
        if ($msiSignature.Status -eq "Valid") {
            throw "Unsigned artifact label used for a signed MSI."
        }
    }
    else {
        $allowedSignerHashes = @(Get-AllowedSignerCertificateHashes $AllowedSignerCertificateSha256)
        foreach ($signature in @($msiSignature, $exeSignature)) {
            if ($signature.Status -ne "Valid" -or $null -eq $signature.SignerCertificate) {
                throw "Signed artifact has an invalid Authenticode signature."
            }
            $signerHash = Get-CertificateSha256 $signature.SignerCertificate
            if ($allowedSignerHashes -inotcontains $signerHash) {
                throw "Signed artifact signer certificate is not in the exact SHA-256 allowlist."
            }
        }
        $metadataHashes = @($metadata.allowedSignerCertificateSha256 |
            ForEach-Object { "$_".ToUpperInvariant() } | Sort-Object -Unique)
        if (($metadataHashes -join ",") -ne ($allowedSignerHashes -join ",")) {
            throw "Metadata signer certificate allowlist mismatch."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $inspectDirectory) {
        Remove-Item -LiteralPath $inspectDirectory -Recurse -Force
    }
}

Write-Host "Release artifact contract verified: $artifactDirectory" -ForegroundColor Green
