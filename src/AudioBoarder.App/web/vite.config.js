import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Builds a fully self-contained, offline Excalidraw whiteboard into
// ../Assets/web. base "./" keeps every asset reference relative so the bundle
// works when served from a WebView2 virtual-host folder mapping.
export default defineConfig({
  base: "./",
  plugins: [react()],
  build: {
    outDir: "../Assets/web",
    emptyOutDir: true,
    chunkSizeWarningLimit: 8000,
    // Excalidraw is large; a single page app doesn't benefit from heavy splitting.
    sourcemap: false,
  },
});
