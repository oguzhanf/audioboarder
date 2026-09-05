# AudioBoarder SVG canvas

This folder contains the custom SVG **Live Architecture Canvas** hosted by the WPF
`ExcalidrawCanvas` control inside WebView2. The control name is historical: Excalidraw is
not loaded here and is not the live surface. .NET supplies semantic scene data, node
centre/size and resolved group bounds; JavaScript renders that authoritative geometry and
handles pan, zoom, accessibility, incremental updates, and drag-to-pin.

Build the committed bundle:

```powershell
.\build-bundle.ps1
```

Verify in headless Edge (the WebView2 engine family):

```powershell
.\verify.ps1
```

No Node.js, dependency install, external server, or visible browser window is required.
The verifier serves only the packaged canvas and fixtures on a temporary .NET loopback
listener and runs an isolated headless Edge process. It checks the actual string-based
WebView2 message protocol, component library/search, drop coordinates, viewport preservation,
supplied geometry, security intent, interaction metadata, keyed identity and pinning.
It does not qualify model semantics; run the .NET
`SemanticReleaseGateTests` for all six intents.

Excalidraw is editable export only. `SceneToExcalidrawConverter` remains the `.excalidraw`
file-export path and consumes the same authoritative geometry. Changes here must not add
network fetches or move semantic/layout/orchestration decisions into JavaScript.
