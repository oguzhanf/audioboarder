/*
  AudioBoarder canvas — architecture renderer.

  Draws the scene the way the Azure Architecture Center draws reference
  architectures: nested boundary containers, product cards with icons, orthogonal
  connectors, and numbered step badges for a request path.

  Conventions follow the Well-Architected guidance on design diagrams
  (learn.microsoft.com/azure/well-architected/architect-role/design-diagrams):
  every relationship is directional, never bidirectional; containers and
  relationships are labelled; line style is consistent and paired with meaning
  rather than relying on colour alone.

  Contract with the C# host is unchanged:
    - host  -> js : window.loadScene(json)
    - js -> host  : { type: "ready" | "scene-change" | "error" }
*/

import { layoutArchitecture } from "./architecture-layout.js";

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
  // Identity & security
  [/\bentra\b|\bactive directory\b|\baad\b|\bmanaged identit/i, "key"],
  [/\bkey vault\b|\bsecret|\bcertificate/i, "lock"],
  [/\bdefender\b|\bwaf\b|\bweb application firewall\b|\bfirewall\b|\bddos\b/i, "shield"],
  [/\bsentinel\b|\bpurview\b|\baudit\b|\bcompliance\b/i, "search"],
  [/\bprivate endpoint\b|\bprivate link\b|\brbac\b|\bencryption\b/i, "lock"],

  // Networking
  [/\bvirtual network\b|\bvnet\b|\bsubnet\b|\bpeering\b|\bnsg\b/i, "network"],
  [/\bfront door\b|\bapplication gateway\b|\bapp gateway\b|\bload balancer\b|\btraffic manager\b|\bcdn\b/i, "network"],
  [/\bexpressroute\b|\bvpn\b|\bgateway\b|\bdns\b|\bbastion\b/i, "network"],

  // Compute & hosting
  [/\bapp service\b|\bweb app\b|\bfunction|\bcontainer app|\blogic app\b/i, "zap"],
  [/\bkubernetes\b|\baks\b|\bcontainer\b|\bdocker\b|\bregistry\b/i, "container"],
  [/\bvirtual machine\b|\bvm\b|\bscale set\b|\bvmss\b|\bserver\b/i, "server"],

  // Data
  [/\bcosmos\b|\bsql\b|\bdatabase\b|\bpostgres|\bmysql\b|\bredis\b|\bdocumentdb\b/i, "database"],
  [/\bstorage\b|\bblob\b|\bdata lake\b|\bonelake\b|\bbackup\b|\barchive\b/i, "archive"],
  [/\bsynapse\b|\bfabric\b|\bdatabricks\b|\bdata factory\b|\bstream analytics\b/i, "workflow"],
  [/\bevent hub\b|\bservice bus\b|\bqueue\b|\bevent grid\b|\bkafka\b/i, "workflow"],

  // AI
  [/\bfoundry\b|\bopenai\b|\bcognitive\b|\bmachine learning\b|\baml\b|\bmodel\b/i, "brain"],
  [/\bcopilot\b|\bagent\b|\bbot\b|\bprompt\b/i, "bot"],
  [/\bai search\b|\bcognitive search\b|\bvector\b|\bembedding\b/i, "search"],

  // Operations
  [/\bmonitor\b|\bapplication insights\b|\blog analytics\b|\bmetric|\btelemetry\b/i, "trending"],
  [/\balert\b|\bincident\b|\bon-call\b/i, "bell"],
  [/\bdevops\b|\bpipeline\b|\bgithub\b|\bci\/cd\b|\bdeploy/i, "workflow"],
  [/\bresource manager\b|\barm\b|\bbicep\b|\bterraform\b|\bpolicy\b/i, "file-text"],
  [/\bsubscription\b|\btenant\b|\blanding zone\b|\bmanagement group\b/i, "cloud"],

  // Generic cloud & endpoints
  [/\bazure\b|\baws\b|\bgcp\b|\bcloud\b/i, "cloud"],
  [/\bapi\b|\bendpoint\b|\brest\b|\bwebhook\b|\bconnector\b/i, "plug"],
  [/\bpower bi\b|\bdashboard\b|\breport\b|\banalytics\b/i, "chart"],
  [/\bteams\b|\bstakeholder\b|\busers?\b|\bcustomer\b|\bclient\b/i, "users"],
  [/\bsharepoint\b|\bonedrive\b|\bdocument\b|\bfile\b/i, "folder"],
  [/\bdeadline\b|\bdue\b|\bsla\b|\blatency\b/i, "clock"],
  [/\bcheckpoint\b|\bmilestone\b|\bschedule\b|\brelease\b/i, "calendar"],
  [/\bapproval\b|\bapprove\b|\bsign-?off\b/i, "check"],
  [/\bgovernance\b|\bdecision\b|\btrade-?off\b/i, "scale"],
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

