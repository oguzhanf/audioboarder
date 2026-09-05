# Release process

The 0.8.0 `release-readiness` work (including v0.8.0-preview.2) remains
pre-release until every gate below passes. This repository does not automatically create
tags or GitHub releases.

## Artifact contract

`scripts/build-release.ps1` produces one directory containing:

- `AudioBoarder-v<version>-win-x64.msi`
- `AudioBoarder-v<version>-win-x64-portable.zip`
- `SHA256SUMS.txt`
- `AudioBoarder-v<version>.spdx.json` (SPDX 2.3)
- `THIRD-PARTY-NOTICES.txt`
- `RELEASE-METADATA.json`

Unsigned prereleases insert `-unsigned` before the MSI extension and after `-portable`
in the ZIP name. Metadata records the exact 40-character source commit, requested version,
mapped MSI numeric version, published executable version/hash/timestamp, dirty-tree state,
runtime, prerelease/signature state and build time.
The MSI and portable ZIP include `appsettings.json`, WebView assets, LICENSE, notices,
SBOM and metadata.

## Local prerelease dry run

```powershell
.\scripts\build-release.ps1 `
  -Version 0.8.0-preview.2 `
  -Prerelease -Unsigned -DryRun
```

The script fails at the first unsuccessful gate:

1. `dotnet restore`
2. PowerShell packaging of offline canvas modules and headless Edge SVG/host-bridge verification (no Node.js)
3. Release .NET build
4. full offline tests (`Category!=LiveModel`)
5. offline semantic golden gates
6. session schema migration/backward-compatibility tests
7. repository privacy/secret scan
8. separate self-contained `win-x64` MSI and portable publishes
9. optional EXE/DLL signing and verification
10. WiX MSI build and optional MSI signing
11. ZIP, SPDX SBOM, notices, metadata and SHA-256 generation
12. artifact/version/hash/content/signature contract inspection

`-DryRun` is metadata only: the script never publishes. Unsigned mode is legal only with
`-Prerelease` and a version containing a prerelease identifier.

## Signed release inputs

GitHub Actions uses a two-job trust boundary:

1. An unprivileged job checks out an exact commit, restores dependencies, runs
   tests, builds, and produces an immutable unsigned signing stage. It receives
   no signing certificate or password.
2. A protected `release-signing` environment downloads that stage. Signing
   secrets are exposed only to two fixed inline signing steps—payload binaries
   and the final MSI. No npm lifecycle script, test, MSBuild target, or
   caller-selected repository script executes while those secrets exist.

Signed builds are restricted to exact commits reachable from protected `main`
and require environment approval. The signing stage is hash-manifested before
upload and verified before signing.

The protected signing steps read:

- secret `WINDOWS_SIGNING_PFX_BASE64`: base64-encoded code-signing PFX
- secret `WINDOWS_SIGNING_PFX_PASSWORD`: PFX password
- variable `WINDOWS_ALLOWED_SIGNER_CERT_SHA256`: exact SHA-256 certificate thumbprint

The local equivalents are `AUDIOBOARDER_SIGNING_PFX_BASE64`,
`AUDIOBOARDER_SIGNING_PFX_PASSWORD`, and
`AUDIOBOARDER_ALLOWED_SIGNER_CERT_SHA256`.
Windows SDK `signtool.exe` signs SHA-256 with RFC 3161 timestamping. A stable/
non-prerelease build fails closed when credentials, timestamping, signature validation,
or exact signer identity validation fail. The PFX is written only under the runner temporary directory and removed in
`finally` inside each narrow signing step.

The same certificate SHA-256 allowlist is embedded as assembly metadata. MSI auto-update
downloads only the stable MSI naming contract, validates the chain/signature with
WinVerifyTrust, and then requires an exact signer certificate hash match. The elevated
staging step repeats hash, Authenticode, and exact identity checks. Portable builds embed
`PortableBuild=true` and never auto-update.

For planned certificate rotation, set the variable to comma- or semicolon-separated exact
SHA-256 certificate hashes, publish an update containing both old and new hashes while the
old certificate is still usable, then remove the old hash in a later signed release. Never
put a subject name, substring, PFX, or private key in this variable. Unsigned prereleases
remain explicitly supported; installed/non-portable automatic update remains fail-closed
when no valid allowlist is embedded.

## SemVer to MSI ProductVersion mapping

Windows Installer accepts only `major.minor.build`, with major/minor at most 255 and build
at most 65535. `scripts/ReleaseVersion.ps1` therefore supports SemVer major/minor up to 255,
patch up to 63, and prerelease sequence 1-199 for `alpha.N`, `beta.N`, `preview.N`, or
`rc.N`. It maps build to `patch * 1024 + ordinal`, where the channel ranges are alpha
1-199, beta 201-399, preview 401-599, rc 601-799, and stable 1023. Thus
`0.8.0-preview.1` < `0.8.0-preview.2` < `0.8.0`, and `0.8.0` < `0.8.1-alpha.1`.
Unsupported versions fail before build. Same-version major upgrades are explicitly
disabled; every supported release stage receives a distinct increasing ProductVersion.

The installer Release/x64 output is deleted before each WiX build and copied only from the
single expected output path. Artifact inspection checks its name/build timestamp,
ProductVersion, and MSI File-table executable version against the exact Release publish
directory; the published executable ProductVersion must embed the requested SemVer and
source commit.

## GitHub Actions

Run **Build release artifacts** with an exact 40-character `source_commit`, version, prerelease flag and
unsigned flag. It builds and uploads a 14-day workflow artifact only. Review
`RELEASE-METADATA.json`, checksums, SBOM, notices, test results and the clean-VM checklist
before separately creating any tag or release.

For an unsigned preview draft whose tag already points at a successfully built commit,
the optional `artifact_run_id` input publishes that run's artifacts directly from the
runner. This path skips rebuilding, checks the build/tag/source metadata and checksums,
uploads the six expected files without overwriting existing assets, verifies GitHub's
asset digests, and only then publishes the draft. It cannot publish stable or signed
releases and does not check out or execute repository code with its publishing token.

The workflow also runs `AudioBoarder.exe healthcheck --package` against the portable
payload on `windows-2022` and `windows-2025`. This check is offline and validates version
metadata plus required settings/WebView/license/notices/SBOM files; it does not contact
Azure or require audio hardware.

Ordinary CI remains offline with respect to Azure/model calls. Live semantic qualification
is a separately approved manual workflow and is not a substitute for offline gates.
