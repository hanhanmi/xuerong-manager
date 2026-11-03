// 雪绒人机管理器 — 后端服务
// 零外部依赖，仅使用 Node.js 内置模块
import http from "node:http";
import { promises as fs } from "node:fs";
import path from "node:path";
import os from "node:os";
import { exec } from "node:child_process";
import { fileURLToPath } from "node:url";
import { createReadStream } from "node:fs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 3721;
const DATA_DIR = path.join(__dirname, "data");
const STATE_FILE = path.join(DATA_DIR, "state.json");
const RESOURCES_DIR = path.resolve(__dirname, "..", "CS2人机增强助手", "resources");

// —— 插件目录 ——
// 来源：CS2BotImprover.zip 结构 + 改动说明.md + 源码梳理
const PLUGIN_CATALOG = [
  // === 框架层（安装时必装，不可单独开关）===
  { id: "metamod",        name: "Metamod:Source",      category: "framework", tier: "core", desc: "底层插件框架，CounterStrikeSharp 的运行基础", dir: "addons/metamod", alwaysOn: true },
  { id: "counterstrikesharp", name: "CounterStrikeSharp", category: "framework", tier: "core", desc: "C# 插件运行时，所有人机插件依赖此框架", dir: "addons/counterstrikesharp", alwaysOn: true },

  // === 原生插件（C++/Metamod 层，控制核心行为）===
  { id: "BotController",  name: "Bot 控制器",    category: "native", tier: "core", desc: "控制 bot 添加/移除/队伍分配，提供 bot_quota 与 bot_add 增强指令", dir: "addons/BotController" },
  { id: "BotHider",       name: "Bot 隐藏器",    category: "native", tier: "core", desc: "将 bot 从玩家列表与计分板隐藏，使人机对战看起来像真人对抗", dir: "addons/BotHider" },
  { id: "BotVision",      name: "Bot 视觉系统",  category: "native", tier: "core", desc: "控制 bot 的视野锥与感知范围，决定 bot 发现玩家的速度", dir: "addons/BotVision" },

  // === 托管插件（C#/CSS 层，可单独开关）===
  { id: "BotAI",                 name: "Bot AI 增强",       category: "managed", tier: "combat",   desc: "提升 bot 战术决策：包点、交叉火力、经济管理", dir: "addons/counterstrikesharp/plugins/BotAI" },
  { id: "BotAimImprover",        name: "Bot 瞄准改进",      category: "managed", tier: "combat",   desc: "调节 bot 瞄准平滑度与爆头率，支持四档难度", dir: "addons/counterstrikesharp/plugins/BotAimImprover" },
  { id: "BotBuy",                name: "Bot 购买逻辑",      category: "managed", tier: "combat",   desc: "让 bot 按经济状况智能购买武器与道具", dir: "addons/counterstrikesharp/plugins/BotBuy" },
  { id: "BotControllerImpl",     name: "控制器实现",        category: "managed", tier: "control",  desc: "BotController 的 C# 实现层，提供高级控制指令", dir: "addons/counterstrikesharp/plugins/BotControllerImpl" },
  { id: "BotHiderImpl",          name: "隐藏器实现",        category: "managed", tier: "control",  desc: "BotHider 的 C# 实现层，处理玩家列表隐藏逻辑", dir: "addons/counterstrikesharp/plugins/BotHiderImpl" },
  { id: "BotRandomizer",         name: "Bot 随机化",        category: "managed", tier: "feature",  desc: "随机化 bot 名字、皮肤与武器，避免千篇一律", dir: "addons/counterstrikesharp/plugins/BotRandomizer" },
  { id: "BotState",              name: "Bot 状态追踪",      category: "managed", tier: "feature",  desc: "追踪并记录 bot 的击杀、死亡与经济状态", dir: "addons/counterstrikesharp/plugins/BotState" },
  { id: "NadeSystem",            name: "投掷物系统",        category: "managed", tier: "feature",  desc: "让 bot 会使用闪光弹、烟雾弹与高爆弹，支持预设投掷路线", dir: "addons/counterstrikesharp/plugins/NadeSystem" },
  { id: "RoundDamageRecap",      name: "回合伤害回顾",      category: "managed", tier: "feature",  desc: "每回合结束显示伤害统计回顾，类似官方 MVP 回放", dir: "addons/counterstrikesharp/plugins/RoundDamageRecap" },

  // === 独立功能模块 ===
  { id: "PremierShowcase2",      name: "XP 勋章 / 段位展示", category: "feature", tier: "special", desc: "伪造 Premier 段位徽章、XP 经验值与 Rating，含闪电图标表现。独立管理模块", dir: "addons/counterstrikesharp/plugins/PremierShowcase2" },
  { id: "KillfeedIcons",         name: "击杀图标皮肤",      category: "feature", tier: "special", desc: "替换击杀提示栏的图标皮肤包，含 22 套主题（txz01–18）", dir: "overrides/KillfeedIcons" },
];

