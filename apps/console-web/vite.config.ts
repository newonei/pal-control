import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    // The post-build budget check uses the manifest to distinguish the eager
    // application entry from feature chunks. Keep this enabled in every build.
    manifest: true
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    proxy: {
      "/api": "http://127.0.0.1:5180",
      "/health": "http://127.0.0.1:5180"
    }
  }
});
