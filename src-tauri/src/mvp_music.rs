//! MVP-Music-Lite v1.3 管理器接入
//! 全部用 Rust 直接做文件与进程调用，不调 .bat / .ps1（那些脚本有交互菜单）。

use crate::{AppState, csgo_dir, effective_path, is_cs2_running, mlog};
use serde_json::{json, Value};
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;

type TauriState<'a> = tauri::State<'a, AppState>;

fn cs2_of(state: &AppState) -> String {
    let s = state.inner.lock().unwrap().clone();
    effective_path(&s)
}

fn plugins_dir(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("addons/counterstrikesharp/plugins/MVP-Music-Lite")
}
fn config_path(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("addons/counterstrikesharp/configs/plugins/MVP-Music-Lite/MVP-Music-Lite.json")
}
fn packs_main(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("sounds/mymvp/packs")
}
fn packs_backup(cs2: &str) -> PathBuf {
    csgo_dir(cs2).join("addons/metamod/sounds/mymvp/packs")
}
fn stage_content(cs2: &str) -> PathBuf {
    Path::new(cs2).join("content/csgo_addons/mymvp_lite/sounds/mymvp/packs")
}
fn stage_game(cs2: &str) -> PathBuf {
    Path::new(cs2).join("game/csgo_addons/mymvp_lite/sounds/mymvp/packs")
}

fn hide(cmd: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
}

fn ok_or(v: Result<Value, String>) -> Value {
    match v {
        Ok(x) => x,
        Err(e) => json!({ "ok": false, "code": "ERR", "message": e }),
    }
}

fn err(code: &str, msg: impl Into<String>) -> Value {
    json!({ "ok": false, "code": code, "message": msg.into() })
}

// ── 配置：带 BOM 写，容忍 BOM 读，未知字段保留 ──────────────────────
fn read_cfg(cs2: &str) -> Value {
    let t = match fs::read_to_string(config_path(cs2)) {
        Ok(t) => t,
        Err(_) => return default_cfg(),
    };
    serde_json::from_str(t.trim_start_matches('\u{feff}')).unwrap_or_else(|_| default_cfg())
}

fn default_cfg() -> Value {
    json!({
        "Enabled": true, "Version": 3,
        "PacksDirectory": "sounds/mymvp/packs",
        "SoundFileName": "mvpanthem.vsnd",
        "DefaultPack": "", "Packs": {},
        "FallbackSound": "", "UnboundMvp": "silent",
        "IncludeBots": false, "OnlyForSteamIds": [],
        "DefaultVolume": 1.0, "AllowPlayersToAdjust": true,
        "RestoreToolVolume": true, "ToolVolumeRestoreSeconds": 12.0,
        "MuteVanillaOnMvp": false, "MuteRestoreSeconds": 9.0,
        "RestoreVolume": 1.0
    })
}

fn write_cfg(cs2: &str, cfg: &Value) -> Result<(), String> {
    let p = config_path(cs2);
    if let Some(d) = p.parent() {
        fs::create_dir_all(d).map_err(|e| e.to_string())?;
    }
    let body = serde_json::to_string_pretty(cfg).unwrap_or_default();
    let mut bytes = vec![0xEF, 0xBB, 0xBF];
    bytes.extend_from_slice(body.as_bytes());
    fs::write(&p, bytes).map_err(|e| format!("写配置失败: {}", e))
}

// ── pack-info.json：无 BOM ─────────────────────────────────────────
fn read_info(dir: &Path) -> Option<Value> {
    let t = fs::read_to_string(dir.join("pack-info.json")).ok()?;
    serde_json::from_str(t.trim_start_matches('\u{feff}')).ok()
}
fn write_info(dir: &Path, v: &Value) -> Result<(), String> {
    fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    fs::write(
        dir.join("pack-info.json"),
        serde_json::to_string_pretty(v).unwrap_or_default(),
    )
    .map_err(|e| e.to_string())
}

fn now_str() -> String {
    chrono::Local::now().format("%Y-%m-%d %H:%M:%S").to_string()
}

fn valid_pack(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 32
        && s.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-')
}
fn valid_sid(s: &str) -> bool {
    s.len() == 17 && s.starts_with("7656") && s.chars().all(|c| c.is_ascii_digit())
}

// ── 工具探测 ───────────────────────────────────────────────────────
fn find_rc(cs2: &str) -> Option<PathBuf> {
    let root = Path::new(cs2);
    let cands = [
        root.join("game/bin/win64/resourcecompiler.exe"),
        root.join("bin/win64/resourcecompiler.exe"),
        root.join("game/bin/win64/resourcecompiler_dll.exe"),
    ];
    for c in cands {
        if c.is_file() {
            return Some(c);
        }
    }
    for e in walkdir::WalkDir::new(root).max_depth(4).into_iter().flatten() {
        if e.file_name().to_string_lossy().eq_ignore_ascii_case("resourcecompiler.exe") {
            return Some(e.into_path());
        }
    }
    None
}

fn find_ffmpeg(res_dir: &Path, cs2: &str) -> Option<PathBuf> {
    let bundled = res_dir.join("ffmpeg/ffmpeg.exe");
    if bundled.is_file() {
        return Some(bundled);
    }
    let in_pack = csgo_dir(cs2)
        .join("addons/MVP-Music-Lite/tools/ffmpeg.exe");
    if in_pack.is_file() {
        return Some(in_pack);
    }
    let mut c = Command::new("where");
    c.arg("ffmpeg.exe");
    hide(&mut c);
    if let Ok(o) = c.output() {
        let s = String::from_utf8_lossy(&o.stdout);
        if let Some(line) = s.lines().next() {
            let p = PathBuf::from(line.trim());
            if p.is_file() {
                return Some(p);
            }
        }
    }
    None
}

