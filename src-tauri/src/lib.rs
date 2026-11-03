// 雪绒人机管理器 — Rust 后端
// 完整移植 server.js 的全部 API 逻辑，使 exe 自包含（不依赖系统 Node）

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::{Emitter, Manager, State as TauriState};
use tokio::io::AsyncWriteExt;

/// CREATE_NO_WINDOW：GUI 进程拉起控制台程序（tasklist / reg / cmd）时不弹黑框。
/// 不加这个标志，每调一次就闪一次命令行窗口——用户看到的「弹好几次命令提示窗」就是它。
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 所有外部命令的统一入口
fn cmd<P: AsRef<std::ffi::OsStr>>(program: P) -> Command {
    let mut c = Command::new(program);
    #[cfg(windows)]
    {
        c.creation_flags(CREATE_NO_WINDOW);
    }
    c
}

// ═══════════════════════════════════════════
// 插件目录
// ═══════════════════════════════════════════

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Plugin {
    pub id: String,
    pub name: String,
    pub category: String,
    pub tier: String,
    #[serde(rename = "desc")]
    pub desc: String,
    pub dir: String,
    pub always_on: bool,
}

fn catalog() -> Vec<Plugin> {
    // category: framework | ai | visual | feature
    let rows: Vec<(&str, &str, &str, &str, &str, &str, bool)> = vec![
        ("metamod", "Metamod:Source", "framework", "core",
         "底层插件框架，CounterStrikeSharp 的运行基础", "addons/metamod", true),
        ("counterstrikesharp", "CounterStrikeSharp", "framework", "core",
         "C# 插件运行时，所有人机插件依赖此框架", "addons/counterstrikesharp", true),

        // —— AI 增强（原生控制层 + 托管决策层统一归入）——
        ("BotController", "Bot 控制器", "ai", "core",
         "控制 bot 添加/移除/队伍分配，是其余 AI 模块的控制底座", "addons/BotController", false),
        ("BotHider", "Bot 身份伪装", "ai", "core",
         "把 bot 伪装成真人：换真名、换名牌、伪造 ping", "addons/BotHider", false),
        ("BotVision", "Bot 视觉系统", "ai", "core",
         "控制 bot 的视野锥、穿烟感知与爆炸物视线", "addons/BotVision", false),
        ("BotControllerImpl", "控制器实现层", "ai", "control",
         "BotController 的 C# 桥接，供托管插件调用", "addons/counterstrikesharp/plugins/BotControllerImpl", false),
        ("BotHiderImpl", "伪装实现层", "ai", "control",
         "BotHider 的 C# 桥接，供托管插件调用", "addons/counterstrikesharp/plugins/BotHiderImpl", false),
        ("BotAI", "Bot AI 决策", "ai", "combat",
         "提升 bot 战术决策：包点、交叉火力、经济管理", "addons/counterstrikesharp/plugins/BotAI", false),
        ("BotAimImprover", "Bot 瞄准", "ai", "combat",
         "调节 bot 瞄准平滑度与爆头率，跟随难度档位", "addons/counterstrikesharp/plugins/BotAimImprover", false),
        ("BotBuy", "Bot 购买逻辑", "ai", "combat",
         "让 bot 按经济状况智能购买武器与道具", "addons/counterstrikesharp/plugins/BotBuy", false),
        ("BotState", "Bot 状态追踪", "ai", "feature",
         "追踪并记录 bot 的击杀、死亡与经济状态", "addons/counterstrikesharp/plugins/BotState", false),
        ("NadeSystem", "投掷物系统", "ai", "feature",
         "让 bot 会按地图点位使用闪光、烟雾与高爆弹", "addons/counterstrikesharp/plugins/NadeSystem", false),

        // —— 外观与展示 ——
        ("BotRandomizer", "Bot 饰品随机", "visual", "feature",
         "随机化 bot 的皮肤、刀、手套、贴纸、挂件与音乐盒", "addons/counterstrikesharp/plugins/BotRandomizer", false),
        ("PremierBadgeMvp", "XP 过载徽章 + 动态 MVP", "visual", "special",
         "写 m_nPersonaDataXpTrailLevel 出闪电徽章，写 m_iCompetitiveRankType≥11 出动态 MVP 名片", "addons/counterstrikesharp/plugins/PremierBadgeMvp", false),
        ("Killfeed_Icons", "击杀图标", "visual", "special",
         "替换击杀提示栏图标，需 gameinfo 挂载 panorama 图标目录", "addons/counterstrikesharp/plugins/Killfeed_Icons", false),

        // —— 功能模块 ——
        ("RoundDamageRecap", "回合伤害回顾", "feature", "feature",
         "每回合结束显示伤害统计回顾", "addons/counterstrikesharp/plugins/RoundDamageRecap", false),
        ("InventorySimulator", "本地皮肤库存", "feature", "feature",
         "模拟饰品库存与 StatTrak，对接 inventory.cstrike.app", "addons/counterstrikesharp/plugins/InventorySimulator", false),
    ];

    rows.into_iter()
        .map(|(id, name, category, tier, desc, dir, always_on)| Plugin {
            id: id.into(),
            name: name.into(),
            category: category.into(),
            tier: tier.into(),
            desc: desc.into(),
            dir: dir.into(),
            always_on,
        })
        .collect()
}

// ═══════════════════════════════════════════
// 持久化状态
// ═══════════════════════════════════════════

#[derive(Clone, Serialize, Deserialize, Default)]
pub struct PluginState {
    pub enabled: bool,
    pub installed: bool,
}

#[derive(Clone, Serialize, Deserialize, Default)]
pub struct PremierConfig {
    #[serde(default = "d_rating")]
    pub rating: u32,
    #[serde(default = "d_xp")]
    pub xp_boost: f64,
    #[serde(default = "d_true")]
    pub lightning_icon: bool,
    #[serde(default = "d_gold")]
    pub badge_style: String,
    /// 每局给不同人机随机分配段位（颜色更杂）
    #[serde(default = "d_true")]
    pub randomize_badge: bool,
}

fn d_rating() -> u32 { 15800 }
fn d_xp() -> f64 { 1.5 }
fn d_true() -> bool { true }
fn d_gold() -> String { "gold".into() }

#[derive(Clone, Serialize, Deserialize)]
pub struct AppStateData {
    #[serde(default)]
    pub cs2_path: String,
    #[serde(default)]
    pub auto_detected_path: String,
    #[serde(default)]
    pub plugins: HashMap<String, PluginState>,
    #[serde(default = "d_medium")]
    pub difficulty: String,
    #[serde(default)]
    pub install_date: Option<String>,
    #[serde(default = "d_version")]
    pub version: String,
    #[serde(default)]
    pub onboarded: bool,
    #[serde(default)]
    pub premier_config: PremierConfig,
    #[serde(default = "d_killfeed")]
    pub killfeed_style: String,
    /// 上次启动时间（用于判断崩溃转储是否属于本次会话）
    #[serde(default)]
    pub last_launch: Option<String>,
    /// BotRandomizer 七个开关
    #[serde(default)]
    pub bot_items: HashMap<String, bool>,
    /// 规则开关：overtime / friendly_fire / boosting / bhop
    #[serde(default)]
    pub rules: HashMap<String, bool>,
    #[serde(default = "d_c4")]
    pub c4_timer: u32,
    #[serde(default = "d_molotov")]
    pub molotov_mult: f64,
    #[serde(default = "d_aim")]
    pub aim: String,
    #[serde(default = "d_nades")]
    pub nades: String,
    /// 只开换肤模式：只保留框架 + 库存插件，其余全部停用
    #[serde(default)]
    pub skin_only: bool,
    /// 更新源：空 = 关闭；github:owner/repo = 走 GitHub Releases；
    /// 其他 = 当作 JSON 地址，内容形如 {"version":"2.2.1","url":"...","notes":"..."}
    #[serde(default)]
    pub update_source: String,
}

fn d_medium() -> String { "Medium".into() }
fn d_version() -> String { "1.1.0".into() }
fn d_killfeed() -> String { "txz03".into() }

/// 检测 CS2 是否正在运行。运行中改插件文件会导致 dll 被锁 / 半写入，进而闪退。
/// 结果缓存 1.2 秒：一次界面刷新会调十几处，每次都开 tasklist 又慢又闪黑框。
static CS2_RUNNING: AtomicBool = AtomicBool::new(false);
static CS2_CHECK_AT: AtomicU64 = AtomicU64::new(0);

fn is_cs2_running() -> bool {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);
    if now.saturating_sub(CS2_CHECK_AT.load(Ordering::Relaxed)) < 1200 {
        return CS2_RUNNING.load(Ordering::Relaxed);
    }
    let running = match cmd("tasklist")
        .args(["/FI", "IMAGENAME eq cs2.exe", "/NH"])
        .output()
    {
        Ok(o) => String::from_utf8_lossy(&o.stdout).to_lowercase().contains("cs2.exe"),
        Err(_) => false,
    };
    CS2_RUNNING.store(running, Ordering::Relaxed);
    CS2_CHECK_AT.store(now, Ordering::Relaxed);
    running
}

/// 扫描崩溃转储（game/bin/win64/cs2_*.mdmp），用于判断上一局是否崩过
fn scan_crash_dumps(cs2_path: &str) -> Vec<(String, u64, u64)> {
    let dir = Path::new(cs2_path).join("game").join("bin").join("win64");
    let mut out = Vec::new();
    let Ok(entries) = fs::read_dir(&dir) else { return out };
    for e in entries.flatten() {
        let name = e.file_name().to_string_lossy().to_string();
        if !name.starts_with("cs2_") || !name.ends_with(".mdmp") {
            continue;
        }
        let Ok(meta) = e.metadata() else { continue };
        let mtime = meta
            .modified()
            .ok()
            .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|d| d.as_secs())
            .unwrap_or(0);
        out.push((name, mtime, meta.len()));
    }
    out.sort_by(|a, b| b.1.cmp(&a.1));
    out
}

impl Default for AppStateData {
    fn default() -> Self {
        let mut plugins = HashMap::new();
        for p in catalog() {
            plugins.insert(
                p.id.clone(),
                PluginState { enabled: p.always_on, installed: false },
            );
        }
        Self {
            cs2_path: String::new(),
            auto_detected_path: String::new(),
            plugins,
            difficulty: d_medium(),
            install_date: None,
            version: d_version(),
            onboarded: false,
            premier_config: PremierConfig { randomize_badge: true, ..Default::default() },
            killfeed_style: d_killfeed(),
            last_launch: None,
            bot_items: bot_items_default(),
            rules: rules_default(),
            c4_timer: d_c4(),
            molotov_mult: d_molotov(),
            aim: d_aim(),
            nades: d_nades(),
            skin_only: false,
            update_source: String::new(),
        }
    }
}

pub struct AppState {
    inner: Mutex<AppStateData>,
    data_dir: PathBuf,
    resources_dir: PathBuf,
}

impl AppState {
    fn state_file(&self) -> PathBuf { self.data_dir.join("state.json") }

    fn load(&self) -> AppStateData {
        if let Ok(text) = fs::read_to_string(self.state_file()) {
            if let Ok(mut s) = serde_json::from_str::<AppStateData>(&text) {
                // 以代码中的目录为准，补齐缺失项
                for p in catalog() {
                    s.plugins.entry(p.id).or_insert(PluginState {
                        enabled: p.always_on,
                        installed: false,
                    });
                }
                return s;
            }
        }
        AppStateData::default()
    }

    fn save(&self, s: &AppStateData) -> Result<(), String> {
        fs::create_dir_all(&self.data_dir).map_err(|e| e.to_string())?;
        let json = serde_json::to_string_pretty(s).map_err(|e| e.to_string())?;
        fs::write(self.state_file(), json).map_err(|e| e.to_string())
    }
}

// ═══════════════════════════════════════════
// CS2 路径检测
// ═══════════════════════════════════════════

