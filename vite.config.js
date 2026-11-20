import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import pkg from "./package.json" with { type: "json" };

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
  // 版本号唯一来源：package.json，避免界面写死导致"装了新版还显示旧版"
  define: { __APP_VERSION__: JSON.stringify(pkg.version) },
  build: {
    // Tauri 使用 Chromium (WebView2)，可target设为现代浏览器
    target: "esnext",
    minify: "esbuild",
    sourcemap: false,
    outDir: "dist",
    emptyOutDir: true,
  },
});
