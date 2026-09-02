# AudioBoarder

> Listens to a meeting on Windows and draws it live on an interactive SVG
> architecture canvas, with structured notes and on-demand illustrations.

AudioBoarder captures your microphone and (optionally) system audio, transcribes the
conversation, and asks an Azure OpenAI model to turn what it hears into a diagram:
technologies carrying icons, systems drawn as labelled boundaries, arrows that say what
actually flows between things, and callouts explaining the subtle parts. Decisions,
action items, risks and open questions are collected in a side panel. Everything can be
exported as a `.excalidraw` file you can keep editing, or as a PNG.

Built on WPF + SkiaSharp + WebView2. The live editor is a vendored offline SVG
surface; Excalidraw remains an editable export format.

**Release status:** v0.7.0 is the current published release and its prerelease cycle is
complete. The 0.8.0 work on `release-readiness` is still **pre-release** until the
offline, semantic, installer, privacy, artifact, and signing gates documented below
pass. Do not treat a branch build as a published release.

---

## Requirements

- Windows 10 build 22000+ or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An Azure subscription with an **Azure AI Foundry / Azure OpenAI** resource containing
  at least one chat-capable deployment
- A microphone
- WebView2 Runtime (ships with Windows 11 and with Edge)

You do **not** need to hardcode any credentials. AudioBoarder signs in with
`DefaultAzureCredential` and discovers your deployments automatically.

---

## Quick start

### Recommended: Windows installer

