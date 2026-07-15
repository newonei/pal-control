import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  // Relative assets let the same build run at /player/ in the Windows bundle
  // and at the site root when it is deployed behind Caddy.
  base: "./",
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5175,
    strictPort: true,
    proxy: {
      "/api/v1/player": {
        target: "http://127.0.0.1:5180",
        changeOrigin: false
      }
    }
  }
});
