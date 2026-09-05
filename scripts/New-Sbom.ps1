[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNetPackagesJson,
    [string]$PackageLockJson,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$NoticesPath
)

$ErrorActionPreference = "Stop"

function ConvertTo-SpdxId([string]$Value) {
    "SPDXRef-" + ($Value -replace "[^A-Za-z0-9.-]", "-")
}

$packages = [ordered]@{}
$dotnet = Get-Content -LiteralPath $DotNetPackagesJson -Raw | ConvertFrom-Json
foreach ($project in $dotnet.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -eq $package -or [string]::IsNullOrWhiteSpace($package.id)) { continue }
            $key = "nuget:$($package.id)@$($package.resolvedVersion)"
            $packages[$key] = [ordered]@{
                SPDXID = ConvertTo-SpdxId $key
                name = $package.id
                versionInfo = $package.resolvedVersion
                downloadLocation = "https://www.nuget.org/packages/$($package.id)/$($package.resolvedVersion)"
                filesAnalyzed = $false
                licenseConcluded = "NOASSERTION"
                licenseDeclared = "NOASSERTION"
                copyrightText = "NOASSERTION"
                externalRefs = @([ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType = "purl"
                    referenceLocator = "pkg:nuget/$([Uri]::EscapeDataString($package.id))@$($package.resolvedVersion)"
                })
            }
        }
    }
}

if ($PackageLockJson) {
    $npm = Get-Content -LiteralPath $PackageLockJson -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $npm.packages.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace($entry.Key)) { continue }
        $name = if ($entry.Value.name) {
            [string]$entry.Value.name
        }
        else {
            ([string]$entry.Key -replace '^node_modules/', '') -replace '/node_modules/', '/'
        }
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $packageVersion = [string]$entry.Value.version
        $key = "npm:$name@$packageVersion"
        $packages[$key] = [ordered]@{
            SPDXID = ConvertTo-SpdxId $key
            name = $name
            versionInfo = $packageVersion
            downloadLocation = if ($entry.Value.resolved) { [string]$entry.Value.resolved } else { "NOASSERTION" }
            filesAnalyzed = $false
            licenseConcluded = "NOASSERTION"
            licenseDeclared = if ($entry.Value.license) { [string]$entry.Value.license } else { "NOASSERTION" }
            copyrightText = "NOASSERTION"
            externalRefs = @([ordered]@{
                referenceCategory = "PACKAGE-MANAGER"
                referenceType = "purl"
                referenceLocator = "pkg:npm/$([Uri]::EscapeDataString($name))@$packageVersion"
            })
        }
    }
}

$created = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$documentNamespace = "https://github.com/oguzhanf/audioboarder/sbom/$Commit/$([Uri]::EscapeDataString($Version))"
$rootId = "SPDXRef-AudioBoarder"
$relationships = @($packages.Values | ForEach-Object {
    [ordered]@{
        spdxElementId = $rootId
        relationshipType = "DEPENDS_ON"
        relatedSpdxElement = $_.SPDXID
    }
})
$rootPackage = [ordered]@{
    SPDXID = $rootId
    name = "AudioBoarder"
    versionInfo = $Version
    downloadLocation = "https://github.com/oguzhanf/audioboarder/tree/$Commit"
    filesAnalyzed = $false
    licenseConcluded = "MIT"
    licenseDeclared = "MIT"
    copyrightText = "Copyright © 2026"
    externalRefs = @([ordered]@{
        referenceCategory = "PACKAGE-MANAGER"
        referenceType = "purl"
        referenceLocator = "pkg:github/oguzhanf/audioboarder@$Commit"
    })
}

$spdx = [ordered]@{
    spdxVersion = "SPDX-2.3"
    dataLicense = "CC0-1.0"
    SPDXID = "SPDXRef-DOCUMENT"
    name = "AudioBoarder-$Version"
    documentNamespace = $documentNamespace
    creationInfo = [ordered]@{
        created = $created
        creators = @("Tool: AudioBoarder scripts/New-Sbom.ps1")
        licenseListVersion = "3.26"
    }
    documentDescribes = @($rootId)
    packages = @($rootPackage) + @($packages.Values)
    relationships = $relationships
}

$spdx | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

$noticeLines = [System.Collections.Generic.List[string]]::new()
$noticeLines.Add("AudioBoarder third-party notices")
$noticeLines.Add("Version: $Version")
$noticeLines.Add("Source commit: $Commit")
$noticeLines.Add("")
$noticeLines.Add("This inventory is generated from resolved dependency manifests. The offline canvas has no JavaScript package dependencies.")
$noticeLines.Add("License texts remain available from each package's source or package page.")
$noticeLines.Add("")
foreach ($package in $packages.Values | Sort-Object name, versionInfo) {
    $noticeLines.Add("$($package.name) $($package.versionInfo) | $($package.licenseDeclared) | $($package.downloadLocation)")
}
$noticeLines | Set-Content -LiteralPath $NoticesPath -Encoding utf8NoBOM
