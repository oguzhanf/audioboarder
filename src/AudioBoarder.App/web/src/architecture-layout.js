/*
  Architecture layout.

  Replaces the strict-tree mind-map layout, which forced first-parent-wins and faded
  every other edge — fine for a brainstorm, but it destroyed architecture graphs,
  where containment IS the structure and most edges are peers rather than branches.

  The model here is the one used by published architecture diagrams:
    - Containers nest (subscription > vnet > subnet > resource) and are sized to
      whatever they hold.
    - Nodes inside a container are laid out by flow rank, so requests read L->R.
    - Containers are packed as blocks, and ungrouped nodes participate as blocks too.
*/

export const PAD = {
  // Room for a container's header (name + subtitle).
  header: 34,
  // Inner padding between a container's edge and its contents.
  inner: 18,
  // Gap between sibling blocks inside the same parent.
  gap: 28,
  // Gap between ranks (columns) of the flow.
  rank: 76,
  // Gap between nodes stacked within a rank.
  stack: 18,
};

export const NODE = { w: 168, h: 56, descH: 16 };

/**
 * Groups the scene into a containment forest and lays every level out.
 * Returns { nodes, groups } with absolute x/y/w/h on each.
 */
export function layoutArchitecture(scene, measure) {
  const nodes = scene.nodes || [];
  const edges = scene.edges || [];
  const groups = scene.groups || [];

  const groupById = new Map(groups.map((g) => [g.id, { ...g, children: [], nodes: [] }]));
  const roots = [];

  // Build the containment forest, ignoring parents that don't exist.
  for (const g of groupById.values()) {
    const parent = g.parent && groupById.get(g.parent);
    if (parent && parent !== g) parent.children.push(g);
    else roots.push(g);
  }

  // Assign nodes to their container, or to the top level.
  const looseNodes = [];
  for (const n of nodes) {
    sizeNode(n, measure);
    const g = n.group && groupById.get(n.group);
    if (g) g.nodes.push(n);
    else looseNodes.push(n);
  }

  // Drop containers that ended up empty — an empty labelled box is pure noise.
  const prune = (g) => {
    g.children = g.children.filter(prune);
    return g.nodes.length > 0 || g.children.length > 0;
  };
  const liveRoots = roots.filter(prune);

  // Lay out each root container, then the loose nodes, as sibling blocks.
  const blocks = [];
  for (const g of liveRoots) {
    layoutGroup(g, edges, measure);
    blocks.push({ kind: "group", ref: g, w: g.w, h: g.h });
  }
  if (looseNodes.length > 0) {
    const placed = layoutFlow(looseNodes, edges);
    blocks.push({ kind: "loose", nodes: looseNodes, w: placed.w, h: placed.h });
  }

  packBlocks(blocks);

  // Blocks were packed with local origins; push absolute positions down the tree.
  for (const b of blocks) {
    if (b.kind === "group") translateGroup(b.ref, b.x, b.y);
    else for (const n of b.nodes) { n.x += b.x; n.y += b.y; }
  }

  return { groups: flatten(liveRoots), nodes };
}

function sizeNode(n, measure) {
  const labelW = measure(n.label, 13, 600);
  const descW = n.desc ? measure(n.desc, 11, 400) : 0;
  n.w = Math.min(240, Math.max(NODE.w, 34 + Math.max(labelW, descW) + 16));
  n.h = NODE.h + (n.desc ? NODE.descH : 0);

  // Text is clipped to the card it was sized for. Without this a long role
  // description runs straight out of the card and over its neighbours.
  const textW = n.w - 40 - 10;
  n.labelText = ellipsize(n.label, textW, 13, 600, measure);
  n.descText = n.desc ? ellipsize(n.desc, textW, 11, 400, measure) : null;
}

/** Trims text to a pixel width, appending an ellipsis when it does not fit. */
function ellipsize(text, maxW, size, weight, measure) {
  if (!text || measure(text, size, weight) <= maxW) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2);
    if (measure(text.slice(0, mid) + "…", size, weight) <= maxW) lo = mid;
    else hi = mid - 1;
  }
  return text.slice(0, Math.max(1, lo)).trimEnd() + "…";
}

/** Lays out one container: its child containers first, then its own nodes. */
function layoutGroup(g, edges, measure) {
  const childBlocks = [];

  for (const c of g.children) {
    layoutGroup(c, edges, measure);
    childBlocks.push({ kind: "group", ref: c, w: c.w, h: c.h });
  }

  if (g.nodes.length > 0) {
    const placed = layoutFlow(g.nodes, edges);
    childBlocks.push({ kind: "loose", nodes: g.nodes, w: placed.w, h: placed.h });
  }

  const inner = packBlocks(childBlocks);

  // Offset contents to clear the container's own header and padding.
  const dx = PAD.inner;
  const dy = PAD.header + PAD.inner * 0.4;
  for (const b of childBlocks) {
    if (b.kind === "group") translateGroup(b.ref, b.x + dx, b.y + dy);
    else for (const n of b.nodes) { n.x += b.x + dx; n.y += b.y + dy; }
  }

  const titleW = measure(g.label || "", 12, 700) + 40;
  g.w = Math.max(inner.w + PAD.inner * 2, titleW);
  g.h = inner.h + PAD.header + PAD.inner;
  g.x = 0;
  g.y = 0;
}

