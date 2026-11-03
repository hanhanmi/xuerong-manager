import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Tauri 需要固定端口，不能用随机端口
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: 5173,
    strictPort: true,
    watch: { ignored: ["**/src-tauri/**"] },
  },
  envPrefix: ["VITE_", "TAURI_"],
  build: {
    // Tauri 使用 Chromium (WebView2)，可target设为现代浏览器
    target: "esnext",
    minify: "esbuild",
    sourcemap: false,
    outDir: "dist",
    emptyOutDir: true,
  },
});