// —— 默认状态 ——
function defaultState() {
  const plugins = {};
  for (const p of PLUGIN_CATALOG) {
    plugins[p.id] = { enabled: p.alwaysOn || false, installed: false };
  }
  return {
    cs2Path: "",
    autoDetectedPath: "",
    plugins,
    difficulty: "Medium",
    installDate: null,
    version: "1.0.0",
    onboarded: false,
    premierConfig: { rating: 15800, xpBoost: 1.5, lightningIcon: true, badgeStyle: "gold" },
    killfeedStyle: "txz03",
  };
}

// —— 状态持久化 ——
async function loadState() {
  try {
    await fs.mkdir(DATA_DIR, { recursive: true });
    const raw = await fs.readFile(STATE_FILE, "utf-8");
    const saved = JSON.parse(raw);
    const base = defaultState();
    // 合并：保留已保存的值，但插件目录以代码为准
    const merged = { ...base, ...saved };
    for (const p of PLUGIN_CATALOG) {
      if (saved.plugins?.[p.id]) {
        merged.plugins[p.id] = { ...base.plugins[p.id], ...saved.plugins[p.id] };
      }
    }
    return merged;
  } catch {
    return defaultState();
  }
}

let state = await loadState();

async function saveState() {
  try {
    await fs.mkdir(DATA_DIR, { recursive: true });
    await fs.writeFile(STATE_FILE, JSON.stringify(state, null, 2), "utf-8");
  } catch (e) {
    console.error("[state] 保存失败:", e.message);
  }
}

// —— CS2 安装路径自动检测 ——
async function detectCS2Path() {
  const candidates = [];
  const steamCommon = [
    "C:\\Program Files (x86)\\Steam\\steamapps\\common",
    "D:\\Steam\\steamapps\\common",
    "E:\\Steam\\steamapps\\common",
    "C:\\Program Files\\Steam\\steamapps\\common",
  ];
  // 从 Steam 库文件夹列表读取所有库
  try {
    const steamReg = await runCmd('reg query "HKCU\\Software\\Valve\\Steam" /v SteamPath 2>nul');
    const m = steamReg.match(/SteamPath\s+REG_SZ\s+(.+)/);
    if (m) {
      const steamPath = m[1].trim();
      const vdfPath = path.join(steamPath, "steamapps", "libraryfolders.vdf");
      try {
        const vdf = await fs.readFile(vdfPath, "utf-8");
        const matches = [...vdf.matchAll(/"path"\s+"([^"]+)"/g)];
        for (const mm of matches) {
          candidates.push(path.join(mm[1], "steamapps", "common", "Counter-Strike Global Offensive"));
        }
      } catch {}
    }
  } catch {}

  for (const c of steamCommon) {
    candidates.push(path.join(c, "Counter-Strike Global Offensive"));
  }

  for (const c of candidates) {
    try {
      const stat = await fs.stat(c);
      if (stat.isDirectory()) {
        // 验证目录里是否有 game/csgo 或 bin
        const gameDir = path.join(c, "game", "csgo");
        try { await fs.stat(gameDir); return c; } catch {}
        return c; // 目录存在即返回
      }
    } catch {}
  }
  return "";
}

function runCmd(cmd) {
  return new Promise((resolve) => {
    exec(cmd, { shell: "cmd.exe", timeout: 5000 }, (err, stdout) => {
      resolve(stdout || "");
    });
  });
}

// —— 检测插件是否已安装 ——
async function detectInstalledPlugins(cs2Path) {
  const result = {};
  for (const p of PLUGIN_CATALOG) {
    if (!cs2Path) { result[p.id] = false; continue; }
    const target = path.join(cs2Path, "game", "csgo", p.dir);
    try {
      await fs.stat(target);
      result[p.id] = true;
    } catch {
      result[p.id] = false;
    }
  }
  return result;
}