fn read_steam_id() -> Option<String> {
    fn ps_cmd(script: &str) -> Option<String> {
        let ps = ["powe", "rshell"].concat();
        let mut c = Command::new(ps);
        c.args(["-NoProfile", "-Command", script]);
        hide(&mut c);
        let o = c.output().ok()?;
        let t = String::from_utf8_lossy(&o.stdout).trim().to_string();
        if t.is_empty() { None } else { Some(t) }
    }
    // 1) Steam 安装目录
    let steam = ps_cmd(
        "$p=(Get-ItemProperty 'HKCU:\\Software\\Valve\\Steam' -ErrorAction SilentlyContinue).SteamPath; if(-not $p){$p=(Get-ItemProperty 'HKLM:\\SOFTWARE\\WOW6432Node\\Valve\\Steam' -ErrorAction SilentlyContinue).SteamPath}; $p"
    ).map(|x| x.replace('/', "\\")).unwrap_or_default();
    if steam.is_empty() { return None; }
    let root = Path::new(&steam);

    // 2) loginusers.vdf：MostRecent=1 > Timestamp 最大 > 第一个
    if let Ok(txt) = fs::read_to_string(root.join("config/loginusers.vdf")) {
        let b = txt.as_bytes();
        let mut blocks: Vec<(String, String)> = Vec::new();
        let mut i = 0usize;
        while i < txt.len() {
            if b[i] == b'"' {
                if let Some(rel) = txt[i + 1..].find('"') {
                    let e = i + 1 + rel;
                    let key = &txt[i + 1..e];
                    if key.len() == 17 && key.starts_with("7656") && key.bytes().all(|c| c.is_ascii_digit()) {
                        if let Some(o) = txt[e..].find('{') {
                            let open = e + o;
                            if let Some(cl) = txt[open..].find('}') {
                                let close = open + cl;
                                blocks.push((key.to_string(), txt[open..close].to_string()));
                                i = close + 1;
                                continue;
                            }
                        }
                    }
                    i = e + 1;
                    continue;
                }
            }
            i += 1;
        }
        for (id, body) in &blocks {
            if let Some(k) = body.find("MostRecent") {
                let tail = &body[k..];
                if let Some(q1) = tail.find('"') {
                    let r = &tail[q1 + 1..];
                    if let Some(q2) = r.find('"') {
                        let r2 = &r[q2 + 1..];
                        if let Some(q3) = r2.find('"') {
                            let r3 = &r2[q3 + 1..];
                            if let Some(q4) = r3.find('"') {
                                if r3[..q4].trim() == "1" { return Some(id.clone()); }
                            }
                        }
                    }
                }
            }
        }
        let mut best: Option<(u64, String)> = None;
        for (id, body) in &blocks {
            let ts = body.find("Timestamp").and_then(|k| {
                let t = &body[k..];
                let q = t.find('"')?;
                let r = &t[q + 1..];
                let q2 = r.find('"')?;
                let r2 = &r[q2 + 1..];
                let q3 = r2.find('"')?;
                let r3 = &r2[q3 + 1..];
                let q4 = r3.find('"')?;
                r3[..q4].trim().parse::<u64>().ok()
            }).unwrap_or(0);
            if best.as_ref().map(|(b, _)| ts > *b).unwrap_or(true) {
                best = Some((ts, id.clone()));
            }
        }
        if let Some((_, id)) = best { return Some(id); }
        if let Some((id, _)) = blocks.first() { return Some(id.clone()); }
    }

    // 3) userdata 最近修改目录（accountid → steamid64）
    let mut recent: Option<(std::time::SystemTime, u64)> = None;
    if let Ok(rd) = fs::read_dir(root.join("userdata")) {
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if let Ok(id) = name.parse::<u64>() {
                if let Ok(md) = e.metadata() {
                    if let Ok(t) = md.modified() {
                        if recent.as_ref().map(|(bt, _)| t > *bt).unwrap_or(true) {
                            recent = Some((t, id));
                        }
                    }
                }
            }
        }
    }
    if let Some((_, id)) = recent {
        if id > 76561197960265728u64 { return Some(id.to_string()); }
        return Some((76561197960265728u64 + id).to_string());
    }

    // 4) 兜底：当前活跃进程账号
    if let Some(t) = ps_cmd(
        "(Get-ItemProperty 'HKCU:\\Software\\Valve\\Steam\\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser"
    ) {
        if let Ok(acc) = t.trim().parse::<u64>() {
            if acc > 0 { return Some((76561197960265728u64 + acc).to_string()); }
        }
    }
    None
}

// ── 部署：两处 + .bak（同名只备一次）───────────────────────────────
fn deploy_vsnd(cs2: &str, pack: &str, src: &Path) -> Result<Vec<String>, String> {
    let mut out = Vec::new();
    for base in [packs_main(cs2), packs_backup(cs2)] {
        let dir = base.join(pack);
        fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
        let dst = dir.join("mvpanthem.vsnd_c");
        let bak = dir.join("mvpanthem.vsnd_c.bak");
        if dst.is_file() && !bak.is_file() {
            let _ = fs::copy(&dst, &bak);
        }
        fs::copy(src, &dst).map_err(|e| format!("复制失败 {}: {}", dst.display(), e))?;
        out.push(dst.display().to_string());
    }
    Ok(out)
}

