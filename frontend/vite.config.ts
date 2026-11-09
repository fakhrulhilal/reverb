import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// `npm run dev` serves the UI with hot reload; point it at a running `reverb edit` with
// REVERB_SERVER=http://127.0.0.1:<port> and open http://localhost:5173/?token=<token>.
const server = process.env.REVERB_SERVER;

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
  server: server
    ? { proxy: { "/ws": { target: server.replace(/^http/, "ws"), ws: true, changeOrigin: true } } }
    : undefined,
});
