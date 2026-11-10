import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The script `reverb export --html` puts inside each page: one file, everything inlined, no chunks.
// vega-embed is swapped for a stub that uses the separate charts script (vite.export-charts.config.ts).
export default defineConfig({
  plugins: [react()],
  define: { "process.env.NODE_ENV": JSON.stringify("production") },
  resolve: { alias: { "vega-embed": fileURLToPath(new URL("./src/export/vega-global.ts", import.meta.url)) } },
  build: {
    outDir: "dist/export",
    emptyOutDir: true,
    cssCodeSplit: false,
    chunkSizeWarningLimit: 1500,
    lib: { entry: "src/export/main.tsx", name: "ReverbExport", formats: ["iife"], fileName: () => "export.js", cssFileName: "export" },
  },
});
