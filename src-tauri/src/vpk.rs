// VPK v2 读写：CS2 只认 overrides/botprofile.vpk 里的 botprofile.db，
// 改明文文件没用，所以自定义难度必须在这里落地。
use std::collections::HashMap;
use std::fs;
use std::path::Path;

pub const VPK_SIG: u32 = 0x55AA1234;

pub fn rd_u32(d: &[u8], at: usize) -> u32 {
    if at + 4 > d.len() { return 0; }
    u32::from_le_bytes([d[at], d[at + 1], d[at + 2], d[at + 3]])
}

/// 在 tree 区里按文件名找条目：返回 (entry 起始位置, 数据偏移, 数据长度)
pub fn vpk_find_entry(d: &[u8], tree_start: usize, tree_end: usize, name: &str) -> Option<(usize, u32, u32)> {
    let needle = format!("{}\0", name).into_bytes();
    let mut i = tree_start;
    while i + needle.len() <= tree_end {
        if &d[i..i + needle.len()] == needle.as_slice() {
            let e = i + needle.len();
            if e + 18 <= tree_end {
                let off = rd_u32(d, e + 8);
                let len = rd_u32(d, e + 12);
                return Some((e, off, len));
            }
        }
        i += 1;
    }
    None
}

/// 取出 vpk 里的某个文件内容
pub fn vpk_read_file(d: &[u8], name: &str) -> Option<Vec<u8>> {
    let tree_size = rd_u32(d, 8) as usize;
    let tree_start = 28usize;
    let tree_end = tree_start + tree_size;
    let data_start = tree_end;
    let (_, off, len) = vpk_find_entry(d, tree_start, tree_end, name)?;
    let s = data_start + off as usize;
    let e = s + len as usize;
    if e > d.len() { return None; }
    Some(d[s..e].to_vec())
}

/// 用新内容替换 vpk 里的一个文件，重排后续偏移并重算 CRC / MD5
pub fn vpk_replace_file(vpk: &Path, name: &str, new_data: &[u8]) -> Result<(), String> {
    let d = fs::read(vpk).map_err(|e| format!("读取 vpk 失败: {}", e))?;
    if rd_u32(&d, 0) != VPK_SIG {
        return Err("不是 VPK 文件（签名不对）".into());
    }
    if rd_u32(&d, 4) != 2 {
        return Err("只支持 VPK v2".into());
    }
    let tree_size = rd_u32(&d, 8) as usize;
    let data_size = rd_u32(&d, 12) as usize;
    let tree_start = 28usize;
    let tree_end = tree_start + tree_size;
    let data_start = tree_end;
    if data_start + data_size > d.len() {
        return Err("vpk 结构异常（数据段越界）".into());
    }

    // 收集所有条目（按文件名探测，未知文件按偏移范围兜底）
    let known = ["botprofile", "bt_default", "bt_config"];
    let mut entries: Vec<(String, usize, u32, u32)> = Vec::new();
    for n in known {
        if let Some((pos, off, len)) = vpk_find_entry(&d, tree_start, tree_end, n) {
            entries.push((n.to_string(), pos, off, len));
        }
    }
    let target_idx = entries
        .iter()
        .position(|(n, _, _, _)| n == name)
        .ok_or_else(|| format!("vpk 里没有 {}", name))?;
    let _ = target_idx;
    entries.sort_by_key(|(_, _, off, _)| *off);

    // 按原顺序重建数据段
    let mut blobs: Vec<(String, Vec<u8>)> = Vec::new();
    for (n, _, off, len) in &entries {
        let s = data_start + *off as usize;
        let e = s + *len as usize;
        let body = if *n == name {
            new_data.to_vec()
        } else if e <= d.len() {
            d[s..e].to_vec()
        } else {
            Vec::new()
        };
        blobs.push((n.clone(), body));
    }
    // 数据段里可能有条目外的空隙，这里按条目紧凑重排（CS2 允许）
    let mut new_data_section: Vec<u8> = Vec::new();
    let mut new_offsets: HashMap<String, (u32, u32)> = HashMap::new();
    for (n, body) in &blobs {
        new_offsets.insert(n.clone(), (new_data_section.len() as u32, body.len() as u32));
        new_data_section.extend_from_slice(body);
    }

    // 改 tree 里的 offset / length / crc
    let mut out = d[..data_start].to_vec();
    for (n, pos, _, _) in &entries {
        let (off, len) = *new_offsets.get(n).unwrap();
        let body = &blobs.iter().find(|(x, _)| x == n).unwrap().1;
        let crc = crc32fast::hash(body);
        out[*pos..*pos + 4].copy_from_slice(&crc.to_le_bytes());
        out[*pos + 8..*pos + 12].copy_from_slice(&off.to_le_bytes());
        out[*pos + 12..*pos + 16].copy_from_slice(&len.to_le_bytes());
    }
    // 更新 fileDataSectionSize
    out[12..16].copy_from_slice(&(new_data_section.len() as u32).to_le_bytes());
    out.extend_from_slice(&new_data_section);

    // otherMD5：tree md5 + fileData md5 + 16 字节保留
    let other_size = rd_u32(&d, 20) as usize;
    if other_size >= 32 {
        use md5::{Digest, Md5};
        let tree_md5 = Md5::digest(&out[tree_start..tree_end]);
        let data_md5 = Md5::digest(&new_data_section);
        out.extend_from_slice(&tree_md5);
        out.extend_from_slice(&data_md5);
        out.extend_from_slice(&[0u8; 16]);
    }

    fs::write(vpk, &out).map_err(|e| format!("写入 vpk 失败: {}", e))?;
    Ok(())
}