fn steam_libraries() -> Vec<PathBuf> {
    let mut libs = Vec::new();
    let mut steam_path: Option<PathBuf> = None;

    // 读取注册表 HKCU\Software\Valve\Steam\SteamPath
    let out = cmd("reg")
        .args(["query", r"HKCU\Software\Valve\Steam", "/v", "SteamPath"])
        .output();
    if let Ok(o) = out {
        let text = String::from_utf8_lossy(&o.stdout);
        // SteamPath    REG_SZ    C:\Program Files (x86)\Steam
        if let Some(idx) = text.find("REG_SZ") {
            let rest = &text[idx + 6..];
            if let Some(p) = rest.split_whitespace().next() {
                steam_path = Some(PathBuf::from(p.trim()));
            }
        }
    }

    if let Some(sp) = &steam_path {
        let vdf = sp.join("steamapps").join("libraryfolders.vdf");
        if let Ok(text) = fs::read_to_string(&vdf) {
            let re = regex::Regex::new(r#""path"\s+"([^"]+)""#).unwrap();
            for cap in re.captures_iter(&text) {
                let p = PathBuf::from(cap.get(1).unwrap().as_str().replace("\\\\", "\\"));
                libs.push(p.join("steamapps").join("common"));
            }
        }
    }

    // 常见默认位置
    if let Some(d) = dirs::data_dir() {
        let _ = d;
    }
    libs.push(PathBuf::from(r"C:\Program Files (x86)\Steam\steamapps\common"));
    libs.push(PathBuf::from(r"C:\Program Files\Steam\steamapps\common"));
    libs.push(PathBuf::from(r"D:\Steam\steamapps\common"));
    libs.push(PathBuf::from(r"E:\Steam\steamapps\common"));
    libs.push(PathBuf::from(r"D:\apps\steam\steamapps\common"));
    libs.push(PathBuf::from(r"D:\Program Files (x86)\Steam\steamapps\common"));

    libs
}

fn detect_cs2_path() -> String {
    for lib in steam_libraries() {
        let candidate = lib.join("Counter-Strike Global Offensive");
        if candidate.join("game").join("csgo").is_dir() {
            return candidate.to_string_lossy().to_string();
        }
        if candidate.is_dir() {
            return candidate.to_string_lossy().to_string();
        }
    }
    String::new()
}

// ═══════════════════════════════════════════
// 已安装检测
// ═══════════════════════════════════════════

fn detect_installed(cs2_path: &str) -> HashMap<String, bool> {
    let mut map = HashMap::new();
    for p in catalog() {
        if cs2_path.is_empty() {
            map.insert(p.id, false);
            continue;
        }
        let target = Path::new(cs2_path)
            .join("game").join("csgo")
            .join(&p.dir);
        map.insert(p.id, target.is_dir());
    }
    map
}

// ═══════════════════════════════════════════
// 真实生效的配置写入（Premier 段位 / 击杀图标）
// ═══════════════════════════════════════════

fn csgo_dir(cs2_path: &str) -> PathBuf {
    Path::new(cs2_path).join("game").join("csgo")
}

/// 读当前登录的 SteamID64（用于按玩家应用击杀图标皮肤）
/// 结果缓存在进程内：reg query + 读 vdf 大约 100~300ms，每次切换都跑会卡 UI
static STEAM_ID_CACHE: std::sync::OnceLock<Option<String>> = std::sync::OnceLock::new();

fn current_steam_id() -> Option<String> {
    STEAM_ID_CACHE.get_or_init(steam_id_uncached).clone()
}

fn steam_id_uncached() -> Option<String> {
    // 候选 Steam 根目录：注册表优先，失败再试常见安装位置
    let mut roots: Vec<PathBuf> = Vec::new();
    if let Ok(out) = cmd("reg")
        .args(["query", r"HKCU\Software\Valve\Steam", "/v", "SteamPath"])
        .output()
    {
        let text = String::from_utf8_lossy(&out.stdout);
        if let Some(idx) = text.find("REG_SZ") {
            if let Some(p) = text[idx + 6..].split_whitespace().next() {
                roots.push(PathBuf::from(p.trim()));
            }
        }
    }
    for guess in [
        r"C:\Program Files (x86)\Steam",
        r"C:\Program Files\Steam",
        r"D:\Steam",
        r"E:\Steam",
        r"D:\apps\steam",
    ] {
        roots.push(PathBuf::from(guess));
    }

    for root in roots {
        let vdf = root.join("config").join("loginusers.vdf");
        let Ok(content) = fs::read_to_string(&vdf) else { continue };

        let Ok(re_user) = regex::Regex::new(r#""(\d{17})"\s*\{"#) else { continue };
        let Ok(re_recent) = regex::Regex::new(r#""MostRecent"\s*"1""#) else { continue };

        // 优先取 MostRecent=1 的账号
        for cap in re_user.captures_iter(&content) {
            let Some(id) = cap.get(1).map(|m| m.as_str().to_string()) else { continue };
            if let Some(start) = content.find(&format!("\"{}\"", id)) {
                let block: String = content[start..].chars().take(400).collect();
                if re_recent.is_match(&block) {
                    return Some(id);
                }
            }
        }
        // 退路：第一个 17 位 ID
        if let Some(m) = re_user.captures(&content).and_then(|c| c.get(1)) {
            return Some(m.as_str().to_string());
        }
    }
    None
}

/// Rating → Premier 徽章档位（RankType）。
/// 实测原配置 15000 → RankType 11，按每 2500 分一档线性映射。
fn rank_type_from_rating(rating: u32) -> u32 {
    let step = (rating as f64 / 2500.0).round() as i64 + 5;
    step.clamp(1, 18) as u32
}

fn badge_label(rank: u32) -> &'static str {
    match rank {
        0..=6 => "灰铁",
        7..=8 => "浅蓝",
        9..=10 => "深蓝",
        11..=12 => "紫罗兰",
        13..=14 => "粉红",
        15..=16 => "猩红",
        _ => "金色",
    }
}





/// 写击杀图标皮肤：插件按 SteamID 读 player_skins.json
fn write_killfeed_config(cs2_path: &str, style: &str) {
    if cs2_path.is_empty() { return; }
    let base = csgo_dir(cs2_path).join("addons").join("counterstrikesharp")
        .join("configs").join("plugins").join("KillfeedIcons");
    let _ = fs::create_dir_all(&base);
    let file = base.join("player_skins.json");

    let mut map: serde_json::Map<String, Value> = fs::read_to_string(&file)
        .ok()
        .and_then(|t| serde_json::from_str::<serde_json::Map<String, Value>>(&t).ok())
        .unwrap_or_default();

    match current_steam_id() {
        Some(id) => { map.insert(id, Value::String(style.to_string())); }
        None => { map.insert("default".into(), Value::String(style.to_string())); }
    }

    let _ = fs::write(&file, serde_json::to_string_pretty(&Value::Object(map)).unwrap_or_default());
}

// ═══════════════════════════════════════════
// gameinfo 修补（只增删自己那两行，保护语言段）
// ═══════════════════════════════════════════

const GAMEINFO_BLOCK: &str =
    "\t\t\tGame\tcsgo/overrides/botprofile.vpk\n\n\t\t\tGame\tcsgo/addons/metamod\n\n";

fn patch_gameinfo(cs2_path: &str, enable: bool) -> Result<(), String> {
    let gi = csgo_dir(cs2_path).join("gameinfo.gi");
    let text = fs::read_to_string(&gi).map_err(|e| format!("读取 gameinfo.gi 失败: {}", e))?;

    let has = text.contains("csgo/addons/metamod");
    if enable == has {
        return Ok(());
    }

    let mut out = text.clone();
    if enable {
        // 插在 Game_LowViolence 之后（必须在 Game csgo 之前才会优先生效）
        let re = regex::Regex::new(r"(?m)^([ \t]*Game_LowViolence[^\r\n]*\r?\n)([ \t]*\r?\n)?").unwrap();
        if !re.is_match(&out) {
            return Err("gameinfo.gi 结构异常：找不到 Game_LowViolence 锚点".into());
        }
        out = re.replace(&out, |c: &regex::Captures| {
            let line = c.get(1).map(|m| m.as_str()).unwrap_or("");
            let blank = c.get(2).map(|m| m.as_str()).unwrap_or("\n");
            format!("{}{}{}", line, blank, GAMEINFO_BLOCK)
        }).to_string();
    } else {
        out = out
            .replace("\t\t\tGame\tcsgo/overrides/botprofile.vpk\n", "")
            .replace("\t\t\tGame\tcsgo/addons/metamod\n", "");
    }

    // 语言段保护：缺一段就不写，避免把用户语言弄丢
    // 注意：只校验语言机制本身是否还在，不要求必须是中文——
    // 否则非中文用户（英文/俄文等）安装时会直接失败。
    for key in ["SetUILanguageOnSteamDropDown", "SupportedLanguages"] {
        if !out.contains(key) {
            return Err(format!("gameinfo.gi 校验失败：缺少 {}，已中止写入", key));
        }
    }
    fs::write(&gi, out).map_err(|e| format!("写入 gameinfo.gi 失败: {}", e))
}

/// 在 gameinfo.gi 的 VpkDirectories 块内补上击杀图标挂载行（幂等）
fn ensure_icon_include(cs2_path: &str) -> Result<(), String> {
    const NEEDLE: &str = "panorama/images/icons/equipment";
    let gi = csgo_dir(cs2_path).join("gameinfo.gi");
    let text = fs::read_to_string(&gi).map_err(|e| format!("读取 gameinfo.gi 失败: {}", e))?;
    if text.contains(NEEDLE) { return Ok(()); }

    // 锚点：VpkDirectories 块内的 "include" "addoninfo.txt"
    let re = regex::Regex::new(r#"(?m)^([ \t]*"include"[ \t]+"addoninfo\.txt"[ \t]*\r?\n)"#).unwrap();
    if !re.is_match(&text) {
        return Err("gameinfo.gi 结构异常：找不到 VpkDirectories 的 addoninfo.txt 锚点".into());
    }
    let out = re.replace(&text, |c: &regex::Captures| {
        let line = c.get(1).map(|m| m.as_str()).unwrap_or("");
        format!("{}\t\t\t\"include\"       \"{}\"\n", line, NEEDLE)
    }).to_string();

    for key in ["SetUILanguageOnSteamDropDown", "SupportedLanguages"] {
        if !out.contains(key) {
            return Err(format!("gameinfo.gi 校验失败：缺少 {}，已中止写入", key));
        }
    }
    fs::write(&gi, out).map_err(|e| format!("写入 gameinfo.gi 失败: {}", e))
}


// ═══════════════════════════════════════════
// 插件开关（重命名 .dll <-> .dll.off）
// ═══════════════════════════════════════════

fn toggle_plugin_files(cs2_path: &str, plugin: &Plugin, enabled: bool) {
    if cs2_path.is_empty() { return; }
    let target = Path::new(cs2_path).join("game").join("csgo").join(&plugin.dir);
    if !target.is_dir() { return; }

    // 递归处理：原生插件真正的 .dll 在 bin/win64/ 子目录里，只扫顶层会漏掉
    for entry in walkdir::WalkDir::new(&target).max_depth(4).into_iter().flatten() {
        let path = entry.path();
        if !path.is_file() { continue; }
        let name = path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        let is_dll = name.ends_with(".dll");
        let is_off = name.ends_with(".dll.off");
        if !is_dll && !is_off { continue; }

        let full = path.to_path_buf();
        let off = PathBuf::from(format!("{}.off", full.to_string_lossy()));
        if enabled {
            if is_off {
                if let Some(base) = full.to_string_lossy().strip_suffix(".off") {
                    let _ = fs::rename(&full, PathBuf::from(base));
                }
            }
        } else if is_dll && !off.exists() {
            let _ = fs::rename(&full, &off);
        }
    }
}

// ═══════════════════════════════════════════
// 安装离线包
// ═══════════════════════════════════════════

fn find_zip(resources_dir: &Path) -> Option<PathBuf> {
    let direct = resources_dir.join("CS2BotImprover.zip");
    if direct.is_file() { return Some(direct); }
    // 在 resources 下递归找
    for entry in walkdir::WalkDir::new(resources_dir).max_depth(3).into_iter().flatten() {
        let p = entry.path();
        if p.is_file() && p.file_name().map(|n| n == "CS2BotImprover.zip").unwrap_or(false) {
            return Some(p.to_path_buf());
        }
    }
    None
}

/// 安全解压 zip 到目标目录（防止路径穿越）
fn extract_zip(zip_path: &Path, dest: &Path) -> Result<(), String> {
    let file = fs::File::open(zip_path).map_err(|e| format!("无法打开安装包: {}", e))?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| format!("安装包损坏: {}", e))?;

    for i in 0..archive.len() {
        let mut entry = archive.by_index(i).map_err(|e| e.to_string())?;
        // 防路径穿越：必须是相对路径且不含 ..
        let Some(rel) = entry.enclosed_name() else { continue };
        let out_path = dest.join(&rel);

        if entry.is_dir() {
            fs::create_dir_all(&out_path).map_err(|e| e.to_string())?;
            continue;
        }
        if let Some(parent) = out_path.parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut outfile = fs::File::create(&out_path).map_err(|e| e.to_string())?;
        std::io::copy(&mut entry, &mut outfile).map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn copy_dir_all(src: &Path, dst: &Path) -> Result<(), String> {
    if !src.exists() { return Ok(()); }
    fs::create_dir_all(dst).map_err(|e| e.to_string())?;
    for entry in fs::read_dir(src).map_err(|e| e.to_string())?.flatten() {
        let from = entry.path();
        let to = dst.join(entry.file_name());
        if from.is_dir() {
            copy_dir_all(&from, &to)?;
        } else {
            fs::copy(&from, &to).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

fn install_plugins(cs2_path: &str, resources_dir: &Path, premier: &PremierConfig) -> Result<Value, String> {
    if cs2_path.is_empty() {
        return Err("未设置 CS2 安装路径".into());
    }
    let game_dir = Path::new(cs2_path).join("game").join("csgo");
    if !game_dir.is_dir() {
        return Err(format!("CS2 游戏目录不存在: {}", game_dir.display()));
    }

    let zip_path = find_zip(resources_dir)
        .ok_or_else(|| format!("未找到离线安装包 CS2BotImprover.zip（搜索目录: {}）", resources_dir.display()))?;

    let temp_dir = std::env::temp_dir().join(format!("xuerong_install_{}", chrono::Utc::now().timestamp()));
    fs::create_dir_all(&temp_dir).map_err(|e| e.to_string())?;

    extract_zip(&zip_path, &temp_dir)?;

    // 备份原有目录（放到游戏根目录下，不污染 game/）
    let stamp = chrono::Utc::now().format("%Y%m%d_%H%M%S").to_string();
    let backup_root = Path::new(cs2_path).join("_xuerong_backup").join(&stamp);
    // 备份会真正被覆盖的目录（panorama 只往里加图标，不备份，省 250MB 拷贝）
    let mut copied: Vec<String> = Vec::new();
    for d in ["addons", "overrides", "cfg", "backup", "panorama"] {
        let src = temp_dir.join(d);
        if !src.is_dir() { continue; }
        let dst = game_dir.join(d);
        if dst.exists() && ["addons", "overrides", "cfg"].contains(&d) {
            let _ = copy_dir_all(&dst, &backup_root.join(d));
        }
        copy_dir_all(&src, &dst)?;
        copied.push(d.to_string());
    }

    // 关键：修补 gameinfo.gi（只加需要的行，保护语言段）
    patch_gameinfo(cs2_path, true)?;
    ensure_icon_include(cs2_path)?;

    // 写入真正生效的插件配置
    let _ = premier;
    write_killfeed_config(cs2_path, "txz03");

    let _ = fs::remove_dir_all(&temp_dir);

    Ok(serde_json::json!({
        "success": true,
        "copied": copied,
        "installPath": cs2_path,
    }))
}

// ═══════════════════════════════════════════
// GitHub 更新检查
// ═══════════════════════════════════════════

async fn check_github_updates() -> Vec<Value> {
    let repos = [
        ("CS2-Bot-Improver", "ed0ard", "CS2-Bot-Improver"),
        ("CounterStrikeSharp", "roflmuffin", "CounterStrikeSharp"),
    ];
    let client = reqwest::Client::builder()
        .user_agent("XuerongBotManager/1.0")
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .unwrap_or_default();

    let mut out = Vec::new();
    for (name, owner, repo) in repos {
        let url = format!("https://api.github.com/repos/{}/{}/releases/latest", owner, repo);
        match client.get(&url).send().await {
            Ok(resp) if resp.status().is_success() => {
                match resp.json::<Value>().await {
                    Ok(data) => {
                        let tag = data.get("tag_name").and_then(|v| v.as_str()).unwrap_or("").to_string();
                        let published = data.get("published_at").and_then(|v| v.as_str()).unwrap_or("").to_string();
                        let notes = data.get("body").and_then(|v| v.as_str()).unwrap_or("");
                        let notes: String = notes.chars().take(300).collect();
                        let html = data.get("html_url").and_then(|v| v.as_str()).unwrap_or("").to_string();
                        out.push(serde_json::json!({
                            "name": name,
                            "repo": format!("{}/{}", owner, repo),
                            "latestVersion": tag,
                            "publishedAt": published,
                            "releaseUrl": html,
                            "notes": notes,
                            "available": true,
                        }));
                    }
                    Err(e) => out.push(serde_json::json!({
                        "name": name, "repo": format!("{}/{}", owner, repo),
                        "available": false, "error": e.to_string(),
                    })),
                }
            }
            Ok(resp) => out.push(serde_json::json!({
                "name": name, "repo": format!("{}/{}", owner, repo),
                "available": false, "error": format!("HTTP {}", resp.status()),
            })),
            Err(e) => out.push(serde_json::json!({
                "name": name, "repo": format!("{}/{}", owner, repo),
                "available": false, "error": e.to_string(),
            })),
        }
    }
    out
}

// ═══════════════════════════════════════════
// Tauri Commands
// ═══════════════════════════════════════════

#[tauri::command(async)]
fn get_state(app: TauriState<'_, AppState>) -> Value {
    let mut s = app.inner.lock().unwrap().clone();

    if s.auto_detected_path.is_empty() {
        let detected = detect_cs2_path();
        if !detected.is_empty() {
            s.auto_detected_path = detected.clone();
            app.inner.lock().unwrap().auto_detected_path = detected;
            let _ = app.save(&s);
        }
    }

    let installed = detect_installed(&s.cs2_path);
    let plugins_state: HashMap<String, PluginState> = s.plugins.iter()
        .map(|(k, v)| (k.clone(), PluginState { enabled: v.enabled, installed: installed.get(k).copied().unwrap_or(false) }))
        .collect();

    serde_json::json!({
        "cs2Path": s.cs2_path,
        "autoDetectedPath": s.auto_detected_path,
        "plugins": plugins_state,
        "installedMap": installed,
        "difficulty": detect_difficulty(&s.cs2_path).unwrap_or_else(|| s.difficulty.clone()),
        "installDate": s.install_date,
        "version": s.version,
        "onboarded": s.onboarded,
        "premierConfig": s.premier_config,
        "killfeedStyle": s.killfeed_style,
        "installed": {
            "metamod": Path::new(&s.cs2_path).join("game").join("csgo").join("addons").join("metamod").is_dir(),
            "css": Path::new(&s.cs2_path).join("game").join("csgo").join("addons").join("counterstrikesharp").is_dir(),
        },
        "pluginCatalog": catalog(),
    })
}

#[tauri::command(async)]
fn detect_path(app: TauriState<'_, AppState>) -> Value {
    let detected = detect_cs2_path();
    if !detected.is_empty() {
        let mut s = app.inner.lock().unwrap();
        s.auto_detected_path = detected.clone();
        let _ = app.save(&s);
    }
    serde_json::json!({ "path": detected })
}

#[tauri::command(async)]
fn set_path(app: TauriState<'_, AppState>, path: String) -> Value {
    if path.is_empty() {
        return serde_json::json!({ "error": "路径不能为空" });
    }
    let game_dir = Path::new(&path).join("game").join("csgo");
    if !game_dir.is_dir() {
        return serde_json::json!({ "error": "路径下未找到 game/csgo 目录，请确认是否为 CS2 根目录" });
    }
    let mut s = app.inner.lock().unwrap();
    s.cs2_path = path.clone();
    let installed = detect_installed(&path);
    for p in catalog() {
        s.plugins.entry(p.id).or_default().installed = installed.get(&p.id).copied().unwrap_or(false);
    }
    let _ = app.save(&s);
    serde_json::json!({ "success": true, "path": path, "installedMap": installed })
}

#[tauri::command(async)]
fn toggle_plugin(app: TauriState<'_, AppState>, plugin_id: String, enabled: bool) -> Value {
    let plugin = match catalog().into_iter().find(|p| p.id == plugin_id) {
        Some(p) => p,
        None => return serde_json::json!({ "error": "未知插件" }),
    };
    if plugin.always_on {
        return serde_json::json!({ "error": "核心框架不可关闭" });
    }
    if is_cs2_running() {
        return serde_json::json!({ "error": "CS2 正在运行，请先退出游戏再切换插件" });
    }

    let mut s = app.inner.lock().unwrap();
    let cs2_path = s.cs2_path.clone();
    let installed = s.plugins.get(&plugin_id).map(|p| p.installed).unwrap_or(false);

    s.plugins.entry(plugin_id.clone()).or_default().enabled = enabled;

    if !cs2_path.is_empty() && installed {
        toggle_plugin_files(&cs2_path, &plugin, enabled);

        // 击杀图标的行为由配置决定，开关后立刻重写选包
        if plugin_id == "Killfeed_Icons" {
            write_killfeed_config(&cs2_path, &s.killfeed_style);
        }
    }
    let _ = app.save(&s);
    serde_json::json!({ "success": true, "pluginId": plugin_id, "enabled": enabled })
}

#[tauri::command(async)]
fn install(app: TauriState<'_, AppState>, cs2_path: Option<String>) -> Value {
    let (target, resources, premier) = {
        let s = app.inner.lock().unwrap();
        let t = cs2_path
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| {
                if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() }
            });
        (t, app.resources_dir.clone(), s.premier_config.clone())
    };

    if target.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 安装路径" });
    }
    if is_cs2_running() {
        return serde_json::json!({ "error": "CS2 正在运行，请先退出游戏再安装（运行中改插件会导致闪退）" });
    }

    match install_plugins(&target, &resources, &premier) {
        Ok(v) => {
            let mut s = app.inner.lock().unwrap();
            s.cs2_path = target.clone();
            s.install_date = Some(chrono::Utc::now().to_rfc3339());
            let installed = detect_installed(&target);
            // 安装完成即默认全部启用（框架常驻，其余按安装结果全开）
            for p in catalog() {
                let entry = s.plugins.entry(p.id.clone()).or_default();
                entry.installed = installed.get(&p.id).copied().unwrap_or(false);
                if entry.installed {
                    entry.enabled = true;
                }
            }
            let _ = app.save(&s);
            // 安装后按当前设置重写管理器 cfg（bot 物品 / 预设 / 规则）
            regenerate_manager_cfg(&target, &s);
            mirror_rules_to_presets(&target, &s);
            // 只开换肤模式下，安装会把插件 dll 重新拷回来，必须再关一次
            if s.skin_only {
                for plugin in catalog() {
                    if SKIN_MODE_KEEP.contains(&plugin.id.as_str()) { continue; }
                    toggle_plugin_files(&target, &plugin, false);
                    if NATIVE_VDF.contains(&plugin.id.as_str()) {
                        set_vdf_enabled(&target, &plugin.id, false);
                    }
                }
            }
            let count = installed.values().filter(|v| **v).count();
            serde_json::json!({
                "success": true,
                "copied": v.get("copied").cloned().unwrap_or(Value::Null),
                "installedCount": count,
                "installPath": target,
            })
        }
        Err(e) => serde_json::json!({ "error": e }),
    }
}

#[tauri::command(async)]
fn uninstall(app: TauriState<'_, AppState>) -> Value {
    let cs2_path = { app.inner.lock().unwrap().cs2_path.clone() };
    if cs2_path.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 路径" });
    }
    if is_cs2_running() {
        return serde_json::json!({ "error": "CS2 正在运行，请先退出游戏再卸载" });
    }

    // 注意：插件真实路径是 game/csgo/addons，不是 game/addons
    let csgo = csgo_dir(&cs2_path);
    let addons = csgo.join("addons");
    let overrides = csgo.join("overrides");
    let stamp = chrono::Utc::now().format("%Y%m%d_%H%M%S").to_string();
    let backup_root = Path::new(&cs2_path)
        .join("_xuerong_backup")
        .join(format!("uninstall_{}", stamp));

    // 先把 gameinfo 还原成官方态（只摘掉自己加的两行，保护语言段）
    let gi_result = patch_gameinfo(&cs2_path, false);

    // 目标目录必须先建出来，否则 rename 会因父目录不存在而失败（之前就是这么静默失败的）
    if let Err(e) = fs::create_dir_all(&backup_root) {
        return serde_json::json!({ "error": format!("创建备份目录失败: {}", e) });
    }

    let mut moved: Vec<String> = Vec::new();
    let mut errors: Vec<String> = Vec::new();
    for (src, name) in [(&addons, "addons"), (&overrides, "overrides")] {
        if !src.exists() { continue; }
        let dst = backup_root.join(name);
        match fs::rename(src, &dst) {
            Ok(_) => moved.push(name.to_string()),
            Err(e) => errors.push(format!("{}: {}", name, e)),
        }
    }

    if !errors.is_empty() {
        return serde_json::json!({
            "error": format!("部分文件移动失败（游戏可能仍占用文件）：{}", errors.join("; ")),
            "moved": moved,
        });
    }

    let mut s = app.inner.lock().unwrap();
    for p in catalog() {
        let entry = s.plugins.entry(p.id.clone()).or_default();
        entry.installed = false;
        if !p.always_on {
            entry.enabled = false;
        }
    }
    s.install_date = None;
    let _ = app.save(&s);

    serde_json::json!({
        "success": true,
        "moved": moved,
        "backupDir": backup_root.to_string_lossy(),
        "gameinfo": gi_result.err().unwrap_or_else(|| "ok".into()),
    })
}

// ── 难度档位（overrides/High|Medium|Low/botprofile.vpk → overrides/botprofile.vpk） ──

fn overrides_dir(cs2_path: &str) -> PathBuf {
    csgo_dir(cs2_path).join("overrides")
}

fn difficulty_levels() -> [&'static str; 3] { ["Low", "Medium", "High"] }

/// 难度档位 → bot_difficulty 数值。
/// 光换 overrides 里的 botprofile.vpk 不够：游戏里显示/生效的难度是
/// bot_difficulty 这个 convar（预设 cfg 里写死 5），不跟着改就永远是"高"。
fn difficulty_convar(level: &str) -> (&'static str, &'static str) {
    match level {
        "Low" => ("1", "1"),
        "High" => ("5", "5"),
        _ => ("3", "3"),
    }
}

fn read_same(a: &Path, b: &Path) -> bool {
    match (fs::read(a), fs::read(b)) {
        (Ok(x), Ok(y)) => x == y,
        _ => false,
    }
}

/// 当前生效的难度：把 overrides/botprofile.vpk 跟三个档位文件逐个比对
fn detect_difficulty(cs2_path: &str) -> Option<String> {
    if cs2_path.is_empty() { return None; }
    let dir = overrides_dir(cs2_path);
    let active = dir.join("botprofile.vpk");
    if !active.is_file() { return None; }
    for lv in difficulty_levels() {
        let f = dir.join(lv).join("botprofile.vpk");
        if f.is_file() && read_same(&active, &f) {
            return Some(lv.to_string());
        }
    }
    None
}

#[tauri::command(async)]
fn get_difficulty(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    let dir = overrides_dir(&cs2);
    let available: Vec<String> = difficulty_levels()
        .iter()
        .filter(|lv| dir.join(lv).join("botprofile.vpk").is_file())
        .map(|lv| lv.to_string())
        .collect();
    serde_json::json!({
        "current": detect_difficulty(&cs2),
        "available": available,
        "activePresent": dir.join("botprofile.vpk").is_file(),
        "cs2Running": is_cs2_running(),
    })
}

#[tauri::command(async)]
fn set_difficulty(app: TauriState<'_, AppState>, difficulty: String) -> Value {
    if !difficulty_levels().contains(&difficulty.as_str()) {
        return err("难度只能是 Low / Medium / High");
    }
    if is_cs2_running() {
        return err("CS2 正在运行，请先完全退出再换难度");
    }
    let cs2 = {
        let mut s = app.inner.lock().unwrap();
        let p = effective_path(&s);
        if p.is_empty() { return err("未设置 CS2 路径"); }
        s.difficulty = difficulty.clone();
        let _ = app.save(&s);
        p
    };

    let dir = overrides_dir(&cs2);
    let src = dir.join(&difficulty).join("botprofile.vpk");
    if !src.is_file() {
        return err(&format!("缺少档位文件 overrides/{}/botprofile.vpk", difficulty));
    }
    let dst = dir.join("botprofile.vpk");

    // 第一次改动前留一份原文件，方便回到出厂状态
    let bak = dir.join("botprofile.vpk.bak");
    if dst.is_file() && !bak.is_file() {
        let _ = fs::copy(&dst, &bak);
    }
    if let Err(e) = fs::copy(&src, &dst) {
        return err(&format!("写入 botprofile.vpk 失败: {}", e));
    }

    // 同步 bot_difficulty / custom_bot_difficulty：这才是游戏里实际读的难度
    let snap = app.inner.lock().unwrap().clone();
    mirror_rules_to_presets(&cs2, &snap);
    regenerate_manager_cfg(&cs2, &snap);
    let (bd, _) = difficulty_convar(&difficulty);

    serde_json::json!({
        "success": true,
        "current": detect_difficulty(&cs2),
        "difficulty": difficulty,
        "botDifficulty": bd,
    })
}

#[tauri::command(async)]
fn set_premier(app: TauriState<'_, AppState>, config: Value) -> Value {
    let mut s = app.inner.lock().unwrap();
    if let Some(v) = config.get("rating").and_then(|v| v.as_u64()) {
        s.premier_config.rating = v.clamp(100, 35000) as u32;
    }
    if let Some(v) = config.get("xpBoost").and_then(|v| v.as_f64()) {
        s.premier_config.xp_boost = v;
    }
    if let Some(v) = config.get("lightningIcon").and_then(|v| v.as_bool()) {
        s.premier_config.lightning_icon = v;
    }
    if let Some(v) = config.get("badgeStyle").and_then(|v| v.as_str()) {
        s.premier_config.badge_style = v.to_string();
    }
    if let Some(v) = config.get("randomizeBadge").and_then(|v| v.as_bool()) {
        s.premier_config.randomize_badge = v;
    }
    let cfg = s.premier_config.clone();
    let _ = app.save(&s);

    let rank = rank_type_from_rating(cfg.rating);
    serde_json::json!({
        "success": true,
        "premierConfig": cfg,
        "rankType": rank,
        "badgeLabel": badge_label(rank),
    })
}

#[tauri::command(async)]
fn set_killfeed(app: TauriState<'_, AppState>, style: String) -> Value {
    let mut s = app.inner.lock().unwrap();
    s.killfeed_style = style.clone();
    let cs2_path = s.cs2_path.clone();
    let _ = app.save(&s);

    if !cs2_path.is_empty() {
        write_killfeed_config(&cs2_path, &style);
    }

    serde_json::json!({ "success": true, "killfeedStyle": style, "applied": !cs2_path.is_empty() })
}

/// 一键把全部已安装插件切到启用/停用
#[tauri::command(async)]
fn set_all_plugins(app: TauriState<'_, AppState>, enabled: bool) -> Value {
    let mut s = app.inner.lock().unwrap();
    let cs2_path = s.cs2_path.clone();
    let mut changed = 0;
    for p in catalog() {
        if p.always_on { continue; }
        let entry = s.plugins.entry(p.id.clone()).or_default();
        if !entry.installed { continue; }
        if entry.enabled != enabled {
            entry.enabled = enabled;
            changed += 1;
        }
        if !cs2_path.is_empty() {
            toggle_plugin_files(&cs2_path, &p, enabled);
        }
    }
    let _ = app.save(&s);
    serde_json::json!({ "success": true, "changed": changed, "enabled": enabled })
}

/// 重新下发所有配置（换机器/换路径后一键修复）
#[tauri::command(async)]
fn apply_configs(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2_path = if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() };
    if cs2_path.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 路径" });
    }

    // 1) gameinfo：挂 metamod 搜索路径 + 击杀图标 include
    let gi = patch_gameinfo(&cs2_path, true);
    let icon = ensure_icon_include(&cs2_path);
    // 2) 管理器 cfg + 三套预设镜像（Bot 物品 / 预设 / 规则）
    regenerate_manager_cfg(&cs2_path, &s);
    mirror_rules_to_presets(&cs2_path, &s);
    // 3) 击杀图标选包
    write_killfeed_config(&cs2_path, &s.killfeed_style);

    let mut problems: Vec<String> = Vec::new();
    if let Err(e) = gi { problems.push(e); }
    if let Err(e) = icon { problems.push(e); }

    serde_json::json!({
        "success": problems.is_empty(),
        "error": if problems.is_empty() { Value::Null } else { Value::String(problems.join("; ")) },
    })
}

/// 扫描崩溃转储，判断上一局是否崩过（崩溃排查用）
#[tauri::command(async)]
fn get_crashes(app: TauriState<'_, AppState>) -> Value {
    let (cs2_path, last) = {
        let s = app.inner.lock().unwrap();
        (s.cs2_path.clone(), s.last_launch.clone())
    };
    if cs2_path.is_empty() {
        return serde_json::json!({ "crashes": [], "sinceLaunch": false });
    }
    let last_ts = last
        .as_deref()
        .and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok())
        .map(|d| d.timestamp().max(0) as u64);

    let dumps = scan_crash_dumps(&cs2_path);
    let crashes: Vec<Value> = dumps
        .iter()
        .take(10)
        .map(|(name, mtime, size)| {
            serde_json::json!({
                "name": name,
                "mtime": mtime,
                "size": size,
                "afterLaunch": last_ts.map(|t| *mtime >= t).unwrap_or(false),
            })
        })
        .collect();
    let since = crashes
        .iter()
        .any(|c| c.get("afterLaunch").and_then(|v| v.as_bool()).unwrap_or(false));

    serde_json::json!({ "crashes": crashes, "sinceLaunch": since })
}

/// 安全模式：关掉最容易引发闪退的插件，只保留框架与核心 AI
#[tauri::command(async)]
fn safe_mode(app: TauriState<'_, AppState>) -> Value {
    const RISKY: [&str; 6] = [
        "BotRandomizer",
        "InventorySimulator",
        "NadeSystem",
        "PremierShowcase2",
        "BotHider",
        "BotVision",
    ];
    let mut s = app.inner.lock().unwrap();
    let cs2_path = s.cs2_path.clone();
    let mut turned_off = Vec::new();

    for p in catalog() {
        if p.always_on || !RISKY.contains(&p.id.as_str()) {
            continue;
        }
        let entry = s.plugins.entry(p.id.clone()).or_default();
        if entry.installed && entry.enabled {
            entry.enabled = false;
            turned_off.push(p.name.clone());
            if !cs2_path.is_empty() {
                toggle_plugin_files(&cs2_path, &p, false);
            }
        }
    }
    let _ = app.save(&s);

    serde_json::json!({ "success": true, "disabled": turned_off })
}

/// 关闭窗口（走 Rust 侧，避免前端权限缺失导致点 × 没反应）
#[tauri::command(async)]
fn close_window(app: tauri::AppHandle) -> Value {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.close();
    } else {
        app.exit(0);
    }
    serde_json::json!({ "success": true })
}

