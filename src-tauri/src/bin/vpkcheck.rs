// 独立验证工具：不加载 Tauri，避免 generate_context 写资源被拦
// 用法: cargo run --bin vpkcheck -- <某个 botprofile.vpk>
use std::fs;

fn main() {
    let path = std::env::args().nth(1).expect("用法: vpkcheck <vpk 路径>");
    let d = fs::read(&path).expect("读文件失败");
    println!("文件 {} 字节", d.len());

    let db = xuerong_bot_manager_lib::vpk::vpk_read_file(&d, "botprofile").expect("读 botprofile.db");
    let text = String::from_utf8_lossy(&db).to_string();
    println!("botprofile.db {} 字节，首行: {}", db.len(), text.lines().next().unwrap_or(""));
    assert!(text.contains("BotProfile"), "内容不像 botprofile");

    // 简单改一处参数，验证写回
    let tuned = text.replacen("Skill = 100", "Skill = 66", 1);
    let tmp = std::env::temp_dir().join("vpkcheck_out.vpk");
    fs::copy(&path, &tmp).unwrap();
    xuerong_bot_manager_lib::vpk::vpk_replace_file(&tmp, "botprofile", tuned.as_bytes()).expect("写入失败");

    let d2 = fs::read(&tmp).unwrap();
    let db2 = xuerong_bot_manager_lib::vpk::vpk_read_file(&d2, "botprofile").expect("回读失败");
    assert_eq!(db2, tuned.as_bytes(), "回读内容不一致");
    assert!(xuerong_bot_manager_lib::vpk::vpk_read_file(&d2, "bt_default").is_some(), "bt_default 丢了");
    assert!(xuerong_bot_manager_lib::vpk::vpk_read_file(&d2, "bt_config").is_some(), "bt_config 丢了");
    println!("往返 OK：{} -> {} 字节，总文件 {} 字节", db.len(), db2.len(), d2.len());
    println!("输出: {}", tmp.display());
}
