// API 适配层
// 运行时检测：Tauri 环境走 invoke，浏览器环境走 Node 服务 fetch
// 这样同一套前端代码既能被 Tauri 打包成 exe，也能在浏览器里跑 Node 版

const IS_TAURI = typeof window !== "undefined" && window.__TAURI_INTERNALS__ !== undefined;

async function tauriInvoke(cmd, args = {}) {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke(cmd, args);
}

async function httpPost(url, body) {
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body || {}),
  });
  return res.json();
}

async function httpGet(url) {
  const res = await fetch(url);
  return res.json();
}

export const isTauri = IS_TAURI;

export const api = {
  state: () => (IS_TAURI ? tauriInvoke("get_state") : httpGet("/api/state")),

  detectPath: () =>
    IS_TAURI ? tauriInvoke("detect_path") : httpPost("/api/detect-path"),

  setPath: (path) =>
    IS_TAURI
      ? tauriInvoke("set_path", { path })
      : httpPost("/api/set-path", { path }),

  togglePlugin: (pluginId, enabled) =>
    IS_TAURI
      ? tauriInvoke("toggle_plugin", { pluginId, enabled })
      : httpPost("/api/toggle-plugin", { pluginId, enabled }),

  install: (cs2Path) =>
    IS_TAURI
      ? tauriInvoke("install", { cs2Path: cs2Path || null })
      : httpPost("/api/install", { cs2Path }),

  uninstall: () =>
    IS_TAURI ? tauriInvoke("uninstall") : httpPost("/api/uninstall"),

  setDifficulty: (difficulty) =>
    IS_TAURI
      ? tauriInvoke("set_difficulty", { difficulty })
      : httpPost("/api/difficulty", { difficulty }),

  getDifficulty: () =>
    IS_TAURI ? tauriInvoke("get_difficulty") : Promise.resolve({ current: null, available: [] }),

  setPremier: (config) =>
    IS_TAURI
      ? tauriInvoke("set_premier", { config })
      : httpPost("/api/premier", config),

  setKillfeed: (style) =>
    IS_TAURI
      ? tauriInvoke("set_killfeed", { style })
      : httpPost("/api/killfeed", { style }),

  markOnboarded: () =>
    IS_TAURI ? tauriInvoke("mark_onboarded") : httpPost("/api/onboarded"),

  updates: () =>
    IS_TAURI ? tauriInvoke("check_updates") : httpGet("/api/updates"),

  // 启动 CS2（insecure 或 normal 模式）
  launchCs2: (mode) =>
    IS_TAURI ? tauriInvoke("launch_cs2", { mode }) : httpPost("/api/launch", { mode }),

  // 仅 Tauri 可用：系统目录选择框
  pickFolder: () => (IS_TAURI ? tauriInvoke("pick_cs2_folder") : null),

  // 仅 Tauri 可用：打开外部链接 / 文件夹
  openUrl: (url) =>
    IS_TAURI ? tauriInvoke("open_url", { url }) : window.open(url, "_blank"),
  openFolder: (path) =>
    IS_TAURI ? tauriInvoke("open_folder", { path }) : null,

  // 一键全开 / 全关
  setAllPlugins: (enabled) =>
    IS_TAURI ? tauriInvoke("set_all_plugins", { enabled }) : Promise.resolve({ success: false }),

  // 重新下发所有配置（换机器/换路径后修复）
  applyConfigs: () =>
    IS_TAURI ? tauriInvoke("apply_configs") : Promise.resolve({ success: false }),

  // 崩溃排查
  getCrashes: () =>
    IS_TAURI ? tauriInvoke("get_crashes") : Promise.resolve({ crashes: [], sinceLaunch: false }),
  safeMode: () =>
    IS_TAURI ? tauriInvoke("safe_mode") : Promise.resolve({ success: false }),

  // ═══ v2.0：对齐 Panel v1.4.7 ═══
  // 清单校验（Panel 的 validate_files 五字段契约）
  validateManifest: () =>
    IS_TAURI ? tauriInvoke("validate_manifest") : Promise.resolve({ ok: false, total: 0, present: 0, missing: [], misplaced: null }),

  // 规则
  getRules: () => (IS_TAURI ? tauriInvoke("get_rules") : Promise.resolve({})),  setRule: (rule, value) => (IS_TAURI ? tauriInvoke("set_rule", { rule, value }) : Promise.resolve({ success: false })),
  setC4Timer: (seconds) => (IS_TAURI ? tauriInvoke("set_c4_timer", { seconds }) : Promise.resolve({ success: false })),
  setMolotovMultiplier: (multiplier) => (IS_TAURI ? tauriInvoke("set_molotov_multiplier", { multiplier }) : Promise.resolve({ success: false })),
  resetRules: () => (IS_TAURI ? tauriInvoke("reset_rules") : Promise.resolve({ success: false })),

  // Bot 物品
  getBotItems: () => (IS_TAURI ? tauriInvoke("get_bot_items") : Promise.resolve({})),
  setBotItem: (item, on) => (IS_TAURI ? tauriInvoke("set_bot_item", { item, on }) : Promise.resolve({ success: false })),

  // 预设
  getPresets: () => (IS_TAURI ? tauriInvoke("get_presets") : Promise.resolve({})),
  setAim: (value) => (IS_TAURI ? tauriInvoke("set_aim", { value }) : Promise.resolve({ success: false })),
  setNades: (value) => (IS_TAURI ? tauriInvoke("set_nades", { value }) : Promise.resolve({ success: false })),

  // PremierBadgeMvp
  getBadge: () => (IS_TAURI ? tauriInvoke("get_badge") : Promise.resolve({ installed: false, config: {} })),
  setBadge: (config) => (IS_TAURI ? tauriInvoke("set_badge", { config }) : Promise.resolve({ success: false })),

  // 击杀图标
  getKillfeed: () => (IS_TAURI ? tauriInvoke("get_killfeed") : Promise.resolve({ packs: [], current: "" })),

  // 软件自身更新
  getAppVersion: () => (IS_TAURI ? tauriInvoke("get_app_version") : Promise.resolve({ version: "" })),
  getUpdateSource: () => (IS_TAURI ? tauriInvoke("get_update_source") : Promise.resolve({ source: "" })),
  setUpdateSource: (source) => (IS_TAURI ? tauriInvoke("set_update_source", { source }) : Promise.resolve({ success: false })),
  downloadUpdate: (url, version) => (IS_TAURI ? tauriInvoke("download_update", { url, version }) : Promise.resolve({ success: false })),
  checkAppUpdate: (source) => (IS_TAURI ? tauriInvoke("check_app_update", { source: source || null }) : Promise.resolve({ hasUpdate: false })),

  // 只开换肤模式
  getSkinMode: () => (IS_TAURI ? tauriInvoke("get_skin_mode") : Promise.resolve({})),
  setSkinMode: (enabled) => (IS_TAURI ? tauriInvoke("set_skin_mode", { enabled }) : Promise.resolve({ success: false })),

  // 击杀图标资源覆盖（兼容性排查）
  getIconOverlay: () => (IS_TAURI ? tauriInvoke("get_icon_overlay") : Promise.resolve({})),
  setIconOverlay: (enabled) => (IS_TAURI ? tauriInvoke("set_icon_overlay", { enabled }) : Promise.resolve({ success: false })),

  // 模式
  getMode: () => (IS_TAURI ? tauriInvoke("get_mode") : Promise.resolve({ current: null })),
  setMode: (mode) => (IS_TAURI ? tauriInvoke("set_mode", { mode }) : Promise.resolve({ success: false })),

  // 最小化 / 关闭：走 Rust 侧命令，不依赖前端 window 权限
  minimize: async () => {
    if (!IS_TAURI) return null;
    try {
      await tauriInvoke("minimize_window");
    } catch (e) {
      console.error("minimize failed", e);
    }
  },
  close: async () => {
    if (!IS_TAURI) return null;
    try {
      await tauriInvoke("close_window");
    } catch (e) {
      console.error("close failed", e);
      // 兜底：直接走 window API
      try {
        const { getCurrentWindow } = await import("@tauri-apps/api/window");
        await getCurrentWindow().close();
      } catch (e2) {
        console.error("fallback close failed", e2);
      }
    }
  },
};
