import "@excalidraw/excalidraw/index.css";
import React from "react";
import { createRoot } from "react-dom/client";
import { Excalidraw } from "@excalidraw/excalidraw";

// Self-host all fonts so the whiteboard works fully offline. Vite copies
// public/ to the build root, so fonts resolve at /fonts relative to origin.
window.EXCALIDRAW_ASSET_PATH = "/";

let excalidrawAPI = null;
let pendingScene = null;

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
  excalidrawAPI.updateScene({
    elements,
    appState: {
      viewBackgroundColor:
        (scene.appState && scene.appState.viewBackgroundColor) || "#ffffff",
    },
  });
  if (elements.length > 0) {
    try {
      excalidrawAPI.scrollToContent(elements, {
        fitToContent: true,
        animate: false,
      });
    } catch (_) {
      /* ignore framing errors */
    }
  }
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