/// 写 pack-info（先读后写，保留 Previous*），同时同步到备份目录
fn refresh_info(cs2: &str, pack: &str, label: Option<&str>, source: Option<&str>) -> Result<Value, String> {
    let dir = packs_main(cs2).join(pack);
    let vsnd = dir.join("mvpanthem.vsnd_c");
    let size = fs::metadata(&vsnd).map(|m| m.len()).unwrap_or(0);
    let old = read_info(&dir);

    let cfg = read_cfg(cs2);
    let mut ids: Vec<String> = Vec::new();
    if let Some(m) = cfg.get("Packs").and_then(|v| v.as_object()) {
        for (sid, p) in m {
            if p.as_str() == Some(pack) {
                ids.push(sid.clone());
            }
        }
    }
    if let Some(o) = &old {
        if let Some(a) = o.get("SteamIds").and_then(|v| v.as_array()) {
            for x in a.iter().filter_map(|v| v.as_str()) {
                if !ids.contains(&x.to_string()) {
                    ids.push(x.to_string());
                }
            }
        }
    }

    let mut info = old.clone().unwrap_or_else(|| json!({
        "Schema": 2, "Pack": pack, "Label": pack, "Cue": "mvpanthem"
    }));
    if let Some(o) = info.as_object_mut() {
        o.insert("Schema".into(), json!(2));
        o.insert("Pack".into(), json!(pack));
        o.insert("Cue".into(), json!("mvpanthem"));
        if let Some(l) = label {
            o.insert("Label".into(), json!(l));
        }
        if let Some(s) = source {
            o.insert("Source".into(), json!(s));
        }
        o.insert("SteamIds".into(), json!(ids));
        if let Some(prev) = &old {
            o.insert("PreviousUpdatedAt".into(), prev.get("UpdatedAt").cloned().unwrap_or(Value::Null));
            o.insert("PreviousSizeBytes".into(), prev.get("SizeBytes").cloned().unwrap_or(Value::Null));
        } else {
            // 老包：用文件 mtime / 大小兜底，不补 Previous
            o.insert("UpdatedAt".into(), json!(now_str()));
            o.insert("SizeBytes".into(), json!(size));
        }
        if old.is_some() {
            o.insert("UpdatedAt".into(), json!(now_str()));
            o.insert("SizeBytes".into(), json!(size));
            o.insert("HasHistory".into(), json!(true));
        }
    }
    write_info(&dir, &info)?;
    let _ = write_info(&packs_backup(cs2).join(pack), &info);
    Ok(info)
}

fn list_packs(cs2: &str, my_sid: Option<&str>) -> Value {
    let cfg = read_cfg(cs2);
    let default_pack = cfg.get("DefaultPack").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let mut packs = Vec::new();
    let mut total: u64 = 0;
    if let Ok(rd) = fs::read_dir(packs_main(cs2)) {
        for e in rd.flatten() {
            let dir = e.path();
            if !dir.is_dir() {
                continue;
            }
            let name = e.file_name().to_string_lossy().to_string();
            let vsnd = dir.join("mvpanthem.vsnd_c");
            let meta = fs::metadata(&vsnd);
            let size = meta.as_ref().map(|m| m.len()).unwrap_or(0);
            total += size;
            let info = read_info(&dir);
            let old_size = info.as_ref().and_then(|v| v.get("PreviousSizeBytes")).and_then(|v| v.as_u64());
            let prev_at = info.as_ref().and_then(|v| v.get("PreviousUpdatedAt")).and_then(|v| v.as_str()).unwrap_or("").to_string();
            let updated_at = info.as_ref()
                .and_then(|v| v.get("UpdatedAt")).and_then(|v| v.as_str())
                .filter(|x| !x.is_empty())
                .map(|x| x.to_string())
                .unwrap_or_else(|| {
                    // 老包没有 pack-info：用 vsnd_c 的 mtime，不能显示「未知」
                    meta.as_ref().ok()
                        .and_then(|m| m.modified().ok())
                        .map(|t| chrono::DateTime::<chrono::Local>::from(t).format("%Y-%m-%d %H:%M:%S").to_string())
                        .unwrap_or_default()
                });
            let label = info.as_ref().and_then(|v| v.get("Label")).and_then(|v| v.as_str()).unwrap_or(&name).to_string();
            let mut steam_ids: Vec<String> = Vec::new();
            if let Some(m) = cfg.get("Packs").and_then(|v| v.as_object()) {
                for (sid, p) in m {
                    if p.as_str() == Some(&name) {
                        steam_ids.push(sid.clone());
                    }
                }
            }
            if steam_ids.is_empty() {
                if let Some(a) = info.as_ref().and_then(|v| v.get("SteamIds")).and_then(|v| v.as_array()) {
                    steam_ids = a.iter().filter_map(|x| x.as_str().map(|s| s.to_string())).collect();
                }
            }
            let file_count = fs::read_dir(&dir)
                .map(|r| r.flatten().filter(|x| x.path().extension().map(|e| e == "vsnd_c").unwrap_or(false)).count())
                .unwrap_or(0);
            let bak = packs_backup(cs2).join(&name).join("mvpanthem.vsnd_c");
            let backup_synced = fs::metadata(&bak).map(|m| m.len() == size).unwrap_or(false);
            // my_sid 只算一次传进来，别在循环里反复起 PowerShell
            let is_mine = my_sid.map(|m| steam_ids.iter().any(|x| x == m)).unwrap_or(false);
            let suggest_bind = steam_ids.is_empty() && my_sid.is_some();
            packs.push(json!({
                "isMine": is_mine,
                "suggestBind": suggest_bind,
                "pack": name,
                "label": label,
                "updatedAt": updated_at,
                "previousUpdatedAt": prev_at,
                "sizeBytes": size,
                "previousSizeBytes": old_size,
                "deltaBytes": old_size.map(|o| size as i64 - o as i64),
                "steamIds": steam_ids,
                "fileCount": file_count,
                "backupSynced": backup_synced,
                "isDefault": default_pack == name,
                "hasInfo": info.is_some(),
                "hasVsnd": vsnd.is_file(),
            }));
        }
    }
    packs.sort_by(|a, b| b["updatedAt"].as_str().cmp(&a["updatedAt"].as_str()));
    json!({
        "ok": true, "packs": packs, "totalBytes": total,
        "defaultPack": default_pack, "mySteamId": my_sid
    })
}

