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

createRoot(document.getElementById("root")).render(React.createElement(App));
