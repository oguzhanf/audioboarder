import "@excalidraw/excalidraw/index.css";
import React from "react";
import { createRoot } from "react-dom/client";
import { Excalidraw } from "@excalidraw/excalidraw";

// Self-host all fonts so the whiteboard works fully offline. Vite copies
// public/ to the build root, so fonts resolve at /fonts relative to origin.
window.EXCALIDRAW_ASSET_PATH = "/";

let excalidrawAPI = null;
let pendingScene = null;
let pendingSceneChange = null;
let sceneChangeTimer = 0;
let lastSentSceneSignature = null;
let suppressSceneMessages = false;
let appliedSceneRevision = null;
let hasFramedNonEmptyScene = false;

function postToHost(message) {
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  } catch (_) {
    /* not hosted in WebView2 (e.g. test harness) */
  }
}

function applyScene(scene) {
  if (!scene) return;
  if (!excalidrawAPI) {
    pendingScene = scene;
    return;
  }

  const elements = Array.isArray(scene.elements) ? scene.elements : [];
  if (sceneChangeTimer !== 0) {
    window.clearTimeout(sceneChangeTimer);
    sceneChangeTimer = 0;
  }
  pendingSceneChange = null;
  appliedSceneRevision = Number.isInteger(scene.sceneRevision)
    ? scene.sceneRevision
    : null;

  suppressSceneMessages = true;

  excalidrawAPI.updateScene({
    elements,
    appState: {
      viewBackgroundColor:
        (scene.appState && scene.appState.viewBackgroundColor) || "#ffffff",
    },
  });

  // Register icon blobs AFTER the scene, not before: addFiles caches only images
  // referenced by elements already in the scene, so calling it first leaves the
  // first frame blank until a throttled refresh eventually repaints.
  if (scene.files) {
    const files = Object.values(scene.files).filter(
      (f) => f && f.id && f.dataURL
    );
    if (files.length > 0) {
      try {
        excalidrawAPI.addFiles(files);
      } catch (e) {
        console.error("addFiles failed", e);
      }
    }
  }

  if (elements.length === 0) {
    hasFramedNonEmptyScene = false;
  } else if (!hasFramedNonEmptyScene) {
    try {
      excalidrawAPI.scrollToContent(elements, {
        fitToContent: true,
        animate: false,
      });
      hasFramedNonEmptyScene = true;
    } catch (_) {
      /* ignore framing errors */
    }
  }

  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      suppressSceneMessages = false;
    });
  });
}

// Public API the C# host (or a test harness) calls to push a scene.
window.loadScene = function (json) {
  try {
    const scene = typeof json === "string" ? JSON.parse(json) : json;
    applyScene(scene);
  } catch (e) {
    console.error("loadScene failed", e);
    postToHost({ type: "error", message: String(e) });
  }
};

// Receive scenes pushed from the WebView2 host as a JSON string.
try {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", (e) => {
      const data = e.data;
      if (typeof data === "string") window.loadScene(data);
      else if (data && data.type === "scene") window.loadScene(data.payload);
    });
  }
} catch (_) {
  /* ignore */
}

// Excalidraw's default is wheel = pan, ctrl+wheel = zoom. On a whiteboard that
// updates itself while you talk, zoom is the gesture you reach for constantly,
// so invert it: plain wheel zooms at the cursor, and panning stays available on
// shift+wheel, space-drag, middle-drag and the hand tool.
//
// Rather than driving appState.zoom ourselves (which would also require
// recomputing scrollX/scrollY to keep the point under the cursor fixed), we
// re-dispatch the event with ctrlKey set and let Excalidraw do its own
// zoom-at-cursor maths. The synthetic event carries ctrlKey, so the guard below
// short-circuits it and there is no recursion.
function installWheelZoom(container) {
  if (!container) return;
  container.addEventListener(
    "wheel",
    (e) => {
      if (e.ctrlKey || e.metaKey) return; // already a zoom gesture
      if (e.shiftKey) return;             // keep shift+wheel as horizontal pan
      e.preventDefault();
      e.stopPropagation();
      e.target.dispatchEvent(
        new WheelEvent("wheel", {
          deltaX: 0,
          deltaY: e.deltaY,
          deltaMode: e.deltaMode,
          clientX: e.clientX,
          clientY: e.clientY,
          ctrlKey: true,
          bubbles: true,
          cancelable: true,
        })
      );
    },
    { capture: true, passive: false }
  );
}

function serializeElement(element) {
  return {
    id: element.id,
    type: element.type,
    x: element.x,
    y: element.y,
    width: element.width,
    height: element.height,
    locked: Boolean(element.locked),
    isDeleted: Boolean(element.isDeleted),
    frameId: element.frameId || null,
    containerId: element.containerId || null,
  };
}

function serializeAppState(appState) {
  return {
    viewBackgroundColor: appState.viewBackgroundColor || "#ffffff",
    theme: appState.theme || null,
    zenModeEnabled:
      typeof appState.zenModeEnabled === "boolean"
        ? appState.zenModeEnabled
        : null,
  };
}

function serializeViewport(appState) {
  return {
    scrollX: typeof appState.scrollX === "number" ? appState.scrollX : 0,
    scrollY: typeof appState.scrollY === "number" ? appState.scrollY : 0,
    zoom:
      appState.zoom && typeof appState.zoom.value === "number"
        ? appState.zoom.value
        : 1,
    width: typeof appState.width === "number" ? appState.width : null,
    height: typeof appState.height === "number" ? appState.height : null,
  };
}

function flushSceneChange() {
  sceneChangeTimer = 0;
  if (!pendingSceneChange) return;
  if (suppressSceneMessages) {
    sceneChangeTimer = window.setTimeout(flushSceneChange, 120);
    return;
  }

  const signature = JSON.stringify(pendingSceneChange);
  if (signature === lastSentSceneSignature) {
    pendingSceneChange = null;
    return;
  }

  lastSentSceneSignature = signature;
  postToHost(pendingSceneChange);
  pendingSceneChange = null;
}

function queueSceneChange(elements, appState) {
  if (suppressSceneMessages) return;

  pendingSceneChange = {
    type: "scene-change",
    sceneRevision: appliedSceneRevision,
    elements: elements.map(serializeElement),
    appState: serializeAppState(appState),
    viewport: serializeViewport(appState),
  };

  if (sceneChangeTimer !== 0) return;
  sceneChangeTimer = window.setTimeout(flushSceneChange, 180);
}

function App() {
  return React.createElement(Excalidraw, {
    excalidrawAPI: (api) => {
      excalidrawAPI = api;
      window.__excalidrawAPI = api;
      if (pendingScene) {
        applyScene(pendingScene);
        pendingScene = null;
      }
      postToHost({ type: "ready" });
    },
    onChange: (elements, appState) => {
      try {
        queueSceneChange(elements, appState);
      } catch (e) {
        console.error("scene-change failed", e);
        postToHost({ type: "error", message: `scene-change failed: ${String(e)}` });
      }
    },
    UIOptions: {
      canvasActions: {
        loadScene: true,
        export: { saveFileToDisk: true },
        saveToActiveFile: false,
      },
    },
  });
}

const container = document.getElementById("root");
createRoot(container).render(React.createElement(App));
installWheelZoom(container);