#[tauri::command(async)]
fn minimize_window(app: tauri::AppHandle) -> Value {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.minimize();
    }
    serde_json::json!({ "success": true })
}

#[tauri::command(async)]
fn mark_onboarded(app: TauriState<'_, AppState>) -> Value {
    let mut s = app.inner.lock().unwrap();
    s.onboarded = true;
    let _ = app.save(&s);
    serde_json::json!({ "success": true })
}

#[tauri::command(async)]
async fn check_updates() -> Value {
    let updates = check_github_updates().await;
    serde_json::json!({ "updates": updates })
}

/// 用系统文件对话框选择 CS2 目录
#[tauri::command(async)]
async fn pick_cs2_folder(app: tauri::AppHandle) -> Result<Option<String>, String> {
    use tauri_plugin_dialog::DialogExt;
    let (tx, rx) = tokio::sync::oneshot::channel::<Option<String>>();
    let tx = std::sync::Arc::new(std::sync::Mutex::new(Some(tx)));
    let tx_clone = tx.clone();
    app.dialog().file().pick_folder(move |result| {
        if let Some(sender) = tx_clone.lock().unwrap().take() {
            let path_str = result.map(|f| f.to_string());
            let _ = sender.send(path_str);
        }
    });
    let result = rx.await.map_err(|e| e.to_string())?;
    Ok(result)
}

