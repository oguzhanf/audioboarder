# AudioBoarder

> Listens to a meeting on Windows and draws it — live — as an annotated Excalidraw
> whiteboard, with structured notes and on-demand illustrations.

AudioBoarder captures your microphone and (optionally) system audio, transcribes the
conversation, and asks an Azure OpenAI model to turn what it hears into a diagram:
technologies carrying icons, systems drawn as labelled boundaries, arrows that say what
actually flows between things, and callouts explaining the subtle parts. Decisions,
action items, risks and open questions are collected in a side panel. Everything can be
exported as a `.excalidraw` file you can keep editing, or as a PNG.

Built on WPF + SkiaSharp + WebView2 + a vendored offline [Excalidraw](https://github.com/excalidraw/excalidraw) bundle.

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

Release binaries are currently unsigned, so Windows SmartScreen may ask you to confirm
the download before installation. SHA-256 checksums are published with every release.

MSI installations check the repository's latest GitHub release when AudioBoarder starts.
When a newer version is available, the app shows its release notes, downloads and verifies
the installer using GitHub's SHA-256 digest, installs the update, and restarts automatically.
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
                              ExcalidrawCanvas      SceneCanvas
                              (WebView2, default)   (SkiaSharp classic)
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
| `tests/AudioBoarder.Tests` | `net10.0-windows` | xUnit + FluentAssertions — 158 tests |
| `tools/AzureProbe` | `net10.0-windows` | Developer CLI for discovery and live model probes |

Dependency direction is `Core ← Services ← App`. No mock or demo services exist in any
production assembly; test doubles live in `tests/AudioBoarder.Tests/Fakes`.

### The live canvas

The central canvas is a typography-first SVG renderer hosted in WebView2 — text and thin
bezier branches rather than boxes, with secondary associations de-emphasised so the
structure reads. The bundle lives in `src/AudioBoarder.App/Assets/web` (~14 KB) and is
rebuilt from the Vite source in `src/AudioBoarder.App/web` (see that folder's README).
Drag any node to pin it; pinned nodes keep their position through later layout passes.

**Export Excalidraw** is unchanged and still emits a real `.excalidraw` document via
`SceneToExcalidrawConverter`, so the file you hand over opens in any Excalidraw instance.

---

## CLI

```powershell
AudioBoarder.exe                            # launch the UI
AudioBoarder.exe healthcheck                # probe audio + transcription + Azure
AudioBoarder.exe healthcheck --llm --image  # add live model calls (slower)
```

| Exit code | Meaning |
|---|---|
| 0 | All probes ready |
| 10 | Audio devices missing |
| 11 | Whisper init failed |
| 12 | Azure auth/discovery failed |
| 13 | `--llm` probe failed |
| 99 | Unexpected error |

---

## Privacy

- **Transcripts, prompts and model responses are never written to disk** unless you
  explicitly set `Diagnostics.VerbosePayloadLogging: true`.
- Logs contain component status, latency, model names and error categories only.
- Logs rotate daily into `%LOCALAPPDATA%\AudioBoarder\logs` with 7-day retention.
- Sessions autosave to `%LOCALAPPDATA%\AudioBoarder\sessions`.
- Audio is streamed to your own Azure resources. Nothing is sent anywhere else.

Meetings often contain other people's words. Check your local recording-consent rules
before using this.

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
dotnet build                                   # build
dotnet test                                    # 158 tests
cd src\AudioBoarder.App\web; npm ci; npm run build   # rebuild the Excalidraw bundle
```

### Build the Windows installer

```powershell
dotnet publish src\AudioBoarder.App\AudioBoarder.App.csproj `
  -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64

$publishDir = (Resolve-Path artifacts\publish\win-x64).Path
dotnet build installer\AudioBoarder.Installer.wixproj -c Release `
  -p:PublishDir="$publishDir"
```

For a GitHub release, publish the MSI, portable ZIP, and `SHA256SUMS.txt` together
under the matching version tag.

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
