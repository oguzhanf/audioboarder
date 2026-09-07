const checks = [];
function check(condition, name) {
  if (!condition) throw new Error(name);
  checks.push(name);
}
function host(value) {
  window.__hostMessage({ data: JSON.stringify(value) });
}
function report(result) {
  document.getElementById("verification-result").textContent = JSON.stringify(result);
  fetch("/verification-result", { method: "POST", body: encodeURIComponent(JSON.stringify(result)) });
}

window.addEventListener("load", async () => {
  try {
    const frontDoor = await (await fetch("/azure-front-door.svg")).text();
    const appGateway = await (await fetch("/application-gateway.svg")).text();
    check(typeof window.loadScene === "function", "canvas module loaded");
    check(window.__messages.some(m => m.type === "ready"), "host readiness");
    host({ type: "theme", theme: "dark" });
    check(document.documentElement.dataset.theme === "dark", "string theme message");
    host({
      type: "component-library",
      source: "https://learn.microsoft.com/azure/architecture/",
      components: [
        { id: "azure-front-door", name: "Azure Front Door", category: "Azure / Networking",
          description: "Global application delivery", aliases: ["front door"], svg: frontDoor, iconIsOfficial: true },
        { id: "application-gateway", name: "Azure Application Gateway", category: "Azure / Networking",
          description: "Regional application gateway", aliases: ["app gateway"], svg: appGateway, iconIsOfficial: true },
      ],
    });
    check(document.querySelectorAll(".component-item").length === 2, "string component library message");
    const search = document.getElementById("componentSearch");
    check(document.querySelectorAll(".component-preview").length === 2, "visual icons in library");
    search.value = "app gateway";
    search.dispatchEvent(new Event("input"));
    check(document.querySelectorAll(".component-item").length === 1 &&
      document.querySelector(".component-item").dataset.componentId === "application-gateway", "component alias search");

    const scene = {
      sceneRevision: 7, intent: "security_zero_trust_architecture",
      nodes: [
        { id: "client", label: "Client", kind: "actor", centerX: 180, centerY: 180, width: 160, height: 64 },
        { id: "api", label: "API", kind: "process", group: "cloud", centerX: 460, centerY: 180, width: 180, height: 72 },
      ],
      edges: [{ id: "request", from: "client", to: "api", kind: "flow", label: "Create order",
        step: 1, protocol: "HTTPS", payload: "JSON", authentication: "OAuth", dataClassification: "Confidential" }],
      groups: [{ id: "cloud", label: "Production", boundaryKind: "cloud_scope", centerX: 460,
        centerY: 180, width: 250, height: 150, depth: 0 }],
    };
    host(scene);
    const node = document.querySelector('.node[data-id="api"]');
    const card = node.querySelector(".node-card");
    check(card.getAttribute("x") === "370" && card.getAttribute("y") === "144", "authoritative center geometry");
    check(node.dataset.kind === "process", "node kind exposed for styling");
    const edge = document.querySelector('[data-layer="edges"] > [data-id="request"]');
    check(edge.classList.contains("boundary-crossing"), "security intent preserved over bridge");
    check(["1", "Create order", "HTTPS", "OAuth", "Confidential"].every(s => edge.textContent.includes(s)), "edge semantics");
    check(edge.querySelector(".edge").getAttribute("marker-end") === "url(#arrow)", "flow remains directional");
    scene.edges[0].kind = "association";
    host(scene);
    check(!edge.querySelector(".edge").hasAttribute("marker-end"), "association has no arrowhead on keyed update");
    for (const kind of ["dependency", "inheritance", "flow"]) {
      scene.edges[0].kind = kind;
      host(scene);
      check(edge.querySelector(".edge").getAttribute("marker-end") === "url(#arrow)", `${kind} restores its arrowhead`);
    }
    scene.edges[0].label = "Routes the complete incoming customer order request to the application";
    host(scene);
    const edgeLabel = edge.querySelector(".edge-label").getBBox();
    check(edgeLabel.width <= 88 && edge.querySelector("title").textContent.includes(scene.edges[0].label),
      "edge labels are bounded without losing full tooltip semantics");
    scene.edges[0].label = "Create order";
    scene.nodes[1].label = "API v2";
    scene.nodes[1].kind = "concept";
    scene.sceneRevision++;
    host(scene);
    check(node === document.querySelector('.node[data-id="api"]'), "keyed node identity");
    check(node.dataset.kind === "concept", "keyed node kind updates to concept");
    node.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    const pin = window.__messages.filter(m => m.type === "scene-change").at(-1);
    check(pin.elements[0].id === "api" && pin.elements[0].locked === true, "keyboard pin bridge");
    node.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    check(window.__messages.filter(m => m.type === "scene-change").at(-1).elements[0].locked === false, "keyboard unpin bridge");

    const stage = document.getElementById("stage");
    const rect = stage.getBoundingClientRect();
    const viewport = document.getElementById("viewport");
    const transform = viewport.getAttribute("transform");
    const matrix = viewport.getCTM();
    const transfer = new DataTransfer();
    transfer.setData("application/x-audioboarder-component", "application-gateway");
    stage.dispatchEvent(new DragEvent("drop", { dataTransfer: transfer, clientX: rect.left + 250,
      clientY: rect.top + 210, bubbles: true, cancelable: true }));
    const dropped = window.__messages.find(m => m.type === "component-drop");
    check(dropped?.componentId === "application-gateway" &&
      Math.abs(dropped.x - (250 - matrix.e) / matrix.a) < .01 &&
      Math.abs(dropped.y - (210 - matrix.f) / matrix.d) < .01, "drop coordinates account for sidebar and zoom");
    host({ ...scene, nodes: [...scene.nodes, { id: "manual", label: "Azure Application Gateway", kind: "technology",
      centerX: dropped.x, centerY: dropped.y, width: 260, height: 116, locked: true, svg: appGateway,
      desc: "Regional layer 7 load balancer and web application firewall." }] });
    check(viewport.getAttribute("transform") === transform, "manual drop does not trigger auto-fit");
    check(document.querySelector(".zoom").getBoundingClientRect().left >= rect.left, "zoom controls outside library");
    const manual = document.querySelector('.node[data-id="manual"]');
    const manualCard = manual.querySelector(".node-card").getBBox();
    check(manual.querySelector(".node-art image")?.getAttribute("href") ===
      "data:image/svg+xml;charset=utf-8," + encodeURIComponent(appGateway), "official artwork preserved unchanged");
    for (const selector of [".node-label", ".node-desc"]) {
      const text = manual.querySelector(selector).getBBox();
      check(text.x >= manualCard.x && text.x + text.width <= manualCard.x + manualCard.width &&
        text.y >= manualCard.y && text.y + text.height <= manualCard.y + manualCard.height,
        `${selector} contained in node card`);
    }
    const explanation = "Meaningful context ".repeat(14).slice(0, 240);
    host({
      intent: "meeting_whiteboard",
      nodes: [
        { id: "concept-detail", label: "Idea explained", kind: "concept", centerX: 180, centerY: 220,
          width: 260, height: 180, desc: explanation },
        { id: "process-detail", label: "Existing process", kind: "process", centerX: 500, centerY: 220,
          width: 260, height: 180, desc: explanation },
        { id: "short-concept", label: "Compact idea", kind: "concept", centerX: 180, centerY: 440,
          width: 260, height: 80, desc: explanation },
      ], edges: [], groups: [],
    });
    check(document.querySelectorAll('[data-id="concept-detail"] .node-desc tspan').length === 6,
      "concept descriptions allow six wrapped lines");
    check(document.querySelectorAll('[data-id="process-detail"] .node-desc tspan').length === 3,
      "other node descriptions retain their three-line limit");
    check(document.querySelectorAll('[data-id="short-concept"] .node-desc tspan').length <= 2,
      "short concept cards still honor available height");
    const geometryTolerance = .01;
    for (const id of ["concept-detail", "process-detail", "short-concept"]) {
      const item = document.querySelector(`.node[data-id="${id}"]`);
      const bounds = item.querySelector(".node-card").getBBox();
      const description = item.querySelector(".node-desc").getBBox();
      const contained = description.x + geometryTolerance >= bounds.x + 64 &&
        description.x + description.width <= bounds.x + bounds.width + geometryTolerance &&
        description.y + geometryTolerance >= bounds.y &&
        description.y + description.height <= bounds.y + bounds.height + geometryTolerance;
      check(contained, `${id} description respects card bounds and icon space` +
        (contained ? "" : `: text=${[description.x, description.y, description.width, description.height]}; ` +
          `card=${[bounds.x, bounds.y, bounds.width, bounds.height]}`));
      check(item.querySelector("title").textContent.includes(explanation), `${id} retains full explanation in tooltip`);
    }
    host({ type: "theme", theme: "light" });
    check(document.documentElement.dataset.theme === "light", "light theme");
    host({ type: "library-visibility", collapsed: true });
    await new Promise(resolve => setTimeout(resolve, 20));
    check(document.body.classList.contains("library-collapsed"), "native library collapse");
    host({
      nodes: [
        { id: "left", label: "Start", kind: "process", centerX: 0, centerY: 0, width: 240, height: 100 },
        { id: "right", label: "End", kind: "process", centerX: 4000, centerY: 2000, width: 240, height: 100 },
      ], edges: [], groups: [],
    });
    document.getElementById("zoomFit").click();
    const visibleStage = stage.getBoundingClientRect();
    for (const card of document.querySelectorAll(".node-card")) {
      const box = card.getBoundingClientRect();
      check(box.left >= visibleStage.left && box.right <= visibleStage.right &&
        box.top >= visibleStage.top && box.bottom <= visibleStage.bottom, "explicit Fit frames the complete diagram");
    }
    check(window.__errors.length === 0, "no browser errors");
    document.documentElement.dataset.verification = "passed";
    report({ passed: true, checks });
  } catch (error) {
    document.documentElement.dataset.verification = "failed";
    report({ passed: false, checks, error: String(error) });
  }
});