/// 在文件管理器中打开目录
#[tauri::command(async)]
fn open_folder(app: tauri::AppHandle, path: String) -> Value {
    use tauri_plugin_opener::OpenerExt;
    let _ = app.opener().open_path(path, None::<&str>);
    serde_json::json!({ "success": true })
}

/// 打开外部链接
#[tauri::command(async)]
fn open_url(app: tauri::AppHandle, url: String) -> Value {
    use tauri_plugin_opener::OpenerExt;
    let _ = app.opener().open_url(url, None::<&str>);
    serde_json::json!({ "success": true })
}

/// 暴露打包进 exe 的资源目录路径（供前端展示）
#[tauri::command(async)]
fn resources_dir(app: TauriState<'_, AppState>) -> Value {
    serde_json::json!({ "path": app.resources_dir.to_string_lossy() })
}

/// 启动 CS2
/// mode: "insecure" = 带 -insecure 参数（允许注入/插件），"normal" = 官方原版启动
#[tauri::command(async)]
fn launch_cs2(app: TauriState<'_, AppState>, mode: String) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2_path = if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() };

    if cs2_path.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 安装路径" });
    }

    // CS2 可执行文件路径：cs2.exe 在 game/bin/win64/ 下
    let exe_path = Path::new(&cs2_path).join("game").join("bin").join("win64").join("cs2.exe");
    if !exe_path.is_file() {
        return serde_json::json!({ "error": format!("未找到 CS2 可执行文件: {}", exe_path.display()) });
    }

    let insecure = mode == "insecure";

    // 启动前先确保 gameinfo 状态正确：插件模式必须挂上 metamod 搜索路径
    if let Err(e) = patch_gameinfo(&cs2_path, insecure) {
        return serde_json::json!({ "error": e });
    }

    if insecure {
        // 徽章档位轮换：PremierBadgeMvp 的 RankType 是全局值，单局内无法逐 bot 变色，
        // 只能做到每局开打前换一档。
        if s.premier_config.randomize_badge {
            let seed = chrono::Utc::now().timestamp_subsec_nanos();
            let rt = (seed % 18 + 1) as u64;
            let mut cfg = read_badge(&cs2_path);
            if let Some(o) = cfg.as_object_mut() {
                o.insert("RankType".into(), serde_json::json!(rt));
            }
            let p = badge_path(&cs2_path);
            if p.is_file() {
                let _ = fs::write(&p, serde_json::to_string_pretty(&cfg).unwrap_or_default());
            }
        }

        // 启动前把管理器设置重新落盘，防止被预设 cfg 覆盖
        regenerate_manager_cfg(&cs2_path, &s);
        mirror_rules_to_presets(&cs2_path, &s);

        // 直接拉起 cs2.exe：-steam 让游戏接管 Steam，-insecure 允许第三方插件加载
        let spawned = cmd(&exe_path)
            .args(["-steam", "-insecure", "-console"])
            .current_dir(exe_path.parent().unwrap_or(Path::new(".")))
            .spawn();

        // 记录本次启动时间，用于判断之后出现的 mdmp 是否属于这一局
        {
            let mut st = app.inner.lock().unwrap();
            st.last_launch = Some(chrono::Utc::now().to_rfc3339());
            let _ = app.save(&st);
        }

        match spawned {
            Ok(_) => serde_json::json!({
                "success": true,
                "mode": mode,
                "message": "CS2 正在以插件模式启动（Metamod / CSS 已挂载）"
            }),
            Err(_) => {
                // 兜底：走 Steam 协议
                let url = "steam://run/730//-insecure";
                let _ = cmd("cmd").args(["/C", "start", "", url]).spawn();
                serde_json::json!({
                    "success": true,
                    "mode": mode,
                    "message": "已通过 Steam 以插件模式启动"
                })
            }
        }
    } else {
        let result = cmd("cmd")
            .args(["/C", "start", "", "steam://run/730"])
            .spawn();

        match result {
            Ok(_) => serde_json::json!({
                "success": true,
                "mode": mode,
                "message": "CS2 正在以官方原版模式启动（插件不加载）"
            }),
            Err(e) => serde_json::json!({ "error": format!("启动失败: {}", e) }),
        }
    }
}