Download the latest `AudioBoarder-v*-win-x64.msi` from
[GitHub Releases](https://github.com/oguzhanf/audioboarder/releases). The MSI includes
the .NET runtime, native audio/AI dependencies, and the offline whiteboard bundle; it
installs everything together and adds AudioBoarder to the Start menu.

The portable ZIP contains the same self-contained application for environments where
software installation is restricted. Extract the complete folder before running
`AudioBoarder.exe`—the executable depends on the adjacent `Assets` and runtime files.

The current v0.7.0 binaries predate the Phase 6 signing gate and may trigger Windows
SmartScreen. New stable/non-prerelease artifacts must be Authenticode-signed and
timestamped; the release build fails closed without signing credentials. Explicit
unsigned prerelease builds carry `-unsigned` in their filenames. Every artifact set
also includes SHA-256 checksums, SPDX SBOM, third-party notices, and source metadata.

MSI installations check the repository's latest GitHub release when AudioBoarder starts.
When a newer signed version is available, the app shows its release notes, downloads and
verifies both the GitHub SHA-256 digest and the MSI's Authenticode chain, then requires an
exact SHA-256 signer-certificate identity match before install and restart. Missing,
invalid, unsigned, or unpinned-signature installers fail closed.
Windows may request administrator approval because the MSI installs for all users. Portable
installations remain manually updated so the app never replaces files in an extracted folder.

On first launch, an in-app guide explains Azure sign-in, microphone selection, privacy,
live capture, editing, and export. Reopen it later with the **Help** button.

### Build from source

```powershell
git clone https://github.com/oguzhanf/audioboarder.git
cd audioboarder

# 1. Sign in to Azure once. Lists what the app will be able to discover.
pwsh -ExecutionPolicy Bypass -File .\scripts\setup-azure.ps1

# 2. Build.
dotnet build

# 3. Confirm all three subsystems are healthy before launching the UI.
.\src\AudioBoarder.App\bin\Debug\net10.0-windows\AudioBoarder.exe healthcheck

# 4. Launch.
dotnet run --project src\AudioBoarder.App
```

`healthcheck` should print three `[OK]` lines and exit `0`. If it does, the UI will work.

### First launch

Three health indicators fill in independently — **Audio devices**, **Transcription**,
**Azure OpenAI** — shown as coloured dots in the status bar, and toolbar buttons enable
as each subsystem becomes ready. If you have no cloud transcription deployment, Whisper
downloads `ggml-base.bin` (~148 MB) once.

Then **Listen** → talk, and the board grows on its own as the conversation develops.
**Refine** runs a deeper pass (optionally with an instruction like "group the security
controls"), the export buttons save a PNG or an editable `.excalidraw` file, and dragging
a node pins it so later passes leave it where you put it.

Live extraction reads only finalized captions after the committed transcript cursor.
Deep synthesis is event-driven rather than periodic: it runs on **Refine**, after a
flushed meeting stop, or after the configured speech pause (25 seconds by default) when
provisional diagram changes exist. Fixed timed deep passes are disabled.

### Diagram intents and switching

The six supported intents are:

1. **Software system architecture** — services, components, dependencies and boundaries.
2. **SaaS multi-tenant architecture** — tenants, control/data planes and isolation.
3. **Security / Zero Trust architecture** — identities, trust boundaries, controls and risks.
4. **Cloud network architecture** — network scopes, subnets, ingress/egress and flows.
5. **Integration / data-flow architecture** — producers, consumers, stores and payload paths.
6. **Discussion summary** — topics, decisions, actions, risks and questions.

In **Auto**, the intent coordinator can suggest and apply an intent as evidence accumulates.
The status shows the applied and suggested intent. Choosing an intent pins
`PinnedByUser`; automatic classification may continue to make a suggestion but cannot
replace the pinned choice. Returning to Auto allows later evidence to switch the intent.
Intent changes affect semantic defaults and layout selection, not the source transcript.

### Model roles, latency, and runtime states

- The transcription backend produces interim captions where supported and commits only
  finalized segments. Streaming Speech is normally sub-second to a few seconds; windowed
  cloud/local transcription completes after an utterance pause and model processing.
- The fast chat deployment performs safe incremental extraction. It is rate-limited by
  `Realtime.MinIntervalSeconds` (10 seconds by default) and adapts upward when observed
  inference takes longer.
- The primary frontier deployment performs deep synthesis on Refine, stop, or the
  configured pause. Fast tiers are often several seconds; reasoning `pro`/`sol` tiers may
  take 30–120 seconds. Azure load and throttling can increase either figure.
- Image generation is a separate optional model path and is **disabled by default**.
  It may take tens of seconds or minutes and is never required for the live SVG canvas.

The visible runtime state distinguishes Initializing, Ready, Listening, Captions current,
Analyzing, Deep refining, Current, Behind, Rate limited, Retrying, Audio gap, Degraded,
and Error. Captions continue while diagram work is queued. On HTTP 429 the backend exposes
its retry time and buffered duration; retries use bounded backoff. Audio queues are bounded:
under sustained overload the oldest unprocessed audio is dropped rather than allowing
unbounded memory growth, and the UI reports **Audio gap** plus dropped duration/count.
Pending finalized statements are retained across diagram-generation retries.

### Semantic contract and limits

The model emits typed ScenePatch operations, never pixel coordinates. Patches are schema
validated and applied transactionally; invalid operations are skipped and reported rather
than trusted. The live canvas renders the resulting SceneGraph and deterministic layout.
Grounding is limited to the finalized transcript window and restored scene state. The app
does not prove that a design is complete, secure, compliant, or deployable; ambiguous,
contradictory, late, or unheard speech can produce omissions. Node/note budgets (80/24 by
default), the rolling transcript window, audio loss, and model context limits deliberately
bound long meetings. User-pinned geometry and edits take precedence over later auto-layout.

### Diagramming a meeting that already happened

**Import** builds a board from an exported transcript — no audio, no live capture, no
transcription cost. It reads **WebVTT** (what Teams, Zoom and Google Meet all export),
**SRT**, and plain text, and recognises speaker names from both Teams' `<v Name>` voice
tags and `Name:` prefixes, so action items come out attributed to the right person.

This is also the practical way to get a Teams meeting onto the board: Teams exposes no
API for the live caption stream, but its exported `.vtt` drops straight in.

---

## Configuration

Everything is optional. With empty configuration AudioBoarder signs you in
interactively and picks the best deployment it can find.

To set your own values, **copy `appsettings.Local.json.example` to
`appsettings.Local.json`** next to the built executable and edit it.
`appsettings.Local.json` is git-ignored, so your tenant and subscription identifiers
never reach source control.

```jsonc
{
  "AudioBoarder": {
    "AzureOpenAI": {
      "TenantId": "",           // blank = your default Azure login
      "SubscriptionId": "",
      "DeploymentName": "",     // blank = auto-discover and rank
      "PreferredRegion": "eastus",
      "UseManagedIdentity": true
    },
    "Audio": {
      "CaptureMicrophone": true,
      "CaptureLoopback": true   // false = mic only; the far side won't be captured
    }
  }
}
```

Any setting can also be supplied as an environment variable:

```powershell
$env:AUDIOBOARDER_AudioBoarder__AzureOpenAI__DeploymentName = "my-deployment"
```

### Choosing the model

`FoundryDiscovery` scans every Cognitive Services account in the subscription and ranks
chat deployments by **parsed version**, then capability tier
(`sol` > `pro` > `terra` > `luna` > `chat` > `mini`). Both dotted model names
(`gpt-5.6-sol`) and the dashed deployment names Azure generates for them
(`gpt-5-6-sol`) parse identically, so the newest frontier deployment wins automatically
even when it lives in a different account or region from everything else.

Set `AzureOpenAI.DeploymentName` to pin one explicitly — it is honoured as an exact
match ahead of scoring, and the endpoint follows whichever account hosts it.

The continuous mid-meeting pass uses the *fast* deployment, which resolves to the quick
sibling of the same family (e.g. `gpt-5-6-luna`) rather than an older small model.

### Transcription backends

| `CloudTranscription.Backend` | Behaviour |
|---|---|
| `auto` (default) | Cloud LLM transcription if a deployment exists, else Azure Speech, else local Whisper |
| `speech` | Azure Speech streaming — lowest latency, needs `AzureSpeech.Region` + `ResourceId` |
| `local` / `whisper` | Local Whisper.net, fully offline |

---

## Diagram richness

AudioBoarder aims at the visual language of the
[Azure Architecture Center](https://learn.microsoft.com/azure/architecture/browse/):
named products inside nested boundaries, with a numbered path a reader can follow. Four
mechanisms keep the board from degenerating into identical rectangles:

**Icons.** Every node carries a vector icon drawn inside its shape. `IconRegistry`
resolves one from the label for ~140 known technologies and concepts — Purview, Fabric,
Power BI, Defender, Entra, Copilot, SQL, Kubernetes — falling back to a per-kind default.
Icons are embedded [Lucide](https://lucide.dev) SVG paths (ISC licence), so they render
crisply at any zoom, take the node's own colour, and work fully offline with no network
fetch. Matching is whole-word, so "Staging environment" doesn't pick up a price tag.

### Official Azure icons (optional)

For diagrams of Azure workloads you can have AudioBoarder draw nodes with Microsoft's
real product artwork instead of the generic icons.

The icon set is **not** shipped with AudioBoarder. Microsoft's terms permit copying and
displaying the icons *only* for architectural diagrams, training material and
documentation, so you download the set yourself — which is where you accept those terms
— and point the app at it:

1. Download the SVG icons from
   [Azure architecture icons](https://learn.microsoft.com/azure/architecture/icons/).
2. Extract the archive anywhere.
3. Set the folder in `appsettings.Local.json`:

```json
{ "AudioBoarder": { "Realtime": { "AzureIconsPath": "C:\\azure-icons" } } }
```

Icons are rendered verbatim — never cropped, flipped, rotated or recoloured — per those
terms. If the path is missing or unreadable the app silently uses its bundled icons.

**System boundaries.** Containers nest the way real topologies do — subscription >
virtual network > subnet > resource — each drawn as a labelled box with an optional
qualifier such as an address range. Nesting is what makes a board read as an
architecture rather than a flat bag of components.

**Numbered request paths.** When the discussion walks through a flow, each connection
carries a step number and the board draws the badges, so a reader can follow the path in
order. This mirrors the Dataflow sections in the Azure Architecture Center.

**Labelled interactions.** Connections between distinct things carry a descriptive label
in the speakers' own terms — "classifies data in", "blocks access when", "owns
structured-data implementation of". Only plain parent → child breakdown is left bare.

**Callouts and descriptions.** `NodeKind.Callout` places a one-sentence explanation on
the canvas, linked by an association edge. Any node may also carry a short `description`
rendered under its label.

### Node kinds

`process` `entity` `decision` `data_store` `actor` `note` `system` `technology`
`security` `cloud` `document` `milestone` `risk` `metric` `external` `callout`

Each maps to a distinct shape, colour, stroke weight and default glyph. Synonyms from the
model are coerced by `TolerantEnumConverter`, which never lets a synonym shadow a real
enum member.

---

## Architecture

```
mic ──┐
      ├─ WasapiAudioCaptureSource ─ AudioPipeline ─ IVoiceActivityDetector
loop ─┘                                                    │
                                              ITranscriptionService (resolved lazily)
                                                           │
                                                   TranscriptBuffer (5 min rolling)
                                                           │
                                              DiagramOrchestrator.GenerateAsync
                                                  │                 │
                                       IScenePatchGenerator    ILayoutEngine
                                        /            \              │
                        AzureOpenAIResponses    AzureOpenAIChat     │
                        (gpt-5*/o1*/o3*)        (gpt-4o, 3.5)       │
                                        \            /              │
                                       ScenePatchApplier ───────────┘
                                                  │
                                             SceneGraph  ── SessionStore (autosave)
                                             /        \
                         Live Architecture Canvas   SceneCanvas
                          (custom SVG/WebView2)     (SkiaSharp classic)
```

The model never emits pixel coordinates — it emits a **ScenePatch**, a small DSL of 13
operations. `ScenePatchApplier` applies it transactionally and skips invalid ops rather
than discarding the whole patch, then a layout engine positions anything unplaced.

### Projects

| Project | TFM | Role |
|---|---|---|
| `src/AudioBoarder.Core` | `net10.0` | SceneGraph, ScenePatch DSL, Excalidraw converter, icon registry, interfaces. No dependencies. |
| `src/AudioBoarder.Services` | `net10.0-windows` | WASAPI capture, transcription backends, Azure OpenAI, layout, rendering, Foundry discovery |
| `src/AudioBoarder.App` | `net10.0-windows` | WPF shell, MVVM, health probes, sessions, export, `healthcheck` CLI |
| `tests/AudioBoarder.Tests` | `net10.0-windows` | xUnit + FluentAssertions; test count is reported by the runner |
| `tools/AzureProbe` | `net10.0-windows` | Developer CLI for discovery and live model probes |

Dependency direction is `Core ← Services ← App`. No mock or demo services exist in any
production assembly; test doubles live in `tests/AudioBoarder.Tests/Fakes`.

### The live canvas

The central live surface is the custom **Live Architecture Canvas**, a typography-first
SVG renderer hosted in WebView2 — it is not an embedded Excalidraw editor. Text, semantic
cards, boundaries and bezier branches are rendered from authoritative .NET geometry, with
secondary associations de-emphasised so the structure reads. The bundle lives in
`src/AudioBoarder.App/Assets/web` and is
rebuilt from the Vite source in `src/AudioBoarder.App/web` (see that folder's README).
Drag any node to pin it; pinned nodes keep their position through later layout passes.

**Export Excalidraw** is an editable export only. It emits a real `.excalidraw` document via
`SceneToExcalidrawConverter`, so the file you hand over opens in any Excalidraw instance.

---

## CLI

```powershell
AudioBoarder.exe                            # launch the UI
AudioBoarder.exe healthcheck                # probe audio + transcription + Azure
AudioBoarder.exe healthcheck --llm --image  # add live model calls (slower)
AudioBoarder.exe healthcheck --package      # offline packaged-file/version check
```

| Exit code | Meaning |
|---|---|
| 0 | All probes ready |
| 10 | Audio devices missing |
| 11 | Whisper init failed |
| 12 | Azure auth/discovery failed |
| 13 | `--llm` probe failed |
| 14 | `--image` probe failed |
| 15 | packaged files/version metadata invalid |
| 99 | Unexpected error |

---

## Privacy

- No external product telemetry is sent. Local numeric UI performance EventSource metrics
  are off by default (`Diagnostics.EnableLocalPerformanceTelemetry=false`) and, when
  explicitly enabled, contain durations, revisions, counts and payload byte sizes only.
- **Transcript text and raw model response bodies are not written to default logs.**
  `Diagnostics.VerbosePayloadLogging` is false in shipped defaults; current production
  logging paths do not emit those bodies.
- Default log formatting redacts content-bearing identifiers and omits exception payloads.
  Logs contain component status, latency, counts, model/backend names and safe error
  categories. They rotate daily into `%LOCALAPPDATA%\AudioBoarder\logs`, retain at most
  seven files, and cap each file at approximately 5 MB.
- Sessions autosave to `%LOCALAPPDATA%\AudioBoarder\sessions` as portable plaintext JSON.
  `current.json` contains derived nodes, edges, groups, labels/descriptions, notes/owners,
  intent state, geometry, lifecycle state, generated-image prompts/bytes/metadata, revision
  and save time. It contains no raw audio or raw transcript. The single current session is
  retained until overwritten or deleted; there is no cloud session store.
- Set `Sessions.AutoSave=false` to disable new saves. Run
  `.\scripts\reset-local-data.ps1` and type `DELETE` (or use `-Force` for managed cleanup)
  to remove sessions, logs, update downloads, UI state, auth record, and app-specific token
  cache. The script only targets AudioBoarder-owned paths.
- Audio/transcript/model requests go only to the Azure resources or local models you choose.
  Azure SDK authentication itself communicates with Microsoft identity endpoints.

Meetings often contain other people's words. Check your local recording-consent rules
and organizational policy, notify participants where required, and do not capture meetings
without the necessary consent.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Azure pill red — "Discovery failed: 403" | Not signed in, or no access to a Foundry resource | Run `scripts\setup-azure.ps1` |
| Azure pill red — "No deployments found" | Subscription has no chat deployment | Create one in Azure AI Foundry |
| Captions never appear, `recentPeak=0.000` in the log | Microphone muted at the Windows endpoint level | Unmute the capture device in Sound settings; check any hardware mute switch |
| Far side of the meeting is missing | `Audio.CaptureLoopback` is `false` | Set it to `true` |
| Transcription pill orange for minutes | Whisper model downloading (first run only) | Wait — `base` is ~148 MB |
| Reasoning models take 30–120 s per diagram | Expected for `pro`/`sol` tiers | Pin a faster deployment, or rely on continuous mode |
| Bluetooth headset captures at 8 kHz | Windows HFP downgrade | Use a wired or USB mic |

---

## Development

```powershell
dotnet restore AudioBoarder.sln
dotnet build AudioBoarder.sln -c Release
dotnet test AudioBoarder.sln -c Release --filter "Category!=LiveModel"
cd src\AudioBoarder.App\web
npm ci
npm run build
# Start `npm run preview` in one terminal, then:
node verify.cjs "http://127.0.0.1:5566/"
```

The browser verifier runs the committed custom SVG canvas in headless Edge, the same engine
family used by WebView2. Offline semantic qualification is:

```powershell
dotnet test tests\AudioBoarder.Tests\AudioBoarder.Tests.csproj -c Release `
  --filter "FullyQualifiedName~SemanticReleaseGateTests"
```

Live-model qualification is intentionally separate and opt-in through
`.github/workflows/semantic-live.yml`; ordinary CI never calls Azure models.

### Build release artifacts

Use the single fail-fast pipeline; do not assemble releases by hand:

```powershell
.\scripts\build-release.ps1 -Version 0.8.0-preview.1 -Prerelease -Unsigned -DryRun
```

It restores, builds/verifies the web canvas, builds/tests .NET excluding `LiveModel`, runs
offline semantic and session-schema compatibility gates, scans tracked files, publishes
separate self-contained MSI/portable payloads, builds WiX, creates the ZIP, SPDX SBOM,
third-party notices, source metadata and checksums, then inspects the complete artifact
contract. Signed builds additionally sign and verify every EXE/DLL and the MSI.

See [`docs/RELEASE.md`](docs/RELEASE.md) for signing secrets and release rules and
[`docs/CLEAN-VM-CHECKLIST.md`](docs/CLEAN-VM-CHECKLIST.md) for install/upgrade/uninstall
qualification. `.github/workflows/release-build.yml` only builds and uploads workflow
artifacts; it never creates a tag or GitHub release.

### Known follow-ups

- **SkiaSharp is pinned to 2.88.** Version 4 removes the `SKPaint` text/font members,
  replaces `SKFilterQuality` with `SKSamplingOptions`, and requires `SKPathBuilder` for
  path mutation. `SceneRenderer.cs` has ~18 affected call sites. The upgrade is worth
  doing but needs visual regression checking of the classic renderer and PNG export.
- **FluentAssertions is pinned to 7.x** deliberately. Version 8 moved to the Xceed
  licence, which requires a paid licence for commercial use; 7.x is the last Apache-2.0
  release.
- **WinUI 3 port.** See the note below.

### Why this is still WPF

A WinUI 3 port is a genuine rewrite rather than a retarget: the XAML dialect differs,
`SKElement` becomes `SKXamlCanvas`, the WPF-UI theme library has no direct equivalent,
`SaveFileDialog` becomes `FileSavePicker` with WinRT interop, and the `healthcheck` CLI
path conflicts with WinUI's packaged app model. The parts worth porting first are
already isolated — `Core` is UI-agnostic and `Services` only depends on SkiaSharp — so
the work is confined to `AudioBoarder.App`. It is tracked as a follow-up rather than
bundled into a release.

---

## Credits

Built on [NAudio](https://github.com/naudio/NAudio),
[SkiaSharp](https://github.com/mono/SkiaSharp),
[Excalidraw](https://github.com/excalidraw/excalidraw),
[Whisper.net](https://github.com/sandrohanea/whisper.net),
[MSAGL](https://github.com/microsoft/automatic-graph-layout),
[Serilog](https://serilog.net/),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
the [Azure SDK for .NET](https://github.com/Azure/azure-sdk-for-net),
and [WPF-UI](https://github.com/lepoco/wpfui).

## License

[MIT](LICENSE)
