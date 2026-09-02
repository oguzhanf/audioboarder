# Clean Windows VM release checklist

Use a disposable, fully patched Windows 11 x64 VM with no .NET SDK, Node.js, repository
checkout, Azure CLI, or developer certificate installed. Record OS build, WebView2 Runtime
version, artifact version, source commit, and whether the VM has network access.

## MSI

- Verify `SHA256SUMS.txt`, then inspect Properties > Digital Signatures.
- Confirm the signature is valid, timestamped, and its exact certificate SHA-256 matches
  the configured signer allowlist.
- Install as a standard user and approve elevation; verify Start menu entry and install
  path under Program Files.
- Launch without Azure configuration. Confirm the custom SVG Live Architecture Canvas,
  health/degraded states, help text, and `image: disabled`.
- Confirm `appsettings.json`, WebView assets, LICENSE, SBOM, notices and metadata are
  installed.
- Exercise microphone-only capture without making an Azure-dependent probe a release
  blocker. Confirm missing Azure/transcription dependencies degrade visibly, not silently.
- Upgrade from the current v0.7.0 MSI, restore a schema-v0/v1 session fixture, and verify
  intent/scene compatibility.
- Tamper with a downloaded MSI and confirm hash rejection. Test an unsigned or
  unpinned-signer MSI in a controlled fixture and confirm update rejection.
- Uninstall and verify Program Files/Start menu/installer registry entries are removed.
  User data under `%LOCALAPPDATA%\AudioBoarder` must remain until the user runs the reset
  script.

## Portable ZIP

- Verify checksum, extract the complete ZIP, and launch `AudioBoarder.exe`.
- Confirm Assets, settings, LICENSE, SBOM, notices and metadata are adjacent.
- Confirm the Live Architecture Canvas opens with no network asset fetch.
- Confirm the portable build never performs or offers automatic update installation.
- Run `scripts\reset-local-data.ps1` from the source package or documented support bundle;
  first cancel, then confirm with `DELETE`, and verify only AudioBoarder-owned paths are
  removed.

## Evidence

Attach installer logs, screenshots of signature identity and runtime degraded states,
artifact inspection output, and reset-script target list to the release approval record.
Azure live-model probes are optional qualification evidence and must remain separate from
ordinary CI and clean-install acceptance.