// ═══════════════════════════════════════════
// 规则引擎 / Bot 物品 / 预设 / 徽章 / 清单校验
// 对齐 CS2-Bot-Improver Panel v1.4.7 的命令面
// ═══════════════════════════════════════════

const MOLOTOV_BASE_DAMAGE: f64 = 40.0;
const AUTOEXEC_BEGIN: &str = "// === XUERONG MANAGER BEGIN ===";
const AUTOEXEC_END: &str = "// === XUERONG MANAGER END ===";
const MGR_CFG: &str = "xuerong_manager.cfg";

fn err(msg: &str) -> Value {
    serde_json::json!({ "error": msg })
}

/// 取有效 CS2 路径：用户手选优先，否则用自动探测
fn effective_path(s: &AppStateData) -> String {
    if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() }
}

/// 所有 my_bot_*_config.cfg（normal / ffa / rush 三套预设）
fn bot_cfg_files(cs2_path: &str) -> Vec<PathBuf> {
    let dir = csgo_dir(cs2_path).join("cfg");
    let mut out = Vec::new();
    if let Ok(rd) = fs::read_dir(&dir) {
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if name.starts_with("my_bot_") && name.ends_with(".cfg") {
                out.push(e.path());
            }
        }
    }
    out.sort();
    out
}


/// 设置 convar：已有则整行替换，没有则追加到末尾
fn cfg_set(text: &str, key: &str, value: &str) -> String {
    let mut out = String::with_capacity(text.len() + 40);
    let mut done = false;
    for line in text.lines() {
        let l = line.trim();
        let hit = !l.is_empty()
            && !l.starts_with("//")
            && l.split_whitespace().next().map(|k| k.eq_ignore_ascii_case(key)).unwrap_or(false);
        if hit && !done {
            out.push_str(key);
            out.push(' ');
            out.push_str(value);
            out.push('\n');
            done = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !done {
        out.push_str(key);
        out.push(' ');
        out.push_str(value);
        out.push('\n');
    }
    out
}

/// 确保 autoexec.cfg 里有一段 exec 我们的 cfg（不动别人的内容）
fn ensure_autoexec(cs2_path: &str) {
    let p = csgo_dir(cs2_path).join("cfg").join("autoexec.cfg");
    let mut text = fs::read_to_string(&p).unwrap_or_default();
    if text.contains(AUTOEXEC_BEGIN) { return; }
    if !text.is_empty() && !text.ends_with('\n') { text.push('\n'); }
    text.push_str(&format!("{}\nexec {}\n{}\n", AUTOEXEC_BEGIN, MGR_CFG, AUTOEXEC_END));
    let _ = fs::write(&p, text);
}

fn bot_items_default() -> HashMap<String, bool> {
    let mut m = HashMap::new();
    for k in ["weapons", "knives", "gloves", "stickers", "charms", "agents", "music"] {
        m.insert(k.to_string(), true);
    }
    m
}

fn rules_default() -> HashMap<String, bool> {
    let mut m = HashMap::new();
    m.insert("overtime".into(), true);
    m.insert("friendly_fire".into(), false);
    m.insert("boosting".into(), true);
    m.insert("bhop".into(), false);
    m
}

fn d_c4() -> u32 { 50 }
fn d_molotov() -> f64 { 0.5 }
fn d_aim() -> String { "mixed".into() }
fn d_nades() -> String { "normal".into() }

/// 把难度写成 convar，同时落到管理器 cfg 和三套预设
fn write_difficulty_convars(cs2_path: &str, bot_diff: &str, custom_diff: &str) {
    if cs2_path.is_empty() { return; }
    for p in bot_cfg_files(cs2_path) {
        if let Ok(mut t) = fs::read_to_string(&p) {
            t = cfg_set(&t, "bot_difficulty", bot_diff);
            t = cfg_set(&t, "custom_bot_difficulty", custom_diff);
            let _ = fs::write(&p, t);
        }
    }
    let mgr = csgo_dir(cs2_path).join("cfg").join(MGR_CFG);
    if let Ok(mut t) = fs::read_to_string(&mgr) {
        t = cfg_set(&t, "bot_difficulty", bot_diff);
        t = cfg_set(&t, "custom_bot_difficulty", custom_diff);
        let _ = fs::write(&mgr, t);
    }
}

/// 整体重写管理器自管 cfg（这个文件归我们所有，不做增量修改）
fn regenerate_manager_cfg(cs2_path: &str, s: &AppStateData) {
    if cs2_path.is_empty() { return; }
    let cfg_dir = csgo_dir(cs2_path).join("cfg");
    let _ = fs::create_dir_all(&cfg_dir);
    let items = if s.bot_items.is_empty() { bot_items_default() } else { s.bot_items.clone() };
    let rules = if s.rules.is_empty() { rules_default() } else { s.rules.clone() };

    let mut t = String::new();
    t.push_str("// 由雪绒人机管理器生成 —— 手改会被下一次保存覆盖\n\n");
    t.push_str("// ── Bot 饰品随机（BotRandomizer 命令） ──\n");
    for k in ["weapons", "knives", "gloves", "stickers", "charms", "agents", "music"] {
        let on = items.get(k).copied().unwrap_or(true);
        t.push_str(&format!("bot_randomizer {} {}\n", k, if on { 1 } else { 0 }));
    }
    t.push_str("\n// ── 预设 ──\n");
    t.push_str(&format!("bot_aim {}\n", s.aim));
    t.push_str(&format!("bot_nades {}\n", s.nades));
    t.push_str("\n// ── 规则 ──\n");
    t.push_str(&format!("mp_overtime_enable {}\n", if rules.get("overtime").copied().unwrap_or(true) { 1 } else { 0 }));
    t.push_str(&format!("mp_friendlyfire {}\n", if rules.get("friendly_fire").copied().unwrap_or(false) { 1 } else { 0 }));
    // mp_solid_enemies 0 = 敌人可互相穿过 = 允许叠人
    t.push_str(&format!("mp_solid_enemies {}\n", if rules.get("boosting").copied().unwrap_or(true) { 0 } else { 1 }));
    let bhop = if rules.get("bhop").copied().unwrap_or(false) { 1 } else { 0 };
    t.push_str(&format!("sv_enablebunnyhopping {}\n", bhop));
    t.push_str(&format!("sv_autobunnyhopping {}\n", bhop));
    t.push_str(&format!("mp_c4timer {}\n", s.c4_timer));
    // 难度：游戏里显示的就是这两个值，必须跟 overrides 的档位一致
    let lv = detect_difficulty(cs2_path).unwrap_or_else(|| s.difficulty.clone());
    let (bd, cbd) = difficulty_convar(&lv);
    t.push_str(&format!("bot_difficulty {}\n", bd));
    t.push_str(&format!("custom_bot_difficulty {}\n", cbd));
    let dmg = MOLOTOV_BASE_DAMAGE * s.molotov_mult;
    t.push_str(&format!("inferno_damage {:.6}\n", dmg));
    t.push_str(&format!("inferno_damage_ct {:.6}\n", dmg));

    let _ = fs::write(cfg_dir.join(MGR_CFG), t);
    ensure_autoexec(cs2_path);
}

/// 设置「命令 + 第一个参数」形式的行（如 bot_randomizer weapons 1）
/// 与 cfg_set 的区别：匹配两个 token，因此同一个命令的多行不会互相覆盖。
fn cfg_set_pair(text: &str, cmd: &str, arg: &str, value: &str) -> String {
    let mut out = String::with_capacity(text.len() + 48);
    let mut done = false;
    for line in text.lines() {
        let l = line.trim();
        let mut it = l.split_whitespace();
        let hit = !l.is_empty()
            && !l.starts_with("//")
            && it.next().map(|k| k.eq_ignore_ascii_case(cmd)).unwrap_or(false)
            && it.next().map(|k| k.eq_ignore_ascii_case(arg)).unwrap_or(false);
        if hit && !done {
            out.push_str(cmd);
            out.push(' ');
            out.push_str(arg);
            out.push(' ');
            out.push_str(value);
            out.push('\n');
            done = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !done {
        out.push_str(cmd);
        out.push(' ');
        out.push_str(arg);
        out.push(' ');
        out.push_str(value);
        out.push('\n');
    }
    out
}

/// 规则同时写进三套预设 cfg，避免加载预设时把值冲掉
fn mirror_rules_to_presets(cs2_path: &str, s: &AppStateData) {
    let rules = if s.rules.is_empty() { rules_default() } else { s.rules.clone() };
    let items = if s.bot_items.is_empty() { bot_items_default() } else { s.bot_items.clone() };
    let pairs: Vec<(&str, String)> = vec![
        ("mp_overtime_enable", if rules.get("overtime").copied().unwrap_or(true) { "1".into() } else { "0".into() }),
        ("mp_friendlyfire", if rules.get("friendly_fire").copied().unwrap_or(false) { "1".into() } else { "0".into() }),
        ("mp_solid_enemies", if rules.get("boosting").copied().unwrap_or(true) { "0".into() } else { "1".into() }),
        ("sv_enablebunnyhopping", if rules.get("bhop").copied().unwrap_or(false) { "1".into() } else { "0".into() }),
        ("sv_autobunnyhopping", if rules.get("bhop").copied().unwrap_or(false) { "1".into() } else { "0".into() }),
        ("mp_c4timer", s.c4_timer.to_string()),
        ("inferno_damage", format!("{:.6}", MOLOTOV_BASE_DAMAGE * s.molotov_mult)),
        ("inferno_damage_ct", format!("{:.6}", MOLOTOV_BASE_DAMAGE * s.molotov_mult)),
        // 预设也是命令，写在 cfg 里等玩家 exec 时执行
        ("bot_aim", s.aim.clone()),
        ("bot_nades", s.nades.clone()),
    ];
    // 难度：以磁盘上实际生效的档位为准
    let lv = detect_difficulty(cs2_path).unwrap_or_else(|| s.difficulty.clone());
    let (bd, cbd) = difficulty_convar(&lv);
    let mut pairs = pairs;
    pairs.push(("bot_difficulty", bd.to_string()));
    pairs.push(("custom_bot_difficulty", cbd.to_string()));
    for p in bot_cfg_files(cs2_path) {
        if let Ok(mut text) = fs::read_to_string(&p) {
            for (k, v) in &pairs {
                text = cfg_set(&text, k, v);
            }
            // bot_randomizer 是 CSS 命令，必须在插件加载后执行（即 exec 预设时）
            for k in ["weapons", "knives", "gloves", "stickers", "charms", "agents", "music"] {
                let on = items.get(k).copied().unwrap_or(true);
                text = cfg_set_pair(&text, "bot_randomizer", k, if on { "1" } else { "0" });
            }
            let _ = fs::write(&p, text);
        }
    }
}

fn read_rules(s: &AppStateData, cs2_path: &str) -> Value {
    let rules = if s.rules.is_empty() { rules_default() } else { s.rules.clone() };
    serde_json::json!({
        "cfgPresent": !bot_cfg_files(cs2_path).is_empty(),
        "overtime": rules.get("overtime").copied().unwrap_or(true),
        "friendlyFire": rules.get("friendly_fire").copied().unwrap_or(false),
        "boosting": rules.get("boosting").copied().unwrap_or(true),
        "bhop": rules.get("bhop").copied().unwrap_or(false),
        "c4Timer": s.c4_timer,
        "molotovMultiplier": s.molotov_mult,
    })
}

#[tauri::command(async)]
fn get_rules(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    read_rules(&s, &cs2)
}

#[tauri::command(async)]
fn set_rule(app: TauriState<'_, AppState>, rule: String, value: bool) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出再改规则"); }
    let (cs2, snapshot) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        if g.rules.is_empty() { g.rules = rules_default(); }
        match rule.as_str() {
            "overtime" | "friendly_fire" | "boosting" | "bhop" => {
                g.rules.insert(rule.clone(), value);
            }
            other => return err(&format!("未知规则: {}", other)),
        }
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snapshot);
    mirror_rules_to_presets(&cs2, &snapshot);
    serde_json::json!({ "success": true, "rules": read_rules(&snapshot, &cs2) })
}

#[tauri::command(async)]
fn set_c4_timer(app: TauriState<'_, AppState>, seconds: u32) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        g.c4_timer = seconds.clamp(10, 120);
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "rules": read_rules(&snap, &cs2) })
}

#[tauri::command(async)]
fn set_molotov_multiplier(app: TauriState<'_, AppState>, multiplier: f64) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        g.molotov_mult = (multiplier.clamp(0.1, 3.0) * 100.0).round() / 100.0;
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "rules": read_rules(&snap, &cs2) })
}

