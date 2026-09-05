/*
  Keyed SVG renderer. Geometry comes from .NET as centre coordinates; this file
  converts centres to SVG top-left coordinates and never performs layout.
*/
const SVG = "http://www.w3.org/2000/svg";
const textMeasure = document.createElement("canvas").getContext("2d");

export function iconDataUrl(svg) {
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg ||
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path fill="none" stroke="#0078d4" stroke-width="2" d="M4 4h16v16H4zM4 10h16M10 10v10"/></svg>');
}

function wrappedLines(text, width, font, maximum) {
  if (!text || maximum < 1) return [];
  textMeasure.font = font;
  const lines = [];
  let line = "";
  for (const word of String(text).trim().split(/\s+/)) {
    const candidate = line ? `${line} ${word}` : word;
    if (line && textMeasure.measureText(candidate).width > width) {
      lines.push(line);
      line = "";
    }
    for (const letter of (line ? ` ${word}` : word)) {
      if (line && textMeasure.measureText(line + letter).width > width) {
        lines.push(line);
        line = "";
      }
      line += letter;
    }
  }
  if (line) lines.push(line);
  if (lines.length > maximum) {
    lines.length = maximum;
    let last = lines[maximum - 1];
    while (last && textMeasure.measureText(last + "\u2026").width > width) last = last.slice(0, -1);
    lines[maximum - 1] = last + "\u2026";
  }
  return lines;
}

function setTextLines(element, lines, x, y, lineHeight) {
  element.replaceChildren();
  lines.forEach((line, index) => {
    const span = el("tspan", { x, y: y + index * lineHeight });
    span.textContent = line;
    element.appendChild(span);
  });
}

const ICONS = {
  box: "M4 4h16v16H4z",
  user: "M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8",
  database: "M12 8c5 0 9-1.3 9-3s-4-3-9-3-9 1.3-9 3 4 3 9 3zM3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3",
  shield: "M20 13c0 5-3.5 7.5-7.7 9a1 1 0 0 1-.7 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.2-2.7a1.2 1.2 0 0 1 1.5 0C14.5 3.8 17 5 19 5a1 1 0 0 1 1 1z",
  cloud: "M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z",
};

const state = {
  svg: null,
  viewport: null,
  groupLayer: null,
  edgeLayer: null,
  nodeLayer: null,
  empty: null,
  groups: new Map(),
  edges: new Map(),
  nodes: new Map(),
};

const el = (name, attrs = {}) => {
  const element = document.createElementNS(SVG, name);
  setAttrs(element, attrs);
  return element;
};

function setAttrs(element, attrs) {
  for (const [name, value] of Object.entries(attrs)) {
    if (value == null) element.removeAttribute(name);
    else element.setAttribute(name, String(value));
  }
}

function ensureStructure(svg) {
  if (state.svg === svg && state.viewport?.isConnected) return;
  svg.replaceChildren();
  state.svg = svg;
  state.groups.clear();
  state.edges.clear();
  state.nodes.clear();

  const defs = el("defs");
  const marker = el("marker", {
    id: "arrow", viewBox: "0 0 10 10", refX: 9, refY: 5,
    markerWidth: 6, markerHeight: 6, orient: "auto",
  });
  marker.appendChild(el("path", { class: "arrow-head", d: "M0 1L9 5L0 9z" }));
  defs.appendChild(marker);
  svg.appendChild(defs);

  state.viewport = el("g", { id: "viewport" });
  state.groupLayer = el("g", { "data-layer": "groups" });
  state.edgeLayer = el("g", { "data-layer": "edges" });
  state.nodeLayer = el("g", { "data-layer": "nodes" });
  state.viewport.append(state.groupLayer, state.edgeLayer, state.nodeLayer);
  svg.appendChild(state.viewport);
  state.empty = el("text", { class: "empty-hint" });
  state.empty.textContent = "Listening — the map will draw itself as people talk.";
  svg.appendChild(state.empty);
}

function sync(map, items, layer, create, update) {
  const live = new Set(items.map((item) => item.id));
  for (const [id, element] of map) {
    if (!live.has(id)) {
      element.remove();
      map.delete(id);
    }
  }
  for (const item of items) {
    let element = map.get(item.id);
    const isNew = !element;
    if (!element) {
      element = create(item);
      map.set(item.id, element);
      layer.appendChild(element);
    }
    update(element, item);
    if (isNew && item.lifecycle === "provisional") {
      element.classList.add("entering");
      setTimeout(() => element.classList.remove("entering"), 900);
    }
  }
}

const left = (item) => item.centerX - item.width / 2;
const top = (item) => item.centerY - item.height / 2;

function lifecycleClass(prefix, item, extra = "") {
  return [prefix, extra, item.lifecycle && `lifecycle-${item.lifecycle}`]
    .filter(Boolean).join(" ");
}

