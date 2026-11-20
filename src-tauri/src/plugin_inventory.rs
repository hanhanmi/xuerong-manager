// 插件清单：读取「安装 / 修复套件」实际解压的那个 zip，
// 列出每个插件的来源、版本、构建时间与本机安装时间。
use serde_json::{json, Value};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use zip::ZipArchive;

/// 安装 / 修复套件会整体覆盖的目录（与 install_plugins 保持一致）
pub const INSTALL_TARGETS: &[&str] = &["addons", "overrides", "cfg", "backup", "panorama"];

/// 已知插件的来源 / 版本 / 说明。key 是插件目录名。
fn origin(name: &str) -> (&'static str, &'static str, &'static str) {
    match name {
        "InventorySimulator" => (
            "ianlucas/cs2-css-inventory-simulator 官方 release",
            "3.8.2",
            "上游 v3.8.2 正式发布版；含最新签名更新",
        ),
        "MapRanks" => (
            "雪绒定制 MapRanks（合并 XP 徽章）",
            "0.2.7-xuerong-xp-merged",
            "段位 / 动态 MVP / XP 徽章；由单一插件拥有段位 netvar",
        ),
        "MVP-Music-Lite" => (
            "自制插件（管理器「音乐」页直接驱动）",
            "v1.3.4",
            "自定义 MVP 音乐，音效包制作 / 分享 / 导入",
        ),
        "BotChatter" => (
            "CS:GO botchat.smx 移植（中文文案）",
            "v1.0.0",
            "人机垃圾话与名字前缀",
        ),
        "Killfeed_Icons" => (
            "雪绒维护源码；CounterStrikeSharp API 1.0.380 / .NET 10",
            "1.0.26",
            "!weaponkill 打开选包菜单；机器人每回合重新随机图标包",
        ),
        "BotFreezeActions" => (
            "雪绒自制源码；CounterStrikeSharp API 1.0.380 / .NET 10",
            "1.0.0",
            "冻结时间内机器人随机短暂切刀或下蹲",
        ),
        "BotRandomizer" => ("整合包内置", "—", "给人机随机皮肤 / 手套 / 挂件"),
        "BotAI" => ("整合包内置（native 补丁）", "—", "人机决策层，含找包行为补丁"),
        "BotController" | "BotControllerImpl" | "BotHider" | "BotHiderImpl" | "BotVision" => {
            ("整合包内置（BotHider 伪装层）", "—", "人机身份伪装与视野模拟")
        }
        "BotAimImprover" | "BotBuy" | "BotState" => ("整合包内置", "—", "人机枪法 / 购买 / 状态"),
        "NadeSystem" => ("整合包内置", "—", "投掷物系统"),
        "RoundDamageRecap" => (
            "CS2-Bot-Improver（按 CSS API 1.0.380 重建）",
            "1.3.0",
            "回合伤害统计；net10 插件",
        ),
        _ => ("整合包内置", "—", ""),
    }
}

fn fmt_zip_time(d: zip::DateTime) -> String {
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        d.year(), d.month(), d.day(), d.hour(), d.minute(), d.second()
    )
}

fn fmt_mtime(p: &Path) -> Option<String> {
    let t = fs::metadata(p).ok()?.modified().ok()?;
    let dt: chrono::DateTime<chrono::Local> = t.into();
    Some(dt.format("%Y-%m-%d %H:%M:%S").to_string())
}

fn find_zip(resources_dir: &Path) -> Option<PathBuf> {
    let direct = resources_dir.join("CS2BotImprover.zip");
    if direct.is_file() {
        return Some(direct);
    }
    for e in walkdir::WalkDir::new(resources_dir)
        .max_depth(3)
        .into_iter()
        .flatten()
    {
        if e.file_type().is_file() && e.file_name() == "CS2BotImprover.zip" {
            return Some(e.path().to_path_buf());
        }
    }
    None
}

#[derive(Default)]
struct Bucket {
    files: usize,
    size: u64,
    built: String,
    main_file: String,
}

impl Bucket {
    fn add(&mut self, size: u64, built: &str, main_file: &str) {
        self.files += 1;
        self.size += size;
        if built > self.built.as_str() {
            self.built = built.to_string();
        }
        if self.main_file.is_empty()
            || main_file.ends_with(".dll")
            || main_file.ends_with(".vpk")
        {
            self.main_file = main_file.to_string();
        }
    }
}

