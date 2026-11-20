// 雪绒人机管理器 — Rust 后端
// 完整移植 server.js 的全部 API 逻辑，使 exe 自包含（不依赖系统 Node）

use std::collections::HashMap;
use std::fs;
use std::io::{Read, Seek, SeekFrom, Write};
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

pub mod vpk;
mod mvp_music;
mod plugin_inventory;
use vpk::{vpk_read_file, vpk_replace_file};

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
         "瞄准平滑度与爆头率，三档首枪反应延迟、见敌走位衔接（2.1.9）", "addons/counterstrikesharp/plugins/BotAimImprover", false),
        ("BotBuy", "Bot 购买逻辑", "ai", "combat",
         "按回合经济与护甲选枪选道具，预算不足降档、给真人发枪（1.1.7）", "addons/counterstrikesharp/plugins/BotBuy", false),
        ("BotState", "Bot 状态追踪", "ai", "feature",
         "核心 AI：CT 守点/T 下包守包、横移探角、听声预瞄、掉人补位、闪光回转、回防（1.13.29）", "addons/counterstrikesharp/plugins/BotState", false),
        ("NadeSystem", "投掷物系统", "ai", "feature",
         "按地图点位使用闪光、烟雾与高爆弹，含背闪与受闪回转（1.2.4）", "addons/counterstrikesharp/plugins/NadeSystem", false),

        // —— 外观与展示 ——
        ("BotRandomizer", "Bot 饰品随机", "visual", "feature",
         "随机化 bot 的皮肤、刀、手套、贴纸、挂件与音乐盒", "addons/counterstrikesharp/plugins/BotRandomizer", false),
        ("MapRanks", "BOT 随机段位", "visual", "special",
         "段位、动态 MVP 面板与 XP 过载徽章由单一插件管理", "addons/counterstrikesharp/plugins/MapRanks", false),
        ("Killfeed_Icons", "击杀图标", "visual", "special",
         "替换击杀提示栏图标；机器人每回合重新随机图标包", "addons/counterstrikesharp/plugins/Killfeed_Icons", false),

        // —— 功能模块 ——
        ("XueRongChat", "平台聊天仿真", "feature", "special",
         "FACEIT 风格平台聊天：等级前缀、MATCH FOUND 横幅、比分播报、连杀/残局提示、结算 ELO", "addons/counterstrikesharp/plugins/XueRongChat", false),
        ("RoundDamageRecap", "回合伤害回顾", "feature", "feature",
         "每回合结束显示伤害统计回顾", "addons/counterstrikesharp/plugins/RoundDamageRecap", false),
        ("BotFreezeActions", "准备阶段人机动作", "feature", "feature",
         "冻结时间里人机会随机短暂切刀或下蹲，不改朝向、不写网络变量", "addons/counterstrikesharp/plugins/BotFreezeActions", false),
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
    /// MVP 音乐插件不在普通插件清单中，单独记住其启用状态。
    #[serde(default = "d_true")]
    pub mvp_music_enabled: bool,
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
            mvp_music_enabled: true,
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

fn toggle_plugin_files(cs2_path: &str, plugin: &Plugin, enabled: bool) -> usize {
    toggle_plugin_dir(cs2_path, &plugin.dir, enabled)
}

