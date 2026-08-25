/*
  AudioBoarder canvas — typography-first renderer.

  Replaces Excalidraw. Excalidraw is deliberately a hand-drawn *sketch* tool; its
  rough strokes and boxes are what made generated boards look like doodles. This
  renders the same SceneGraph as clean typography: no boxes, thin bezier branches,
  generous whitespace, one accent colour.

  Contract with the C# host is unchanged:
    - host  -> js : window.loadScene(json)   (ExcalidrawDocument-shaped payload)
    - js -> host  : { type: "ready" | "scene-change" | "error" }
  so ExcalidrawCanvas.cs keeps working without modification.
*/

const SVG = "http://www.w3.org/2000/svg";

// Lucide 24x24 outlines, stroked so they inherit the node's colour.
const ICONS = {
  sparkle: "M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z",
  cog: "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M6.3 17.7l-1.4 1.4M19.1 4.9l-1.4 1.4",
  box: "M21 8a2 2 0 0 0-1-1.7l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.7l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16zM3.3 7 12 12l8.7-5M12 22V12",
  branch: "M6 3v12M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM18 9a9 9 0 0 1-9 9",
  database: "M12 8c5 0 9-1.3 9-3s-4-3-9-3-9 1.3-9 3 4 3 9 3zM3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3",
  user: "M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8",
  users: "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M22 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8",
  note: "M16 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11l5-5V5a2 2 0 0 0-2-2zM15 21v-4a2 2 0 0 1 2-2h4",
  server: "M20 2H4a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2zM20 14H4a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2zM6 6h.01M6 18h.01",
  wrench: "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z",
  shield: "M20 13c0 5-3.5 7.5-7.7 9a1 1 0 0 1-.7 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.2-2.7a1.2 1.2 0 0 1 1.5 0C14.5 3.8 17 5 19 5a1 1 0 0 1 1 1z",
  cloud: "M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z",
  doc: "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7zM14 2v4a2 2 0 0 0 2 2h4M10 9H8M16 13H8M16 17H8",
  flag: "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1zM4 22v-7",
  alert: "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0zM12 9v4M12 17h.01",
  trending: "M22 7l-8.5 8.5-5-5L2 17M16 7h6v6",
  globe: "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20",
  bulb: "M15 14c.2-1 .7-1.7 1.5-2.5A6 6 0 1 0 6 8c0 1 .2 2.2 1.5 3.5.8.8 1.3 1.5 1.5 2.5M9 18h6M10 22h4",
  search: "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM21 21l-4.3-4.3",
  lock: "M19 11H5a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7a2 2 0 0 0-2-2zM7 11V7a5 5 0 0 1 10 0v4",
  key: "M15.5 7.5l2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4M21 2l-9.6 9.6M7.5 21a5.5 5.5 0 1 0 0-11 5.5 5.5 0 0 0 0 11z",
  clock: "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 6v6l4 2",
  calendar: "M8 2v4M16 2v4M19 4H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2zM3 10h18",
  check: "M22 11.1V12a10 10 0 1 1-5.9-9.1M22 4 12 14.01l-3-3",
  scale: "M12 3v18M3 7h2c2 0 5-1 7-2 2 1 5 2 7 2h2M16 16l3-8 3 8a4 4 0 0 1-6 0zM2 16l3-8 3 8a4 4 0 0 1-6 0z",
  bot: "M12 8V4H8M18 8H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2zM2 14h2M20 14h2M15 13v2M9 13v2",
  chart: "M12 20V10M18 20V4M6 20v-4",
  plug: "M12 22v-5M9 8V2M15 8V2M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8z",
  workflow: "M9 3H5a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2zM7 11v4a2 2 0 0 0 2 2h4M19 13h-4a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2z",
};

// Scene node kind -> icon + accent role.
const KIND = {
  process: ["cog"], entity: ["box"], decision: ["branch"], data_store: ["database"],
  actor: ["user"], note: ["note"], system: ["server"], technology: ["wrench"],
  security: ["shield", "risk"], cloud: ["cloud"], document: ["doc"],
  milestone: ["flag", "ok"], risk: ["alert", "risk"], metric: ["trending"],
  external: ["globe"], callout: ["bulb"],
};