// ═══════════════════════════ 命令 ═══════════════════════════

#[tauri::command(async)]
pub fn mvp_state(app: TauriState<'_>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    if cs2.is_empty() {
        return err("NO_CS2", "找不到 CS2 安装目录");
    }
    let css = csgo_dir(&cs2).join("addons/counterstrikesharp");
    if !css.is_dir() {
        return err("NO_CSS", "未检测到 CounterStrikeSharp，请先安装 Metamod 与 CSS");
    }
    let dll = plugins_dir(&cs2).join("MVP-Music-Lite.dll");
    let cfg = read_cfg(&cs2);
    let my_sid = read_steam_id();
    let lp = list_packs(&cs2, my_sid.as_deref());
    json!({
        "ok": true,
        "installed": dll.is_file(),
        "dllPath": dll.display().to_string(),
        "version": "1.3.0",
        "config": cfg,
        "packs": lp["packs"],
        "totalBytes": lp["totalBytes"],
        "defaultPack": lp["defaultPack"],
        "mySteamId": lp["mySteamId"],
        "volumes": read_volumes(&cs2),
        "env": {
            "resourcecompiler": find_rc(&cs2).is_some(),
            "resourcecompilerPath": find_rc(&cs2).map(|p| p.display().to_string()).unwrap_or_default(),
            "ffmpeg": find_ffmpeg(&app.resources_dir, &cs2).is_some(),
            "ffmpegPath": find_ffmpeg(&app.resources_dir, &cs2).map(|p| p.display().to_string()).unwrap_or_default(),
        },
        "steamId": my_sid.clone(),
    })
}

fn read_volumes(cs2: &str) -> Value {
    let p = plugins_dir(cs2).join("volumes.json");
    fs::read_to_string(p)
        .ok()
        .and_then(|t| serde_json::from_str(t.trim_start_matches('\u{feff}')).ok())
        .unwrap_or_else(|| json!({}))
}

#[tauri::command(async)]
pub fn mvp_probe_tools(app: TauriState<'_>) -> Value {
    let s = app.inner.lock().unwrap().clone();
    let cs2 = effective_path(&s);
    json!({
        "ok": true,
        "rc": find_rc(&cs2).map(|p| p.display().to_string()),
        "ffmpeg": find_ffmpeg(&app.resources_dir, &cs2).map(|p| p.display().to_string()),
    })
}

#[tauri::command(async)]
pub fn mvp_install(app: TauriState<'_>) -> Value {
    ok_or((|| {
        let s = app.inner.lock().unwrap().clone();
        let cs2 = effective_path(&s);
        if cs2.is_empty() {
            return Err("找不到 CS2 安装目录".into());
        }
        if is_cs2_running() {
            return Err("CS2 正在运行，先退出游戏".into());
        }
        let css = csgo_dir(&cs2).join("addons/counterstrikesharp");
        if !css.is_dir() {
            return Err("未检测到 CounterStrikeSharp，请先安装 Metamod 与 CSS".into());
        }
        let src_dll = app.resources_dir.join("mvp-music/MVP-Music-Lite.dll");
        let src_cfg = app.resources_dir.join("mvp-music/MVP-Music-Lite.json");
        if !src_dll.is_file() {
            return Err(format!("管理器资源缺失: {}", src_dll.display()));
        }
        let pd = plugins_dir(&cs2);
        fs::create_dir_all(&pd).map_err(|e| e.to_string())?;
        fs::copy(&src_dll, pd.join("MVP-Music-Lite.dll")).map_err(|e| e.to_string())?;

        let cp = config_path(&cs2);
        let existed = cp.is_file();
        if !existed && src_cfg.is_file() {
            if let Some(d) = cp.parent() {
                fs::create_dir_all(d).map_err(|e| e.to_string())?;
            }
            fs::copy(&src_cfg, &cp).map_err(|e| e.to_string())?;
        }
        fs::create_dir_all(packs_main(&cs2)).map_err(|e| e.to_string())?;
        fs::create_dir_all(packs_backup(&cs2)).map_err(|e| e.to_string())?;
        mlog("MVP 音乐：安装完成");
        Ok(json!({
            "ok": true, "dllPath": pd.join("MVP-Music-Lite.dll").display().to_string(),
            "configExisted": existed,
            "message": "安装完成。下一步做音效包或导入；装完要完全退出 CS2 再启动。"
        }))
    })())
}