const boundaryText = (kind) => ({
  tenant: "Tenant boundary",
  network: "Network boundary",
  trust_zone: "Trust zone",
  cloud_scope: "Cloud scope",
  system: "System boundary",
  environment: "Environment boundary",
  external: "External boundary",
  generic: "Boundary",
}[kind] || "Boundary");

function createGroup(item) {
  const group = el("g", { "data-id": item.id });
  group.append(el("rect"), el("text"), el("text"), el("text"));
  return group;
}

function updateGroup(group, item) {
  setAttrs(group, {
    class: lifecycleClass("container", item, `depth-${Math.min(item.depth || 0, 3)}`),
    "data-id": item.id,
    "data-boundary-kind": item.boundaryKind || "generic",
  });
  const [rect, icon, name, subtitle] = group.children;
  setAttrs(rect, {
    class: "container-box", x: left(item), y: top(item),
    width: item.width, height: item.height, rx: 10,
  });
  setAttrs(icon, { class: "container-kind", x: left(item) + 14, y: top(item) + 19 });
  icon.textContent = `▣ ${boundaryText(item.boundaryKind)}`;
  setAttrs(name, { class: "container-name", x: left(item) + 14, y: top(item) + 38 });
  name.textContent = item.label || "";
  setAttrs(subtitle, { class: "container-subtitle", x: left(item) + 14, y: top(item) + 55 });
  subtitle.textContent = item.subtitle || "";
}

function routePath(a, b) {
  const ax = a.centerX + a.width / 2;
  const ay = a.centerY;
  const bx = b.centerX - b.width / 2;
  const by = b.centerY;
  if (bx >= ax + 20) {
    const mid = (ax + bx) / 2;
    return { d: `M${ax} ${ay}H${mid}V${by}H${bx}`, mx: mid, my: (ay + by) / 2 };
  }
  const drop = Math.max(a.centerY + a.height / 2, b.centerY + b.height / 2) + 28;
  return {
    d: `M${a.centerX} ${a.centerY + a.height / 2}V${drop}H${b.centerX}V${b.centerY + b.height / 2}`,
    mx: (a.centerX + b.centerX) / 2,
    my: drop,
  };
}

function createEdge(item) {
  const group = el("g", { "data-id": item.id });
  group.append(el("path"), el("circle"), el("text"), el("rect"), el("text"), el("text"));
  return group;
}

function edgeMetadata(item) {
  return [
    item.protocol,
    item.payload,
    item.authentication && `auth: ${item.authentication}`,
    item.dataClassification && `class: ${item.dataClassification}`,
    item.interactionMode?.replaceAll("_", " "),
  ].filter(Boolean).join(" · ");
}

function updateEdge(group, item, byId, intent) {
  const from = byId.get(item.from);
  const to = byId.get(item.to);
  if (!from || !to) {
    group.hidden = true;
    return;
  }
  group.hidden = false;
  setAttrs(group, {
    class: lifecycleClass(
      "edge-item",
      item,
      intent === "security_zero_trust_architecture" && from.group !== to.group
        ? "boundary-crossing" : ""),
    "data-id": item.id,
  });
  const [path, badge, step, plate, label, metadata] = group.children;
  const route = routePath(from, to);
  setAttrs(path, {
    class: `edge ${["dependency", "association"].includes(item.kind) ? "soft" : ""}`,
    d: route.d, "marker-end": "url(#arrow)",
  });

  const hasStep = Number.isInteger(item.step) && item.step > 0;
  setAttrs(badge, {
    class: "step-badge", cx: route.mx, cy: route.my, r: 11,
    display: hasStep ? null : "none",
  });
  setAttrs(step, {
    class: "step-num", x: route.mx, y: route.my,
    display: hasStep ? null : "none",
  });
  step.textContent = hasStep ? String(item.step) : "";

  const labelX = route.mx + (hasStep ? 18 : 0);
  const labelY = route.my - 13;
  const labelText = item.label || "";
  const plateWidth = Math.max(0, labelText.length * 6.5 + 12);
  setAttrs(plate, {
    class: "edge-label-bg", x: labelX - 5, y: labelY - 9,
    width: plateWidth, height: 18, rx: 4,
    display: labelText ? null : "none",
  });
  setAttrs(label, { class: "edge-label", x: labelX, y: labelY });
  label.textContent = labelText;
  setAttrs(metadata, { class: "edge-metadata", x: labelX, y: route.my + 14 });
  metadata.textContent = edgeMetadata(item);
}

function iconName(item) {
  if (item.kind === "actor" || item.kind === "identity") return "user";
  if (item.kind === "data_store") return "database";
  if (item.kind === "security" || item.kind === "risk") return "shield";
  if (item.kind === "cloud") return "cloud";
  return "box";
}