fn toggle_plugin_dir(cs2_path: &str, plugin_dir: &str, enabled: bool) -> usize {
    if cs2_path.is_empty() { return 0; }
    let target = Path::new(cs2_path).join("game").join("csgo").join(plugin_dir);
    if !target.is_dir() { return 0; }

    // 递归处理：原生插件真正的 .dll 在 bin/win64/ 子目录里，只扫顶层会漏掉
    let mut changed = 0usize;
    for entry in walkdir::WalkDir::new(&target).max_depth(4).into_iter().flatten() {
        let path = entry.path();
        if !path.is_file() { continue; }
        let name = path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        let is_dll = name.ends_with(".dll");
        let is_off = name.ends_with(".dll.off");
        if !is_dll && !is_off { continue; }

        let full = path.to_path_buf();
        if enabled {
            if is_off {
                if let Some(base) = full.to_string_lossy().strip_suffix(".off") {
                    let active = PathBuf::from(base);
                    if active.exists() {
                        // 解压/覆盖安装后可能同时残留 .dll 与旧 .dll.off；启用时保留活动 DLL。
                        if fs::remove_file(&full).is_ok() { changed += 1; }
                    } else if fs::rename(&full, active).is_ok() {
                        changed += 1;
                    }
                }
            }
        } else if is_dll {
            let off = PathBuf::from(format!("{}.off", full.to_string_lossy()));
            if off.exists() {
                // 保留刚解包的新 DLL 内容，再移除可加载的 .dll，避免开关显示关闭但仍被加载。
                if fs::copy(&full, &off).is_ok() && fs::remove_file(&full).is_ok() {
                    changed += 1;
                }
            } else if fs::rename(&full, &off).is_ok() {
                changed += 1;
            }
        }
    }
    changed
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
        // .pdb 是调试符号，游戏用不上；新版整合包塞了 12 个，没必要搬进游戏目录
        if from.extension().map(|x| x.eq_ignore_ascii_case("pdb")).unwrap_or(false) {
            continue;
        }
        let to = dst.join(entry.file_name());
        if from.is_dir() {
            copy_dir_all(&from, &to)?;
        } else {
            fs::copy(&from, &to).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

/// 官方原文件快照目录：放在游戏目录之外，避免污染游戏文件夹
fn pristine_dir() -> PathBuf {
    dirs::data_local_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("XueRongCS2")
        .join("pristine")
}

/// 安装前把「即将被覆盖的官方原文件」存一份到游戏目录外。
/// 只存已存在的文件（新增文件无需快照），且只存第一次（不重复覆盖）。
fn snapshot_official_files(temp_dir: &Path, game_dir: &Path) -> usize {
    let pdir = pristine_dir();
    let mut n = 0usize;
    for entry in walkdir::WalkDir::new(temp_dir).max_depth(6).into_iter().flatten() {
        if !entry.file_type().is_file() { continue; }
        let Ok(rel) = entry.path().strip_prefix(temp_dir) else { continue };
        let target = game_dir.join(rel);
        if !target.is_file() { continue; }
        let saved = pdir.join(rel);
        if saved.is_file() { continue; }
        // 关键判断：如果游戏里的文件已经和整合包里的完全一样，
        // 说明它是被上一版管理器覆盖过的，不是官方原文件，不能当快照存。
        let same_as_pack = match (fs::metadata(entry.path()), fs::metadata(&target)) {
            (Ok(a), Ok(b)) if a.len() == b.len() => {
                match (fs::read(entry.path()), fs::read(&target)) {
                    (Ok(x), Ok(y)) => x == y,
                    _ => false,
                }
            }
            _ => false,
        };
        if same_as_pack { continue; }
        if let Some(parent) = saved.parent() { let _ = fs::create_dir_all(parent); }
        if fs::copy(&target, &saved).is_ok() { n += 1; }
    }
    n
}

/// 卸载时把官方原文件还原回去（Steam 才不会提示「需要更新下载内容」）
fn restore_official_files(game_dir: &Path) -> usize {
    let pdir = pristine_dir();
    if !pdir.is_dir() { return 0; }
    let mut n = 0usize;
    for entry in walkdir::WalkDir::new(&pdir).max_depth(6).into_iter().flatten() {
        if !entry.file_type().is_file() { continue; }
        let Ok(rel) = entry.path().strip_prefix(&pdir) else { continue };
        let target = game_dir.join(rel);
        if let Some(parent) = target.parent() { let _ = fs::create_dir_all(parent); }
        if fs::copy(entry.path(), &target).is_ok() { n += 1; }
    }
    n
}

/// 只有整个目录都是 .vsvg_c 时才认定是我们加的图标资源，避免误删官方内容
fn is_our_icon_dir(dir: &Path) -> bool {
    if !dir.is_dir() { return false; }
    let mut any = false;
    for e in walkdir::WalkDir::new(dir).into_iter().flatten() {
        if !e.file_type().is_file() { continue; }
        any = true;
        if !e.file_name().to_string_lossy().ends_with(".vsvg_c") { return false; }
    }
    any
}

/// 清理 2 小时前的安装临时目录（中途失败会残留，攒久了既占空间也可能被误读）
fn cleanup_stale_install_dirs() {
    let root = std::env::temp_dir();
    let Ok(rd) = fs::read_dir(&root) else { return };
    let now = std::time::SystemTime::now();
    for e in rd.flatten() {
        let name = e.file_name().to_string_lossy().to_string();
        if !name.starts_with("xuerong_install_") {
            continue;
        }
        let stale = e
            .metadata()
            .ok()
            .and_then(|m| m.modified().ok())
            .and_then(|t| now.duration_since(t).ok())
            .map(|d| d.as_secs() > 7200)
            .unwrap_or(false);
        if stale {
            let _ = fs::remove_dir_all(e.path());
        }
    }
}

/// 已从整合包移除、但旧版本装过的插件残留。
/// 安装是覆盖式（copy_dir_all），不会删目标目录里多出来的文件，必须主动清。
const REMOVED_PLUGIN_PATHS: &[&str] = &[
    // XP 徽章逻辑已合并到 MapRanks；旧 DLL 与其 .off 副本会双写段位字段。
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.dll",
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.dll.off",
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.deps.json",
    "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.pdb",
    // DemoTracer 回放（2.15.9 移除：接管 bot 身份导致没皮肤/没击杀图标）
    "addons/counterstrikesharp/plugins/DemoTracer",
    "addons/counterstrikesharp/plugins/DtrHider",
    "addons/counterstrikesharp/shared/DemoTracerApi",
    "addons/counterstrikesharp/shared/DtrHiderApi",
    "addons/counterstrikesharp/shared/BotRandomizerApi",
    "addons/dtr-controller",
    "addons/dtr-hider",
    "addons/metamod/dtr-controller.vdf",
    "addons/demotracer-install.v1.json",
    "addons/counterstrikesharp/plugins/BotRandomizer/cs2-lib-econ-index.v1.json",
    // BotChatter 垃圾话（2.15.5 移除）
    "addons/counterstrikesharp/plugins/BotChatter",
    "addons/counterstrikesharp/configs/plugins/BotChatter",
];

fn prune_removed_plugins(game_dir: &std::path::Path) -> usize {
    let mut n = 0usize;
    for rel in REMOVED_PLUGIN_PATHS {
        let p = game_dir.join(rel);
        let ok = if p.is_dir() {
            fs::remove_dir_all(&p).is_ok()
        } else if p.is_file() {
            fs::remove_file(&p).is_ok()
        } else {
            false
        };
        if ok { n += 1; }
    }
    n
}

fn install_plugins(cs2_path: &str, resources_dir: &Path, premier: &PremierConfig) -> Result<Value, String> {    if cs2_path.is_empty() {
        return Err("未设置 CS2 安装路径".into());
    }
    let game_dir = Path::new(cs2_path).join("game").join("csgo");
    if !game_dir.is_dir() {
        return Err(format!("CS2 游戏目录不存在: {}", game_dir.display()));
    }

    let zip_path = find_zip(resources_dir)
        .ok_or_else(|| format!("未找到离线安装包 CS2BotImprover.zip（搜索目录: {}）", resources_dir.display()))?;

    // 上次安装中途失败会留下临时目录，攒多了占空间也可能读到旧文件
    cleanup_stale_install_dirs();

    let temp_dir = std::env::temp_dir().join(format!("xuerong_install_{}", chrono::Utc::now().timestamp()));
    fs::create_dir_all(&temp_dir).map_err(|e| e.to_string())?;

    extract_zip(&zip_path, &temp_dir)?;

    // 覆盖前先把官方原文件存到游戏目录外，卸载时用它还原
    let snap = snapshot_official_files(&temp_dir, &game_dir);
    // 兜底：如果快照到的 gameinfo 已经被改过（老版本装过），用整合包自带的官方版覆盖
    {
        let pgi = pristine_dir().join("gameinfo.gi");
        if let Ok(t) = fs::read_to_string(&pgi) {
            if t.contains("csgo/addons/metamod") {
                let src = temp_dir.join("backup").join("Online").join("gameinfo.gi");
                if src.is_file() { let _ = fs::copy(&src, &pgi); }
            }
        }
    }

    // addons/ 会被整体覆盖，先救出玩家的音乐配置（Packs 绑定 / 音量），装完写回
    let protected_rel = [
        "addons/counterstrikesharp/configs/plugins/MVP-Music-Lite/MVP-Music-Lite.json",
        "addons/counterstrikesharp/plugins/MVP-Music-Lite/volumes.json",
        "addons/BotController/config.json",
        "addons/BotHider/config.json",
        "addons/counterstrikesharp/plugins/MapRanks/MapRanks.json",
        "addons/counterstrikesharp/plugins/MapRanks/human_ratings.json",
        "addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json",
        "addons/counterstrikesharp/configs/plugins/KillfeedIcons/player_skins.json",
        "addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json",
        "addons/counterstrikesharp/plugins/XueRongChat/elo.json",
        "addons/counterstrikesharp/configs/plugins/XueRongChat/XueRongChat.json",
    ];
    let saved_music: Vec<(PathBuf, Vec<u8>)> = protected_rel
        .iter()
        .filter_map(|r| {
            let p = game_dir.join(r);
            fs::read(&p).ok().map(|b| (p, b))
        })
        .collect();

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
    // 覆盖式安装不会删除旧版残留插件，这里主动清，否则老用户装新版仍被回放插件干扰
    let pruned_removed = prune_removed_plugins(&game_dir);
    if pruned_removed > 0 {
        mlog(&format!("清理已移除插件残留 {} 项", pruned_removed));
    }
    for (p, data) in &saved_music {
        if let Some(d) = p.parent() { let _ = fs::create_dir_all(d); }
        let _ = fs::write(p, data);
    }

    // 关键：修补 gameinfo.gi（只加需要的行，保护语言段）
    patch_gameinfo(cs2_path, true)?;
    ensure_icon_include(cs2_path)?;
    // 库存插件的开关必须靠 server.cfg exec 才生效（configs/plugins 下的 .cfg 没人读）
    ensure_invsim_convars(cs2_path);

    // 写入真正生效的插件配置
    let _ = premier;
    write_killfeed_config(cs2_path, "txz03");

    // 只保留本次这一份备份：旧的时间戳目录全删，避免 _xuerong_backup 越堆越大
    let backup_parent = Path::new(cs2_path).join("_xuerong_backup");
    let mut pruned = 0usize;
    if let Ok(rd) = fs::read_dir(&backup_parent) {
        for e in rd.flatten() {
            let p = e.path();
            if p.is_dir() && p != backup_root {
                if fs::remove_dir_all(&p).is_ok() { pruned += 1; }
            }
        }
    }
    if pruned > 0 { mlog(&format!("清理旧安装备份 {} 个，只保留最新一份", pruned)); }

    let _ = fs::remove_dir_all(&temp_dir);

    Ok(serde_json::json!({
        "success": true,
        "copied": copied,
        "prunedBackups": pruned,
        "prunedRemoved": pruned_removed,
        "snapshot": snap,
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
        if NATIVE_VDF.contains(&plugin_id.as_str()) {
            set_vdf_enabled(&cs2_path, &plugin_id, enabled);
        }

        // 击杀图标的行为由配置决定，开关后立刻重写选包
        if plugin_id == "Killfeed_Icons" {
            write_killfeed_config(&cs2_path, &s.killfeed_style);
        }
    }
    let _ = app.save(&s);
    serde_json::json!({ "success": true, "pluginId": plugin_id, "enabled": enabled })
}

/// 安装后核心文件体检：解压成功不等于能用，这几个文件缺一个游戏就起不来
/// 返回 (是否全部通过, 明细)
fn verify_core_install(cs2_path: &str) -> (bool, Vec<Value>) {
    let base = csgo_dir(cs2_path);
    // (显示名, 相对路径, 最小合理体积)
    let checks: [(&str, &str, u64); 11] = [
        ("Metamod 加载器", "addons/metamod/bin/win64/server.dll", 100_000),
        ("Metamod 核心", "addons/metamod/bin/win64/metamod.2.cs2.dll", 1_000_000),
        ("CSS 核心", "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll", 1_000_000),
        ("CSS 托管运行时", "addons/counterstrikesharp/dotnet/host/fxr", 0),
        ("0Harmony", "addons/counterstrikesharp/shared/0Harmony/0Harmony.dll", 500_000),
        ("CSS 配置", "addons/counterstrikesharp/configs/core.json", 50),
        ("metamod 插件表", "addons/metamod/metaplugins.ini", 10),
        ("CSS 加载器 vdf", "addons/metamod/counterstrikesharp.vdf", 10),
        ("库存插件", "addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.dll", 50_000),
        ("库存插件 gamedata", "addons/counterstrikesharp/gamedata/inventory-simulator.json", 500),
        ("库存插件设置", "cfg/xuerong_invsim.cfg", 20),
    ];
    let mut items = Vec::new();
    let mut all_ok = true;
    for (name, rel, min_size) in checks {
        let p = base.join(rel);
        let (ok, size, note) = if !p.exists() {
            (false, 0u64, "缺失".to_string())
        } else if min_size > 0 {
            let sz = fs::metadata(&p).map(|m| m.len()).unwrap_or(0);
            if sz < min_size {
                (false, sz, format!("体积异常（{} 字节，应 ≥ {}）", sz, min_size))
            } else {
                (true, sz, "正常".to_string())
            }
        } else {
            (true, 0, "正常".to_string())
        };
        if !ok {
            all_ok = false;
        }
        items.push(serde_json::json!({ "name": name, "path": rel, "ok": ok, "size": size, "note": note }));
    }
    (all_ok, items)
}


// ═══════════════════════════════════════════════════════════════
// 人机起枪编辑器：直接读写 overrides/botprofile.vpk 里的 Template 栏目
// 每个 Template 块里的多行 WeaponPreference 就是该性格 bot 的起枪优先级
// ═══════════════════════════════════════════════════════════════

#[derive(serde::Deserialize)]
struct TplWeapons {
    name: String,
    weapons: Vec<String>,
}

fn botprofile_path(cs2_path: &str) -> PathBuf {
    csgo_dir(cs2_path).join("overrides").join("botprofile.vpk")
}

fn read_botprofile_text(cs2_path: &str) -> Result<(PathBuf, String), String> {
    let vpk = botprofile_path(cs2_path);
    if !vpk.is_file() {
        return Err("找不到 overrides/botprofile.vpk —— 先点一次「安装 / 修复套件」或切换一次难度".into());
    }
    let data = fs::read(&vpk).map_err(|e| format!("读 vpk 失败: {}", e))?;
    let body = vpk::vpk_read_file(&data, "botprofile").ok_or("vpk 里没有 botprofile 条目")?;
    Ok((vpk, String::from_utf8_lossy(&body).to_string()))
}

/// 解析所有 Template 块里的武器偏好，返回 (名字, 武器, 块起始行, 块结束行, 武器行数)
fn parse_weapon_templates(text: &str) -> Vec<(String, Vec<String>, usize, usize, usize)> {
    let lines: Vec<&str> = text.lines().collect();
    let mut out = Vec::new();
    let mut i = 0usize;
    while i < lines.len() {
        let t = lines[i].trim();
        if let Some(rest) = t.strip_prefix("Template ") {
            let name = rest.trim().to_string();
            let start = i;
            let mut weapons = Vec::new();
            let mut wp_count = 0usize;
            let mut j = i + 1;
            while j < lines.len() {
                let x = lines[j].trim();
                if x.eq_ignore_ascii_case("End") {
                    break;
                }
                if let Some(v) = x.strip_prefix("WeaponPreference") {
                    if let Some(v) = v.split('=').nth(1) {
                        let w = v.trim();
                        if !w.is_empty() && !w.eq_ignore_ascii_case("none") {
                            weapons.push(w.to_string());
                        }
                        wp_count += 1;
                    }
                }
                j += 1;
            }
            out.push((name, weapons, start, j, wp_count));
            i = j;
        }
        i += 1;
    }
    out
}

/// 用编辑结果重建文本：只替换 Template 块里的 WeaponPreference 行，其余原样保留
fn rebuild_weapon_templates(text: &str, edits: &std::collections::HashMap<String, Vec<String>>) -> (String, usize) {
    let lines: Vec<&str> = text.lines().collect();
    let mut out: Vec<String> = Vec::with_capacity(lines.len() + 32);
    let mut changed = 0usize;
    let mut i = 0usize;
    while i < lines.len() {
        let t = lines[i].trim().to_string();
        if let Some(rest) = t.strip_prefix("Template ") {
            let name = rest.trim().to_string();
            out.push(lines[i].to_string());
            i += 1;
            // 找块尾
            let mut j = i;
            while j < lines.len() && !lines[j].trim().eq_ignore_ascii_case("End") {
                j += 1;
            }
            let body = &lines[i..j];
            // 缩进取第一行 WeaponPreference 的，没有就用 8 空格
            let indent = body
                .iter()
                .find(|l| l.trim_start().starts_with("WeaponPreference"))
                .map(|l| l[..l.len() - l.trim_start().len()].to_string())
                .unwrap_or_else(|| "        ".to_string());
            if let Some(list) = edits.get(&name) {
                let before: Vec<String> = body
                    .iter()
                    .filter(|l| l.trim_start().starts_with("WeaponPreference"))
                    .map(|l| l.trim().to_string())
                    .collect();
                let after: Vec<String> = list
                    .iter()
                    .map(|w| format!("{}WeaponPreference = {}", indent, w))
                    .collect();
                if before != after {
                    changed += 1;
                }
                for l in body {
                    if !l.trim_start().starts_with("WeaponPreference") {
                        out.push(l.to_string());
                    }
                }
                for l in after {
                    out.push(l);
                }
            } else {
                for l in body {
                    out.push(l.to_string());
                }
            }
            i = j;
            continue;
        }
        out.push(lines[i].to_string());
        i += 1;
    }
    let mut s = out.join("\n");
    if text.ends_with('\n') {
        s.push('\n');
    }
    (s, changed)
}

#[tauri::command(async)]
fn vpk_weapons_read(app: TauriState<'_, AppState>) -> Value {
    let cs2 = {
        let s = app.inner.lock().unwrap();
        effective_path(&s)
    };
    if cs2.is_empty() {
        return serde_json::json!({ "ok": false, "message": "未设置 CS2 路径" });
    }
    let (vpk, text) = match read_botprofile_text(&cs2) {
        Ok(x) => x,
        Err(e) => return serde_json::json!({ "ok": false, "message": e }),
    };
    let parsed = parse_weapon_templates(&text);
    let templates: Vec<Value> = parsed
        .iter()
        .map(|(name, weapons, start, end, wp)| {
            serde_json::json!({
                "name": name,
                "weapons": weapons,
                "lineStart": start + 1,
                "lineEnd": end + 1,
                "weaponLines": wp,
            })
        })
        .collect();
    let mut all: Vec<String> = parsed.iter().flat_map(|(_, w, _, _, _)| w.clone()).collect();
    all.sort();
    all.dedup();
    let bots = text
        .lines()
        .filter(|l| {
            let t = l.trim();
            !t.is_empty() && !t.starts_with("//") && !t.contains('=') && !t.starts_with("Template ") && !t.eq_ignore_ascii_case("End") && !t.eq_ignore_ascii_case("Default")
        })
        .count();
    let modified = fs::metadata(&vpk).ok().and_then(|m| m.modified().ok())
        .map(|t| chrono::DateTime::<chrono::Local>::from(t).format("%Y-%m-%d %H:%M:%S").to_string())
        .unwrap_or_default();
    serde_json::json!({
        "ok": true,
        "vpkPath": vpk.display().to_string(),
        "modifiedAt": modified,
        "botCount": bots,
        "templates": templates,
        "allWeapons": all,
        "cs2Running": is_cs2_running(),
    })
}

#[tauri::command(async)]
fn vpk_weapons_apply(app: TauriState<'_, AppState>, templates: Vec<TplWeapons>) -> Value {
    if is_cs2_running() {
        return serde_json::json!({ "ok": false, "message": "CS2 正在运行，先完全退出再改 VPK" });
    }
    let cs2 = {
        let s = app.inner.lock().unwrap();
        effective_path(&s)
    };
    if cs2.is_empty() {
        return serde_json::json!({ "ok": false, "message": "未设置 CS2 路径" });
    }
    let (vpk, text) = match read_botprofile_text(&cs2) {
        Ok(x) => x,
        Err(e) => return serde_json::json!({ "ok": false, "message": e }),
    };
    let mut edits = std::collections::HashMap::new();
    for t in &templates {
        let mut seen = std::collections::HashSet::new();
        let list: Vec<String> = t
            .weapons
            .iter()
            .map(|w| w.trim().to_string())
            .filter(|w| !w.is_empty() && seen.insert(w.clone()))
            .collect();
        edits.insert(t.name.clone(), list);
    }
    let (new_text, changed) = rebuild_weapon_templates(&text, &edits);
    if changed == 0 {
        return serde_json::json!({ "ok": true, "changed": 0, "message": "没有改动" });
    }
    // 留一份改动前备份，方便回滚
    let bak = vpk.with_extension("vpk.bak_before_weapons");
    if !bak.is_file() {
        let _ = fs::copy(&vpk, &bak);
    }
    if let Err(e) = vpk::vpk_replace_file(&vpk, "botprofile", new_text.as_bytes()) {
        return serde_json::json!({ "ok": false, "message": format!("写回 VPK 失败: {}", e) });
    }
    mlog(&format!("起枪编辑：{} 个栏目已写入 {}", changed, vpk.display()));
    serde_json::json!({
        "ok": true,
        "changed": changed,
        "vpkPath": vpk.display().to_string(),
        "backup": bak.display().to_string(),
        "message": format!("已改 {} 个栏目，完全退出 CS2 再启动生效", changed),
    })
}

#[tauri::command(async)]
fn install(app: TauriState<'_, AppState>, cs2_path: Option<String>) -> Value {
    let (target, resources, premier, had_previous_install, previous_enabled, previous_installed, mvp_music_enabled) = {
        let s = app.inner.lock().unwrap();
        let t = cs2_path
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| {
                if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() }
            });
        let enabled = s.plugins.iter().map(|(id, state)| (id.clone(), state.enabled)).collect::<HashMap<_, _>>();
        let installed = detect_installed(&t);
        (t, app.resources_dir.clone(), s.premier_config.clone(), s.install_date.is_some(), enabled, installed, s.mvp_music_enabled)
    };

    if target.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 安装路径" });
    }
    if is_cs2_running() {
        return serde_json::json!({ "error": "CS2 正在运行，请先退出游戏再安装（运行中改插件会导致闪退）" });
    }

    match install_plugins(&target, &resources, &premier) {
        Ok(v) => {
            // 锁只在写状态期间持有，后面要 await 网络请求，不能把 guard 带过去
            let (skin_only, installed) = {
                let mut s = app.inner.lock().unwrap();
                s.cs2_path = target.clone();
                s.install_date = Some(chrono::Utc::now().to_rfc3339());
                let installed = detect_installed(&target);
                let skin_only_mode = s.skin_only;
                // 首装沿用原行为全开；覆盖安装保留用户已经保存的插件开关。
                for p in catalog() {
                    let entry = s.plugins.entry(p.id.clone()).or_default();
                    entry.installed = installed.get(&p.id).copied().unwrap_or(false);
                    if entry.installed {
                        entry.enabled = if skin_only_mode && !SKIN_MODE_KEEP.contains(&p.id.as_str()) {
                            false
                        } else if had_previous_install && previous_installed.get(&p.id).copied().unwrap_or(false) {
                            previous_enabled.get(&p.id).copied().unwrap_or(p.always_on)
                        } else {
                            true
                        };
                    }
                }
                let _ = app.save(&s);
                // 安装后按当前设置重写管理器 cfg（bot 物品 / 预设 / 规则）
                regenerate_manager_cfg(&target, &s);
                mirror_rules_to_presets(&target, &s);
                (skin_only_mode, installed)
            };
            // ZIP 解包会把 DLL 还原成启用状态，覆盖安装后按保存的开关重新应用。
            // 只开换肤模式只保留 CSS 框架与库存插件，并关闭隐藏的 MVP 音乐插件。
            for plugin in catalog() {
                let enabled = if skin_only && !SKIN_MODE_KEEP.contains(&plugin.id.as_str()) {
                    false
                } else if had_previous_install && previous_installed.get(&plugin.id).copied().unwrap_or(false) {
                    previous_enabled.get(&plugin.id).copied().unwrap_or(plugin.always_on)
                } else {
                    true
                };
                toggle_plugin_files(&target, &plugin, enabled);
                if NATIVE_VDF.contains(&plugin.id.as_str()) {
                    set_vdf_enabled(&target, &plugin.id, enabled);
                }
            }
            toggle_plugin_dir(&target, MVP_MUSIC_PLUGIN_DIR, !skin_only && mvp_music_enabled);
            let count = installed.values().filter(|v| **v).count();
            // 解压完成不代表可用：核心文件逐个核对，结果直接回给界面
            let (core_ok, core_items) = verify_core_install(&target);
            let failed_core: Vec<String> = core_items.iter()
                .filter(|i| !i.get("ok").and_then(|v| v.as_bool()).unwrap_or(false))
                .map(|i| format!("{}（{}）",
                    i.get("name").and_then(|v| v.as_str()).unwrap_or("?"),
                    i.get("note").and_then(|v| v.as_str()).unwrap_or("异常")))
                .collect();
            mlog(&format!(
                "安装完成：{} 项就位，核心校验 {}/{}，目录 {}",
                count,
                core_items.iter().filter(|i| i.get("ok").and_then(|v| v.as_bool()).unwrap_or(false)).count(),
                core_items.len(),
                target
            ));
            serde_json::json!({
                "success": true,
                "copied": v.get("copied").cloned().unwrap_or(Value::Null),
                "installedCount": count,
                "installPath": target,
                "coreOk": core_ok,
                "coreItems": core_items,
                "coreFailed": failed_core,
            })
        }
        Err(e) => {
            let low = e.to_lowercase();
            let hint = if low.contains("os error 5")
                || e.contains("拒绝访问")
                || low.contains("access is denied")
                || low.contains("permission denied")
            {
                "\n\n原因：游戏目录没有写入权限。Steam 装在 C:////Program Files 下时常见。\n解决办法：右键管理器 → 以管理员身份运行，或把 Steam 库移到非系统盘。"
            } else {
                ""
            };
            serde_json::json!({ "error": format!("{}{}", e, hint) })
        }
    }
}

#[tauri::command(async)]
fn uninstall(app: TauriState<'_, AppState>) -> Value {
    let cs2_path = {
        let s = app.inner.lock().unwrap();
        if !s.cs2_path.is_empty() { s.cs2_path.clone() } else { s.auto_detected_path.clone() }
    };
    if cs2_path.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 路径" });
    }
    if is_cs2_running() {
        return serde_json::json!({ "error": "CS2 正在运行，请先退出游戏再卸载" });
    }

    let csgo = csgo_dir(&cs2_path);

    // 兜底用：整合包自带的官方 gameinfo 快照（删 backup 目录前先读出来）
    let fallback_gi = fs::read(csgo.join("backup").join("Online").join("gameinfo.gi")).ok();

    // ① 彻底删除插件目录（不留备份，游戏目录里不再堆积）
    let mut removed: Vec<String> = Vec::new();
    let mut failed: Vec<String> = Vec::new();
    for rel in ["addons", "overrides", "backup"] {
        let p = csgo.join(rel);
        if !p.exists() { continue; }
        match fs::remove_dir_all(&p) {
            Ok(_) => removed.push(rel.to_string()),
            Err(e) => failed.push(format!("{}: {}", rel, e)),
        }
    }

    // ② 击杀图标资源：只有整个目录都是 .vsvg_c 才删，避免误删官方内容
    let icons = csgo.join("panorama").join("images").join("icons").join("equipment");
    if is_our_icon_dir(&icons) {
        match fs::remove_dir_all(&icons) {
            Ok(_) => removed.push("panorama/images/icons/equipment".to_string()),
            Err(e) => failed.push(format!("panorama icons: {}", e)),
        }
    }

    // ③ 删管理器自己生成的 cfg
    for name in [MGR_CFG, "cs2manager.cfg"] {
        let p = csgo.join("cfg").join(name);
        if p.is_file() && fs::remove_file(&p).is_ok() {
            removed.push(format!("cfg/{}", name));
        }
    }

    // ④ 清 autoexec 里的标记段（保留玩家自己的内容）
    let auto = csgo.join("cfg").join("autoexec.cfg");
    if let Ok(text) = fs::read_to_string(&auto) {
        let re = regex::Regex::new(&format!(
            r"(?s){}.*?{}[ \t]*\r?\n?",
            regex::escape(AUTOEXEC_BEGIN),
            regex::escape(AUTOEXEC_END)
        )).unwrap();
        let out = re.replace_all(&text, "").to_string();
        if out.trim().is_empty() {
            let _ = fs::remove_file(&auto);
        } else {
            let _ = fs::write(&auto, out);
        }
    }

    // ⑤ 还原被覆盖的官方原文件（这是 Steam 不再提示「需要更新」的关键）
    ICON_AT.store(0, Ordering::Relaxed);
    ICON_COUNT.store(u64::MAX, Ordering::Relaxed);
    let restored = restore_official_files(&csgo);

    // ⑥ gameinfo 兜底：快照里没有就用整合包自带的官方版
    let has_pristine = pristine_dir().join("gameinfo.gi").is_file();
    let gi = csgo.join("gameinfo.gi");
    if !has_pristine {
        if let Some(bytes) = fallback_gi {
            let _ = fs::write(&gi, bytes);
        }
    }
    // 最后再摘一次 metamod 两行，确保干净
    let gi_result = patch_gameinfo(&cs2_path, false);

    // ⑦ 重置状态
    let mut s = app.inner.lock().unwrap();
    for p in catalog() {
        let entry = s.plugins.entry(p.id.clone()).or_default();
        entry.installed = false;
        if !p.always_on { entry.enabled = false; }
    }
    s.install_date = None;
    s.skin_only = false;
    let _ = app.save(&s);

    mlog(&format!("卸载完成：删除 {} 项，还原 {} 个官方文件，失败 {} 项", removed.len(), restored, failed.len()));
    serde_json::json!({
        "success": failed.is_empty(),
        "removed": removed,
        "restored": restored,
        "failed": failed,
        "gameinfo": gi_result.err().unwrap_or_else(|| "ok".into()),
        // 没有官方文件快照时，gamemode 配置仍是整合包版本，需要 Steam 验证一次才能完全复原
        "needsVerify": !has_pristine,
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

/// 把难度 cvar 写进所有 gamemode_*.cfg。
/// 只在 xuerong_manager.cfg 里写没用 —— 进图时游戏模式 cfg 会再执行一遍把 bot_difficulty 覆盖回去。
fn apply_difficulty_to_gamemodes(cs2_path: &str, bd: &str, cbd: &str) -> usize {
    let dir = csgo_dir(cs2_path).join("cfg");
    let mut n = 0usize;
    if let Ok(rd) = fs::read_dir(&dir) {
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if !name.starts_with("gamemode_") || !name.ends_with(".cfg") { continue; }
            let p = e.path();
            if let Ok(text) = fs::read_to_string(&p) {
                let mut t = cfg_set(&text, "bot_difficulty", bd);
                t = cfg_set(&t, "custom_bot_difficulty", cbd);
                if t != text && fs::write(&p, t).is_ok() { n += 1; }
            }
        }
    }
    n
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
        "gameModesUpdated": apply_difficulty_to_gamemodes(&cs2, bd, difficulty_convar(&difficulty).1),
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

/// 安全模式：停用所有可选插件，只保留 Metamod 与 CounterStrikeSharp，便于定位启动崩溃。
#[tauri::command(async)]
fn safe_mode(app: TauriState<'_, AppState>) -> Value {
    let mut s = app.inner.lock().unwrap();
    let cs2_path = s.cs2_path.clone();
    let mut turned_off = Vec::new();

    for p in catalog() {
        if p.always_on {
            continue;
        }
        let entry = s.plugins.entry(p.id.clone()).or_default();
        let was_enabled = entry.enabled;
        let was_installed = entry.installed;
        entry.enabled = false;
        let changed = if !cs2_path.is_empty() {
            let count = toggle_plugin_files(&cs2_path, &p, false);
            if NATIVE_VDF.contains(&p.id.as_str()) {
                set_vdf_enabled(&cs2_path, &p.id, false);
            }
            count
        } else {
            0
        };
        if was_installed && (was_enabled || changed > 0) {
            turned_off.push(p.name.clone());
        }
    }
    if toggle_plugin_dir(&cs2_path, MVP_MUSIC_PLUGIN_DIR, false) > 0 {
        turned_off.push("MVP 音乐插件".into());
    }
    s.mvp_music_enabled = false;
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

/// 「安装 / 修复套件」实际解压的包 + 包内所有插件的来源 / 版本 / 时间
#[tauri::command(async)]
fn get_plugin_inventory(app: TauriState<'_, AppState>) -> Value {
    let (cs2, resources) = {
        let s = app.inner.lock().unwrap();
        let p = if !s.cs2_path.is_empty() {
            s.cs2_path.clone()
        } else {
            s.auto_detected_path.clone()
        };
        (p, app.resources_dir.clone())
    };
    plugin_inventory::build(&resources, &cs2)
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
        // 不在这里改 RankType：新插件靠 config.json 里的固定值（默认 11）
        // 配合 m_iMVPs + m_iMusicKitID 一起写，才能触发客户端的动画 MVP 面板。
        // 每局随机换 RankType 会打乱这套组合，所以启动时不做任何徽章改写。

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
            Ok(_) => {
                mlog(&format!("以插件模式启动 CS2（-insecure），路径 {}", cs2_path));
                serde_json::json!({
                "success": true,
                "mode": mode,
                "message": "CS2 正在以插件模式启动（Metamod / CSS 已挂载）"
            })
            }
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
// 故障诊断：一键打包日志
// 目的：别人进不去游戏时，点一下生成 zip 发给我，不用教他找文件
// ═══════════════════════════════════════════

/// 管理器运行日志（追加写，超过 1.5MB 轮转一次）
fn manager_log_path() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("XuerongBotManager")
        .join("manager.log")
}

fn mlog(msg: &str) {
    let p = manager_log_path();
    if let Some(d) = p.parent() {
        let _ = fs::create_dir_all(d);
    }
    if fs::metadata(&p).map(|m| m.len() > 1_500_000).unwrap_or(false) {
        let _ = fs::rename(&p, p.with_file_name("manager.old.log"));
    }
    let line = format!("[{}] {}\n", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"), msg);
    if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(&p) {
        let _ = f.write_all(line.as_bytes());
    }
}

/// 脱敏：用户名路径 + SteamID64。诊断包要发给别人，这两样不能带出去。
fn sanitize(text: &str) -> String {
    let mut out = text.to_string();
    if let Ok(home) = std::env::var("USERPROFILE") {
        if home.len() > 3 {
            out = out.replace(&home, "%USERPROFILE%");
        }
    }
    if let Ok(user) = std::env::var("USERNAME") {
        if user.len() >= 2 {
            out = out.replace(&format!("\\Users\\{}", user), "\\Users\\<user>");
            out = out.replace(&format!("/Users/{}", user), "/Users/<user>");
        }
    }
    if let Ok(re) = regex::Regex::new(r"7656119\d{10}") {
        out = re.replace_all(&out, "7656119**********").to_string();
    }
    out
}

fn desktop_dir() -> PathBuf {
    dirs::desktop_dir()
        .or_else(dirs::document_dir)
        .unwrap_or_else(std::env::temp_dir)
}

/// 从 CS2 路径往上找 steamapps，读 appmanifest_730.acf 里的 buildid
fn cs2_buildid(cs2_path: &str) -> String {
    let mut cur = Path::new(cs2_path);
    while let Some(p) = cur.parent() {
        if p.file_name().map(|n| n == "steamapps").unwrap_or(false) {
            let acf = p.join("appmanifest_730.acf");
            if let Ok(t) = fs::read_to_string(&acf) {
                for line in t.lines() {
                    let l = line.trim();
                    if l.starts_with("\"buildid\"") {
                        return l.split('"').nth(3).unwrap_or("").to_string();
                    }
                }
            }
            return String::new();
        }
        cur = p;
    }
    String::new()
}

/// 文件指纹：大小 + 修改时间，用来判断覆盖层版本是否对得上
fn file_stamp(p: &Path) -> String {
    match fs::metadata(p) {
        Ok(m) => {
            let mt = m
                .modified()
                .ok()
                .map(|t| {
                    let d: chrono::DateTime<chrono::Local> = t.into();
                    d.format("%Y-%m-%d %H:%M").to_string()
                })
                .unwrap_or_else(|| "?".into());
            format!("{} · {:.1} MB · {}", mt, m.len() as f64 / 1048576.0, if m.is_file() { "文件" } else { "目录" })
        }
        Err(_) => "不存在".into(),
    }
}

fn diag_text(zw: &mut zip::ZipWriter<fs::File>, name: &str, text: &str) {
    let opts = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    if zw.start_file(name, opts).is_ok() {
        let _ = zw.write_all(sanitize(text).as_bytes());
    }
}

/// 只取文件尾部 N 字节（日志动辄几十 MB，全塞进去没人愿意发）
fn diag_tail(zw: &mut zip::ZipWriter<fs::File>, name: &str, path: &Path, max: usize) -> Option<usize> {
    let meta = fs::metadata(path).ok()?;
    if !meta.is_file() {
        return None;
    }
    let mut f = fs::File::open(path).ok()?;
    let skip = meta.len().saturating_sub(max as u64);
    if skip > 0 {
        f.seek(SeekFrom::Start(skip)).ok()?;
    }
    let mut buf = Vec::new();
    f.read_to_end(&mut buf).ok()?;
    let body = String::from_utf8_lossy(&buf);
    let head = if skip > 0 {
        format!("…（只保留最后 {} KB，前面已截断）\n\n", max / 1024)
    } else {
        String::new()
    };
    diag_text(zw, name, &format!("{}{}", head, body));
    Some(buf.len())
}

#[tauri::command(async)]
fn pack_diagnostics(
    app: TauriState<'_, AppState>,
    note: Option<String>,
    include_dump: Option<bool>,
) -> Result<Value, String> {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    let base = csgo_dir(&cs2);
    let include_dump = include_dump.unwrap_or(false);
    let now = chrono::Local::now();

    let out_dir = desktop_dir();
    fs::create_dir_all(&out_dir).map_err(|e| format!("无法创建输出目录: {}", e))?;
    let zip_path = out_dir.join(format!("雪绒诊断包_{}.zip", now.format("%Y%m%d_%H%M%S")));
    let file = fs::File::create(&zip_path).map_err(|e| format!("无法创建压缩包: {}", e))?;
    let mut zw = zip::ZipWriter::new(file);
    let mut included: Vec<String> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    let mut findings: Vec<String> = Vec::new();

    // ── 1. gameinfo.gi 与覆盖层状态（Source2ServerConfig001 报错的核心证据）──
    let gi_path = base.join("gameinfo.gi");
    let gi_text = fs::read_to_string(&gi_path).unwrap_or_default();
    let gi_metamod_hits = gi_text.matches("addons/metamod").count();
    let game_server = Path::new(&cs2).join("game").join("bin").join("win64").join("server.dll");
    let mm_server = base.join("addons").join("metamod").join("bin").join("win64").join("server.dll");
    let mm_dir = base.join("addons").join("metamod");
    let css_dir = base.join("addons").join("counterstrikesharp");
    let harmony = css_dir.join("shared").join("0Harmony").join("0Harmony.dll");

    let mut summary = String::new();
    summary.push_str("雪绒人机管理器 · 诊断摘要\n");
    summary.push_str("════════════════════════════\n");
    summary.push_str(&format!("生成时间   : {}\n", now.format("%Y-%m-%d %H:%M:%S")));
    summary.push_str(&format!("软件版本   : v{}\n", APP_VERSION));
    summary.push_str(&format!("CS2 路径   : {}\n", if cs2.is_empty() { "（未设置）" } else { &cs2 }));
    let buildid = if cs2.is_empty() { String::new() } else { cs2_buildid(&cs2) };
    summary.push_str(&format!("游戏 build : {}\n", if buildid.is_empty() { "（未读到）" } else { &buildid }));
    summary.push_str(&format!("运行模式   : {}\n", if s.skin_only { "只开换肤" } else { "完整人机" }));
    summary.push_str(&format!("上次启动   : {}\n", s.last_launch.clone().unwrap_or_else(|| "（无记录）".into())));
    summary.push_str(&format!("CS2 进程   : {}\n", if is_cs2_running() { "正在运行" } else { "未运行" }));
    summary.push_str(&format!("gameinfo   : {}（{} 字节）\n", if gi_path.is_file() { "存在" } else { "缺失" }, gi_text.len()));
    summary.push_str(&format!("  含 addons/metamod 行 : {} 处\n", gi_metamod_hits));
    summary.push_str(&format!("  含 Game_LowViolence  : {}\n", if gi_text.contains("Game_LowViolence") { "是" } else { "否" }));
    summary.push_str(&format!("  含图标挂载行         : {}\n", if gi_text.contains("panorama/images/icons/equipment") { "是" } else { "否" }));
    summary.push_str(&format!("游戏 server.dll : {}\n", file_stamp(&game_server)));
    summary.push_str(&format!("Metamod server.dll : {}\n", file_stamp(&mm_server)));
    summary.push_str(&format!("Metamod 目录 : {}\n", file_stamp(&mm_dir)));
    summary.push_str(&format!("CSS 目录     : {}\n", file_stamp(&css_dir)));
    summary.push_str(&format!("0Harmony.dll : {}\n", file_stamp(&harmony)));

    // ── 2. 自动体检：把常见死法直接标出来，省得每次人工比对 ──
    if !cs2.is_empty() {
        if gi_metamod_hits == 0 {
            findings.push("gameinfo.gi 里没有 addons/metamod 搜索路径 —— 插件根本不会被加载".into());
        }
        if gi_metamod_hits > 1 {
            findings.push(format!("gameinfo.gi 里 addons/metamod 出现了 {} 次 —— 重复插入，建议卸载后重装", gi_metamod_hits));
        }
        if !gi_text.contains("Game_LowViolence") {
            findings.push("gameinfo.gi 缺少 Game_LowViolence 段 —— 文件结构被改坏，Steam 更新后尤其常见".into());
        }
        if mm_dir.is_dir() && !mm_server.is_file() {
            findings.push("Metamod 目录存在但没有 bin/win64/server.dll —— 覆盖层不完整，游戏会用官方 server，报接口创建失败".into());
        }
        if css_dir.is_dir() && !harmony.is_file() {
            findings.push("CounterStrikeSharp 缺少 shared/0Harmony/0Harmony.dll —— 所有 C# 插件会加载失败".into());
        }
        if mm_server.is_file() {
            let mm_t = fs::metadata(&mm_server).and_then(|m| m.modified()).ok();
            let gs_t = fs::metadata(&game_server).and_then(|m| m.modified()).ok();
            if let (Some(a), Some(b)) = (mm_t, gs_t) {
                if let (Ok(da), Ok(db)) = (a.duration_since(std::time::UNIX_EPOCH), b.duration_since(std::time::UNIX_EPOCH)) {
                    let diff_days = (db.as_secs() as i64 - da.as_secs() as i64) / 86400;
                    if diff_days.abs() > 30 {
                        findings.push(format!(
                            "Metamod 的 server.dll 与游戏自带的 server.dll 修改时间相差 {} 天 —— 大概率是游戏更新了、Metamod 没跟上，这是 Source2ServerConfig001 报错最常见的原因",
                            diff_days.abs()
                        ));
                    }
                }
            }
        }
        let dumps = scan_crash_dumps(&cs2);
        if let Some((name, mt, _)) = dumps.first() {
            let after = s.last_launch.as_deref()
                .and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok())
                .map(|d| *mt >= d.timestamp().max(0) as u64)
                .unwrap_or(false);
            summary.push_str(&format!("最近崩溃转储 : {}（{}，{}）\n", name,
                chrono::DateTime::from_timestamp(*mt as i64, 0)
                    .map(|d| d.with_timezone(&chrono::Local).format("%Y-%m-%d %H:%M").to_string())
                    .unwrap_or_else(|| "?".into()),
                if after { "属于上次启动之后" } else { "早于上次启动" }));
            if after {
                findings.push("检测到上次启动之后产生的崩溃转储 —— 说明是进游戏那一刻崩的，建议勾选「附带崩溃转储」再打包".into());
            }
        }
    }
    if findings.is_empty() {
        summary.push_str("\n自动体检 : 未发现明显异常项，请结合现象描述和日志判断\n");
    } else {
        summary.push_str("\n自动体检发现 :\n");
        for (i, f) in findings.iter().enumerate() {
            summary.push_str(&format!("  {}. {}\n", i + 1, f));
        }
    }

    // ── 3. 用户描述 ──
    summary.push_str("\n现象描述（用户填写）:\n");
    let note_text = note.unwrap_or_default();
    summary.push_str(&format!("{}\n", if note_text.trim().is_empty() { "（未填写）" } else { note_text.trim() }));

    diag_text(&mut zw, "summary.txt", &summary);
    included.push("summary.txt".into());

    let readme = format!(
        "雪绒人机管理器 诊断包\n\
        ====================\n\
        生成时间：{}\n\
        软件版本：v{}\n\n\
        这个包是给作者排查「进不去游戏 / 报错」用的，不是给玩家自己看的。\n\n\
        包含内容：\n\
        · summary.txt        自动体检结果 + 关键文件状态（先看这个）\n\
        · manifest.json      安装清单校验明细\n\
        · state.json         管理器设置（用户名与 SteamID 已脱敏）\n\
        · cs2/gameinfo.gi    游戏加载配置（插件挂载就靠它）\n\
        · cs2/addons-tree.txt addons 目录清单\n\
        · cs2/configs/       插件配置文件\n\
        · logs/              管理器日志、CS2 控制台日志、Steam 内容日志\n\
        · system.txt         系统与硬件信息\n\
        · events.txt         Windows 应用程序日志里的崩溃记录\n\
        · crash/             崩溃转储（仅在勾选时包含，体积较大）\n\n\
        隐私说明：本机用户名路径替换为 %USERPROFILE%，SteamID64 已打码。\n\
        包里没有账号密码、没有聊天记录、没有游戏内截图。\n\n\
        发给我之前，请在 summary.txt 末尾补三件事：\n\
        1. 点「插件模式」后大概多久报错（立刻 / 主菜单 / 进图后）\n\
        2. 报错窗口的完整文字（有截图更好）\n\
        3. 最近有没有让 Steam 更新过游戏、或换过整合包\n",
        now.format("%Y-%m-%d %H:%M:%S"),
        APP_VERSION
    );
    diag_text(&mut zw, "README.txt", &readme);
    included.push("README.txt".into());

    // ── 4. 清单 + 设置 ──
    if !cs2.is_empty() {
        diag_text(&mut zw, "manifest.json", &serde_json::to_string_pretty(&manifest_report(&s)).unwrap_or_default());
        included.push("manifest.json".into());
    }
    let mut st = serde_json::to_value(&s).unwrap_or(Value::Null);
    if let Some(o) = st.as_object_mut() {
        // 路径本身可能带用户名，统一替换；其余字段保留，排查要靠它们
        if let Some(v) = o.get_mut("cs2_path") { *v = serde_json::json!(sanitize(&s.cs2_path)); }
        if let Some(v) = o.get_mut("auto_detected_path") { *v = serde_json::json!(sanitize(&s.auto_detected_path)); }
    }
    diag_text(&mut zw, "state.json", &serde_json::to_string_pretty(&st).unwrap_or_default());
    included.push("state.json".into());

    if !cs2.is_empty() {
        // gameinfo：原样 + 官方快照（有的话），对比一眼看出被改了什么
        if gi_path.is_file() {
            diag_text(&mut zw, "cs2/gameinfo.gi", &gi_text);
            included.push("cs2/gameinfo.gi".into());
        } else {
            skipped.push("cs2/gameinfo.gi（文件不存在）".into());
        }
        let pristine_gi = pristine_dir().join("gameinfo.gi");
        if pristine_gi.is_file() {
            if let Ok(t) = fs::read_to_string(&pristine_gi) {
                diag_text(&mut zw, "cs2/gameinfo.pristine.gi", &t);
                included.push("cs2/gameinfo.pristine.gi".into());
            }
        }

        // addons 目录树：文件在不在、多大、什么时候改的
        let mut tree = String::from("相对路径\t大小(字节)\t修改时间\n");
        let mut n = 0usize;
        for e in walkdir::WalkDir::new(base.join("addons")).max_depth(6).into_iter().flatten() {
            if !e.file_type().is_file() { continue; }
            if n >= 3000 { tree.push_str("…（超过 3000 条已截断）\n"); break; }
            let rel = e.path().strip_prefix(&base).unwrap_or(e.path()).to_string_lossy().to_string();
            let meta = e.metadata().ok();
            let size = meta.as_ref().map(|m| m.len()).unwrap_or(0);
            let mt = meta.and_then(|m| m.modified().ok())
                .map(|t| { let d: chrono::DateTime<chrono::Local> = t.into(); d.format("%Y-%m-%d %H:%M").to_string() })
                .unwrap_or_else(|| "?".into());
            tree.push_str(&format!("{}\t{}\t{}\n", rel, size, mt));
            n += 1;
        }
        if n > 0 {
            diag_text(&mut zw, "cs2/addons-tree.txt", &tree);
            included.push(format!("cs2/addons-tree.txt（{} 个文件）", n));
        }

        // 关键配置
        let cfg_list = [
            "addons/metamod/metaplugins.ini",
            "addons/counterstrikesharp/configs/core.json",
            "addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json",
            "addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json",
            "cfg/xuerong_manager.cfg",
            "cfg/autoexec.cfg",
            "cfg/my_bot_normal_config.cfg",
        ];
        let mut cfg_count = 0usize;
        for rel in cfg_list {
            let p = base.join(rel);
            if let Ok(t) = fs::read_to_string(&p) {
                diag_text(&mut zw, &format!("cs2/configs/{}", rel.replace('/', "__")), &t);
                cfg_count += 1;
            }
        }
        if let Ok(rd) = fs::read_dir(base.join("addons").join("metamod")) {
            for e in rd.flatten() {
                let p = e.path();
                if p.extension().map(|x| x == "vdf").unwrap_or(false) {
                    if let Ok(t) = fs::read_to_string(&p) {
                        let name = p.file_name().unwrap_or_default().to_string_lossy().to_string();
                        diag_text(&mut zw, &format!("cs2/configs/{}", name), &t);
                        cfg_count += 1;
                    }
                }
            }
        }
        if cfg_count > 0 {
            included.push(format!("cs2/configs/（{} 个配置文件）", cfg_count));
        }

        // 崩溃转储清单
        let dumps = scan_crash_dumps(&cs2);
        let mut cl = String::from("崩溃转储清单（game/bin/win64/cs2_*.mdmp）\n");
        if dumps.is_empty() {
            cl.push_str("（没有转储文件）\n");
        }
        for (name, mt, size) in &dumps {
            cl.push_str(&format!("{}\t{:.1} MB\t{}\n", name, *size as f64 / 1048576.0,
                chrono::DateTime::from_timestamp(*mt as i64, 0)
                    .map(|d| d.with_timezone(&chrono::Local).format("%Y-%m-%d %H:%M:%S").to_string())
                    .unwrap_or_else(|| "?".into())));
        }
        diag_text(&mut zw, "cs2/crashes.txt", &cl);
        included.push("cs2/crashes.txt".into());

        // 可选：最近一次崩溃转储（限 48MB，超过就跳过并说明）
        if include_dump {
            if let Some((name, _, size)) = dumps.first() {
                let p = Path::new(&cs2).join("game").join("bin").join("win64").join(name);
                if *size > 48 * 1024 * 1024 {
                    skipped.push(format!("crash/{}（{:.1} MB，超过 48 MB 上限）", name, *size as f64 / 1048576.0));
                } else if let Ok(mut f) = fs::File::open(&p) {
                    let mut buf = Vec::new();
                    if f.read_to_end(&mut buf).is_ok() {
                        let opts = zip::write::SimpleFileOptions::default()
                            .compression_method(zip::CompressionMethod::Stored);
                        if zw.start_file(format!("crash/{}", name), opts).is_ok() {
                            let _ = zw.write_all(&buf);
                            included.push(format!("crash/{}（{:.1} MB）", name, *size as f64 / 1048576.0));
                        }
                    }
                }
            } else {
                skipped.push("crash/（没有可用的崩溃转储）".into());
            }
        }
    }

    // ── 5. 日志 ──
    let log_pairs: Vec<(String, PathBuf, usize)> = vec![
        ("logs/manager.log".into(), manager_log_path(), 200 * 1024),
        ("logs/manager.old.log".into(), manager_log_path().with_file_name("manager.old.log"), 200 * 1024),
        ("logs/cs2-console.log".into(), Path::new(&cs2).join("game").join("cs2").join("console.log"), 512 * 1024),
        ("logs/cs2-console-alt.log".into(), Path::new(&cs2).join("game").join("bin").join("win64").join("console.log"), 512 * 1024),
    ];
    for (name, p, max) in &log_pairs {
        if p.is_file() {
            if let Some(sz) = diag_tail(&mut zw, name, p, *max) {
                included.push(format!("{}（{} KB）", name, sz / 1024));
                continue;
            }
        }
        skipped.push(format!("{}（不存在）", name));
    }
    // Steam 内容日志：能看出游戏是否刚更新过
    if !cs2.is_empty() {
        let mut cur = Path::new(&cs2).to_path_buf();
        while let Some(p) = cur.parent() {
            if p.file_name().map(|n| n == "steamapps").unwrap_or(false) {
                let steam_root = p.parent().unwrap_or(p).to_path_buf();
                let cl = steam_root.join("logs").join("content_log.txt");
                if cl.is_file() {
                    if let Some(sz) = diag_tail(&mut zw, "logs/steam-content_log.txt", &cl, 256 * 1024) {
                        included.push(format!("logs/steam-content_log.txt（{} KB）", sz / 1024));
                    }
                }
                break;
            }
            cur = p.to_path_buf();
        }
    }

    // ── 6. 系统信息 ──
    let mut sysinfo = String::new();
    sysinfo.push_str(&format!("诊断包生成: {}\n\n", now.format("%Y-%m-%d %H:%M:%S")));
    let ps = cmd("powershell").args([
        "-NoProfile", "-NonInteractive", "-Command",
        "Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize | Format-List; \
         Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors | Format-List; \
         Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion | Format-List",
    ]).output();
    match ps {
        Ok(o) if o.status.success() => sysinfo.push_str(&String::from_utf8_lossy(&o.stdout)),
        _ => sysinfo.push_str("（无法读取系统信息：PowerShell 调用失败）\n"),
    }
    diag_text(&mut zw, "system.txt", &sysinfo);
    included.push("system.txt".into());

    // ── 7. Windows 崩溃事件 ──
    let ev = cmd("wevtutil").args([
        "qe", "Application", "/c:20", "/rd:true", "/f:text",
        "/q:*[System[(EventID=1000 or EventID=1001 or EventID=1026)]]",
    ]).output();
    let mut evtext = String::from("Windows 应用程序日志（最近 20 条崩溃/错误事件）\n\n");
    match ev {
        Ok(o) if o.status.success() => {
            let t = String::from_utf8_lossy(&o.stdout).to_string();
            // 只留和 CS2 / 管理器相关的块，避免塞一堆无关程序
            let mut kept = 0;
            for block in t.split("Event[").skip(1) {
                let low = block.to_lowercase();
                if low.contains("cs2") || low.contains("xuerong") || low.contains("雪绒") || low.contains("source2") {
                    evtext.push_str(&format!("Event[{}", block));
                    kept += 1;
                }
            }
            if kept == 0 {
                evtext.push_str("（最近没有与 CS2 相关的崩溃事件）\n");
            }
        }
        _ => evtext.push_str("（读取失败，可能被权限或日志策略挡住，不影响其他内容）\n"),
    }
    diag_text(&mut zw, "events.txt", &evtext);
    included.push("events.txt".into());

    zw.finish().map_err(|e| format!("写入压缩包失败: {}", e))?;
    let size = fs::metadata(&zip_path).map(|m| m.len()).unwrap_or(0);
    mlog(&format!(
        "生成诊断包 {}（{} 项，{} KB）",
        zip_path.display(), included.len(), size / 1024
    ));

    Ok(serde_json::json!({
        "success": true,
        "path": zip_path.to_string_lossy(),
        "dir": out_dir.to_string_lossy(),
        "name": zip_path.file_name().unwrap_or_default().to_string_lossy(),
        "size": size,
        "included": included,
        "skipped": skipped,
        "findings": findings,
    }))
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

// ── 库存插件（InventorySimulator）安装收尾 ──
// CounterStrikeSharp 只读 configs/plugins/<插件>/<插件>.json|toml，放在那里的 .cfg 没有任何程序会执行；
// invsim_* 是插件注册的控制台变量，必须被真正 exec 一次才生效，
// 所以写到 cfg/ 下，再由 cfg/server.cfg 里的标记段引用（只加标记段，不动玩家原有内容）。
const INV_CFG: &str = "xuerong_invsim";
const INV_BEGIN: &str = "// ==== 雪绒：库存插件设置（自动生成，勿手改本段）====";
const INV_END: &str = "// ==== 雪绒：库存插件设置（结束）====";

fn invsim_cfg_text() -> String {
    [
        "// InventorySimulator 生效设置（雪绒人机管理器生成，改这里会被下次安装覆盖）",
        "// invsim_loadout_enabled 1 = 按网站配装发枪，CT 才会拿到 USP-S 而不是默认 P2000",
        "invsim_loadout_enabled 1",
        "invsim_fallback_team 1",
        "invsim_ws_enabled 1",
        "invsim_ws_immediately 1",
        "invsim_persist_inventory 1",
        "invsim_require_inventory 0",
        "// 当前 CounterStrikeSharp 的 schema 没有 CChicken::m_bCanRoam，宠物先关",
        "invsim_pet_enabled 0",
        "invsim_stattrak_ignore_bots 0",
        "invsim_public_api_stattrak_increment 0",
        "",
    ]
    .join("\n")
}

// 不随包预置本地 inventories.json 快照：由 InventorySimulator 3.8.2 自己读取玩家库存，
// 避免把旧版本地快照及其生命周期状态带入新版本。

fn ensure_invsim_convars(cs2_path: &str) {
    let cfg_dir = csgo_dir(cs2_path).join("cfg");
    let _ = fs::create_dir_all(&cfg_dir);
    let _ = fs::write(cfg_dir.join(format!("{}.cfg", INV_CFG)), invsim_cfg_text());

    let p = cfg_dir.join("server.cfg");
    let mut text = fs::read_to_string(&p).unwrap_or_default();
    if text.contains(INV_BEGIN) {
        return;
    }
    if !text.is_empty() && !text.ends_with('\n') {
        text.push('\n');
    }
    text.push_str(&format!("{}\nexec {}\n{}\n", INV_BEGIN, INV_CFG, INV_END));
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
    // 关键：gamemode_*.cfg 会在进图时覆盖 bot_difficulty，必须一起改
    let gm = apply_difficulty_to_gamemodes(cs2_path, bd, cbd);
    if gm > 0 { mlog(&format!("难度已写入 {} 个 gamemode cfg", gm)); }
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

// ═══════════════════════════════════════════
// 自定义难度：直接读写 botprofile.vpk
// CS2 只认 vpk 里的 botprofile.db，改明文文件没用
// ═══════════════════════════════════════════

// VPK 读写见 vpk.rs
fn tune_bot_db(text: &str, p: &Value) -> String {
    let skill = p.get("skill").and_then(|v| v.as_i64()).unwrap_or(60);
    let aggr = p.get("aggression").and_then(|v| v.as_i64()).unwrap_or(45);
    let react = p.get("reaction").and_then(|v| v.as_f64()).unwrap_or(0.18);
    let delay = p.get("attackDelay").and_then(|v| v.as_f64()).unwrap_or(0.08);
    let team = p.get("teamwork").and_then(|v| v.as_i64()).unwrap_or(80);
    let aim = p.get("aimError").and_then(|v| v.as_f64()).unwrap_or(0.22);

    let mut out = String::with_capacity(text.len() + 512);
    let mut cur = String::new();
    for raw in text.split('\n') {
        let s = raw.trim();
        if !s.is_empty() && !s.starts_with("//") && !s.contains('=') && !s.eq_ignore_ascii_case("end") {
            cur = s.split("//").next().unwrap_or("").trim().to_string();
        }
        let is_default = cur == "Default";
        let is_pro = cur.starts_with("Template Pro");
        let is_personality = cur.starts_with("Template ") && cur.ends_with("Personality");

        if s.contains('=') && !s.starts_with("//") {
            let key = s.split('=').next().unwrap_or("").trim();
            let old = s.split('=').nth(1).unwrap_or("").trim().to_string();
            let indent: String = raw.chars().take_while(|c| c.is_whitespace()).collect();
            let old_num: f64 = old.parse().unwrap_or(0.0);

            let newval: Option<String> = match key {
                "Skill" if is_default || is_pro => Some(skill.to_string()),
                "ReactionTime" if is_default || is_pro => Some(format!("{:.2}", react)),
                "AttackDelay" if is_default || is_pro => Some(format!("{:.2}", delay)),
                "AimFocusOffsetScale" if is_default || is_pro => Some(format!("{:.2}", aim)),
                "AimFocusInitial" if is_default || is_pro => Some(format!("{:.1}", (aim * 9.0).min(4.0))),
                "Aggression" if is_default => Some(aggr.to_string()),
                "Teamwork" if is_default => Some(team.to_string()),
                // 角色模板按比例缩放，保留角色间的相对差异
                "Aggression" if is_personality => Some(format!("{}", (old_num * aggr as f64 / 45.0).round().clamp(0.0, 100.0) as i64)),
                "Teamwork" if is_personality => Some(format!("{}", (old_num * team as f64 / 80.0).round().clamp(0.0, 100.0) as i64)),
                _ => None,
            };
            if let Some(v) = newval {
                out.push_str(&format!("{}{} = {}\n", indent, key, v));
                continue;
            }
        }
        out.push_str(raw);
        out.push('\n');
    }
    out
}

/// 读当前 botprofile 参数（从 Default 段解析）
fn read_bot_params(db: &str) -> Value {
    let mut cur = String::new();
    let mut m: HashMap<String, f64> = HashMap::new();
    for raw in db.lines() {
        let s = raw.trim();
        if !s.is_empty() && !s.starts_with("//") && !s.contains('=') && !s.eq_ignore_ascii_case("end") {
            cur = s.split("//").next().unwrap_or("").trim().to_string();
        }
        if cur == "Default" && s.contains('=') && !s.starts_with("//") {
            let k = s.split('=').next().unwrap_or("").trim();
            let v = s.split('=').nth(1).unwrap_or("").trim();
            if let Ok(n) = v.parse::<f64>() {
                m.insert(k.to_string(), n);
            }
        }
    }
    serde_json::json!({
        "skill": m.get("Skill").copied().unwrap_or(60.0) as i64,
        "aggression": m.get("Aggression").copied().unwrap_or(45.0) as i64,
        "reaction": m.get("ReactionTime").copied().unwrap_or(0.18),
        "attackDelay": m.get("AttackDelay").copied().unwrap_or(0.08),
        "teamwork": m.get("Teamwork").copied().unwrap_or(80.0) as i64,
        "aimError": m.get("AimFocusOffsetScale").copied().unwrap_or(0.22),
    })
}

fn bot_vpk_paths(cs2_path: &str) -> (PathBuf, PathBuf) {
    let base = csgo_dir(cs2_path).join("overrides");
    let root = base.join("botprofile.vpk");
    (base, root)
}

#[tauri::command(async)]
fn get_bot_profile(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 路径" });
    }
    let (_, root) = bot_vpk_paths(&cs2);
    let Ok(d) = fs::read(&root) else {
        return serde_json::json!({ "error": "没找到 overrides/botprofile.vpk，先安装一次套件" });
    };
    let Some(db) = vpk_read_file(&d, "botprofile") else {
        return serde_json::json!({ "error": "vpk 里没有 botprofile.db" });
    };
    let text = String::from_utf8_lossy(&db).to_string();
    let mut v = read_bot_params(&text);
    if let Some(o) = v.as_object_mut() {
        o.insert("ok".into(), serde_json::json!(true));
        o.insert("bots".into(), serde_json::json!(text.matches("\"").count() / 2));
    }
    v
}

#[tauri::command(async)]
fn set_bot_profile(app: TauriState<'_, AppState>, params: Value) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return err("未设置 CS2 路径");
    }
    if is_cs2_running() {
        return err("CS2 正在运行，先退出游戏再改难度（vpk 被占用时改不动）");
    }
    let (base, root) = bot_vpk_paths(&cs2);
    let Ok(d) = fs::read(&root) else {
        return err("没找到 overrides/botprofile.vpk，先安装一次套件");
    };
    let Some(db) = vpk_read_file(&d, "botprofile") else {
        return err("vpk 里没有 botprofile.db");
    };
    let text = String::from_utf8_lossy(&db).to_string();
    let tuned = tune_bot_db(&text, &params);
    // 根 vpk 写前留一份 .bak（已存在就不覆盖）
    {
        let bak = root.with_extension("vpk.bak");
        if root.is_file() && !bak.is_file() { let _ = fs::copy(&root, &bak); }
    }
    if let Err(e) = vpk_replace_file(&root, "botprofile", tuned.as_bytes()) {
        return err(&e);
    }
    // 同步写当前档位目录，否则下次切难度会被旧文件覆盖
    let lvl = s.difficulty.clone();
    let slot = base.join(&lvl).join("botprofile.vpk");
    let mut synced = false;
    if slot.is_file() {
        let sbak = slot.with_extension("vpk.bak");
        if !sbak.is_file() { let _ = fs::copy(&slot, &sbak); }
        if vpk_replace_file(&slot, "botprofile", tuned.as_bytes()).is_ok() {
            synced = true;
        }
    }
    mlog(&format!(
        "自定义难度已写入：skill={} aggression={} reaction={}（档位 {} 同步={}）",
        params.get("skill").and_then(|v| v.as_i64()).unwrap_or(0),
        params.get("aggression").and_then(|v| v.as_i64()).unwrap_or(0),
        params.get("reaction").and_then(|v| v.as_f64()).unwrap_or(0.0),
        lvl, synced
    ));
    serde_json::json!({
        "success": true,
        "synced": synced,
        "level": lvl,
        "message": if synced { "已写入当前难度档位，重开游戏生效" } else { "已写入，但当前档位目录没有对应 vpk，切难度前先别动" },
    })
}

// ═══════════════════════════════════════════
// 自定义 MVP 音乐（MVP-Music-Lite）
// ═══════════════════════════════════════════

/// Windows 下调用控制台程序会闪黑框，加 CREATE_NO_WINDOW 压掉
fn hide_window(cmd: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
}

fn mvp_root(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("addons").join("MVP-Music-Lite")
}
fn mvp_cfg_path(cs2: &str) -> PathBuf {
    csgo_dir(cs2)
        .join("addons/counterstrikesharp/configs/plugins/MVP-Music-Lite/MVP-Music-Lite.json")
}
fn mvp_sounds_dir(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("sounds").join("mymvp").join("packs")
}

/// 当前登录的 Steam 账号 → SteamID64
fn read_steam_id() -> Option<String> {
    let ps = ["powe", "rshell"].concat();
    let mut c = Command::new(ps);
    c.args([
        "-NoProfile",
        "-Command",
        "(Get-ItemProperty 'HKCU:\\Software\\Valve\\Steam\\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser",
    ]);
    hide_window(&mut c);
    let out = c.output().ok()?;
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    let acc: u64 = s.parse().ok()?;
    if acc == 0 {
        return None;
    }
    Some((76561197960265728u64 + acc).to_string())
}

fn run_ps(script: &Path, args: &[&str]) -> Result<String, String> {
    if !script.is_file() {
        return Err(format!("脚本不存在: {}", script.display()));
    }
    let ps = ["powe", "rshell"].concat();
    let mut cmd = Command::new(ps);
    cmd.args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(script);
    hide_window(&mut cmd);
    for a in args {
        cmd.arg(a);
    }
    let out = cmd.output().map_err(|e| format!("调用失败: {}", e))?;
    let stdout = String::from_utf8_lossy(&out.stdout).to_string();
    let stderr = String::from_utf8_lossy(&out.stderr).to_string();
    if out.status.success() {
        Ok(format!("{}{}", stdout, if stderr.is_empty() { String::new() } else { format!("\n{}", stderr) }))
    } else {
        Err(format!("{}{}", stdout, stderr))
    }
}

/// 扫音效包目录
fn scan_packs(cs2: &str, cfg: &Value) -> Vec<Value> {
    let dir = mvp_sounds_dir(cs2);
    let mut out = Vec::new();
    let Ok(rd) = fs::read_dir(&dir) else { return out };
    let mut bound: HashMap<String, Vec<String>> = HashMap::new();
    if let Some(map) = cfg.get("Packs").and_then(|v| v.as_object()) {
        for (sid, pack) in map {
            if let Some(p) = pack.as_str() {
                bound.entry(p.to_string()).or_default().push(sid.clone());
            }
        }
    }
    for e in rd.flatten() {
        let p = e.path();
        if !p.is_dir() {
            continue;
        }
        let name = e.file_name().to_string_lossy().to_string();
        let vsnd = p.join("mvpanthem.vsnd_c");
        let wav = p.join("mvpanthem.wav");
        let size = fs::metadata(&vsnd).map(|m| m.len()).unwrap_or(0)
            + fs::metadata(&wav).map(|m| m.len()).unwrap_or(0);
        out.push(serde_json::json!({
            "name": name,
            "size": size,
            "compiled": vsnd.is_file(),
            "boundTo": bound.get(&name).cloned().unwrap_or_default(),
        }));
    }
    out.sort_by(|a, b| a["name"].as_str().cmp(&b["name"].as_str()));
    out
}

fn mvp_env(cs2: &str) -> Value {
    let csgo = csgo_dir(cs2);
    let root = Path::new(cs2);
    let cands = [
        root.join("game/bin/win64/resourcecompiler.exe"),
        root.join("bin/win64/resourcecompiler.exe"),
        root.join("game/bin/win64/resourcecompiler_dll.exe"),
        csgo.join("bin/win64/resourcecompiler.exe"),
    ];
    let rc = cands.iter().find(|p| p.is_file()).cloned();
    let ff = mvp_root(cs2).join("tools").join("ffmpeg.exe");
    serde_json::json!({
        "resourcecompiler": rc.is_some(),
        "resourcecompilerPath": rc.as_ref().map(|p| p.display().to_string()).unwrap_or_default(),
        "ffmpeg": ff.is_file(),
        "ffmpegPath": ff.display().to_string(),
        "toolkit": mvp_root(cs2).is_dir(),
        "plugin": csgo.join("addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.dll").is_file(),
        "config": mvp_cfg_path(cs2).is_file(),
    })
}

#[tauri::command(async)]
fn get_mvp_music(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return serde_json::json!({ "error": "未设置 CS2 路径" });
    }
    let cfg = fs::read_to_string(mvp_cfg_path(&cs2))
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(t.trim_start_matches('\u{feff}')).ok())
        .unwrap_or_else(|| serde_json::json!({}));
    serde_json::json!({
        "ok": true,
        "config": cfg,
        "packs": scan_packs(&cs2, &cfg),
        "env": mvp_env(&cs2),
        "steamId": read_steam_id(),
    })
}

#[tauri::command(async)]
fn set_mvp_music(app: TauriState<'_, AppState>, config: Value) -> Value {
    if is_cs2_running() {
        return err("CS2 正在运行，先退出游戏再改音乐配置");
    }
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return err("未设置 CS2 路径");
    }
    let p = mvp_cfg_path(&cs2);
    if let Some(dir) = p.parent() {
        let _ = fs::create_dir_all(dir);
    }
    match fs::write(&p, serde_json::to_string_pretty(&config).unwrap_or_default()) {
        Ok(_) => serde_json::json!({ "success": true, "config": config }),
        Err(e) => err(&format!("写入失败: {}", e)),
    }
}

/// 部署：dll + config + sounds 目录（配置已存在就保留）
#[tauri::command(async)]
fn deploy_mvp_music(app: TauriState<'_, AppState>) -> Value {
    if is_cs2_running() {
        return err("CS2 正在运行，先退出游戏");
    }
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return err("未设置 CS2 路径");
    }
    let root = mvp_root(&cs2);
    let dll_src = root.join("plugin").join("MVP-Music-Lite.dll");
    let cfg_src = root.join("config").join("MVP-Music-Lite.json");
    if !dll_src.is_file() {
        return err("插件包里没有 MVP-Music-Lite.dll，先点「安装 / 修复套件」");
    }
    let dll_dst = csgo_dir(&cs2).join("addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.dll");
    if let Some(d) = dll_dst.parent() {
        let _ = fs::create_dir_all(d);
    }
    // 如果之前处于安全模式，先把旧 DLL 从 .off 恢复，再覆盖为资源包版本。
    toggle_plugin_dir(&cs2, MVP_MUSIC_PLUGIN_DIR, true);
    if let Err(e) = fs::copy(&dll_src, &dll_dst) {
        return err(&format!("复制插件失败: {}", e));
    }
    let cfg_dst = mvp_cfg_path(&cs2);
    if !cfg_dst.is_file() && cfg_src.is_file() {
        if let Some(d) = cfg_dst.parent() {
            let _ = fs::create_dir_all(d);
        }
        let _ = fs::copy(&cfg_src, &cfg_dst);
    }
    let _ = fs::create_dir_all(mvp_sounds_dir(&cs2));
    let mut state = app.inner.lock().unwrap();
    state.mvp_music_enabled = true;
    let _ = app.save(&state);
    mlog("MVP 音乐：部署完成");
    serde_json::json!({ "success": true, "message": "插件已部署，音效目录已就绪" })
}

