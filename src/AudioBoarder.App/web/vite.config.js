import { defineConfig } from "vite";

// Builds the fully self-contained, offline SVG canvas into
// ../Assets/web. base "./" keeps every asset reference relative so the bundle
// works when served from a WebView2 virtual-host folder mapping.
export default defineConfig({
  base: "./",
  build: {
    outDir: "../Assets/web",
    emptyOutDir: true,
    chunkSizeWarningLimit: 8000,
    // Keep the embedded canvas in one small offline bundle.
    sourcemap: false,
  },
});