/**
 * Ranks nodes by flow direction so a request reads left to right, then stacks the
 * members of each rank. A longest-path rank is what makes numbered steps line up.
 */
function layoutFlow(nodes, edges) {
  const ids = new Set(nodes.map((n) => n.id));
  const incoming = new Map(nodes.map((n) => [n.id, []]));
  const outgoing = new Map(nodes.map((n) => [n.id, []]));

  for (const e of edges) {
    if (!ids.has(e.from) || !ids.has(e.to) || e.from === e.to) continue;
    outgoing.get(e.from).push(e.to);
    incoming.get(e.to).push(e.from);
  }

  // Longest-path ranking, iterated to a fixed point so cycles can't hang it.
  const rank = new Map(nodes.map((n) => [n.id, 0]));
  for (let pass = 0; pass < nodes.length; pass++) {
    let moved = false;
    for (const n of nodes) {
      for (const to of outgoing.get(n.id)) {
        if (rank.get(to) < rank.get(n.id) + 1) {
          rank.set(to, rank.get(n.id) + 1);
          moved = true;
        }
      }
    }
    if (!moved) break;
  }

  const byRank = new Map();
  for (const n of nodes) {
    const r = rank.get(n.id);
    if (!byRank.has(r)) byRank.set(r, []);
    byRank.get(r).push(n);
  }

  let x = 0;
  let maxH = 0;
  // Cap how tall one rank may grow. Without this a dozen unconnected components
  // all land at rank 0 and form a single very tall column.
  const maxStack = Math.max(4, Math.ceil(Math.sqrt(nodes.length) * 1.3));

  for (const r of [...byRank.keys()].sort((a, b) => a - b)) {
    const column = byRank.get(r);
    // Split an over-tall rank into side-by-side sub-columns.
    for (let i = 0; i < column.length; i += maxStack) {
      const slice = column.slice(i, i + maxStack);
      const colW = Math.max(...slice.map((n) => n.w));
      let y = 0;
      for (const n of slice) {
        n.x = x + (colW - n.w) / 2;
        n.y = y;
        y += n.h + PAD.stack;
      }
      maxH = Math.max(maxH, y - PAD.stack);
      x += colW + (i + maxStack < column.length ? PAD.stack * 2 : PAD.rank);
    }
  }

  return { w: Math.max(0, x - PAD.rank), h: maxH };
}

/**
 * Shelf-packs blocks, filling each shelf greedily.
 *
 * A plain sorted shelf leaves a tall narrow container (a vnet) sitting beside dead
 * space while a wide short block wraps below it. Best-fit backfills that gap, which
 * is what keeps the enclosing container from ballooning with empty area.
 */
function packBlocks(blocks) {
  if (blocks.length === 0) return { w: 0, h: 0 };

  const total = blocks.reduce((s, b) => s + (b.w + PAD.gap) * (b.h + PAD.gap), 0);
  const widest = Math.max(...blocks.map((b) => b.w));
  const limit = Math.max(widest, Math.sqrt(total * 1.9));

  const pending = blocks.slice().sort((a, z) => z.h - a.h);
  let shelfY = 0, maxW = 0;

  while (pending.length > 0) {
    let shelfX = 0, shelfH = 0;

    // Seed the shelf with the tallest remaining block, then backfill.
    for (let i = 0; i < pending.length; ) {
      const b = pending[i];
      if (shelfX > 0 && shelfX + b.w > limit) { i++; continue; }
      b.x = shelfX;
      b.y = shelfY;
      shelfX += b.w + PAD.gap;
      shelfH = Math.max(shelfH, b.h);
      maxW = Math.max(maxW, shelfX - PAD.gap);
      pending.splice(i, 1);
      // Restart the scan so the next-tallest that fits is considered first.
      i = 0;
    }

    shelfY += shelfH + PAD.gap;
  }

  return { w: maxW, h: Math.max(0, shelfY - PAD.gap) };
}

function translateGroup(g, dx, dy) {
  g.x += dx;
  g.y += dy;
  for (const n of g.nodes) { n.x += dx; n.y += dy; }
  for (const c of g.children) translateGroup(c, dx, dy);
}

function flatten(roots) {
  const out = [];
  const walk = (g, depth) => {
    g.depth = depth;
    out.push(g);
    for (const c of g.children) walk(c, depth + 1);
  };
  for (const g of roots) walk(g, 0);
  return out;
}