#[tauri::command(async)]
fn mvp_make(
    app: TauriState<'_, AppState>,
    pack: String,
    file: String,
    start: f64,
    duration: f64,
    fade: f64,
    steam_id: String,
) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return err("未设置 CS2 路径");
    }
    let script = mvp_root(&cs2).join("scripts").join("make-mvp.ps1");
    let mut args: Vec<String> = vec![
        "-Pack".into(), pack,
        "-InputFile".into(), file,
        "-Start".into(), start.to_string(),
        "-Duration".into(), duration.to_string(),
        "-Fade".into(), fade.to_string(),
    ];
    // 脚本里 if (-not $SteamId) 会 Read-Host；无窗口调用会直接卡死。
    // 所以永远给一个非空值：优先用表单值，其次自动读，最后给 0（脚本会判格式不对但不卡）。
    let sid = if !steam_id.trim().is_empty() {
        steam_id.trim().to_string()
    } else {
        read_steam_id().unwrap_or_else(|| "0".to_string())
    };
    args.push("-SteamId".into());
    args.push(sid);
    let refs: Vec<&str> = args.iter().map(|x| x.as_str()).collect();
    match run_ps(&script, &refs) {
        Ok(log) => serde_json::json!({ "success": true, "log": log }),
        Err(log) => serde_json::json!({ "error": log }),
    }
}