const NAMED = [
  [/\bpurview\b/i, "search"], [/\bdefender\b/i, "shield"], [/\bsentinel\b/i, "search"],
  [/\bentra\b|\bactive directory\b/i, "key"], [/\bcopilot\b|\bagent\b/i, "bot"],
  [/\bpower bi\b|\bdashboard\b|\breport\b/i, "chart"], [/\bapi\b|\bendpoint\b/i, "plug"],
  [/\bazure\b|\baws\b|\bgcp\b/i, "cloud"], [/\bsql\b|\bdatabase\b/i, "database"],
  [/\bteams\b|\bstakeholder\b/i, "users"], [/\bdeadline\b|\bdue\b/i, "clock"],
  [/\bcheckpoint\b|\bschedule\b/i, "calendar"], [/\bapproval\b|\bapprove\b/i, "check"],
  [/\bgovernance\b|\bcompliance\b|\bpolicy\b/i, "scale"], [/\bpipeline\b|\bworkflow\b/i, "workflow"],
  [/\bencryption\b|\brbac\b/i, "lock"],
];

function iconFor(label, kind) {
  for (const [re, name] of NAMED) if (re.test(label || "")) return name;
  return (KIND[kind] || ["box"])[0];
}
const toneFor = (kind) => (KIND[kind] || [])[1] || null;

const el = (n, a = {}) => {
  const e = document.createElementNS(SVG, n);
  for (const k in a) if (a[k] != null) e.setAttribute(k, a[k]);
  return e;
};

let measureCtx = null;
function textWidth(t, size, weight) {
  measureCtx ||= document.createElement("canvas").getContext("2d");
  measureCtx.font = `${weight} ${size}px Inter, "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif`;
  return measureCtx.measureText(t || "").width;
}

const ICON_W = 24;
const ROW_H = 26;
const ROW_GAP = 16;
const COL_GAP = 132;

/**
 * Lays the graph out as a left-to-right branch tree.
 *
 * Radial/force layouts produce the diagonal spaghetti this replaces. A tidy tree
 * (Reingold-Tilford style: children stacked, parent centred on its children) is
 * what reads as a designed mind map.
 */
function layoutTree(nodes, edges) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const kids = new Map(nodes.map((n) => [n.id, []]));
  const indeg = new Map(nodes.map((n) => [n.id, 0]));

  for (const e of edges) {
    if (!byId.has(e.from) || !byId.has(e.to) || e.from === e.to) continue;
    // Keep a strict tree: the first parent wins. Everything else is a cross-link,
    // which is drawn far more quietly — a mind map with every association at full
    // weight is exactly the spaghetti this layout replaces.
    if (indeg.get(e.to) === 0) {
      kids.get(e.from).push(e.to);
      indeg.set(e.to, 1);
      e._tree = true;
    } else {
      e._tree = false;
    }
  }

  // Measure every node first — the row height depends on whether it has a detail line.
  for (const n of nodes) {
    n._size = n.root ? 19 : 15;
    n._weight = n.root ? 650 : 500;
    n._tw = textWidth(n.label, n._size, n._weight);
    n._dw = n.desc ? textWidth(n.desc, 12, 450) : 0;
    n._w = ICON_W + Math.max(n._tw, n._dw);
    n._h = n.desc ? 36 : ROW_H;
  }

  const roots = nodes.filter((n) => indeg.get(n.id) === 0);
  if (roots.length === 0 && nodes.length) roots.push(nodes[0]);
  if (roots.length === 1) roots[0].root = true;

  // Depth-first: stack leaves vertically, centre each parent on its children.
  let cursorY = 0;
  const place = (id, depth, seen) => {
    if (seen.has(id)) return 0;
    seen.add(id);
    const n = byId.get(id);
    const children = kids.get(id).filter((c) => !seen.has(c));
    n.x = depth * COL_GAP + (depth > 0 ? depth * 60 : 0);

    if (children.length === 0) {
      n.y = cursorY;
      cursorY += n._h + ROW_GAP;
      return n.y;
    }
    const ys = children.map((c) => place(c, depth + 1, seen));
    n.y = (Math.min(...ys) + Math.max(...ys)) / 2;
    return n.y;
  };

  const seen = new Set();
  for (const r of roots) {
    place(r.id, 0, seen);
    cursorY += ROW_GAP * 1.5;
  }
  // Anything unreachable (shouldn't happen) still gets a slot.
  for (const n of nodes) {
    if (n.x == null) { n.x = 0; n.y = cursorY; cursorY += n._h + ROW_GAP; }
  }

  // Column x needs the widest node in each preceding column.
  const cols = new Map();
  for (const n of nodes) {
    const c = Math.round(n.x / COL_GAP);
    n._col = c;
    cols.set(c, Math.max(cols.get(c) || 0, n._w));
  }
  let x = 0;
  const colX = new Map();
  for (const c of [...cols.keys()].sort((a, b) => a - b)) {
    colX.set(c, x);
    x += cols.get(c) + COL_GAP;
  }
  for (const n of nodes) n.x = colX.get(n._col);

  return { byId, kids };
}