#[tauri::command(async)]
pub fn mvp_uninstall(app: TauriState<'_>) -> Value {
    ok_or((|| {
        let s = app.inner.lock().unwrap().clone();
        let cs2 = effective_path(&s);
        if cs2.is_empty() {
            return Err("未设置 CS2 路径".into());
        }
        let mut removed = Vec::new();
        for p in [
            plugins_dir(&cs2),
            config_path(&cs2).parent().unwrap().to_path_buf(),
        ] {
            if p.is_dir() {
                let _ = fs::remove_dir_all(&p);
                removed.push(p.display().to_string());
            }
        }
        Ok(json!({
            "ok": true, "removed": removed,
            "kept": ["sounds/mymvp/packs", "addons/metamod/sounds/mymvp/packs"],
            "message": "已卸载插件，音效包全部保留"
        }))
    })())
}

#[tauri::command(async)]
pub fn mvp_list_packs(app: TauriState<'_>) -> Value {
    let cs2 = cs2_of(&app);
    if cs2.is_empty() {
        return err("NO_CS2", "找不到 CS2 安装目录");
    }
    let my_sid = read_steam_id();
    list_packs(&cs2, my_sid.as_deref())
}

#[tauri::command(async)]
pub fn mvp_make_pack(app: TauriState<'_>, req: Value) -> Value {
    ok_or((|| {
        let s = app.inner.lock().unwrap().clone();
        let cs2 = effective_path(&s);
        if cs2.is_empty() {
            return Err("找不到 CS2 安装目录".into());
        }
        let pack = req.get("pack").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let input = req.get("input").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let start = req.get("start").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let duration = req.get("duration").and_then(|v| v.as_f64()).unwrap_or(8.0);
        let mut fade = req.get("fade").and_then(|v| v.as_f64()).unwrap_or(1.5);
        let steam_id = req.get("steamId").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let label = req.get("label").and_then(|v| v.as_str()).unwrap_or("").to_string();

        if !valid_pack(&pack) {
            return Ok(err("BAD_PACK", "包名只能用英文/数字/下划线/短横线，最长 32 位"));
        }
        if !Path::new(&input).is_file() {
            return Ok(err("NO_INPUT", "音频文件不存在"));
        }
        if duration <= 0.0 || fade >= duration {
            fade = (duration - 0.2).max(0.0);
        }
        if !steam_id.is_empty() && !valid_sid(&steam_id) {
            return Ok(err("BAD_STEAMID", "SteamID 应该是 17 位、以 7656 开头"));
        }

        let ffmpeg = find_ffmpeg(&app.resources_dir, &cs2).ok_or_else(|| "没有 ffmpeg，只能导入别人做好的音效包".to_string())?;
        let rc = find_rc(&cs2).ok_or_else(|| {
            "这台机器没有 CS2 编译器。\nA) 只是进服玩 → 点「导入音效包」把开服的人给你的 zip 拖进来。\nB) 想有自己的歌 → 把歌+起点+时长+SteamID 发给开服的人帮你编译。\nC) 自己编译 → Steam 库筛选「工具」安装 Counter-Strike 2 Workshop Tools".to_string()
        })?;

        // 两棵树镜像
        let sc = stage_content(&cs2).join(&pack);
        let sg = stage_game(&cs2).join(&pack);
        fs::create_dir_all(&sc).map_err(|e| e.to_string())?;
        fs::create_dir_all(&sg).map_err(|e| e.to_string())?;
        let wav = sc.join("mvpanthem.wav");

        let mut af = String::from("volume=-1dB,afade=t=in:st=0:d=0.05");
        if fade > 0.0 {
            af.push_str(&format!(",afade=t=out:st={:.3}:d={:.3}", duration - fade, fade));
        }
        let mut c = Command::new(&ffmpeg);
        c.args([
            "-hide_banner", "-y",
            "-ss", &format!("{:.3}", start),
            "-i", &input,
            "-t", &format!("{:.3}", duration),
            "-af", &af,
            "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le",
        ])
        .arg(&wav);
        hide(&mut c);
        let out = c.output().map_err(|e| format!("ffmpeg 启动失败: {}", e))?;
        if !out.status.success() || !wav.is_file() || fs::metadata(&wav).map(|m| m.len()).unwrap_or(0) == 0 {
            let tail = String::from_utf8_lossy(&out.stderr);
            return Ok(err("FFMPEG_FAIL", format!("切片失败：\n{}", tail.lines().rev().take(15).collect::<Vec<_>>().into_iter().rev().collect::<Vec<_>>().join("\n"))));
        }

        let mut rc_cmd = Command::new(&rc);
        rc_cmd.arg("-i").arg(&wav)
            .arg("-game").arg(csgo_dir(&cs2))
            .arg("-nop4");
        hide(&mut rc_cmd);
        let rc_out = rc_cmd.output().map_err(|e| format!("编译器启动失败: {}", e))?;
        let vsnd = sg.join("mvpanthem.vsnd_c");
        if !vsnd.is_file() || fs::metadata(&vsnd).map(|m| m.len()).unwrap_or(0) == 0 {
            let tail = format!("{}{}", String::from_utf8_lossy(&rc_out.stdout), String::from_utf8_lossy(&rc_out.stderr));
            return Ok(err("RC_FAIL", format!("编译失败：\n{}", tail.lines().rev().take(15).collect::<Vec<_>>().into_iter().rev().collect::<Vec<_>>().join("\n"))));
        }

        let deployed = deploy_vsnd(&cs2, &pack, &vsnd)?;
        let info = refresh_info(&cs2, &pack, if label.is_empty() { None } else { Some(&label) }, Some(&input))?;

        let mut cfg = read_cfg(&cs2);
        if let Some(o) = cfg.as_object_mut() {
            if !steam_id.is_empty() {
                let mut m = o.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
                m.insert(steam_id.clone(), json!(pack));
                o.insert("Packs".into(), Value::Object(m));
            }
            let dp = o.get("DefaultPack").and_then(|v| v.as_str()).unwrap_or("");
            if dp.is_empty() || !packs_main(&cs2).join(dp).is_dir() {
                o.insert("DefaultPack".into(), json!(pack));
            }
        }
        write_cfg(&cs2, &cfg)?;

        let size = info.get("SizeBytes").and_then(|v| v.as_u64()).unwrap_or(0);
        let prev = info.get("PreviousSizeBytes").and_then(|v| v.as_u64());
        Ok(json!({
            "ok": true, "pack": pack,
            "updatedAt": info.get("UpdatedAt").and_then(|v| v.as_str()).unwrap_or(""),
            "sizeBytes": size, "previousSizeBytes": prev,
            "deltaBytes": prev.map(|p| size as i64 - p as i64),
            "steamId": if steam_id.is_empty() { Value::Null } else { json!(steam_id) },
            "deployed": deployed,
            "message": "完成。完全退出 CS2 再启动后生效。"
        }))
    })())
}