#[tauri::command(async)]
fn reset_rules(app: TauriState<'_, AppState>) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        g.rules = rules_default();
        g.c4_timer = d_c4();
        g.molotov_mult = d_molotov();
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "rules": read_rules(&snap, &cs2) })
}

// ── Bot 物品（BotRandomizer 七个开关 + 身份伪装） ──

#[tauri::command(async)]
fn get_bot_items(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    let items = if s.bot_items.is_empty() { bot_items_default() } else { s.bot_items.clone() };
    let hider = csgo_dir(&cs2).join("addons").join("BotHider").join("bin").join("win64").join("BotHider.dll");
    let mut out = serde_json::Map::new();
    for k in ["weapons", "knives", "gloves", "stickers", "charms", "agents", "music"] {
        out.insert(k.into(), Value::Bool(items.get(k).copied().unwrap_or(true)));
    }
    out.insert("profiles".into(), Value::Bool(hider.is_file()));
    out.insert("cfgPresent".into(), Value::Bool(!cs2.is_empty()));
    Value::Object(out)
}

#[tauri::command(async)]
fn set_bot_item(app: TauriState<'_, AppState>, item: String, on: bool) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    // profiles 走插件目录开关（BotHider），其余走 bot_randomizer 命令
    if item == "profiles" {
        let s = app.inner.lock().unwrap().clone();
        let cs2 = effective_path(&s);
        if let Some(p) = catalog().into_iter().find(|p| p.id == "BotHider") {
            toggle_plugin_files(&cs2, &p, on);
        }
        return serde_json::json!({ "success": true, "items": get_bot_items(app) });
    }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        if g.bot_items.is_empty() { g.bot_items = bot_items_default(); }
        if !g.bot_items.contains_key(&item) { return err(&format!("未知物品: {}", item)); }
        g.bot_items.insert(item.clone(), on);
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "items": get_bot_items(app) })
}

// ── 预设（瞄准 / 投掷物） ──

#[tauri::command(async)]
fn get_presets(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    serde_json::json!({
        "aim": s.aim,
        "nades": s.nades,
        "cfgPresent": !bot_cfg_files(&effective_path(&s)).is_empty(),
    })
}

#[tauri::command(async)]
fn set_aim(app: TauriState<'_, AppState>, value: String) -> Value {
    if !["head", "body", "mixed"].contains(&value.as_str()) {
        return err("瞄准模式只能是 head / body / mixed");
    }
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        g.aim = value.clone();
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "aim": value })
}

#[tauri::command(async)]
fn set_nades(app: TauriState<'_, AppState>, value: String) -> Value {
    if !["off", "less", "normal", "more", "max"].contains(&value.as_str()) {
        return err("投掷物档位只能是 off / less / normal / more / max");
    }
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let (cs2, snap) = {
        let mut g = app.inner.lock().unwrap();
        let cs2 = effective_path(&g);
        if cs2.is_empty() { return err("未设置 CS2 路径"); }
        g.nades = value.clone();
        let snap = g.clone();
        let _ = app.save(&g);
        (cs2, snap)
    };
    regenerate_manager_cfg(&cs2, &snap);
    mirror_rules_to_presets(&cs2, &snap);
    serde_json::json!({ "success": true, "nades": value })
}

// ── PremierBadgeMvp（内联 config.json） ──

fn badge_path(cs2_path: &str) -> PathBuf {
    csgo_dir(cs2_path)
        .join("addons").join("counterstrikesharp").join("plugins")
        .join("PremierBadgeMvp").join("config.json")
}

fn read_badge(cs2_path: &str) -> Value {
    let p = badge_path(cs2_path);
    let mut v: Value = fs::read_to_string(&p)
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_else(|| serde_json::json!({}));
    if let Some(o) = v.as_object_mut() {
        o.entry("EnableXpBadge").or_insert(Value::Bool(true));
        o.entry("XpMin").or_insert(serde_json::json!(1));
        o.entry("XpMax").or_insert(serde_json::json!(8));
        o.entry("XpMode").or_insert(Value::String("slot".into()));
        o.entry("SelfXp").or_insert(serde_json::json!(7));
        o.entry("XpToHumans").or_insert(Value::Bool(true));
        o.entry("EnableMvp").or_insert(Value::Bool(true));
        o.entry("RankType").or_insert(serde_json::json!(11));
        o.entry("WriteRankTypeToHumans").or_insert(Value::Bool(false));
    }
    v
}

#[tauri::command(async)]
fn get_badge(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let installed = badge_path(&cs2).is_file();
    serde_json::json!({ "installed": installed, "config": read_badge(&cs2) })
}

#[tauri::command(async)]
fn set_badge(app: TauriState<'_, AppState>, config: Value) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let p = badge_path(&cs2);
    let mut cur = read_badge(&cs2);
    if let (Some(dst), Some(src)) = (cur.as_object_mut(), config.as_object()) {
        for (k, v) in src {
            // 只接受已知字段，避免把垃圾写进插件配置
            if matches!(k.as_str(),
                "EnableXpBadge" | "XpMin" | "XpMax" | "XpMode" | "SelfXp" | "XpToHumans"
                | "EnableMvp" | "RankType" | "WriteRankTypeToHumans") {
                dst.insert(k.clone(), v.clone());
            }
        }
        // 值域纠正：XP 档位 1~8，RankType 1~18
        if let Some(n) = dst.get("XpMin").and_then(|v| v.as_u64()) {
            dst.insert("XpMin".into(), serde_json::json!(n.clamp(1, 8)));
        }
        if let Some(n) = dst.get("XpMax").and_then(|v| v.as_u64()) {
            dst.insert("XpMax".into(), serde_json::json!(n.clamp(1, 8)));
        }
        if let Some(n) = dst.get("SelfXp").and_then(|v| v.as_u64()) {
            dst.insert("SelfXp".into(), serde_json::json!(n.clamp(1, 8)));
        }
        if let Some(n) = dst.get("RankType").and_then(|v| v.as_u64()) {
            dst.insert("RankType".into(), serde_json::json!(n.clamp(1, 18)));
        }
        // XpMin 不得大于 XpMax
        let lo = dst.get("XpMin").and_then(|v| v.as_u64()).unwrap_or(1);
        let hi = dst.get("XpMax").and_then(|v| v.as_u64()).unwrap_or(8);
        if lo > hi {
            dst.insert("XpMin".into(), serde_json::json!(hi));
        }
    }
    if let Some(dir) = p.parent() {
        let _ = fs::create_dir_all(dir);
    }
    fs::write(&p, serde_json::to_string_pretty(&cur).unwrap_or_default())
        .map_err(|e| format!("写入徽章配置失败: {}", e)).ok();
    serde_json::json!({ "success": true, "config": cur })
}

// ── 击杀图标 ──

fn killfeed_config_path(cs2_path: &str) -> PathBuf {
    csgo_dir(cs2_path)
        .join("addons").join("counterstrikesharp").join("configs")
        .join("plugins").join("Killfeed_Icons").join("Killfeed_Icons.json")
}

#[tauri::command(async)]
fn get_killfeed(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let cfg = killfeed_config_path(&cs2);
    let json: Value = fs::read_to_string(&cfg)
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_else(|| serde_json::json!({}));
    let packs: Vec<String> = json.get("AvailableSkinPacks")
        .and_then(|v| v.as_array())
        .map(|a| a.iter().filter_map(|x| x.as_str().map(|s| s.to_string())).collect())
        .unwrap_or_default();
    // 当前选中：读玩家数据目录（KillfeedIcons，无下划线）
    let skins = csgo_dir(&cs2).join("addons").join("counterstrikesharp").join("configs")
        .join("plugins").join("KillfeedIcons").join("player_skins.json");
    let mut current = String::new();
    if let Ok(t) = fs::read_to_string(&skins) {
        if let Ok(v) = serde_json::from_str::<Value>(&t) {
            let id = current_steam_id();
            let key = id.as_deref().unwrap_or("default");
            current = v.get(key).and_then(|x| x.as_str()).unwrap_or("").to_string();
            if current.is_empty() {
                current = v.get("default").and_then(|x| x.as_str()).unwrap_or("").to_string();
            }
        }
    }
    let icons = csgo_dir(&cs2).join("panorama").join("images").join("icons").join("equipment");
    let icon_count = fs::read_dir(&icons).map(|rd| rd.flatten().count()).unwrap_or(0);
    let mounted = fs::read_to_string(csgo_dir(&cs2).join("gameinfo.gi"))
        .map(|t| t.contains("panorama/images/icons/equipment"))
        .unwrap_or(false);
    serde_json::json!({
        "installed": killfeed_config_path(&cs2).is_file(),
        "packs": packs,
        "current": if current.is_empty() { s.killfeed_style.clone() } else { current },
        "iconCount": icon_count,
        "mounted": mounted,
        "cs2Running": is_cs2_running(),
    })
}

// ── 击杀图标资源覆盖开关（排查 D3D / 主界面闪退用） ──

fn icon_dir(cs2_path: &str) -> PathBuf {
    csgo_dir(cs2_path)
        .join("panorama").join("images").join("icons").join("equipment")
}

