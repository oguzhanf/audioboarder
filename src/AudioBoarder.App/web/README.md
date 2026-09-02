# AudioBoarder SVG canvas

This folder contains the custom SVG **Live Architecture Canvas** hosted by the WPF
`ExcalidrawCanvas` control inside WebView2. The control name is historical: Excalidraw is
not loaded here and is not the live surface. .NET supplies semantic scene data, node
centre/size and resolved group bounds; JavaScript renders that authoritative geometry and
handles pan, zoom, accessibility, incremental updates, and drag-to-pin.

Build the committed bundle:

```powershell
npm ci
npm run build
```

Verify in headless Edge (the WebView2 engine family):

```powershell
npm run preview
# In a second terminal:
node verify.cjs "http://localhost:5566/"
```

The verifier checks semantic IDs, supplied geometry, groups, edge step/label/protocol/
authentication/classification metadata, keyed element identity, keyboard/drag pinning,
console errors, and a screenshot. It does not qualify model semantics; run the .NET
`SemanticReleaseGateTests` for all six intents.

Excalidraw is editable export only. `SceneToExcalidrawConverter` remains the `.excalidraw`
file-export path and consumes the same authoritative geometry. Changes here must not add
network fetches or move semantic/layout/orchestration decisions into JavaScript.