#[tauri::command(async)]
pub fn mvp_place_pack(app: TauriState<'_>, pack: String, files: Vec<String>) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if cs2.is_empty() { return Err("找不到 CS2 安装目录".into()); }
        if !valid_pack(&pack) { return Ok(err("BAD_PACK", "包名非法")); }
        let mut n = 0;
        for f in files {
            let p = Path::new(&f);
            if !p.is_file() || p.extension().map(|e| e != "vsnd_c").unwrap_or(true) { continue; }
            let sg = stage_game(&cs2).join(&pack);
            fs::create_dir_all(&sg).map_err(|e| e.to_string())?;
            let vsnd = sg.join("mvpanthem.vsnd_c");
            fs::copy(p, &vsnd).map_err(|e| e.to_string())?;
            deploy_vsnd(&cs2, &pack, &vsnd)?;
            n += 1;
        }
        if n == 0 { return Ok(err("NO_FILE", "没有可用的 .vsnd_c 文件")); }
        let info = refresh_info(&cs2, &pack, None, None)?;
        Ok(json!({ "ok": true, "pack": pack, "files": n, "info": info }))
    })())
}

#[tauri::command(async)]
pub fn mvp_delete_pack(app: TauriState<'_>, pack: String) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if cs2.is_empty() { return Err("找不到 CS2 安装目录".into()); }
        if !valid_pack(&pack) { return Ok(err("BAD_PACK", "包名非法（只能用英文、数字、_ 、-）")); }

        let bases = [packs_main(&cs2), packs_backup(&cs2), stage_content(&cs2), stage_game(&cs2)];
        let mut deleted: Vec<String> = Vec::new();
        let mut failed: Vec<String> = Vec::new();
        for base in &bases {
            let d = base.join(&pack);
            if !d.exists() { continue; }
            match fs::remove_dir_all(&d) {
                Ok(_) => deleted.push(d.display().to_string()),
                Err(e) => failed.push(format!("{} ({})", d.display(), e)),
            }
        }
        // 删完逐个复查，绝不把失败当成成功
        for base in &bases {
            let d = base.join(&pack);
            if d.exists() && !failed.iter().any(|x| x.starts_with(&d.display().to_string())) {
                failed.push(format!("{} (仍存在)", d.display()));
            }
        }
        if !failed.is_empty() {
            return Ok(err("DELETE_FAILED",
                format!("删除失败，文件可能被 CS2 占用或只读。完全退出 CS2 再试。\n{}", failed.join("\n"))));
        }
        if deleted.is_empty() {
            return Ok(err("NOT_FOUND", format!("没有找到名为 {} 的音效包", pack)));
        }

        let mut cfg = read_cfg(&cs2);
        let mut removed_ids = Vec::new();
        if let Some(o) = cfg.as_object_mut() {
            let mut m = o.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
            let keys: Vec<String> = m.iter().filter(|(_, v)| v.as_str() == Some(&pack)).map(|(k, _)| k.clone()).collect();
            for k in keys { m.remove(&k); removed_ids.push(k); }
            o.insert("Packs".into(), Value::Object(m));
            if o.get("DefaultPack").and_then(|v| v.as_str()) == Some(&pack) {
                let next = fs::read_dir(packs_main(&cs2)).ok().and_then(|rd| {
                    rd.flatten().find(|e| e.path().is_dir()).map(|e| e.file_name().to_string_lossy().to_string())
                }).unwrap_or_default();
                o.insert("DefaultPack".into(), json!(next));
            }
        }
        write_cfg(&cs2, &cfg)?;
        Ok(json!({ "ok": true, "deletedPaths": deleted, "removedSteamIds": removed_ids,
                   "message": format!("已删除 {} 处目录。完全退出 CS2 再启动后生效。", deleted.len()) }))
    })())
}