#[tauri::command(async)]
fn get_icon_overlay(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    let dir = icon_dir(&cs2);
    let off = dir.with_extension("off");
    let gi = fs::read_to_string(csgo_dir(&cs2).join("gameinfo.gi")).unwrap_or_default();
    serde_json::json!({
        "enabled": dir.is_dir() && gi.contains("panorama/images/icons/equipment"),
        "dirPresent": dir.is_dir(),
        "disabledPresent": off.is_dir(),
        "mounted": gi.contains("panorama/images/icons/equipment"),
        "iconCount": fs::read_dir(&dir).map(|rd| rd.flatten().count()).unwrap_or(0),
    })
}

#[tauri::command(async)]
fn set_icon_overlay(app: TauriState<'_, AppState>, enabled: bool) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出"); }
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }

    let dir = icon_dir(&cs2);
    let off = dir.with_extension("off");
    let gi_path = csgo_dir(&cs2).join("gameinfo.gi");

    if enabled {
        // 目录改回来
        if !dir.is_dir() && off.is_dir() {
            if let Err(e) = fs::rename(&off, &dir) {
                return err(&format!("恢复图标目录失败: {}", e));
            }
        }
        if let Err(e) = ensure_icon_include(&cs2) {
            return err(&e);
        }
    } else {
        // 先摘掉 gameinfo 的挂载行，再改名目录
        if let Ok(text) = fs::read_to_string(&gi_path) {
            let re = regex::Regex::new(
                r#"(?m)^[ \t]*"include"[ \t]+"panorama/images/icons/equipment"[ \t]*\r?\n"#,
            ).unwrap();
            let out = re.replace_all(&text, "").to_string();
            if out != text {
                let _ = fs::write(&gi_path, out);
            }
        }
        if dir.is_dir() {
            if let Err(e) = fs::rename(&dir, &off) {
                return err(&format!("停用图标目录失败: {}", e));
            }
        }
    }

    serde_json::json!({ "success": true, "enabled": enabled })
}

// ── 只开换肤模式 ──
// 只保留 Metamod + CounterStrikeSharp + InventorySimulator，
// 其余插件全部改名停用；不改难度、不改 overrides 挂载。

/// 换肤模式必须保留的插件
const SKIN_MODE_KEEP: [&str; 3] = ["metamod", "counterstrikesharp", "InventorySimulator"];
/// 原生插件的 vdf 声明（只改 dll 不改 vdf，metamod 还是会尝试加载）
const NATIVE_VDF: [&str; 3] = ["BotController", "BotHider", "BotVision"];

fn vdf_path(cs2_path: &str, id: &str) -> PathBuf {
    csgo_dir(cs2_path).join("addons").join("metamod").join(format!("{}.vdf", id))
}

fn set_vdf_enabled(cs2_path: &str, id: &str, on: bool) {
    let vdf = vdf_path(cs2_path, id);
    let off = PathBuf::from(format!("{}.off", vdf.to_string_lossy()));
    if on {
        if off.is_file() { let _ = fs::rename(&off, &vdf); }
    } else if vdf.is_file() {
        let _ = fs::rename(&vdf, &off);
    }
}

/// CounterStrikeSharp 要允许本地库存模拟（core.json 里这条必须是 false）
fn ensure_core_guideline(cs2_path: &str) -> Result<(), String> {
    let p = csgo_dir(cs2_path)
        .join("addons").join("counterstrikesharp").join("configs").join("core.json");
    let text = fs::read_to_string(&p).unwrap_or_else(|_| "{}".to_string());
    let mut v: Value = serde_json::from_str(&text).unwrap_or_else(|_| serde_json::json!({}));
    let Some(obj) = v.as_object_mut() else {
        return Err("core.json 格式异常".into());
    };
    let already = obj.get("FollowCS2ServerGuidelines").and_then(|x| x.as_bool()) == Some(false);
    obj.insert("FollowCS2ServerGuidelines".into(), Value::Bool(false));
    if !already {
        fs::write(&p, serde_json::to_string_pretty(&v).unwrap_or_default())
            .map_err(|e| format!("写入 core.json 失败: {}", e))?;
    }
    Ok(())
}

#[tauri::command(async)]
fn get_skin_mode(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return serde_json::json!({ "enabled": false, "ready": false });
    }
    let base = csgo_dir(&cs2).join("addons").join("counterstrikesharp").join("plugins");
    let inv = base.join("InventorySimulator").join("InventorySimulator.dll").is_file()
        || base.join("InventorySimulator").join("InventorySimulator.dll.off").is_file();
    let bot_off = base.join("BotAI").join("BotAI.dll.off").is_file();
    serde_json::json!({
        "enabled": s.skin_only,
        "ready": inv,
        "inventoryPresent": inv,
        "botDisabled": bot_off,
        "cs2Running": is_cs2_running(),
    })
}

#[tauri::command(async)]
fn set_skin_mode(app: TauriState<'_, AppState>, enabled: bool) -> Value {
    if is_cs2_running() {
        return err("CS2 正在运行，请先完全退出再切模式");
    }
    let cs2 = {
        let s = app.inner.lock().unwrap();
        let p = effective_path(&s);
        if p.is_empty() { return err("未设置 CS2 路径"); }
        p
    };

    if enabled {
        // 库存插件必须在
        let inv = csgo_dir(&cs2)
            .join("addons").join("counterstrikesharp").join("plugins")
            .join("InventorySimulator").join("InventorySimulator.dll");
        if !inv.is_file() && !PathBuf::from(format!("{}.off", inv.to_string_lossy())).is_file() {
            return err("没找到 InventorySimulator 插件，请先点「安装 / 修复套件」");
        }
        // 框架必须在
        if let Err(e) = patch_gameinfo(&cs2, true) {
            return err(&e);
        }
        if let Err(e) = ensure_core_guideline(&cs2) {
            return err(&e);
        }
    }

    // 逐个插件开 / 关。
    // 注意：必须跳过框架——counterstrikesharp 目录递归下去会扫到 plugins/，
    // 对它调 toggle 等于把所有人机插件一次性重新启用。
    let mut changed = 0usize;
    for plugin in catalog() {
        if SKIN_MODE_KEEP.contains(&plugin.id.as_str()) { continue; }
        let on = !enabled;
        toggle_plugin_files(&cs2, &plugin, on);
        if NATIVE_VDF.contains(&plugin.id.as_str()) {
            set_vdf_enabled(&cs2, &plugin.id, on);
        }
        changed += 1;
    }

    let mut s = app.inner.lock().unwrap();
    s.skin_only = enabled;
    for plugin in catalog() {
        let keep = SKIN_MODE_KEEP.contains(&plugin.id.as_str());
        if let Some(st) = s.plugins.get_mut(&plugin.id) {
            st.enabled = if enabled { keep } else { true };
        }
    }
    let _ = app.save(&s);

    serde_json::json!({
        "success": true,
        "enabled": enabled,
        "changed": changed,
        "message": if enabled {
            "已切到只开换肤：仅加载库存换肤，人机 AI 全部停用"
        } else {
            "已切回完整人机：全部插件恢复启用"
        },
    })
}

// ── 清单校验（照搬 Panel validate_files 的五字段契约） ──

const MANIFEST_FILES: [&str; 51] = [
    "addons/BotController/bin/win64/BotController.dll",
    "addons/BotHider/bin/win64/BotHider.dll",
    "addons/BotVision/bin/win64/BotVision.dll",
    "addons/counterstrikesharp/plugins/BotAI/BotAI.dll",
    "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll",
    "addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll",
    "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImpl.dll",
    "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImpl.dll",
    "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll",
    "addons/counterstrikesharp/plugins/BotState/BotState.dll",
    "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.dll",
    "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.dll",
    "addons/counterstrikesharp/shared/0Harmony/0Harmony.dll",
    "addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll",
    "addons/counterstrikesharp/shared/BotHiderApi/BotHiderApi.dll",
    "addons/metamod/BotController.vdf",
    "addons/metamod/BotHider.vdf",
    "addons/metamod/BotVision.vdf",
    "backup/Online/gameinfo.gi",
    "backup/WithBots/gameinfo.gi",
    "cfg/bot_buy.cfg",
    "cfg/gamemode_armsrace.cfg",
    "cfg/gamemode_casual.cfg",
    "cfg/gamemode_competitive.cfg",
    "cfg/gamemode_competitive2v2.cfg",
    "cfg/gamemode_competitive2v2_offline.cfg",
    "cfg/gamemode_competitive_offline.cfg",
    "cfg/gamemode_custom.cfg",
    "cfg/gamemode_deathmatch.cfg",
    "cfg/gamemode_dm_freeforall.cfg",
    "cfg/gamemode_retakecasual.cfg",
    "cfg/gamemode_rush.cfg",
    "cfg/gamemode_rush_offline.cfg",
    "cfg/gamemode_teamdeathmatch.cfg",
    "cfg/gamemode_workshop.cfg",
    "cfg/my_bot_ffa_config.cfg",
    "cfg/my_bot_normal_config.cfg",
    "cfg/my_bot_rush_config.cfg",
    "gameinfo.gi",
    "overrides/High/botprofile.vpk",
    "overrides/Low/botprofile.vpk",
    "overrides/Medium/botprofile.vpk",
    "overrides/botprofile.vpk",
    "addons/counterstrikesharp/plugins/Killfeed_Icons/Killfeed_Icons.dll",
    "addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.dll",
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.dll",
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json",
    "addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json",
    "addons/counterstrikesharp/configs/core.json",
    "addons/metamod/metaplugins.ini",
    "addons/metamod/counterstrikesharp.vdf",
];

#[tauri::command(async)]
fn validate_manifest(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return serde_json::json!({ "ok": false, "total": 0, "present": 0, "missing": [], "misplaced": null, "reason": "未设置 CS2 路径" });
    }
    let base = csgo_dir(&cs2);

    let mut missing: Vec<String> = Vec::new();
    let mut disabled: Vec<String> = Vec::new();
    let mut present = 0usize;
    for rel in MANIFEST_FILES {
        if base.join(rel).is_file() {
            present += 1;
        } else if base.join(format!("{}.off", rel)).is_file() {
            // 被管理器改名停用（.dll.off）——文件在，只是没启用，不算缺失
            present += 1;
            disabled.push(rel.to_string());
        } else {
            missing.push(rel.to_string());
        }
    }

    // 图标资源目录：只数数量，不逐个列
    let icons = base.join("panorama").join("images").join("icons").join("equipment");
    let icon_count = fs::read_dir(&icons)
        .map(|rd| rd.flatten().filter(|e| e.file_name().to_string_lossy().ends_with(".vsvg_c")).count())
        .unwrap_or(0);
    if icon_count == 0 {
        missing.push("panorama/images/icons/equipment/*.vsvg_c（击杀图标资源，需要 ≥1 个）".into());
    } else {
        present += 1;
    }

    // gameinfo 挂载行
    let gi = fs::read_to_string(base.join("gameinfo.gi")).unwrap_or_default();
    if gi.contains("panorama/images/icons/equipment") {
        present += 1;
    } else {
        missing.push("gameinfo.gi 缺少击杀图标挂载行 \"panorama/images/icons/equipment\"".into());
    }
    if gi.contains("Game_LowViolence") {
        present += 1;
    } else {
        missing.push("gameinfo.gi 缺少 Game_LowViolence 标记".into());
    }

    // 击杀图标包列表非空
    let kf = fs::read_to_string(killfeed_config_path(&cs2)).unwrap_or_default();
    let packs_ok = serde_json::from_str::<Value>(&kf).ok()
        .and_then(|v| v.get("AvailableSkinPacks").and_then(|x| x.as_array()).map(|a| !a.is_empty()))
        .unwrap_or(false);
    if packs_ok {
        present += 1;
    } else {
        missing.push("Killfeed_Icons.json 的 AvailableSkinPacks 为空（插件会静默不工作）".into());
    }

    // misplaced 探针：解压到 game/csgo 根或更上层
    let mut misplaced: Option<String> = None;
    for (probe, correct) in [
        ("addons/counterstrikesharp/plugins/BotAI/BotAI.dll", "game/csgo/addons/counterstrikesharp/plugins/BotAI/BotAI.dll"),
        ("addons/BotHider/bin/win64/BotHider.dll", "game/csgo/addons/BotHider/bin/win64/BotHider.dll"),
        ("backup/WithBots/gameinfo.gi", "game/csgo/backup/WithBots/gameinfo.gi"),
    ] {
        for root in [Path::new(&cs2).join("game"), Path::new(&cs2).to_path_buf()] {
            let p = root.join(probe);
            if p.is_file() && root != base {
                misplaced = Some(correct.to_string());
                break;
            }
        }
        if misplaced.is_some() { break; }
    }

    let total = MANIFEST_FILES.len() + 4;
    serde_json::json!({
        "ok": missing.is_empty(),
        "total": total,
        "present": present,
        "missing": missing,
        "disabled": disabled,
        "misplaced": misplaced,
        "iconCount": icon_count,
        "cs2Running": is_cs2_running(),
    })
}