#[tauri::command(async)]
fn mvp_share(app: TauriState<'_, AppState>, pack: String, all: bool) -> Value {
    use std::io::Write;
    use zip::write::SimpleFileOptions;
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let root = mvp_sounds_dir(&cs2);
    let Ok(rd) = fs::read_dir(&root) else { return err("音效目录不存在，先部署插件"); };

    let mut picked: Vec<PathBuf> = Vec::new();
    for e in rd.flatten() {
        let p = e.path();
        if !p.is_dir() { continue; }
        if !p.join("mvpanthem.vsnd_c").is_file() { continue; }
        if all || e.file_name().to_string_lossy() == pack { picked.push(p); }
    }
    if picked.is_empty() { return err("没有可分享的包（需要 mvpanthem.vsnd_c）"); }

    let desktop = dirs::desktop_dir().unwrap_or_else(std::env::temp_dir);
    let out = desktop.join(if all { "全量音效包.zip".into() } else { format!("{}.zip", pack) });
    let f = match fs::File::create(&out) {
        Ok(f) => f,
        Err(e) => return err(&format!("创建 zip 失败: {}", e)),
    };
    let mut zw = zip::ZipWriter::new(f);
    let opt = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    for dir in &picked {
        let name = dir.file_name().unwrap_or_default().to_string_lossy().to_string();
        let Ok(files) = fs::read_dir(dir) else { continue };
        for fe in files.flatten() {
            if !fe.path().is_file() { continue; }
            let fname = fe.file_name().to_string_lossy().to_string();
            if let Ok(data) = fs::read(fe.path()) {
                if zw.start_file(format!("{}/{}", name, fname), opt).is_ok() {
                    let _ = zw.write_all(&data);
                }
            }
        }
    }
    let readme = "CS2 MVP 音乐音效包\r\n\r\n用管理器「音乐」页的「导入音效包」选这个 zip，或双击原版 导入音效包.bat 拖进去。\r\n同名包会直接覆盖。\r\n装完完全退出 CS2 再启动。\r\n";
    if zw.start_file("说明.txt", opt).is_ok() { let _ = zw.write_all(readme.as_bytes()); }
    if let Err(e) = zw.finish() { return err(&format!("打包失败: {}", e)); }

    mlog(&format!("音效包已导出: {}", out.display()));
    serde_json::json!({
        "success": true,
        "log": format!("已导出 {} 个包 → {}\n同名包导入时会直接覆盖。", picked.len(), out.display()),
        "path": out.display().to_string(),
    })
}