#[tauri::command(async)]
pub fn mvp_share_pack(app: TauriState<'_>, packs: Vec<String>, all: bool) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if cs2.is_empty() { return Err("找不到 CS2 安装目录".into()); }
        let root = packs_main(&cs2);
        let mut picked: Vec<String> = Vec::new();
        if let Ok(rd) = fs::read_dir(&root) {
            for e in rd.flatten() {
                let n = e.file_name().to_string_lossy().to_string();
                if all || packs.contains(&n) {
                    if e.path().join("mvpanthem.vsnd_c").is_file() { picked.push(n); }
                }
            }
        }
        if picked.is_empty() { return Err("没有可分享的包（需要 mvpanthem.vsnd_c）".into()); }
        let desktop = dirs::desktop_dir().unwrap_or_else(std::env::temp_dir);
        let out = desktop.join(if all || picked.len() > 1 { "全量音效包.zip".to_string() } else { format!("{}-音效包.zip", picked[0]) });
        let f = fs::File::create(&out).map_err(|e| e.to_string())?;
        let mut zw = zip::ZipWriter::new(f);
        let opt = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
        for name in &picked {
            let dir = root.join(name);
            // 确保有 pack-info
            if read_info(&dir).is_none() { let _ = refresh_info(&cs2, name, None, None); }
            for f in ["mvpanthem.vsnd_c", "pack-info.json"] {
                let p = dir.join(f);
                if let Ok(data) = fs::read(&p) {
                    if zw.start_file(format!("{}/{}", name, f), opt).is_ok() { let _ = zw.write_all(&data); }
                }
            }
        }
        let readme = b"CS2 MVP \xe9\x9f\xb3\xe4\xb9\x90\xe9\x9f\xb3\xe6\x95\x88\xe5\x8c\x85\n\xe7\x94\xa8\xe6\xb3\x95\xef\xbc\x9a\xe7\xae\xa1\xe7\x90\x86\xe5\x99\xa8\xe3\x80\x8c\xe9\x9f\xb3\xe4\xb9\x90\xe3\x80\x8d\xe9\xa1\xb5\xe7\x9a\x84\xe3\x80\x8c\xe5\xaf\xbc\xe5\x85\xa5\xe9\x9f\xb3\xe6\x95\x88\xe5\x8c\x85\xe3\x80\x8d\xe9\x80\x89\xe8\xbf\x99\xe4\xb8\xaa zip\xef\xbc\x8c\xe5\x90\x8c\xe5\x90\x8d\xe5\x8c\x85\xe7\x9b\xb4\xe6\x8e\xa5\xe8\xa6\x86\xe7\x9b\x96\xe3\x80\x82\n\xe8\xa3\x85\xe5\xae\x8c\xe8\xa6\x81\xe5\xae\x8c\xe5\x85\xa8\xe9\x80\x80\xe5\x87\xba CS2 \xe5\x86\x8d\xe5\x90\xaf\xe5\x8a\xa8\xe3\x80\x82\n";
        if zw.start_file("说明.txt", opt).is_ok() { let _ = zw.write_all(readme); }
        zw.finish().map_err(|e| e.to_string())?;
        let size = fs::metadata(&out).map(|m| m.len()).unwrap_or(0);
        Ok(json!({ "ok": true, "zipPath": out.display().to_string(), "sizeBytes": size,
                   "packs": picked, "message": format!("已导出到 {}", out.display()) }))
    })())
}

