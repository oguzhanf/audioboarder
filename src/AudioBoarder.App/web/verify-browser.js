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
    const edge = document.querySelector('[data-layer="edges"] > [data-id="request"]');
    check(edge.classList.contains("boundary-crossing"), "security intent preserved over bridge");
    check(["1", "Create order", "HTTPS", "OAuth", "Confidential"].every(s => edge.textContent.includes(s)), "edge semantics");
    scene.nodes[1].label = "API v2";
    scene.sceneRevision++;
    host(scene);
    check(node === document.querySelector('.node[data-id="api"]'), "keyed node identity");
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
    host({ type: "theme", theme: "light" });
    check(document.documentElement.dataset.theme === "light", "light theme");
    check(window.__errors.length === 0, "no browser errors");
    document.documentElement.dataset.verification = "passed";
    report({ passed: true, checks });
  } catch (error) {
    document.documentElement.dataset.verification = "failed";
    report({ passed: false, checks, error: String(error) });
  }
});