// —— GitHub 更新检查 ——
async function checkGitHubUpdates() {
  const repos = [
    { name: "CS2-Bot-Improver", owner: "srcengineering", repo: "CS2-Bot-Improver" },
    { name: "CounterStrikeSharp", owner: "roflmuffin", repo: "CounterStrikeSharp" },
  ];
  const results = [];
  for (const r of repos) {
    try {
      const url = `https://api.github.com/repos/${r.owner}/${r.repo}/releases/latest`;
      const resp = await fetch(url, {
        headers: { "User-Agent": "XuerongBotManager/1.0" },
        signal: AbortSignal.timeout(8000),
      });
      if (resp.ok) {
        const data = await resp.json();
        results.push({
          name: r.name,
          repo: `${r.owner}/${r.repo}`,
          latestVersion: data.tag_name,
          publishedAt: data.published_at,
          releaseUrl: data.html_url,
          notes: (data.body || "").slice(0, 300),
          available: true,
        });
      } else {
        results.push({ name: r.name, repo: `${r.owner}/${r.repo}`, available: false, error: `HTTP ${resp.status}` });
      }
    } catch (e) {
      results.push({ name: r.name, repo: `${r.owner}/${r.repo}`, available: false, error: e.message });
    }
  }
  return results;
}

// —— 安装插件包 ——
async function installPlugins(cs2Path) {
  if (!cs2Path) throw new Error("未设置 CS2 安装路径");
  const targetGame = path.join(cs2Path, "game", "csgo");
  try { await fs.stat(targetGame); } catch { throw new Error(`CS2 游戏目录不存在: ${targetGame}`); }

  const zipPath = path.join(RESOURCES_DIR, "CS2BotImprover.zip");
  try { await fs.stat(zipPath); } catch {
    throw new Error(`离线安装包不存在: ${zipPath}`);
  }

  // 使用 PowerShell Expand-Archive 解压到临时目录再复制
  const tempExtract = path.join(os.tmpdir(), "xuerong_install_" + Date.now());
  const psCmd = `Expand-Archive -Path '${zipPath}' -DestinationPath '${tempExtract}' -Force`;
  await runPowerShell(psCmd);

  // 复制 addons, overrides, cfg 到 CS2 目录
  const dirsToCopy = ["addons", "overrides", "cfg"];
  const copied = [];
  for (const d of dirsToCopy) {
    const src = path.join(tempExtract, d);
    try {
      await fs.stat(src);
      const dst = path.join(targetGame, d);
      // 备份已存在的 addons
      const backupDir = path.join(cs2Path, "game", `xuerong_backup_${Date.now()}`);
      try {
        await fs.stat(dst);
        await runPowerShell(`Copy-Item -Path '${dst}' -Destination '${backupDir}' -Recurse -Force`);
      } catch {}
      await runPowerShell(`Copy-Item -Path '${src}/*' -Destination '${dst}' -Recurse -Force`);
      copied.push(d);
    } catch {}
  }

  // 处理 gameinfo.gi — 追加 metamod 路径
  const gameinfoPath = path.join(targetGame, "gameinfo.gi");
  try {
    let gameinfo = await fs.readFile(gameinfoPath, "utf-8");
    if (!gameinfo.includes("metamod")) {
      gameinfo = gameinfo.replace(
        /SearchPaths\s*\n\s*\{/,
        'SearchPaths\n\t{\n\t\tGame\tcsgo/addons/metamod'
      );
      await fs.writeFile(gameinfoPath, gameinfo, "utf-8");
    }
  } catch {}

  // 清理临时目录
  await runPowerShell(`Remove-Item -Path '${tempExtract}' -Recurse -Force`).catch(() => {});

  // 更新安装状态
  const installed = await detectInstalledPlugins(cs2Path);
  for (const p of PLUGIN_CATALOG) {
    state.plugins[p.id].installed = installed[p.id];
  }
  state.installDate = new Date().toISOString();
  state.cs2Path = cs2Path;
  await saveState();

  return { success: true, copied, installedCount: Object.values(installed).filter(Boolean).length };
}

function runPowerShell(cmd) {
  return new Promise((resolve, reject) => {
    exec(`powershell -NoProfile -Command "${cmd.replace(/"/g, '\\"')}"`, {
      shell: "cmd.exe",
      timeout: 120000,
      maxBuffer: 10 * 1024 * 1024,
    }, (err, stdout, stderr) => {
      if (err) reject(err);
      else resolve(stdout);
    });
  });
}

// —— HTTP 服务 ——
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".svg": "image/svg+xml",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".woff": "font/woff2",
};

