// Headless verification for the custom SVG surface in the same Edge engine WebView2 uses.
const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

const fallbackScene = {
  sceneRevision: 7,
  intent: "integration_data_flow_architecture",
  nodes: [
    { id: "client", label: "Client", kind: "actor", lifecycle: "confirmed",
      centerX: 180, centerY: 180, width: 160, height: 64 },
    { id: "api", label: "API", kind: "process", group: "cloud", lifecycle: "confirmed",
      centerX: 460, centerY: 180, width: 180, height: 72 },
  ],
  edges: [
    { id: "request", from: "client", to: "api", kind: "flow", label: "Create order",
      step: 1, protocol: "HTTPS", payload: "JSON", authentication: "OAuth",
      dataClassification: "Confidential", interactionMode: "synchronous", lifecycle: "confirmed" },
  ],
  groups: [
    { id: "cloud", label: "Production", subtitle: "West Europe", boundaryKind: "cloud_scope",
      lifecycle: "confirmed", centerX: 460, centerY: 180, width: 250, height: 150, depth: 0 },
  ],
};

function check(value, message) {
  if (!value) throw new Error(message);
}

(async () => {
  const url = process.argv[2] || "http://localhost:5566/";
  const samplePath = process.argv[3];
  const shotPath = process.argv[4] || "verify-shot.png";
  const sample = samplePath && fs.existsSync(samplePath)
    ? JSON.parse(fs.readFileSync(samplePath, "utf8"))
    : fallbackScene;

  const browser = await chromium.launch({ channel: "msedge", headless: true });
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const errors = [];
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("pageerror", (error) => errors.push(`PAGEERROR: ${error.message}`));
  await page.addInitScript(() => {
    window.__hostMessages = [];
    window.chrome = {
      webview: {
        postMessage: (message) => window.__hostMessages.push(message),
        addEventListener: () => {},
      },
    };
  });

  await page.goto(url, { waitUntil: "load", timeout: 30000 });
  await page.waitForFunction(() => typeof window.loadScene === "function");
  await page.evaluate((value) => window.loadScene(value), sample);
  await page.waitForTimeout(100);

  const ids = await page.evaluate(() => ({
    nodes: [...document.querySelectorAll('[data-layer="nodes"] > [data-id]')].map((x) => x.dataset.id),
    edges: [...document.querySelectorAll('[data-layer="edges"] > [data-id]')].map((x) => x.dataset.id),
    groups: [...document.querySelectorAll('[data-layer="groups"] > [data-id]')].map((x) => x.dataset.id),
  }));
  check(ids.nodes.includes("client") && ids.nodes.includes("api"), "nodes were not rendered by data-id");
  check(ids.edges.includes("request"), "edge was not rendered by data-id");
  check(ids.groups.includes("cloud"), "group was not rendered by data-id");

  const geometry = await page.locator('[data-layer="nodes"] > [data-id="api"] .node-card')
    .evaluate((rect) => ({
      x: Number(rect.getAttribute("x")), y: Number(rect.getAttribute("y")),
      width: Number(rect.getAttribute("width")), height: Number(rect.getAttribute("height")),
    }));
  check(geometry.x === 370 && geometry.y === 144 &&
        geometry.width === 180 && geometry.height === 72,
  "supplied centre geometry was not honoured");

  const semanticText = await page.locator('[data-layer="edges"] > [data-id="request"]')
    .evaluate((element) => element.textContent);
  check(semanticText.includes("1") && semanticText.includes("Create order"),
    "step badge and interaction label must both be visible");
  check(semanticText.includes("HTTPS") && semanticText.includes("OAuth") &&
        semanticText.includes("Confidential"),
  "edge semantic metadata is not visible");

  const identityPreserved = await page.evaluate((value) => {
    const before = document.querySelector('[data-layer="nodes"] > [data-id="api"]');
    const changed = structuredClone(value);
    changed.sceneRevision++;
    changed.nodes.find((node) => node.id === "api").label = "API v2";
    window.loadScene(changed);
    return before === document.querySelector('[data-layer="nodes"] > [data-id="api"]');
  }, sample);
  check(identityPreserved, "keyed incremental load replaced an existing node element");

  const node = page.locator('[data-layer="nodes"] > [data-id="api"]');
  const box = await node.boundingBox();
  check(box, "node has no browser bounding box");
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2 + 20);
  await page.mouse.up();
  const pinMessage = await page.evaluate(() =>
    window.__hostMessages.find((message) => message?.type === "scene-change"));
  check(pinMessage?.elements?.[0]?.id === "api" && pinMessage.elements[0].locked === true,
    "drag did not post a locked scene-change message");

  await node.focus();
  await page.keyboard.press("Enter");
  const unpinMessage = await page.evaluate(() =>
    window.__hostMessages.filter((message) => message?.type === "scene-change").at(-1));
  check(unpinMessage?.elements?.[0]?.id === "api" && unpinMessage.elements[0].locked === false,
    "keyboard pin toggle did not post an unlocked scene-change message");

  await page.screenshot({ path: shotPath, fullPage: false });
  await browser.close();
  check(errors.length === 0, `console errors: ${errors.join(" | ")}`);

  console.log(JSON.stringify({
    nodes: ids.nodes.length,
    edges: ids.edges.length,
    groups: ids.groups.length,
    keyedIdentityPreserved: identityPreserved,
    pinMessage: pinMessage.elements[0],
    unpinMessage: unpinMessage.elements[0],
    consoleErrors: errors,
    screenshot: path.resolve(shotPath),
  }, null, 2));
  console.log("VERIFY OK");
})().catch((error) => {
  console.error("VERIFY ERROR", error);
  process.exit(1);
});