/** Horizontal-first bezier: leaves the parent right, arrives from the left. */
function branchPath(a, b) {
  const x1 = a.x + a._w + 10;
  const y1 = a.y;
  const x2 = b.x - 10;
  const y2 = b.y;
  const dx = Math.max(30, (x2 - x1) * 0.5);
  return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`;
}

export function renderScene(svg, scene, view) {
  svg.replaceChildren();
  const root = el("g", { id: "viewport" });
  svg.appendChild(root);

  const nodes = scene.nodes || [];
  if (nodes.length === 0) {
    const hint = el("text", { class: "empty-hint", x: 0, y: 0 });
    hint.textContent = "Listening — the map will draw itself as people talk.";
    root.appendChild(hint);
    root.setAttribute("transform", `translate(${svg.clientWidth / 2},${svg.clientHeight / 2})`);
    return;
  }

  const { byId } = layoutTree(nodes, scene.edges || []);

  // group captions: a hairline rule, never a heavy frame
  const gLayer = el("g");
  root.appendChild(gLayer);
  for (const g of scene.groups || []) {
    const members = nodes.filter((n) => n.group === g.id);
    if (members.length === 0) continue;
    const minX = Math.min(...members.map((n) => n.x)) - 18;
    const maxX = Math.max(...members.map((n) => n.x + n._w)) + 18;
    const topY = Math.min(...members.map((n) => n.y)) - 30;
    gLayer.appendChild(el("line", { class: "group-rule", x1: minX, y1: topY, x2: maxX, y2: topY }));
    const t = el("text", { class: "group-name", x: minX, y: topY - 9 });
    t.textContent = g.label || "";
    gLayer.appendChild(t);
  }

  // branches
  const eLayer = el("g");
  root.appendChild(eLayer);
  for (const e of scene.edges || []) {
    const a = byId.get(e.from), b = byId.get(e.to);
    if (!a || !b) continue;

    // Tree branches carry the structure and get a label. Cross-links are context,
    // not structure: hairline, no label, so they never compete with the reading.
    const cls = e._tree
      ? "edge" + (e.kind === "dependency" || e.kind === "association" ? " soft" : "")
      : "edge cross";
    eLayer.appendChild(el("path", { class: cls, d: branchPath(a, b) }));

    if (e.label && e._tree) {
      const mx = (a.x + a._w + b.x) / 2, my = (a.y + b.y) / 2;
      const w = textWidth(e.label, 11, 450) + 12;
      eLayer.appendChild(el("rect", {
        class: "edge-label-bg", x: mx - w / 2, y: my - 8, width: w, height: 16, rx: 4,
      }));
      const t = el("text", { class: "edge-label", x: mx, y: my });
      t.textContent = e.label;
      eLayer.appendChild(t);
    }
  }

  // nodes
  const nLayer = el("g");
  root.appendChild(nLayer);
  for (const n of nodes) {
    const tone = toneFor(n.kind);
    const g = el("g", {
      class: ["node", n.root && "root", tone].filter(Boolean).join(" "),
      "data-id": n.id,
    });

    g.appendChild(el("rect", {
      class: "node-plate",
      x: n.x - 7, y: n.y - n._h / 2 - 3,
      width: n._w + 14, height: n._h + 6, rx: 7,
    }));

    const ic = el("g", { transform: `translate(${n.x}, ${n.y - 8}) scale(0.66)` });
    ic.appendChild(el("path", { class: "node-icon", d: ICONS[iconFor(n.label, n.kind)] || ICONS.box }));
    g.appendChild(ic);

    const label = el("text", {
      class: "node-label", x: n.x + ICON_W, y: n.desc ? n.y - 7 : n.y,
    });
    label.textContent = n.label;
    g.appendChild(label);

    if (n.desc) {
      const d = el("text", { class: "node-desc", x: n.x + ICON_W, y: n.y + 11 });
      d.textContent = n.desc;
      g.appendChild(d);
    }
    nLayer.appendChild(g);
  }

  root.setAttribute("transform", `translate(${view.x},${view.y}) scale(${view.k})`);
  return { bounds: bounds(nodes) };
}

export function bounds(nodes) {
  if (!nodes.length) return { x: 0, y: 0, w: 0, h: 0 };
  const minX = Math.min(...nodes.map((n) => n.x));
  const maxX = Math.max(...nodes.map((n) => n.x + n._w));
  const minY = Math.min(...nodes.map((n) => n.y - n._h / 2));
  const maxY = Math.max(...nodes.map((n) => n.y + n._h / 2));
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}
