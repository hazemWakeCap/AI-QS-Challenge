import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev server proxies /api → the ASP.NET Core backend so the browser hits one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.API_URL ?? "http://localhost:5070",
        changeOrigin: true,
      },
    },
  },
});