/// 从 zip 内的相对路径判断它属于哪个「单元」
fn bucket_of(name: &str) -> Option<(String, String)> {
    // (类型, 单元名)
    const P: &str = "addons/counterstrikesharp/plugins/";
    // XP 徽章配置沿用旧路径，但它现在归 MapRanks 单一插件管理。
    if name.starts_with("addons/counterstrikesharp/plugins/PremierBadgeMvp/") {
        return Some(("css_plugin".into(), "MapRanks".into()));
    }
    if let Some(rest) = name.strip_prefix(P) {
        let seg = rest.split('/').next().unwrap_or("");
        if !seg.is_empty() && !seg.contains('.') {
            return Some(("css_plugin".into(), seg.to_string()));
        }
    }
    let map = [
        ("addons/metamod/", "framework", "Metamod"),
        ("addons/counterstrikesharp/api/", "framework", "CounterStrikeSharp API"),
        ("addons/counterstrikesharp/gamedata/", "framework", "Gamedata 签名表"),
        ("addons/counterstrikesharp/configs/", "config", "CSS 配置"),
        ("addons/counterstrikesharp/bin/", "framework", "CSS 运行时"),
        ("addons/MVP-Music-Lite/", "toolkit", "MVP 音乐工具包"),
        ("addons/BotHider/", "toolkit", "BotHider 身份库"),
        ("addons/Harmony/", "framework", "Harmony"),
        ("panorama/", "resource", "击杀图标资源"),
        ("cfg/", "config", "对局配置 cfg"),
        ("overrides/", "resource", "botprofile.vpk 难度档位"),
        ("backup/", "resource", "官方原文件备份"),
        ("sounds/", "resource", "音效资源"),
    ];
    for (prefix, kind, unit) in map {
        if name.starts_with(prefix) {
            return Some((kind.to_string(), unit.to_string()));
        }
    }
    None
}

/// 游戏目录里对应的安装路径（用于取安装时间、判断是否已装）
fn installed_probe(csgo: &Path, kind: &str, unit: &str) -> Option<PathBuf> {
    let p = match kind {
        "css_plugin" => csgo
            .join("addons/counterstrikesharp/plugins")
            .join(unit)
            .join(format!("{}.dll", unit)),
        "framework" => match unit {
            "Metamod" => csgo.join("addons/metamod/bin/win64/metamod.2.cs2.dll"),
            "CSS 运行时" => csgo.join("addons/counterstrikesharp/bin/win64/counterstrikesharp.dll"),
            _ => return None,
        },
        "toolkit" => match unit {
            "MVP 音乐工具包" => csgo.join("addons/MVP-Music-Lite/scripts/make-mvp.ps1"),
            "BotHider 身份库" => csgo.join("addons/BotHider/bot_info.json"),
            _ => return None,
        },
        "resource" => match unit {
            "击杀图标资源" => csgo.join("panorama/images/icons/equipment"),
            "botprofile.vpk 难度档位" => csgo.join("overrides/botprofile.vpk"),
            _ => return None,
        },
        "config" => match unit {
            "对局配置 cfg" => csgo.join("cfg/xuerong_manager.cfg"),
            _ => return None,
        },
        _ => return None,
    };
    if p.exists() { Some(p) } else { None }
}

fn config_path_of(name: &str) -> String {
    format!(
        "addons/counterstrikesharp/configs/plugins/{0}/{0}.json",
        name
    )
}