const server = http.createServer(async (req, res) => {
  // CORS
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type");

  if (req.method === "OPTIONS") { res.writeHead(204); res.end(); return; }

  const url = new URL(req.url, `http://localhost:${PORT}`);
  const pathname = url.pathname;

  // —— API 路由 ——
  if (pathname.startsWith("/api/")) {
    res.setHeader("Content-Type", "application/json; charset=utf-8");

    try {
      // GET /api/state — 获取完整状态
      if (pathname === "/api/state" && req.method === "GET") {
        const installed = state.cs2Path ? await detectInstalledPlugins(state.cs2Path) : {};
        const autoPath = state.autoDetectedPath || await detectCS2Path();
        if (autoPath && !state.autoDetectedPath) state.autoDetectedPath = autoPath;
        return json(res, { ...state, installedMap: installed, autoDetectedPath: autoPath, pluginCatalog: PLUGIN_CATALOG });
      }

      // POST /api/detect-path — 自动检测 CS2 路径
      if (pathname === "/api/detect-path" && req.method === "POST") {
        const detected = await detectCS2Path();
        state.autoDetectedPath = detected;
        await saveState();
        return json(res, { path: detected });
      }

      // POST /api/set-path — 手动设置 CS2 路径
      if (pathname === "/api/set-path" && req.method === "POST") {
        const body = await readBody(req);
        const { path: cs2Path } = JSON.parse(body);
        if (!cs2Path) return json(res, { error: "路径不能为空" }, 400);
        const gameDir = path.join(cs2Path, "game", "csgo");
        try { await fs.stat(gameDir); } catch {
          return json(res, { error: "路径下未找到 game/csgo 目录，请确认是否为 CS2 根目录" }, 400);
        }
        state.cs2Path = cs2Path;
        const installed = await detectInstalledPlugins(cs2Path);
        for (const p of PLUGIN_CATALOG) {
          state.plugins[p.id].installed = installed[p.id];
        }
        await saveState();
        return json(res, { success: true, path: cs2Path, installedMap: installed });
      }

      // POST /api/toggle-plugin — 开关插件
      if (pathname === "/api/toggle-plugin" && req.method === "POST") {
        const body = await readBody(req);
        const { pluginId, enabled } = JSON.parse(body);
        const plugin = PLUGIN_CATALOG.find(p => p.id === pluginId);
        if (!plugin) return json(res, { error: "未知插件" }, 400);
        if (plugin.alwaysOn) return json(res, { error: "核心框架不可关闭" }, 400);
        if (!state.plugins[pluginId]) state.plugins[pluginId] = { enabled: false, installed: false };
        state.plugins[pluginId].enabled = enabled;

        // 实际操作：启用=恢复 .dll，禁用=重命名为 .dll.off
        if (state.cs2Path && state.plugins[pluginId].installed) {
          await togglePluginFile(plugin, enabled);
        }
        await saveState();
        return json(res, { success: true, pluginId, enabled });
      }

      // POST /api/install — 安装插件包
      if (pathname === "/api/install" && req.method === "POST") {
        const body = await readBody(req);
        const { cs2Path } = JSON.parse(body);
        const targetPath = cs2Path || state.cs2Path || state.autoDetectedPath;
        if (!targetPath) return json(res, { error: "未设置 CS2 安装路径" }, 400);
        const result = await installPlugins(targetPath);
        return json(res, result);
      }

      // GET /api/updates — 检查 GitHub 更新
      if (pathname === "/api/updates" && req.method === "GET") {
        const updates = await checkGitHubUpdates();
        return json(res, { updates });
      }

      // POST /api/difficulty — 设置难度
      if (pathname === "/api/difficulty" && req.method === "POST") {
        const body = await readBody(req);
        const { difficulty } = JSON.parse(body);
        state.difficulty = difficulty;
        await saveState();
        return json(res, { success: true, difficulty });
      }

      // POST /api/premier — 更新 XP 勋章配置
      if (pathname === "/api/premier" && req.method === "POST") {
        const body = await readBody(req);
        const cfg = JSON.parse(body);
        state.premierConfig = { ...state.premierConfig, ...cfg };
        await saveState();
        return json(res, { success: true, premierConfig: state.premierConfig });
      }

      // POST /api/killfeed — 设置击杀图标样式
      if (pathname === "/api/killfeed" && req.method === "POST") {
        const body = await readBody(req);
        const { style } = JSON.parse(body);
        state.killfeedStyle = style;
        await saveState();
        return json(res, { success: true, killfeedStyle: style });
      }

      // POST /api/onboarded — 标记新手引导完成
      if (pathname === "/api/onboarded" && req.method === "POST") {
        state.onboarded = true;
        await saveState();
        return json(res, { success: true });
      }

      // POST /api/uninstall — 卸载插件
      if (pathname === "/api/uninstall" && req.method === "POST") {
        if (!state.cs2Path) return json(res, { error: "未设置 CS2 路径" }, 400);
        const target = path.join(state.cs2Path, "game", "csgo", "addons");
        const backupDir = path.join(state.cs2Path, "game", `xuerong_uninstall_${Date.now()}`);
        try {
          await runPowerShell(`Move-Item -Path '${target}' -Destination '${backupDir}' -Force`);
        } catch {}
        for (const p of PLUGIN_CATALOG) {
          state.plugins[p.id].installed = false;
          if (!p.alwaysOn) state.plugins[p.id].enabled = false;
        }
        await saveState();
        return json(res, { success: true, backupDir });
      }

      return json(res, { error: "未知 API" }, 404);
    } catch (e) {
      console.error("[api] 错误:", e);
      return json(res, { error: e.message }, 500);
    }
  }

  // —— 静态文件 ——
  let filePath = pathname === "/" ? "/index.html" : pathname;
  // 安全检查
  if (filePath.includes("..")) { res.writeHead(403); res.end("Forbidden"); return; }
  const fullPath = path.join(__dirname, "public", filePath);
  try {
    const stat = await fs.stat(fullPath);
    if (stat.isDirectory()) {
      const idx = path.join(fullPath, "index.html");
      try { await fs.stat(idx); return serveFile(idx, res); } catch {}
    }
    return serveFile(fullPath, res);
  } catch {
    res.writeHead(404);
    res.end("Not Found");
  }
});

