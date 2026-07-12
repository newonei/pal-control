import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
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