/**
 * Orthogonal route between two node boxes. Architecture diagrams use stepped
 * connectors, not free diagonals — a diagonal across a container reads as an error.
 */
function routePath(a, b) {
  const ax = a.x + a.w, ay = a.y + a.h / 2;
  const bx = b.x, by = b.y + b.h / 2;

  // Target is to the right: leave right, enter left, with a mid dogleg.
  if (bx >= ax + 24) {
    const mid = (ax + bx) / 2;
    return { d: `M ${ax} ${ay} H ${mid} V ${by} H ${bx}`, mx: mid, my: (ay + by) / 2 };
  }

  // Target is left or overlapping: go around underneath.
  const ay2 = a.y + a.h, by2 = b.y + b.h / 2;
  const drop = Math.max(ay2, b.y + b.h) + 26;
  const sx = a.x + a.w / 2, ex = b.x + b.w / 2;
  return {
    d: `M ${sx} ${ay2} V ${drop} H ${ex} V ${b.y + b.h}`,
    mx: (sx + ex) / 2,
    my: drop,
  };
}

export function renderScene(svg, scene, view) {
  svg.replaceChildren();

  // Arrowheads are defined once and reused. WAF guidance is explicit that every
  // relationship must be directional, so no connector is drawn without one.
  const defs = el("defs");
  const marker = el("marker", {
    id: "arrow", viewBox: "0 0 10 10", refX: 9, refY: 5,
    markerWidth: 6, markerHeight: 6, orient: "auto-start-reverse",
  });
  marker.appendChild(el("path", { class: "arrow-head", d: "M 0 1 L 9 5 L 0 9 z" }));
  defs.appendChild(marker);
  svg.appendChild(defs);

  const root = el("g", { id: "viewport" });
  svg.appendChild(root);

  const nodes = scene.nodes || [];
  if (nodes.length === 0) {
    // Placed OUTSIDE the pan/zoom group and centred on the SVG itself. Inside the
    // viewport it inherits the current translate, which pushes it off-panel and
    // clips it mid-word.
    const hint = el("text", {
      class: "empty-hint",
      x: svg.clientWidth / 2,
      y: svg.clientHeight / 2,
    });
    hint.textContent = "Listening — the map will draw itself as people talk.";
    svg.appendChild(hint);
    return;
  }

  const { groups } = layoutArchitecture(scene, textWidth);
  const byId = new Map(nodes.map((n) => [n.id, n]));

  // ---- containers, outermost first so nesting reads correctly ----------------
  const gLayer = el("g");
  root.appendChild(gLayer);
  for (const g of groups) {
    const box = el("g", { class: `container depth-${Math.min(g.depth, 3)}` });
    box.appendChild(el("rect", {
      class: "container-box",
      x: g.x, y: g.y, width: g.w, height: g.h, rx: 10,
    }));

    const title = el("text", { class: "container-name", x: g.x + 14, y: g.y + 21 });
    title.textContent = g.label || "";
    box.appendChild(title);

    if (g.subtitle) {
      const sub = el("text", {
        class: "container-subtitle",
        x: g.x + 16 + textWidth(g.label || "", 12, 700),
        y: g.y + 21,
      });
      sub.textContent = g.subtitle;
      box.appendChild(sub);
    }
    gLayer.appendChild(box);
  }

  // ---- connectors ------------------------------------------------------------
  const eLayer = el("g");
  root.appendChild(eLayer);

  /** True when a point lands on a node card, where a label would be unreadable. */
  const overlapsNode = (x, y) =>
    nodes.some((n) => x > n.x - 6 && x < n.x + n.w + 6 && y > n.y - 6 && y < n.y + n.h + 6);

  for (const e of scene.edges || []) {
    const a = byId.get(e.from), b = byId.get(e.to);
    if (!a || !b) continue;

    const soft = e.kind === "dependency" || e.kind === "association";
    const { d, mx, my } = routePath(a, b);
    eLayer.appendChild(el("path", {
      class: "edge" + (soft ? " soft" : ""),
      d,
      "marker-end": "url(#arrow)",
    }));

    // A numbered step badge is what turns a picture into a walkthrough. A step and
    // a label at the same point would collide, so the badge wins.
    if (e.step) {
      eLayer.appendChild(el("circle", { class: "step-badge", cx: mx, cy: my, r: 11 }));
      const num = el("text", { class: "step-num", x: mx, y: my });
      num.textContent = String(e.step);
      const tip = el("title");
      tip.textContent = e.label ? `Step ${e.step}: ${e.label}` : `Step ${e.step}`;
      num.appendChild(tip);
      eLayer.appendChild(num);
    } else if (e.label && !overlapsNode(mx, my)) {
      const w = textWidth(e.label, 11, 450) + 12;
      eLayer.appendChild(el("rect", {
        class: "edge-label-bg", x: mx - w / 2, y: my - 9, width: w, height: 18, rx: 4,
      }));
      const t = el("text", { class: "edge-label", x: mx, y: my });
      t.textContent = e.label;
      eLayer.appendChild(t);
    }
  }

  // ---- node cards ------------------------------------------------------------
  const nLayer = el("g");
  root.appendChild(nLayer);
  for (const n of nodes) {
    const tone = toneFor(n.kind);
    const g = el("g", {
      class: ["node", tone, n.locked && "pinned"].filter(Boolean).join(" "),
      "data-id": n.id,
    });

    g.appendChild(el("rect", {
      class: "node-card", x: n.x, y: n.y, width: n.w, height: n.h, rx: 8,
    }));

    // Official Azure artwork when the user has the icon set; otherwise a bundled
    // generic icon. Official icons are inserted verbatim and never recoloured,
    // cropped or rotated, per Microsoft's icon terms.
    if (n.svg) {
      const holder = el("g", {
        class: "node-art",
        transform: `translate(${n.x + 10}, ${n.y + n.h / 2 - 13})`,
      });
      // Parsed rather than assigned as innerHTML so the SVG root is scaled to fit.
      const parsed = new DOMParser().parseFromString(n.svg, "image/svg+xml");
      const art = parsed.documentElement;
      if (art && art.nodeName.toLowerCase() === "svg") {
        art.setAttribute("width", "26");
        art.setAttribute("height", "26");
        holder.appendChild(document.importNode(art, true));
        g.appendChild(holder);
      }
    } else {
      const ic = el("g", { transform: `translate(${n.x + 12}, ${n.y + n.h / 2 - 11}) scale(0.92)` });
      ic.appendChild(el("path", { class: "node-icon", d: ICONS[iconFor(n.label, n.kind)] || ICONS.box }));
      g.appendChild(ic);
    }

    const tx = n.x + 40;
    const label = el("text", {
      class: "node-label", x: tx, y: n.desc ? n.y + n.h / 2 - 7 : n.y + n.h / 2,
    });
    label.textContent = n.labelText ?? n.label;
    // Full text on hover, since the card may have truncated it.
    const tip = el("title");
    tip.textContent = n.desc ? `${n.label} — ${n.desc}` : n.label;
    g.appendChild(tip);
    g.appendChild(label);

    if (n.desc) {
      const d = el("text", { class: "node-desc", x: tx, y: n.y + n.h / 2 + 11 });
      d.textContent = n.descText ?? n.desc;
      g.appendChild(d);
    }

    // Small dot marking a user-pinned node.
    g.appendChild(el("circle", {
      class: "node-pin", cx: n.x + n.w - 9, cy: n.y + 9, r: 3,
    }));

    nLayer.appendChild(g);
  }

  root.setAttribute("transform", `translate(${view.x},${view.y}) scale(${view.k})`);
  return { bounds: bounds(nodes, groups) };
}

export function bounds(nodes, groups = []) {
  const boxes = [
    ...nodes.map((n) => ({ x: n.x, y: n.y, w: n.w, h: n.h })),
    ...groups.map((g) => ({ x: g.x, y: g.y, w: g.w, h: g.h })),
  ].filter((b) => Number.isFinite(b.x) && Number.isFinite(b.y));

  if (boxes.length === 0) return { x: 0, y: 0, w: 0, h: 0 };
  const minX = Math.min(...boxes.map((b) => b.x));
  const maxX = Math.max(...boxes.map((b) => b.x + b.w));
  const minY = Math.min(...boxes.map((b) => b.y));
  const maxY = Math.max(...boxes.map((b) => b.y + b.h));
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}
