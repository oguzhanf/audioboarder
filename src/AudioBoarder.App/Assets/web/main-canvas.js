/*
  WebView2 entry point for the AudioBoarder canvas.

  Host contract:
    host -> js : postMessage(json)  /  window.loadScene(json)
    js -> host : { type: "ready" | "error" }
*/
import { renderScene, bounds } from "./canvas.js";

const svg = document.getElementById("canvas");
const stage = document.getElementById("stage");
const zoomLabel = document.getElementById("zoomLevel");
const componentSearch = document.getElementById("componentSearch");
const componentResults = document.getElementById("componentResults");
const componentSource = document.getElementById("componentSource");

let scene = { nodes: [], edges: [], groups: [] };
let components = [];
let sceneRevision = null;
let view = { x: 120, y: 120, k: 1 };
// Auto-fit keeps the growing diagram in frame during a live meeting, and stops
// the moment the user pans or zooms — snatching the view back mid-sentence would
// be worse than a badly framed board.
let userTookControl = false;
let lastLayout = null;

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

function stagePoint(ev) {
  const rect = stage.getBoundingClientRect();
  return { x: ev.clientX - rect.left, y: ev.clientY - rect.top };
}

function draw() {
  lastLayout = renderScene(svg, scene, view);
  applyView();
}

function renderComponentLibrary() {
  if (!componentResults) return;
  const terms = (componentSearch?.value || "").toLocaleLowerCase().trim()
    .split(/\s+/).filter(Boolean);
  const matches = components.filter((item) => {
    const text = [item.name, item.category, item.description, ...(item.aliases || [])]
      .join(" ").toLocaleLowerCase();
    return terms.every((term) => text.includes(term));
  }).slice(0, 80);

  componentResults.replaceChildren();
  let category = null;
  for (const item of matches) {
    if (item.category !== category) {
      category = item.category;
      const heading = document.createElement("div");
      heading.className = "component-category";
      heading.textContent = category;
      componentResults.appendChild(heading);
    }
    const entry = document.createElement("div");
    entry.className = "component-item";
    entry.draggable = true;
    entry.tabIndex = 0;
    entry.dataset.componentId = item.id;
    const name = document.createElement("strong");
    name.textContent = item.name;
    const description = document.createElement("span");
    description.textContent = item.description;
    entry.append(name, description);
    entry.addEventListener("dragstart", (event) => {
      event.dataTransfer.setData("application/x-audioboarder-component", item.id);
      event.dataTransfer.effectAllowed = "copy";
    });
    componentResults.appendChild(entry);
  }
}

/** Keeps the diagram framed as it grows, until the user takes manual control. */
function autoFit(force = false) {
  if ((userTookControl && !force) || !scene.nodes || scene.nodes.length === 0) return;
  // Bounds must include containers: a boundary extends well past its members, so
  // fitting to nodes alone crops the outermost box.
  const b = lastLayout?.bounds ?? bounds(scene.nodes);
  if (!b || b.w <= 0 || b.h <= 0) return;
  const pad = 70;
  const raw = Math.min(
    (svg.clientWidth - pad * 2) / b.w,
    (svg.clientHeight - pad * 2) / b.h);

  // Never shrink below readable. A board you have to scroll beats one you cannot
  // read at all — which is what fitting a wide tree into the panel produces.
  const MIN_READABLE = 0.55;
  const k = Math.min(1.15, Math.max(MIN_READABLE, raw));
  view.k = k;

  if (raw >= MIN_READABLE) {
    // Fits: centre it.
    view.x = (svg.clientWidth - b.w * k) / 2 - b.x * k;
    view.y = (svg.clientHeight - b.h * k) / 2 - b.y * k;
  } else {
    // Too wide to fit legibly — anchor at the root so the newest branches grow
    // into view rather than the whole thing shrinking away.
    view.x = pad - b.x * k;
    view.y = (svg.clientHeight - b.h * k) / 2 - b.y * k;
    if (b.h * k > svg.clientHeight - pad * 2) view.y = pad - b.y * k;
  }
  applyView();
}

