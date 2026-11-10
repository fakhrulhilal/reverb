import { defineConfig } from "vite";

// vega-embed for exported pages that have charts. Sets window.reverbVegaEmbed.
export default defineConfig({
  define: { "process.env.NODE_ENV": JSON.stringify("production") },
  build: {
    outDir: "dist/export-charts",
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
    lib: { entry: "src/export/charts.ts", name: "ReverbCharts", formats: ["iife"], fileName: () => "charts.js" },
  },
});
