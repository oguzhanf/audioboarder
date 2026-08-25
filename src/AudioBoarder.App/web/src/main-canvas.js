/*
  WebView2 entry point for the AudioBoarder canvas.

  Host contract is unchanged from the Excalidraw build:
    host -> js : postMessage(json)  /  window.loadScene(json)
    js -> host : { type: "ready" | "error" }
*/
import "./canvas.css";
import { renderScene, bounds } from "./canvas.js";

const svg = document.getElementById("canvas");
const stage = document.getElementById("stage");
const zoomLabel = document.getElementById("zoomLevel");

let scene = { nodes: [], edges: [], groups: [] };
let view = { x: 120, y: 120, k: 1 };
// Auto-fit keeps the growing diagram in frame during a live meeting, and stops
// the moment the user pans or zooms — snatching the view back mid-sentence would
// be worse than a badly framed board.
let userTookControl = false;

function postToHost(message) {
  try {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
  } catch (_) {
    /* not hosted in WebView2 (test harness) */
  }
}

function applyView() {
  const vp = document.getElementById("viewport");
  if (vp) vp.setAttribute("transform", `translate(${view.x},${view.y}) scale(${view.k})`);
  if (zoomLabel) zoomLabel.textContent = Math.round(view.k * 100) + "%";
}

function draw() {
  renderScene(svg, scene, view);
  applyView();
}

/** Keeps the diagram framed as it grows, until the user takes manual control. */
function autoFit(force = false) {
  if ((userTookControl && !force) || !scene.nodes || scene.nodes.length === 0) return;
  const b = bounds(scene.nodes);
  if (b.w <= 0 || b.h <= 0) return;
  const pad = 80;
  const k = Math.min(
    1.15,
    Math.max(0.25, Math.min(
      (svg.clientWidth - pad * 2) / b.w,
      (svg.clientHeight - pad * 2) / b.h)));
  view.k = k;
  view.x = (svg.clientWidth - b.w * k) / 2 - b.x * k;
  view.y = (svg.clientHeight - b.h * k) / 2 - b.y * k;
  applyView();
}

window.loadScene = function (json) {
  try {
    const next = typeof json === "string" ? JSON.parse(json) : json;
    if (!next) return;
    scene = {
      nodes: next.nodes || [],
      edges: next.edges || [],
      groups: next.groups || [],
    };
    if (scene.nodes.length === 0) userTookControl = false;
    draw();
    autoFit();
  } catch (e) {
    console.error("loadScene failed", e);
    postToHost({ type: "error", message: String(e) });
  }
};

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

// ---- pan + zoom ------------------------------------------------------------
stage.addEventListener("wheel", (ev) => {
  ev.preventDefault();
  userTookControl = true;
  const prev = view.k;
  const next = Math.min(3, Math.max(0.2, prev * (ev.deltaY < 0 ? 1.1 : 1 / 1.1)));
  // keep the point under the cursor fixed
  view.x = ev.clientX - (ev.clientX - view.x) * (next / prev);
  view.y = ev.clientY - (ev.clientY - view.y) * (next / prev);
  view.k = next;
  applyView();
}, { passive: false });

let drag = null;
stage.addEventListener("pointerdown", (ev) => {
  drag = { x: ev.clientX - view.x, y: ev.clientY - view.y };
  stage.classList.add("panning");
  stage.setPointerCapture(ev.pointerId);
});
stage.addEventListener("pointermove", (ev) => {
  if (!drag) return;
  userTookControl = true;
  view.x = ev.clientX - drag.x;
  view.y = ev.clientY - drag.y;
  applyView();
});
const endDrag = () => { drag = null; stage.classList.remove("panning"); };
stage.addEventListener("pointerup", endDrag);
stage.addEventListener("pointercancel", endDrag);

document.getElementById("zoomIn").onclick = () => {
  userTookControl = true; view.k = Math.min(3, view.k * 1.15); applyView();
};
document.getElementById("zoomOut").onclick = () => {
  userTookControl = true; view.k = Math.max(0.2, view.k / 1.15); applyView();
};
// "Fit" also hands auto-framing back to the app.
document.getElementById("zoomFit").onclick = () => { userTookControl = false; autoFit(true); };

window.addEventListener("resize", () => autoFit());

draw();
postToHost({ type: "ready" });