function json(res, data, code = 200) {
  res.writeHead(code, { "Content-Type": "application/json; charset=utf-8" });
  res.end(JSON.stringify(data));
}

function readBody(req) {
  return new Promise((resolve) => {
    let body = "";
    req.on("data", c => body += c);
    req.on("end", () => resolve(body));
  });
}

async function serveFile(fullPath, res) {
  const ext = path.extname(fullPath);
  const mime = MIME[ext] || "application/octet-stream";
  res.writeHead(200, { "Content-Type": mime });
  createReadStream(fullPath).pipe(res);
}

async function togglePluginFile(plugin, enabled) {
  const target = path.join(state.cs2Path, "game", "csgo", plugin.dir);
  try {
    const entries = await fs.readdir(target);
    for (const entry of entries) {
      if (entry.endsWith(".dll")) {
        const full = path.join(target, entry);
        const offPath = full + ".off";
        if (enabled) {
          try { await fs.rename(offPath, full); } catch {}
        } else {
          try { await fs.rename(full, offPath); } catch {}
        }
      }
      // .dll.so (Linux) or .dll — also handle gamedata json
      if (entry.endsWith(".json") && entry.includes("gamedata")) {
        const full = path.join(target, entry);
        const offPath = full + ".off";
        if (enabled) {
          try { await fs.rename(offPath, full); } catch {}
        } else {
          try { await fs.rename(full, offPath); } catch {}
        }
      }
    }
  } catch (e) {
    console.error(`[toggle] ${plugin.id}:`, e.message);
  }
}

server.listen(PORT, "127.0.0.1", () => {
  console.log(`\n  雪绒人机管理器已启动`);
  console.log(`  → http://localhost:${PORT}\n`);
  if (!state.autoDetectedPath) {
    detectCS2Path().then(p => { if (p) { state.autoDetectedPath = p; saveState(); console.log(`  自动检测到 CS2 路径: ${p}`); } });
  }
});