pub fn build(resources_dir: &Path, cs2_path: &str) -> Value {
    let Some(zip_path) = find_zip(resources_dir) else {
        return json!({ "ok": false, "error": "未找到离线安装包 CS2BotImprover.zip" });
    };
    let Ok(f) = fs::File::open(&zip_path) else {
        return json!({ "ok": false, "error": "无法打开离线安装包" });
    };
    let Ok(mut ar) = ZipArchive::new(f) else {
        return json!({ "ok": false, "error": "离线安装包损坏" });
    };

    let mut buckets: BTreeMap<(String, String), Bucket> = BTreeMap::new();
    let mut manifest_meta: BTreeMap<String, Value> = BTreeMap::new();
    let mut manifest_raw: Vec<u8> = Vec::new();
    let mut bundle_version = String::new();
    let mut entries = 0usize;
    let mut zip_built = String::new();

    for i in 0..ar.len() {
        let Ok(e) = ar.by_index(i) else { continue };
        if e.is_dir() {
            continue;
        }
        entries += 1;
        let name = e.name().to_string();
        let size = e.size();
        let built = e
            .last_modified()
            .map(fmt_zip_time)
            .unwrap_or_default();
        if built.as_str() > zip_built.as_str() {
            zip_built = built.clone();
        }
        if name.ends_with("插件检测清单.json") {
            // 单独处理，不进桶
            continue;
        }
        if let Some(key) = bucket_of(&name) {
            buckets.entry(key).or_default().add(size, &built, &name);
        }
    }
    drop(ar);

    // 检测清单里的作者元数据（displayName / version / source）
    if let Ok(f2) = fs::File::open(&zip_path) {
        if let Ok(mut ar2) = ZipArchive::new(f2) {
            if let Ok(mut e) = ar2.by_name("插件检测清单.json") {
                use std::io::Read;
                let _ = e.read_to_end(&mut manifest_raw);
            }
        }
    }
    if let Ok(v) = serde_json::from_slice::<Value>(&manifest_raw) {
        bundle_version = v
            .get("bundleVersion")
            .and_then(|x| x.as_str())
            .unwrap_or("")
            .to_string();
        if let Some(arr) = v.get("pluginMeta").and_then(|x| x.as_array()) {
            for m in arr {
                if let Some(n) = m.get("name").and_then(|x| x.as_str()) {
                    manifest_meta.insert(n.to_string(), m.clone());
                }
            }
        }
    }

    let csgo = if cs2_path.is_empty() {
        PathBuf::new()
    } else {
        Path::new(cs2_path).join("game").join("csgo")
    };
    let zip_mtime = fmt_mtime(&zip_path).unwrap_or_default();

    let mut plugins: Vec<Value> = Vec::new();
    let mut components: Vec<Value> = Vec::new();
    for ((kind, unit), b) in &buckets {
        let meta = manifest_meta.get(unit);
        let (src, ver, note) = origin(unit);
        let display = meta
            .and_then(|m| m.get("displayName"))
            .and_then(|x| x.as_str())
            .unwrap_or(unit);
        let version = meta
            .and_then(|m| m.get("version"))
            .and_then(|x| x.as_str())
            .filter(|s| !s.is_empty())
            .unwrap_or(ver);
        let source = meta
            .and_then(|m| m.get("source"))
            .and_then(|x| x.as_str())
            .filter(|s| !s.is_empty())
            .unwrap_or(src);
        let desc = if note.is_empty() {
            meta.and_then(|m| m.get("status"))
                .and_then(|x| x.as_str())
                .unwrap_or("")
                .to_string()
        } else {
            note.to_string()
        };
        let probe = installed_probe(&csgo, kind, unit);
        let installed_at = probe.as_ref().and_then(|p| fmt_mtime(p)).unwrap_or_default();
        let config = if kind == "css_plugin" {
            let inline = format!(
                "addons/counterstrikesharp/plugins/{}/config.json",
                unit
            );
            if unit == "MVP-Music-Lite" {
                inline
            } else if unit == "MapRanks" {
                "addons/counterstrikesharp/plugins/MapRanks/MapRanks.json; addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json".into()
            } else {
                config_path_of(unit)
            }
        } else {
            String::new()
        };
        let item = json!({
            "name": unit,
            "displayName": display,
            "kind": kind,
            "version": version,
            "source": source,
            "desc": desc,
            "files": b.files,
            "size": b.size,
            "builtAt": b.built,
            "installedAt": installed_at,
            "installed": probe.is_some(),
            "mainFile": b.main_file,
            "configPath": config,
        });
        if kind == "css_plugin" {
            plugins.push(item);
        } else {
            components.push(item);
        }
    }
    plugins.sort_by(|a, b| a["name"].as_str().cmp(&b["name"].as_str()));
    components.sort_by(|a, b| a["name"].as_str().cmp(&b["name"].as_str()));

    json!({
        "ok": true,
        "source": {
            "name": "CS2BotImprover.zip",
            "path": zip_path.display().to_string(),
            "size": fs::metadata(&zip_path).map(|m| m.len()).unwrap_or(0),
            "entries": entries,
            "builtAt": zip_built,
            "packedAt": zip_mtime,
            "bundleVersion": bundle_version,
        },
        "installTargets": INSTALL_TARGETS,
        "pluginCount": plugins.len(),
        "componentCount": components.len(),
        "plugins": plugins,
        "components": components,
    })
}