// ── 模式切换（online / bots） ──

#[tauri::command(async)]
fn get_mode(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return serde_json::json!({ "current": null, "pending": false, "cs2Running": false });
    }
    let gi = fs::read_to_string(csgo_dir(&cs2).join("gameinfo.gi")).unwrap_or_default();
    let bots = gi.contains("csgo/addons/metamod");
    serde_json::json!({
        "current": if bots { "bots" } else { "online" },
        "pending": false,
        "cs2Running": is_cs2_running(),
    })
}

#[tauri::command(async)]
fn set_mode(app: TauriState<'_, AppState>, mode: String) -> Value {
    if is_cs2_running() { return err("CS2 正在运行，请先完全退出再切模式"); }
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let enable = mode == "bots";
    match patch_gameinfo(&cs2, enable) {
        Ok(_) => serde_json::json!({ "success": true, "current": mode }),
        Err(e) => err(&e),
    }
}

// ═══════════════════════════════════════════
// 软件自身更新检查
// ═══════════════════════════════════════════

const APP_VERSION: &str = env!("CARGO_PKG_VERSION");

/// 把 "v2.2.0" / "2.2.1-beta" 之类规整成 (2,2,0)
fn parse_version(v: &str) -> (u64, u64, u64) {
    let core = v.trim().trim_start_matches(['v', 'V']);
    let core = core.split(['-', '+']).next().unwrap_or(core);
    let mut it = core.split('.').map(|x| x.trim().parse::<u64>().unwrap_or(0));
    (it.next().unwrap_or(0), it.next().unwrap_or(0), it.next().unwrap_or(0))
}

fn is_newer(latest: &str, current: &str) -> bool {
    parse_version(latest) > parse_version(current)
}

fn http_client() -> reqwest::Client {
    reqwest::Client::builder()
        .user_agent(format!("XuerongBotManager/{}", APP_VERSION))
        .timeout(std::time::Duration::from_secs(12))
        .build()
        .unwrap_or_default()
}

#[tauri::command(async)]
fn get_app_version() -> Value {
    serde_json::json!({ "version": APP_VERSION })
}

#[tauri::command(async)]
fn get_update_source(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    serde_json::json!({ "source": s.update_source, "version": APP_VERSION })
}

#[tauri::command(async)]
fn set_update_source(app: TauriState<'_, AppState>, source: String) -> Value {
    let mut s = app.inner.lock().unwrap();
    s.update_source = source.trim().to_string();
    let _ = app.save(&s);
    serde_json::json!({ "success": true, "source": s.update_source })
}

/// 检查更新。source 为空时用已保存的配置。
/// 返回 { hasUpdate, current, latest, url, notes, error }
#[tauri::command]
async fn check_app_update(app: TauriState<'_, AppState>, source: Option<String>) -> Result<Value, String> {
    let src = match source.map(|x| x.trim().to_string()).filter(|x| !x.is_empty()) {
        Some(x) => x,
        None => {
            let s = app.inner.lock().unwrap().clone();
            s.update_source.clone()
        }
    };

    if src.is_empty() {
        return Ok(serde_json::json!({
            "hasUpdate": false, "current": APP_VERSION,
            "latest": Value::Null, "url": Value::Null, "notes": Value::Null,
            "error": "未设置更新源",
        }));
    }

    let client = http_client();

    // GitHub 仓库形式：github:owner/repo → 走 Releases API，不用自己写 JSON
    if let Some(repo) = src.strip_prefix("github:") {
        let url = format!("https://api.github.com/repos/{}/releases/latest", repo.trim());
        let resp = match client.get(&url).send().await {
            Ok(r) => r,
            Err(_) => return Ok(serde_json::json!({ "hasUpdate": false, "current": APP_VERSION, "error": "网络请求失败" })),
        };
        if !resp.status().is_success() {
            return Ok(serde_json::json!({
                "hasUpdate": false, "current": APP_VERSION,
                "error": format!("GitHub 返回 {}", resp.status()),
            }));
        }
        let data: Value = match resp.json().await {
            Ok(d) => d,
            Err(_) => return Ok(serde_json::json!({ "hasUpdate": false, "current": APP_VERSION, "error": "响应解析失败" })),
        };
        let tag = data.get("tag_name").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let html = data.get("html_url").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let notes: String = data.get("body").and_then(|v| v.as_str()).unwrap_or("").chars().take(400).collect();
        let mut dl = html.clone();
        if let Some(assets) = data.get("assets").and_then(|v| v.as_array()) {
            for a in assets {
                let name = a.get("name").and_then(|v| v.as_str()).unwrap_or("");
                if name.to_lowercase().ends_with(".exe") {
                    if let Some(u) = a.get("browser_download_url").and_then(|v| v.as_str()) {
                        dl = u.to_string();
                        break;
                    }
                }
            }
        }
        let has = is_newer(&tag, APP_VERSION);
        return Ok(serde_json::json!({
            "hasUpdate": has,
            "current": APP_VERSION,
            "latest": tag,
            "url": dl,
            "notes": notes,
        }));
    }

    // 自定义 JSON：{"version":"2.2.1","url":"https://.../setup.exe","notes":"..."}
    let resp = match client.get(&src).send().await {
        Ok(r) => r,
        Err(_) => return Ok(serde_json::json!({ "hasUpdate": false, "current": APP_VERSION, "error": "网络请求失败" })),
    };
    if !resp.status().is_success() {
        return Ok(serde_json::json!({
            "hasUpdate": false, "current": APP_VERSION,
            "error": format!("HTTP {}", resp.status()),
        }));
    }
    let data: Value = match resp.json().await {
        Ok(d) => d,
        Err(_) => return Ok(serde_json::json!({ "hasUpdate": false, "current": APP_VERSION, "error": "JSON 解析失败" })),
    };
    let latest = data.get("version").and_then(|v| v.as_str()).unwrap_or("").to_string();
    if latest.is_empty() {
        return Ok(serde_json::json!({ "hasUpdate": false, "current": APP_VERSION, "error": "JSON 里没有 version 字段" }));
    }
    Ok(serde_json::json!({
        "hasUpdate": is_newer(&latest, APP_VERSION),
        "current": APP_VERSION,
        "latest": latest,
        "url": data.get("url").and_then(|v| v.as_str()).unwrap_or(""),
        "notes": data.get("notes").and_then(|v| v.as_str()).unwrap_or(""),
    }))
}

/// 下载更新包并静默安装。
/// 通过 `update-progress` 事件把进度推给前端：{downloaded,total,percent}
#[tauri::command]
async fn download_update(
    app: tauri::AppHandle,
    url: String,
    version: String,
) -> Result<Value, String> {
    if url.trim().is_empty() {
        return Err("下载地址为空，请先检查更新".into());
    }

    let dir = std::env::temp_dir().join("xuerong_update");
    fs::create_dir_all(&dir).map_err(|e| format!("创建临时目录失败: {}", e))?;
    let safe_ver: String = version.chars().filter(|c| c.is_ascii_alphanumeric() || *c == '.' || *c == '-').collect();
    let file = dir.join(format!("xuerong_setup_{}.exe", if safe_ver.is_empty() { "latest".into() } else { safe_ver }));
    let _ = fs::remove_file(&file);

    let client = http_client();
    let mut resp = client
        .get(url.trim())
        .send()
        .await
        .map_err(|e| format!("连接下载地址失败: {}", e))?;
    if !resp.status().is_success() {
        return Err(format!("下载地址返回 {}", resp.status()));
    }
    let total = resp.content_length().unwrap_or(0);

    let mut out = tokio::fs::File::create(&file)
        .await
        .map_err(|e| format!("创建文件失败: {}", e))?;

    let mut downloaded: u64 = 0;
    let mut last = std::time::Instant::now();
    let _ = app.emit("update-progress", serde_json::json!({
        "downloaded": 0u64, "total": total, "percent": 0u32, "phase": "downloading"
    }));

    while let Some(chunk) = resp.chunk().await.map_err(|e| format!("下载中断: {}", e))? {
        out.write_all(&chunk).await.map_err(|e| format!("写入失败: {}", e))?;
        downloaded += chunk.len() as u64;
        if last.elapsed().as_millis() >= 120 || (total > 0 && downloaded >= total) {
            last = std::time::Instant::now();
            let pct = if total > 0 { ((downloaded as f64 / total as f64) * 100.0).min(100.0) as u32 } else { 0 };
            let _ = app.emit("update-progress", serde_json::json!({
                "downloaded": downloaded, "total": total, "percent": pct, "phase": "downloading"
            }));
        }
    }
    out.flush().await.ok();
    drop(out);

    // 大小校验：防止下到半截或下到 HTML 错误页
    let got = fs::metadata(&file).map(|m| m.len()).unwrap_or(0);
    if got < 1024 * 1024 {
        let _ = fs::remove_file(&file);
        return Err(format!("下载的文件只有 {} 字节，明显不对（可能是错误页）", got));
    }
    if total > 0 && got != total {
        let _ = fs::remove_file(&file);
        return Err(format!("下载不完整：{}/{} 字节", got, total));
    }

    // 去掉 Mark-of-the-Web，减少 SmartScreen 拦截
    let _ = fs::remove_file(format!("{}:Zone.Identifier", file.display()));

    let _ = app.emit("update-progress", serde_json::json!({
        "downloaded": got, "total": got, "percent": 100u32, "phase": "installing"
    }));

    // 延迟 2 秒再拉起安装器：先让本程序完全退出，否则安装器会被文件占用卡住
    let path = file.to_string_lossy().to_string();
    let cmdline = format!("ping -n 3 127.0.0.1 >nul & start \"\" \"{}\" /S", path);
    let _ = cmd("cmd").args(["/C", &cmdline]).spawn();

    std::thread::sleep(std::time::Duration::from_millis(400));
    app.exit(0);
    Ok(serde_json::json!({ "success": true, "path": path }))
}

// ═══════════════════════════════════════════
// Entry
// ═══════════════════════════════════════════

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            // 数据目录：%APPDATA%\XuerongBotManager
            let data_dir = app.path().app_data_dir().unwrap_or_else(|_| {
                dirs::data_dir().unwrap_or_else(std::env::temp_dir).join("XuerongBotManager")
            });
            fs::create_dir_all(&data_dir).ok();

            // 资源目录：exe 同级 resources/，或开发时的 ../CS2人机增强助手/resources
            let resources_dir = app.path().resource_dir().map(|d| d.join("resources")).unwrap_or_else(|_| {
                let exe_dir = std::env::current_exe()
                    .ok()
                    .and_then(|p| p.parent().map(|d| d.to_path_buf()))
                    .unwrap_or_else(|| PathBuf::from("."));
                let candidates = [
                    exe_dir.join("resources"),
                    exe_dir.join("..").join("CS2人机增强助手").join("resources"),
                    PathBuf::from("..").join("CS2人机增强助手").join("resources"),
                ];
                candidates.into_iter().find(|c| c.is_dir()).unwrap_or(exe_dir.join("resources"))
            });

            let state = AppStateData::default();
            let app_state = AppState { inner: Mutex::new(state), data_dir, resources_dir };

            // 从磁盘加载
            let loaded = app_state.load();
            *app_state.inner.lock().unwrap() = loaded;

            app.manage(app_state);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_state,
            detect_path,
            set_path,
            toggle_plugin,
            install,
            uninstall,
            set_difficulty,
            set_premier,
            set_killfeed,
            set_all_plugins,
            apply_configs,
            get_crashes,
            safe_mode,
            close_window,
            minimize_window,
            mark_onboarded,
            check_updates,
            pick_cs2_folder,
            open_folder,
            open_url,
            resources_dir,
            launch_cs2,
            // ── v2.0 新增：对齐 Panel v1.4.7 ──
            validate_manifest,
            get_app_version,
            get_update_source,
            set_update_source,
            check_app_update,
            download_update,
            get_skin_mode,
            set_skin_mode,
            get_icon_overlay,
            set_icon_overlay,
            get_difficulty,
            get_rules,
            set_rule,
            set_c4_timer,
            set_molotov_multiplier,
            reset_rules,
            get_bot_items,
            set_bot_item,
            get_presets,
            set_aim,
            set_nades,
            get_badge,
            set_badge,
            get_killfeed,
            get_mode,
            set_mode,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
