import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    // Caddy does the same in production, so the browser always sees one origin.
    // OCTOPUSHY_API points the dev server at another backend, such as `just demo`'s.
    proxy: {
      "/api": {
        target: process.env.OCTOPUSHY_API ?? "http://127.0.0.1:8080",
        // Keep the browser's Host so the API can compare it with Origin.
        changeOrigin: false,
      },
    },
  },
  preview: { host: "127.0.0.1", port: 4173 },
  build: { target: "es2022", sourcemap: false },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    css: false,
  },
});
