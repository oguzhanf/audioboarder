// Headless verification that the vendored Excalidraw bundle renders and accepts
// scenes in the same Chromium/Edge engine WebView2 uses (channel: msedge).
const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

(async () => {
  const url = process.argv[2] || "http://localhost:5566/";
  const samplePath = process.argv[3];
  const shotPath = process.argv[4] || "verify-shot.png";

  const browser = await chromium.launch({ channel: "msedge", headless: true });
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });

  const errors = [];
  page.on("console", (m) => {
    if (m.type() === "error") errors.push(m.text());
  });
  page.on("pageerror", (e) => errors.push("PAGEERROR: " + e.message));

  await page.goto(url, { waitUntil: "load", timeout: 30000 });

  // Wait for the Excalidraw API to be ready (excalidrawAPI callback fired).
  await page.waitForFunction(() => !!window.__excalidrawAPI, { timeout: 30000 });

  const sample = fs.readFileSync(samplePath, "utf8");
  await page.evaluate((json) => window.loadScene(json), sample);

  // Give it a moment to apply + frame.
  await page.waitForTimeout(1500);

  const count = await page.evaluate(() =>
    window.__excalidrawAPI.getSceneElements().length
  );
  const hasCanvas = await page.evaluate(
    () => document.querySelectorAll("canvas").length
  );

  await page.screenshot({ path: shotPath, fullPage: false });
  await browser.close();

  const result = {
    sceneElementCount: count,
    canvasCount: hasCanvas,
    consoleErrors: errors,
    screenshot: path.resolve(shotPath),
  };
  console.log(JSON.stringify(result, null, 2));
  if (count <= 0) {
    console.error("FAIL: no scene elements loaded");
    process.exit(2);
  }
  console.log("VERIFY OK");
})().catch((e) => {
  console.error("VERIFY ERROR", e);
  process.exit(1);
});