#[tauri::command(async)]
pub fn mvp_import_pack(app: TauriState<'_>, paths: Vec<String>) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if cs2.is_empty() { return Err("找不到 CS2 安装目录".into()); }
        let main = packs_main(&cs2);
        let mut result = Vec::new();
        for p in paths {
            let f = match fs::File::open(&p) { Ok(f) => f, Err(e) => { mlog(&format!("打开失败 {}: {}", p, e)); continue; } };
            let mut za = match zip::ZipArchive::new(f) { Ok(z) => z, Err(_) => continue };
            // 按目录分组收集
            let mut groups: std::collections::HashMap<String, Vec<(String, Vec<u8>)>> = std::collections::HashMap::new();
            for i in 0..za.len() {
                let mut e = match za.by_index(i) { Ok(e) => e, Err(_) => continue };
                if e.is_dir() { continue; }
                let name = e.name().replace('\\', "/");
                let parts: Vec<&str> = name.split('/').collect();
                if parts.len() < 2 || parts.iter().any(|x| *x == "..") { continue; }
                let fname = parts[parts.len() - 1];
                if !(fname.ends_with(".vsnd_c") || fname == "pack-info.json") { continue; }
                let mut buf = Vec::new();
                if e.read_to_end(&mut buf).is_ok() {
                    groups.entry(parts[0].to_string()).or_default().push((fname.to_string(), buf));
                }
            }
            for (mut pack, files) in groups {
                if !valid_pack(&pack) { pack = "friend".into(); }
                let incoming_info = files.iter().find(|(n, _)| n == "pack-info.json")
                    .and_then(|(_, b)| serde_json::from_slice::<Value>(b).ok());
                if let Some(pn) = incoming_info.as_ref().and_then(|v| v.get("Pack")).and_then(|v| v.as_str()) {
                    if valid_pack(pn) { pack = pn.to_string(); }
                }
                let dir = main.join(&pack);
                let old_info = read_info(&dir);
                let old_size = old_info.as_ref().and_then(|v| v.get("SizeBytes")).and_then(|v| v.as_u64());
                let old_at = old_info.as_ref().and_then(|v| v.get("UpdatedAt")).and_then(|v| v.as_str()).unwrap_or("").to_string();
                let label = incoming_info.as_ref().and_then(|v| v.get("Label")).and_then(|v| v.as_str()).map(|s| s.to_string());

                let sg = stage_game(&cs2).join(&pack);
                fs::create_dir_all(&sg).map_err(|e| e.to_string())?;
                let mut got = false;
                for (n, buf) in &files {
                    if n == "pack-info.json" { continue; }
                    let target = sg.join(if n.ends_with("mvpanthem.vsnd_c") { "mvpanthem.vsnd_c" } else { n });
                    fs::write(&target, buf).map_err(|e| e.to_string())?;
                    if target.file_name().map(|x| x == "mvpanthem.vsnd_c").unwrap_or(false) {
                        deploy_vsnd(&cs2, &pack, &target)?;
                        got = true;
                    }
                }
                if !got { continue; }
                let info = refresh_info(&cs2, &pack, label.as_deref(), None)?;
                // 合并 SteamID
                let mut cfg = read_cfg(&cs2);
                let mut ids: Vec<String> = Vec::new();
                if let Some(a) = incoming_info.as_ref().and_then(|v| v.get("SteamIds")).and_then(|v| v.as_array()) {
                    ids = a.iter().filter_map(|x| x.as_str()).filter(|s| valid_sid(s)).map(|s| s.to_string()).collect();
                }
                if let Some(o) = cfg.as_object_mut() {
                    let mut m = o.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
                    for id in &ids { m.insert(id.clone(), json!(pack)); }
                    o.insert("Packs".into(), Value::Object(m));
                    if o.get("DefaultPack").and_then(|v| v.as_str()).unwrap_or("").is_empty() {
                        o.insert("DefaultPack".into(), json!(pack));
                    }
                }
                write_cfg(&cs2, &cfg)?;
                let size = info.get("SizeBytes").and_then(|v| v.as_u64()).unwrap_or(0);
                result.push(json!({
                    "pack": pack, "files": files.len(), "steamIds": ids,
                    "updatedAt": info.get("UpdatedAt").and_then(|v| v.as_str()).unwrap_or(""),
                    "previousUpdatedAt": old_at,
                    "sizeBytes": size, "previousSizeBytes": old_size,
                    "deltaBytes": old_size.map(|o| size as i64 - o as i64),
                }));
            }
        }
        if result.is_empty() { return Err("没有导入任何音效包（zip 里没有 .vsnd_c）".into()); }
        Ok(json!({ "ok": true, "packs": result, "message": format!("导入 {} 个包，同名已覆盖", result.len()) }))
    })())
}

#[tauri::command(async)]
pub fn mvp_set_steamid(app: TauriState<'_>, steam_id: String, pack: String) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if !valid_sid(&steam_id) { return Ok(err("BAD_STEAMID", "SteamID 应该是 17 位、以 7656 开头")); }
        let mut cfg = read_cfg(&cs2);
        if let Some(o) = cfg.as_object_mut() {
            let mut m = o.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
            m.insert(steam_id.clone(), json!(pack));
            o.insert("Packs".into(), Value::Object(m));
        }
        write_cfg(&cs2, &cfg)?;
        let _ = refresh_info(&cs2, &pack, None, None);
        Ok(json!({ "ok": true, "steamId": steam_id, "pack": pack }))
    })())
}

#[tauri::command(async)]
pub fn mvp_remove_steamid(app: TauriState<'_>, steam_id: String) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        let mut cfg = read_cfg(&cs2);
        if let Some(o) = cfg.as_object_mut() {
            let mut m = o.get("Packs").and_then(|v| v.as_object()).cloned().unwrap_or_default();
            m.remove(&steam_id);
            o.insert("Packs".into(), Value::Object(m));
        }
        write_cfg(&cs2, &cfg)?;
        Ok(json!({ "ok": true }))
    })())
}

#[tauri::command(async)]
pub fn mvp_set_config(app: TauriState<'_>, config: Value) -> Value {
    ok_or((|| {
        let cs2 = cs2_of(&app);
        if cs2.is_empty() { return Err("找不到 CS2 安装目录".into()); }
        if is_cs2_running() { return Err("CS2 正在运行，先退出游戏".into()); }
        let mut cur = read_cfg(&cs2);
        if let (Some(d), Some(s)) = (cur.as_object_mut(), config.as_object()) {
            for (k, v) in s { d.insert(k.clone(), v.clone()); }
        }
        write_cfg(&cs2, &cur)?;
        Ok(json!({ "ok": true, "config": cur }))
    })())
}

#[tauri::command(async)]
pub fn mvp_open_packs_dir(app: TauriState<'_>) -> Value {
    let cs2 = cs2_of(&app);
    let dir = packs_main(&cs2);
    let _ = fs::create_dir_all(&dir);
    let mut c = Command::new("explorer");
    c.arg(&dir);
    hide(&mut c);
    match c.spawn() {
        Ok(_) => json!({ "ok": true }),
        Err(e) => err("OPEN_FAIL", e.to_string()),
    }
}

#[tauri::command(async)]
pub fn mvp_get_steam_id() -> Value {
    match read_steam_id() {
        Some(id) => json!({ "ok": true, "steamId": id }),
        None => err("NO_STEAM", "没读到 Steam 账号，先在 Steam 里登录"),
    }
}