window.loadScene = function (json) {
  try {
    const next = typeof json === "string" ? JSON.parse(json) : json;
    if (!next) return;
    sceneRevision = Number.isInteger(next.sceneRevision) ? next.sceneRevision : null;
    scene = {
      intent: next.intent,
      nodes: (next.nodes || []).map((n) => ({ ...n })),
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

if (window.chrome?.webview) {
  window.chrome.webview.addEventListener("message", (e) => {
    try {
      const data = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
      if (data?.type === "theme") {
        applyTheme(data.theme);
      } else if (data?.type === "component-library") {
        components = Array.isArray(data.components) ? data.components : [];
        if (componentSource) componentSource.href = data.source || "#";
        renderComponentLibrary();
      } else {
        window.loadScene(data?.type === "scene" ? data.payload : data);
      }
    } catch (error) {
      console.error("Host message failed", error);
      postToHost({ type: "error", message: String(error) });
    }
  });
}

/** Follows the app's theme; the WebView cannot see it otherwise. */
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
}

// ---- pan + zoom ------------------------------------------------------------
stage.addEventListener("wheel", (ev) => {
  ev.preventDefault();
  userTookControl = true;
  const point = stagePoint(ev);
  const prev = view.k;
  const next = Math.min(3, Math.max(0.2, prev * (ev.deltaY < 0 ? 1.1 : 1 / 1.1)));
  // keep the point under the cursor fixed
  view.x = point.x - (point.x - view.x) * (next / prev);
  view.y = point.y - (point.y - view.y) * (next / prev);
  view.k = next;
  applyView();
}, { passive: false });

let drag = null;
let nodeDrag = null;

/** Node under the pointer, if any — drives drag-to-pin. */
function hitNode(ev) {
  const g = ev.target.closest ? ev.target.closest(".node") : null;
  if (!g) return null;
  return scene.nodes.find((n) => n.id === g.getAttribute("data-id")) || null;
}

function publishNodeLock(node) {
  postToHost({
    type: "scene-change",
    sceneRevision,
    elements: [{
      id: node.id,
      type: "rectangle",
      x: node.centerX - node.width / 2,
      y: node.centerY - node.height / 2,
      width: node.width,
      height: node.height,
      locked: node.locked,
      isDeleted: false,
    }],
  });
}

function toggleNodeLock(node) {
  node.locked = !node.locked;
  userTookControl = true;
  draw();
  publishNodeLock(node);
}

stage.addEventListener("pointerdown", (ev) => {
  const point = stagePoint(ev);
  const node = hitNode(ev);
  if (node) {
    // Dragging a node pins it: the layout stops moving it on later passes.
    nodeDrag = {
      node,
      dx: node.centerX - (point.x - view.x) / view.k,
      dy: node.centerY - (point.y - view.y) / view.k,
    };
    stage.setPointerCapture(ev.pointerId);
    return;
  }
  drag = { x: point.x - view.x, y: point.y - view.y };
  stage.classList.add("panning");
  stage.setPointerCapture(ev.pointerId);
});

stage.addEventListener("pointermove", (ev) => {
  const point = stagePoint(ev);
  if (nodeDrag) {
    userTookControl = true;
    const n = nodeDrag.node;
    n.centerX = (point.x - view.x) / view.k + nodeDrag.dx;
    n.centerY = (point.y - view.y) / view.k + nodeDrag.dy;
    n.locked = true;
    draw();
    return;
  }
  if (!drag) return;
  userTookControl = true;
  view.x = point.x - drag.x;
  view.y = point.y - drag.y;
  applyView();
});

function endDrag() {
  if (nodeDrag) {
    const n = nodeDrag.node;
    publishNodeLock(n);
    nodeDrag = null;
  }
  drag = null;
  stage.classList.remove("panning");
}
stage.addEventListener("pointerup", endDrag);
stage.addEventListener("pointercancel", endDrag);
stage.addEventListener("dragover", (ev) => {
  if (!ev.dataTransfer.types.includes("application/x-audioboarder-component")) return;
  ev.preventDefault();
  ev.dataTransfer.dropEffect = "copy";
  stage.classList.add("drop-target");
});
stage.addEventListener("dragleave", () => stage.classList.remove("drop-target"));
stage.addEventListener("drop", (ev) => {
  ev.preventDefault();
  stage.classList.remove("drop-target");
  const componentId = ev.dataTransfer.getData("application/x-audioboarder-component");
  if (!componentId) return;
  userTookControl = true;
  const point = stagePoint(ev);
  postToHost({
    type: "component-drop",
    componentId,
    x: (point.x - view.x) / view.k,
    y: (point.y - view.y) / view.k,
  });
});
stage.addEventListener("dblclick", (ev) => {
  const node = hitNode(ev);
  if (node) toggleNodeLock(node);
});
stage.addEventListener("keydown", (ev) => {
  if (ev.key !== "Enter" && ev.key !== " ") return;
  const node = hitNode(ev);
  if (!node) return;
  ev.preventDefault();
  toggleNodeLock(node);
});

document.getElementById("zoomIn").onclick = () => {
  userTookControl = true; view.k = Math.min(3, view.k * 1.15); applyView();
};
document.getElementById("zoomOut").onclick = () => {
  userTookControl = true; view.k = Math.max(0.2, view.k / 1.15); applyView();
};
// "Fit" also hands auto-framing back to the app.
document.getElementById("zoomFit").onclick = () => { userTookControl = false; autoFit(true); };
if (componentSearch) componentSearch.addEventListener("input", renderComponentLibrary);
document.getElementById("libraryToggle").onclick = () => {
  document.body.classList.toggle("library-collapsed");
  const collapsed = document.body.classList.contains("library-collapsed");
  document.getElementById("libraryToggle").innerHTML = collapsed ? "&rsaquo;" : "&lsaquo;";
  setTimeout(() => { draw(); autoFit(); }, 0);
};

window.addEventListener("resize", () => {
  // Redraw as well as refit: the empty-state hint is centred on the SVG at draw
  // time, so a resize (or a side panel opening) would otherwise leave it offset.
  draw();
  autoFit();
});

draw();
postToHost({ type: "ready" });