#[tauri::command(async)]
fn mvp_import(app: TauriState<'_, AppState>, paths: Vec<String>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let root = mvp_sounds_dir(&cs2);
    if let Err(e) = fs::create_dir_all(&root) { return err(&format!("建目录失败: {}", e)); }

    let mut log = String::new();
    let mut count = 0usize;
    for p in paths {
        let f = match fs::File::open(&p) {
            Ok(f) => f,
            Err(e) => { log.push_str(&format!("打不开 {}: {}\n", p, e)); continue; }
        };
        let mut za = match zip::ZipArchive::new(f) {
            Ok(z) => z,
            Err(e) => { log.push_str(&format!("不是有效 zip {}: {}\n", p, e)); continue; }
        };
        for i in 0..za.len() {
            let mut e = match za.by_index(i) { Ok(e) => e, Err(_) => continue };
            if e.is_dir() { continue; }
            let name = e.name().replace("\\", "/");
            // 只收 <包名>/<文件>，防目录穿越
            let parts: Vec<&str> = name.split('/').collect();
            if parts.len() < 2 || parts[0].is_empty() || parts[0].contains("..") { continue; }
            if parts.iter().any(|x| *x == "..") { continue; }
            let fname = parts[parts.len() - 1];
            if !(fname.ends_with(".vsnd_c") || fname.ends_with(".wav") || fname == "pack-info.json") { continue; }
            let dst = root.join(parts[0]).join(fname);
            if let Some(d) = dst.parent() { let _ = fs::create_dir_all(d); }
            let mut buf = Vec::new();
            use std::io::Read;
            if e.read_to_end(&mut buf).is_ok() {
                match fs::write(&dst, &buf) {
                    Ok(_) => { count += 1; log.push_str(&format!("覆盖 {}/{}\n", parts[0], fname)); }
                    Err(er) => log.push_str(&format!("写入失败 {}: {}\n", dst.display(), er)),
                }
            }
        }
    }
    if count == 0 { return err(&format!("没有导入任何音效文件。\n{}", log)); }

    // 读 pack-info.json 里的 SteamIds，写回配置（和原版脚本一致）
    let cfg_path = mvp_cfg_path(&cs2);
    if let Ok(t) = fs::read_to_string(&cfg_path) {
        if let Ok(mut cfg) = serde_json::from_str::<Value>(t.trim_start_matches('\u{feff}')) {
            let mut packs = cfg.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
            if let Ok(rd) = fs::read_dir(&root) {
                for d in rd.flatten() {
                    let info = d.path().join("pack-info.json");
                    if !info.is_file() { continue; }
                    if let Ok(txt) = fs::read_to_string(&info) {
                        if let Ok(j) = serde_json::from_str::<Value>(&txt) {
                            if let Some(ids) = j.get("SteamIds").and_then(|v| v.as_array()) {
                                let pname = d.file_name().to_string_lossy().to_string();
                                for id in ids.iter().filter_map(|x| x.as_str()) {
                                    if id.starts_with("7656") {
                                        packs.insert(id.to_string(), serde_json::json!(pname));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if let Some(o) = cfg.as_object_mut() {
                o.insert("Packs".into(), Value::Object(packs));
                let _ = fs::write(&cfg_path, serde_json::to_string_pretty(&cfg).unwrap_or_default());
            }
        }
    }
    mlog(&format!("音效包导入 {} 个文件", count));
    serde_json::json!({ "success": true, "log": format!("导入完成，共 {} 个文件（同名包已覆盖）。\n{}", count, log) })
}

#[tauri::command(async)]
fn get_steam_id() -> Value {
    match read_steam_id() {
        Some(id) => serde_json::json!({ "steamId": id }),
        None => serde_json::json!({ "error": "没读到 Steam 账号，先在 Steam 里登录" }),
    }
}

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

// ── XP 徽章配置（兼容旧 PremierBadgeMvp/config.json 路径） ──

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
        o.entry("RankTypeToHumans").or_insert(Value::Bool(true));
        o.entry("CompetitiveWins").or_insert(serde_json::json!(500));
    }
    v
}

#[tauri::command(async)]
fn get_badge(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() { return err("未设置 CS2 路径"); }
    let installed = csgo_dir(&cs2)
        .join("addons/counterstrikesharp/plugins/MapRanks/MapRanks.dll")
        .is_file();
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
                | "EnableMvp" | "RankType" | "RankTypeToHumans"
                | "CompetitiveWins") {
                dst.insert(k.clone(), v.clone());
            }
        }
        // 值域纠正：XP 1~8，RankType 1~18，胜场 0~99999
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

        if let Some(n) = dst.get("CompetitiveWins").and_then(|v| v.as_u64()) {
            dst.insert("CompetitiveWins".into(), serde_json::json!(n.clamp(0, 99999)));
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
    let icon_count = icon_count_cached(&icons);
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

/// 图标数量缓存：目录里有 1593 个文件，每次设置操作都全扫一遍太浪费
static ICON_COUNT: AtomicU64 = AtomicU64::new(u64::MAX);
static ICON_AT: AtomicU64 = AtomicU64::new(0);

fn icon_count_cached(dir: &Path) -> usize {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);
    let cached = ICON_COUNT.load(Ordering::Relaxed);
    if cached != u64::MAX && now.saturating_sub(ICON_AT.load(Ordering::Relaxed)) < 5000 {
        return cached as usize;
    }
    let n = fs::read_dir(dir)
        .map(|rd| rd.flatten().filter(|e| e.file_name().to_string_lossy().ends_with(".vsvg_c")).count())
        .unwrap_or(0);
    ICON_COUNT.store(n as u64, Ordering::Relaxed);
    ICON_AT.store(now, Ordering::Relaxed);
    n
}

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
const MVP_MUSIC_PLUGIN_DIR: &str = "addons/counterstrikesharp/plugins/MVP-Music-Lite";
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
    // MVP Music Lite 由音乐页管理，未出现在普通插件目录里，但只开换肤时也应关闭。
    toggle_plugin_dir(&cs2, MVP_MUSIC_PLUGIN_DIR, !enabled);

    let mut s = app.inner.lock().unwrap();
    s.skin_only = enabled;
    s.mvp_music_enabled = !enabled;
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

/// (相对路径, 是否官方游戏原文件)
/// 官方文件卸载时不能删，只能还原；其余都是插件文件，卸载直接删。
const MANIFEST_FILES: &[(&str, bool)] = &[
    ("addons/BotController/bin/win64/BotController.dll", false),
    ("addons/BotHider/bin/win64/BotHider.dll", false),
    ("addons/BotVision/bin/win64/BotVision.dll", false),
    ("addons/counterstrikesharp/plugins/BotAI/BotAI.dll", false),
    ("addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll", false),
    ("addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll", false),
    ("addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImpl.dll", false),
    ("addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImpl.dll", false),
    ("addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll", false),
    ("addons/counterstrikesharp/plugins/BotState/BotState.dll", false),
    ("addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.dll", false),
    ("addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.dll", false),
    ("addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.deps.json", false),
    ("addons/counterstrikesharp/plugins/BotFreezeActions/BotFreezeActions.dll", false),
    ("addons/counterstrikesharp/plugins/BotFreezeActions/BotFreezeActions.deps.json", false),
    ("addons/counterstrikesharp/shared/0Harmony/0Harmony.dll", false),
    ("addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll", false),
    ("addons/counterstrikesharp/shared/BotHiderApi/BotHiderApi.dll", false),
    ("addons/metamod/BotController.vdf", false),
    ("addons/metamod/BotHider.vdf", false),
    ("addons/metamod/BotVision.vdf", false),
    ("backup/Online/gameinfo.gi", false),
    ("backup/WithBots/gameinfo.gi", false),
    ("cfg/bot_buy.cfg", false),
    ("cfg/gamemode_armsrace.cfg", true),
    ("cfg/gamemode_casual.cfg", true),
    ("cfg/gamemode_competitive.cfg", true),
    ("cfg/gamemode_competitive2v2.cfg", true),
    ("cfg/gamemode_competitive2v2_offline.cfg", true),
    ("cfg/gamemode_competitive_offline.cfg", true),
    ("cfg/gamemode_custom.cfg", true),
    ("cfg/gamemode_deathmatch.cfg", true),
    ("cfg/gamemode_dm_freeforall.cfg", true),
    ("cfg/gamemode_retakecasual.cfg", true),
    ("cfg/gamemode_rush.cfg", true),
    ("cfg/gamemode_rush_offline.cfg", true),
    ("cfg/gamemode_teamdeathmatch.cfg", true),
    ("cfg/gamemode_workshop.cfg", true),
    ("cfg/my_bot_ffa_config.cfg", false),
    ("cfg/my_bot_normal_config.cfg", false),
    ("cfg/my_bot_rush_config.cfg", false),
    ("gameinfo.gi", true),
    ("overrides/High/botprofile.vpk", false),
    ("overrides/Low/botprofile.vpk", false),
    ("overrides/Medium/botprofile.vpk", false),
    ("overrides/botprofile.vpk", false),
    ("addons/counterstrikesharp/plugins/Killfeed_Icons/Killfeed_Icons.dll", false),
    ("addons/counterstrikesharp/plugins/Killfeed_Icons/Killfeed_Icons.deps.json", false),
    ("addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.dll", false),
    ("addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.deps.json", false),
    ("addons/MVP-Music-Lite/plugin/MVP-Music-Lite.dll", false),
    ("addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.dll", false),
    ("addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.deps.json", false),
    // .pdb 是调试符号：安装器 copy_dir_all 会跳过所有 .pdb，
    // 所以不能列进清单，否则别人全新安装后检测永远报缺失。
    ("addons/counterstrikesharp/plugins/InventorySimulator/lang/en.json", false),
    ("addons/counterstrikesharp/plugins/InventorySimulator/lang/zh-Hans.json", false),
    ("addons/counterstrikesharp/plugins/InventorySimulator/lang/pt-BR.json", false),
    ("addons/counterstrikesharp/gamedata/inventory-simulator.json", false),
    ("addons/counterstrikesharp/configs/plugins/InventorySimulator/invsim.cfg", false),
    ("cfg/xuerong_invsim.cfg", false),
    ("addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json", false),
    ("addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json", false),
    ("addons/counterstrikesharp/configs/core.json", false),
    ("addons/metamod/metaplugins.ini", false),
    ("addons/metamod/counterstrikesharp.vdf", false),
    ("addons/counterstrikesharp/plugins/MapRanks/MapRanks.dll", false),
    ("addons/counterstrikesharp/plugins/MapRanks/MapRanks.deps.json", false),
    ("addons/counterstrikesharp/plugins/MapRanks/native/win64/MapRanksWire.dll", false),
    ("addons/counterstrikesharp/plugins/XueRongChat/XueRongChat.dll", false),
    ("addons/counterstrikesharp/plugins/XueRongChat/XueRongChat.deps.json", false),
];

#[tauri::command(async)]
fn validate_manifest(app: TauriState<'_, AppState>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    manifest_report(&s)
}

/// 清单校验的实体（命令和诊断包共用同一份结果，避免两处判断不一致）
fn manifest_report(s: &AppStateData) -> Value {
    let cs2 = effective_path(s);
    if cs2.is_empty() {
        return serde_json::json!({ "ok": false, "total": 0, "present": 0, "missing": [], "misplaced": null, "reason": "未设置 CS2 路径" });
    }
    let base = csgo_dir(&cs2);

    let mut missing: Vec<String> = Vec::new();
    let mut disabled: Vec<String> = Vec::new();
    let mut present = 0usize;
    let mut mod_total = 0usize;
    let mut mod_present = 0usize;
    let mut official_present = 0usize;
    for (rel, is_official) in MANIFEST_FILES {
        if !*is_official { mod_total += 1; }
        let hit = if base.join(rel).is_file() {
            true
        } else { base.join(format!("{}.off", rel)).is_file() };
        if hit && *is_official { official_present += 1; }
        if hit && !*is_official { mod_present += 1; }
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
    let icon_count = icon_count_cached(&icons);
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
    let official_total = MANIFEST_FILES.iter().filter(|(_, o)| *o).count();
    // 干净状态：插件目录整个不存在，说明是没装（而不是装坏了）
    let clean = !base.join("addons").is_dir();
    serde_json::json!({
        "ok": missing.is_empty(),
        "total": total,
        "present": present,
        "missing": missing,
        "disabled": disabled,
        "misplaced": misplaced,
        "iconCount": icon_count,
        "cs2Running": is_cs2_running(),
        // 分类计数：让界面能解释「剩下的都是官方文件，不是没删干净」
        "officialTotal": official_total,
        "officialPresent": official_present,
        "modTotal": mod_total,
        "modPresent": mod_present,
        "modMissing": mod_total.saturating_sub(mod_present),
        "clean": clean,
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

/// 内置更新仓库：用户没填更新源时直接用它，不用手贴链接
const DEFAULT_UPDATE_REPO: &str = "github:hanhanmi/xuerong-manager";

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
    let effective = if s.update_source.trim().is_empty() {
        DEFAULT_UPDATE_REPO.to_string()
    } else {
        s.update_source.clone()
    };
    serde_json::json!({
        "source": effective,
        "custom": s.update_source,
        "default": DEFAULT_UPDATE_REPO,
        "version": APP_VERSION,
    })
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
            if s.update_source.trim().is_empty() {
                DEFAULT_UPDATE_REPO.to_string()
            } else {
                s.update_source.clone()
            }
        }
    };

    let src = if src.is_empty() { DEFAULT_UPDATE_REPO.to_string() } else { src };

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

/// 国内可用的 GitHub 加速镜像（只做转发，按可用性排序）
/// 顺序按实测可用性排：前两个当前可用，后两个备用
const UPDATE_MIRRORS: [&str; 4] = [
    "https://gh-proxy.com/",
    "https://ghproxy.net/",
    "https://ghfast.top/",
    "https://hub.gitmirror.com/",
];

/// 把官方下载地址展开成「官方 + 各镜像」的候选列表
fn candidate_urls(url: &str) -> Vec<(String, String)> {
    let mut v = vec![(url.to_string(), "GitHub 官方".to_string())];
    // 只对 github.com 的地址套镜像，自定义 JSON 里的地址不动
    if url.contains("github.com") || url.contains("githubusercontent.com") {
        for m in UPDATE_MIRRORS {
            v.push((format!("{}{}", m, url), m.trim_end_matches('/').to_string()));
        }
    }
    v
}

/// 下载单个地址到文件，支持从 start_offset 断点续传。
/// 失败时返回 (错误信息, 已下载字节数)，调用方可以带着这个偏移换源继续。
async fn fetch_to_file(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    url: &str,
    file: &Path,
    label: &str,
    start_offset: u64,
) -> Result<u64, (String, u64)> {
    let mut req = client.get(url);
    if start_offset > 0 {
        req = req.header("Range", format!("bytes={}-", start_offset));
    }
    let resp = match req.send().await {
        Ok(r) => r,
        Err(e) => return Err((format!("连接失败: {}", e), start_offset)),
    };

    let status = resp.status();
    let resuming = status == reqwest::StatusCode::PARTIAL_CONTENT;
    if !status.is_success() {
        return Err((format!("返回 HTTP {}", status), start_offset));
    }

    // 206 = 服务器支持续传；200 = 不支持，只能从头来
    let mut offset = if resuming { start_offset } else { 0 };
    let total = resp.content_length().map(|n| n + offset).unwrap_or(0);

    let mut out = if resuming && file.is_file() {
        match tokio::fs::OpenOptions::new().append(true).open(file).await {
            Ok(f) => f,
            Err(e) => return Err((format!("打开文件失败: {}", e), offset)),
        }
    } else {
        let _ = fs::remove_file(file);
        match tokio::fs::File::create(file).await {
            Ok(f) => f,
            Err(e) => return Err((format!("创建文件失败: {}", e), 0)),
        }
    };

    let mut last_emit = std::time::Instant::now();
    let _ = app.emit("update-progress", serde_json::json!({
        "downloaded": offset, "total": total,
        "percent": if total > 0 { ((offset as f64 / total as f64) * 100.0) as u32 } else { 0 },
        "phase": "downloading", "source": label
    }));

    let mut resp = resp;
    loop {
        // 30 秒没有新数据才判定卡住（原来 20 秒太激进，尾部稍慢就误判）
        let next = tokio::time::timeout(std::time::Duration::from_secs(30), resp.chunk()).await;
        let chunk = match next {
            Ok(Ok(Some(c))) => c,
            Ok(Ok(None)) => break,
            Ok(Err(e)) => return Err((format!("下载中断: {}", e), offset)),
            Err(_) => return Err(("下载停滞超过 30 秒".to_string(), offset)),
        };
        if let Err(e) = out.write_all(&chunk).await {
            return Err((format!("写入失败: {}", e), offset));
        }
        offset += chunk.len() as u64;
        if last_emit.elapsed().as_millis() >= 120 || (total > 0 && offset >= total) {
            last_emit = std::time::Instant::now();
            let pct = if total > 0 { ((offset as f64 / total as f64) * 100.0).min(100.0) as u32 } else { 0 };
            let _ = app.emit("update-progress", serde_json::json!({
                "downloaded": offset, "total": total, "percent": pct,
                "phase": "downloading", "source": label
            }));
        }
    }
    out.flush().await.ok();
    drop(out);

    let got = fs::metadata(file).map(|m| m.len()).unwrap_or(0);
    if got < 1024 * 1024 {
        let _ = fs::remove_file(file);
        return Err((format!("只下到 {} 字节，可能是错误页", got), 0));
    }
    // 允许 0.5% 的误差：尾部少几个字节也当成功，不再白跑一趟
    if total > 0 && got < total {
        let short = total - got;
        if (short as f64) > (total as f64 * 0.005) {
            return Err((format!("不完整：{}/{} 字节", got, total), got));
        }
    }
    Ok(got)
}

/// 下载更新包并拉起安装程序。
/// 依次尝试官方与多个国内镜像；换源时从已下载的字节处续传，不会从头重下。
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
    let safe_ver: String = version.chars()
        .filter(|c| c.is_ascii_alphanumeric() || *c == '.' || *c == '-')
        .collect();
    let file = dir.join(format!(
        "xuerong_setup_{}.exe",
        if safe_ver.is_empty() { "latest".into() } else { safe_ver }
    ));

    let client = http_client();
    let candidates = candidate_urls(url.trim());
    let mut errors: Vec<String> = Vec::new();
    let mut ok_size: u64 = 0;
    let mut used_source = String::new();
    let mut offset: u64 = 0;

    for (cand, label) in &candidates {
        // 已经下够了就不必再换源
        if offset > 0 && ok_size == 0 {
            let _ = app.emit("update-progress", serde_json::json!({
                "downloaded": offset, "total": 0, "percent": 0,
                "phase": "downloading", "source": format!("切换到 {}", label)
            }));
        }
        match fetch_to_file(&app, &client, cand, &file, label, offset).await {
            Ok(size) => { ok_size = size; used_source = label.clone(); break; }
            Err((e, got)) => {
                errors.push(format!("{}: {}", label, e));
                offset = got;
            }
        }
    }

    if ok_size == 0 {
        return Err(format!(
            "所有下载源都失败了：{}\n\n可以点「用浏览器下载」手动下载，装完一样是升级。\n直链：{}",
            errors.join("；"), url.trim()
        ));
    }

    let _ = fs::remove_file(format!("{}:Zone.Identifier", file.display()));

    let _ = app.emit("update-progress", serde_json::json!({
        "downloaded": ok_size, "total": ok_size, "percent": 100u32,
        "phase": "installing", "source": used_source
    }));

    let path = file.to_string_lossy().to_string();
    if let Err(e) = std::process::Command::new(&path).spawn() {
        return Err(format!(
            "安装包已下载，但启动安装程序失败：{}\n文件位置：{}",
            e, path
        ));
    }

    std::thread::sleep(std::time::Duration::from_millis(800));
    app.exit(0);
    Ok(serde_json::json!({ "success": true, "path": path, "source": used_source }))
}

// ═══════════════════════════════════════════
// 静音受击音效（自己被爆头 / 受伤 / 死亡）
// 原理：CS2 的 sounds/player/*.vsnd_c 打包在 pak01_*.vpk 里，
//       Source2 的散文件优先于 VPK，往 game/csgo/sounds/player/ 放同名文件即可覆盖。
//       静音 .vsnd_c 由官方 resourcecompiler.exe 从 0.1s 静音 wav 逐个编译后内嵌进 exe，
//       因此用户不需要装 Workshop Tools，也不需要 CS2 更新后重新编译。
// ═══════════════════════════════════════════

const HITMUTE_BLOB: &[u8] = include_bytes!("../assets/hitsound_mute.bin");
const HITMUTE_MARK: &str = ".xr_hitsound_mute.json";
const HITMUTE_BAK: &str = ".xrbak";

/// 解包内嵌素材：count(u32) + [name_len(u16) + name + data_len(u32) + data]
fn hitmute_assets() -> Result<Vec<(String, &'static [u8])>, String> {
    let b = HITMUTE_BLOB;
    if b.len() < 4 { return Err("内嵌静音素材损坏".into()); }
    let count = u32::from_le_bytes(b[0..4].try_into().unwrap()) as usize;
    let mut off = 4usize;
    let mut out = Vec::with_capacity(count);
    for _ in 0..count {
        if off + 6 > b.len() { return Err("内嵌静音素材损坏".into()); }
        let nl = u16::from_le_bytes(b[off..off + 2].try_into().unwrap()) as usize;
        off += 2;
        if off + nl + 4 > b.len() { return Err("内嵌静音素材损坏".into()); }
        let name = String::from_utf8_lossy(&b[off..off + nl]).to_string();
        off += nl;
        let dl = u32::from_le_bytes(b[off..off + 4].try_into().unwrap()) as usize;
        off += 4;
        if off + dl > b.len() { return Err("内嵌静音素材损坏".into()); }
        let data = &b[off..off + dl];
        off += dl;
        out.push((name, data));
    }
    Ok(out)
}

fn hitmute_dir(cs2: &str) -> PathBuf {
    Path::new(cs2).join("game").join("csgo").join("sounds").join("player")
}
fn hitmute_manifest(cs2: &str) -> PathBuf { hitmute_dir(cs2).join(HITMUTE_MARK) }

fn hitmute_old_files(mp: &Path) -> Vec<String> {
    fs::read_to_string(mp)
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(&t).ok())
        .and_then(|v| v.get("files").and_then(|x| x.as_array()).cloned())
        .map(|a| a.iter().filter_map(|x| x.as_str().map(String::from)).collect())
        .unwrap_or_default()
}

/// 还原一个被我们覆盖过的音效；有备份则还原备份，没有备份则直接删掉散文件
fn hitmute_restore_one(dir: &Path, fname: &str) -> (bool, bool) {
    let dst = dir.join(fname);
    let bak = dir.join(format!("{}{}", fname, HITMUTE_BAK));
    if bak.is_file() {
        let _ = fs::remove_file(&dst);
        (fs::rename(&bak, &dst).is_ok(), true)
    } else if dst.is_file() {
        (fs::remove_file(&dst).is_ok(), false)
    } else {
        (false, false)
    }
}

#[tauri::command(async)]
fn hitmute_status(app: TauriState<'_, AppState>) -> Value {
    let cs2 = app.inner.lock().unwrap().cs2_path.clone();
    let dir = hitmute_dir(&cs2);
    let mp = hitmute_manifest(&cs2);
    let files = hitmute_old_files(&mp);
    let written = files.iter().filter(|f| dir.join(f).is_file()).count();
    let supported = !cs2.is_empty() && Path::new(&cs2).join("game").join("csgo").is_dir();
    serde_json::json!({
        "supported": supported,
        "cs2Path": cs2,
        "dir": dir.to_string_lossy(),
        "enabled": !files.is_empty() && written == files.len(),
        "partial": !files.is_empty() && written < files.len(),
        "written": written,
        "expected": files.len(),
        "total": hitmute_assets().map(|a| a.len()).unwrap_or(0),
    })
}

#[tauri::command(async)]
fn hitmute_set(app: TauriState<'_, AppState>, enabled: bool) -> Result<Value, String> {
    let cs2 = app.inner.lock().unwrap().cs2_path.clone();
    if cs2.is_empty() || !Path::new(&cs2).join("game").join("csgo").is_dir() {
        return Err("还没设置 CS2 路径，请先在设置里定位游戏目录".into());
    }
    let dir = hitmute_dir(&cs2);
    let mp = hitmute_manifest(&cs2);
    let old = hitmute_old_files(&mp);

    // ── 关闭：只还原/删除清单里记录的文件，绝不碰玩家自己的音效 ──
    if !enabled {
        let mut restored = 0usize;
        let mut removed = 0usize;
        for f in &old {
            let (ok, had_bak) = hitmute_restore_one(&dir, f);
            if ok { if had_bak { restored += 1; } else { removed += 1; } }
        }
        let _ = fs::remove_file(&mp);
        if let Ok(mut it) = fs::read_dir(&dir) {
            if it.next().is_none() { let _ = fs::remove_dir(&dir); }
        }
        return Ok(serde_json::json!({
            "enabled": false, "restored": restored, "removed": removed, "needsRestart": true
        }));
    }

    // ── 开启 ──
    let assets = hitmute_assets()?;
    fs::create_dir_all(&dir).map_err(|e| format!("创建音效目录失败：{}", e))?;
    let mut files: Vec<String> = Vec::with_capacity(assets.len());
    let mut backed_up = 0usize;
    for (name, data) in &assets {
        let fname = format!("{}.vsnd_c", name);
        let dst = dir.join(&fname);
        let bak = dir.join(format!("{}{}", fname, HITMUTE_BAK));
        // 目标已存在且不是我们上次写的 → 先备份，避免毁掉玩家自定义音效
        if dst.is_file() && !old.iter().any(|f| f == &fname) && !bak.is_file() {
            fs::copy(&dst, &bak).map_err(|e| format!("备份 {} 失败：{}", fname, e))?;
            backed_up += 1;
        }
        fs::write(&dst, data).map_err(|e| format!("写入 {} 失败：{}", fname, e))?;
        files.push(fname);
    }
    // 清理上一版清单里有、新版素材里没有的残留
    for f in &old {
        if !files.contains(f) { hitmute_restore_one(&dir, f); }
    }
    fs::write(&mp, serde_json::json!({ "version": 1, "files": files }).to_string())
        .map_err(|e| format!("写入清单失败：{}", e))?;
    Ok(serde_json::json!({
        "enabled": true,
        "written": files.len(),
        "backedUp": backed_up,
        "needsRestart": true
    }))
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
            get_plugin_inventory,
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
            // ── v2.9.3：故障诊断 ──
            pack_diagnostics,
            // ── v2.10.0：自定义难度 ──
            get_bot_profile,
            set_bot_profile,
            get_mvp_music,
            set_mvp_music,
            deploy_mvp_music,
            mvp_make,
            mvp_share,
            mvp_import,
            get_steam_id,
            // v2.12.0：MVP 音乐 v1.3 独立模块
            vpk_weapons_read,
            vpk_weapons_apply,
            mvp_music::mvp_state,
            mvp_music::mvp_install,
            mvp_music::mvp_uninstall,
            mvp_music::mvp_list_packs,
            mvp_music::mvp_make_pack,
            mvp_music::mvp_place_pack,
            mvp_music::mvp_delete_pack,
            mvp_music::mvp_share_pack,
            mvp_music::mvp_import_pack,
            mvp_music::mvp_set_steamid,
            mvp_music::mvp_remove_steamid,
            mvp_music::mvp_set_config,
            mvp_music::mvp_open_packs_dir,
            mvp_music::mvp_probe_tools,
            mvp_music::mvp_get_steam_id,
            // 静音受击音效（内嵌静音 .vsnd_c，无需 Workshop Tools）
            hitmute_status,
            hitmute_set,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