function createNode(item) {
  const group = el("g", { "data-id": item.id });
  group.append(el("rect"), el("g"), el("text"), el("text"), el("circle"), el("title"));
  return group;
}

function updateNode(group, item) {
  const x = left(item);
  const y = top(item);
  setAttrs(group, {
    class: lifecycleClass("node", item, item.locked ? "pinned" : ""),
    "data-id": item.id,
    tabindex: 0,
    role: "button",
    "aria-label": `${item.label || "Node"}. ${item.locked ? "Pinned" : "Unpinned"}. Press Enter to toggle.`,
    "data-center-x": item.centerX,
    "data-center-y": item.centerY,
    "data-width": item.width,
    "data-height": item.height,
  });
  const [card, icon, label, desc, pin, title] = group.children;
  setAttrs(card, { class: "node-card", x, y, width: item.width, height: item.height, rx: 8 });
  icon.replaceChildren();
  if (item.svg) {
    setAttrs(icon, { class: "node-art", transform: `translate(${x + 12},${item.centerY - 20})` });
    icon.appendChild(el("rect", { width: 40, height: 40, rx: 6, fill: "#fff" }));
    icon.appendChild(el("image", { href: iconDataUrl(item.svg), x: 4, y: 4,
      width: 32, height: 32, preserveAspectRatio: "xMidYMid meet" }));
  } else {
    setAttrs(icon, { class: "node-glyph", transform: `translate(${x + 12},${item.centerY - 11}) scale(.92)` });
    icon.appendChild(el("path", { class: "node-icon", d: ICONS[iconName(item)] }));
  }
  const textX = x + 64;
  const textWidth = Math.max(12, item.width - 78);
  const availableHeight = Math.max(16, item.height - 24);
  setAttrs(label, { class: "node-label" });
  setAttrs(desc, { class: "node-desc" });
  const labelStyle = getComputedStyle(label);
  const descStyle = getComputedStyle(desc);
  const labelLines = wrappedLines(item.label, textWidth,
    `${labelStyle.fontWeight} ${labelStyle.fontSize} ${labelStyle.fontFamily}`,
    Math.min(2, Math.floor(availableHeight / 16)));
  const descLines = wrappedLines(item.desc, textWidth,
    `${descStyle.fontWeight} ${descStyle.fontSize} ${descStyle.fontFamily}`,
    Math.min(3, Math.floor((availableHeight - labelLines.length * 16 - 4) / 14)));
  const textHeight = labelLines.length * 16 + (descLines.length ? 4 + descLines.length * 14 : 0);
  const textTop = item.centerY - textHeight / 2;
  setTextLines(label, labelLines, textX, textTop + 8, 16);
  setTextLines(desc, descLines, textX, textTop + labelLines.length * 16 + 11, 14);
  setAttrs(pin, { class: "node-pin", cx: x + item.width - 9, cy: y + 9, r: 3 });
  title.textContent = `${item.desc ? `${item.label} — ${item.desc}` : item.label || ""}. ` +
    `${item.locked ? "Pinned" : "Unpinned"}; double-click or press Enter to toggle.`;
}

export function renderScene(svg, scene, view) {
  ensureStructure(svg);
  const nodes = scene.nodes || [];
  const edges = scene.edges || [];
  const groups = scene.groups || [];
  const byId = new Map(nodes.map((node) => [node.id, node]));

  sync(state.groups, groups, state.groupLayer, createGroup, updateGroup);
  sync(state.edges, edges, state.edgeLayer, createEdge,
    (element, item) => updateEdge(element, item, byId, scene.intent));
  sync(state.nodes, nodes, state.nodeLayer, createNode, updateNode);

  state.empty.setAttribute("x", svg.clientWidth / 2);
  state.empty.setAttribute("y", svg.clientHeight / 2);
  state.empty.style.display = nodes.length === 0 ? "" : "none";
  state.viewport.setAttribute("transform", `translate(${view.x},${view.y}) scale(${view.k})`);
  return { bounds: bounds(nodes, groups) };
}

export function bounds(nodes, groups = []) {
  const boxes = [...nodes, ...groups]
    .filter((item) => Number.isFinite(item.centerX) && Number.isFinite(item.centerY) &&
      Number.isFinite(item.width) && Number.isFinite(item.height))
    .map((item) => ({
      x: left(item), y: top(item), w: item.width, h: item.height,
    }));
  if (boxes.length === 0) return { x: 0, y: 0, w: 0, h: 0 };
  const minX = Math.min(...boxes.map((box) => box.x));
  const minY = Math.min(...boxes.map((box) => box.y));
  const maxX = Math.max(...boxes.map((box) => box.x + box.w));
  const maxY = Math.max(...boxes.map((box) => box.y + box.h));
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}
