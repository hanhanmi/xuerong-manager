# -*- coding: utf-8 -*-
"""
CS2 人机名字替换工具（自包含，只改名字，不动任何其它逻辑）
================================================================

作用：把 botprofile.vpk 里 botprofile.db 的**人机名字**按清单逐个替换，其余字节原样保留。

用法（Windows）：
    python 改人机名字_参考脚本.py --check                      只看现状，不改任何文件
    python 改人机名字_参考脚本.py --apply                      就地替换（每个文件自动 .bak 备份）
    python 改人机名字_参考脚本.py --apply --dir "D:\\...\\overrides"
    python 改人机名字_参考脚本.py --apply --zip "C:\\...\\CS2BotImprover.zip"   # 直接改包，输出 *_中文名.zip

默认：
    overrides 目录 = D:\\apps\\steam\\steamapps\\common\\Counter-Strike Global Offensive\\game\\csgo\\overrides
    名字清单       = 与本脚本同目录的「01_人机名字清单_1969.txt」

覆盖范围（存在哪个就改哪个）：
    <dir>/botprofile.vpk
    <dir>/High/botprofile.vpk
    <dir>/Medium/botprofile.vpk
    <dir>/Low/botprofile.vpk
    <dir>/**/botprofile.db            （模组工具偶尔会额外放一份裸露的 db）

只改这些行：  模板链 + 空格 + "名字"       例如  ProSlow+SniperPro+SniperPersonality "Zhang Weiwei"
不改任何其它内容：注释、VoicePitch/LookAngle/ReactionTime 等参数、行为树 kv3、目录结构、条目顺序。
"""
import argparse
import hashlib
import os
import re
import shutil
import struct
import sys
import tempfile
import zlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

_TMPDIR = tempfile.mkdtemp(prefix="bpname_")


def _tmpfile(name):
    return os.path.join(_TMPDIR, name)

# ───────────────────────── VPK v2 读写（字节级对齐 Valve 规范） ─────────────────────────
SIG = 0x55AA1234
INLINE = 0x7FFF


class VpkEntry:
    def __init__(self, path, data, crc=None):
        self.path = path
        self.data = data
        self.crc = (zlib.crc32(data) & 0xFFFFFFFF) if crc is None else crc

    def split(self):
        d, name = self.path.rsplit("/", 1) if "/" in self.path else (" ", self.path)
        stem, ext = name.rsplit(".", 1) if "." in name else (name, "")
        return d, stem, ext


def vpk_read(path):
    """读 VPK v2；只支持"数据内联在 _dir.vpk 里"（archiveIndex=0x7FFF）这种包。"""
    blob = open(path, "rb").read()
    sig, ver, tree_sz, data_sz, arch_sz, other_sz, sig_sz = struct.unpack_from("<7I", blob, 0)
    if sig != SIG or ver != 2:
        raise ValueError(f"不是 VPK v2：sig={sig:#x} ver={ver}")
    hdr = 28
    data_start = hdr + tree_sz
    o, tree_end = hdr, hdr + tree_sz
    entries = []

    def cstr(pos):
        e = blob.index(b"\x00", pos)
        return blob[pos:e].decode("utf-8", "replace"), e + 1

    while o < tree_end:
        ext, o = cstr(o)
        if not ext:
            break
        while True:
            d, o = cstr(o)
            if not d:
                break
            while True:
                name, o = cstr(o)
                if not name:
                    break
                crc, preload_bytes, arch, off, ln = struct.unpack_from("<IHHII", blob, o)
                o += 16
                term, = struct.unpack_from("<H", blob, o)
                o += 2
                if term != 0xFFFF:
                    raise ValueError("条目结束标记异常")
                preload = b""
                if preload_bytes:
                    preload = blob[o:o + preload_bytes]
                    o += preload_bytes
                if arch != INLINE:
                    raise ValueError(f"该 VPK 用了外部分片 arch={arch}，本脚本不支持")
                p = name + ("." + ext if ext else "")
                if d.strip():
                    p = d + "/" + p
                entries.append(VpkEntry(p, preload + blob[data_start + off:data_start + off + ln], crc))
    return {"entries": entries, "arch_md5_sz": arch_sz, "other_md5_sz": other_sz, "sig_sz": sig_sz}


def vpk_build(entries, arch_md5_sz=0, other_md5_sz=48, sig_sz=0):
    """按 Valve 规范重新打包（全部内联）。"""
    tree, data = bytearray(), bytearray()
    ext_order = []
    for e in entries:
        _d, _s, ext = e.split()
        if ext not in ext_order:
            ext_order.append(ext)
    for ext in ext_order:
        tree += ext.encode("utf-8") + b"\x00"
        dirs = []
        for e in entries:
            d, _s, ex = e.split()
            if ex == ext and d not in dirs:
                dirs.append(d)
        for d in dirs:
            tree += d.encode("utf-8") + b"\x00"
            for e in entries:
                ed, stem, ex = e.split()
                if ex != ext or ed != d:
                    continue
                off = len(data)
                data += e.data
                tree += stem.encode("utf-8") + b"\x00"
                tree += struct.pack("<IHHII", e.crc, 0, INLINE, off, len(e.data))
                tree += struct.pack("<H", 0xFFFF)
            tree += b"\x00"
        tree += b"\x00"
    tree += b"\x00"
    arch_md5 = b""
    if arch_md5_sz:
        arch_md5 = b"".join(
            struct.pack("<III", 0, 0, 0) + hashlib.md5(b"").digest() + b"\x00" * 16
            for _ in range(arch_md5_sz // 28)
        )
    body = struct.pack("<7I", SIG, 2, len(tree), len(data), arch_md5_sz, other_md5_sz, sig_sz)
    body += bytes(tree) + bytes(data) + arch_md5
    if other_md5_sz == 48:
        tree_md5 = hashlib.md5(bytes(tree)).digest()
        arch_md5_md5 = hashlib.md5(arch_md5).digest()
        whole_md5 = hashlib.md5(body + tree_md5 + arch_md5_md5).digest()
        body += tree_md5 + arch_md5_md5 + whole_md5
    elif other_md5_sz:
        body += b"\x00" * other_md5_sz
    return body


# ───────────────────────── 名字替换 ─────────────────────────
# 只认"整行就是一个模板链 + 一个带引号的名字"这种行
DECL = re.compile(r'^([A-Za-z0-9_]+(?:\+[A-Za-z0-9_]+)*)[ \t]+"([^"]*)"[ \t]*$')


def load_names(path):
    out = []
    for line in open(path, encoding="utf-8-sig").read().splitlines():
        n = line.strip()
        if not n or n.startswith("//"):
            continue
        n = "".join(ch for ch in n if ch >= " " and ch != "\x7f")
        n = n.replace('"', "").replace("\\", "")
        n = "".join(ch for ch in n if ord(ch) <= 0xFFFF)
        n = n.strip()
        if n:
            out.append(n)
    seen, uniq = set(), []
    for n in out:
        if n not in seen:
            seen.add(n)
            uniq.append(n)
    return uniq


def rewrite_db(db: bytes, names):
    """把 db 里所有档案的名字按顺序换成 names；多出的名字以"整块克隆"方式追加。返回 (新db, 档案数, 追加数, 实际用到的名字)"""
    text = db.decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    body = text.replace("\r\n", "\n")
    lines = body.split("\n")

    blocks = []          # (decl_line_index, end_line_index)
    i = 0
    while i < len(lines):
        if DECL.match(lines[i]):
            j = i + 1
            while j < len(lines) and lines[j].strip() != "End":
                j += 1
            if j >= len(lines):
                raise ValueError(f"第 {i} 行的档案块没有 End，无法安全改写")
            blocks.append((i, j))
            i = j + 1
        else:
            i += 1
    if not blocks:
        raise ValueError("没找到任何档案块（文件格式变了？）")

    used = []
    for idx, (s, _e) in enumerate(blocks):
        name = names[idx % len(names)]          # 名字不够就循环用
        m = DECL.match(lines[s])
        lines[s] = f'{m.group(1)} "{name}"'
        used.append(name)

    added = 0
    if len(names) > len(blocks):
        extra = names[len(blocks):]
        tail = ["", "//---- 追加档案：名字清单里多余的昵称（整块克隆，模板/参数沿用原档案）----"]
        for k, nm in enumerate(extra):
            s, e = blocks[(k * 7) % len(blocks)]
            for line in lines[s:e + 1]:
                m = DECL.match(line)
                tail.append(f'{m.group(1)} "{nm}"' if m else line)
            used.append(nm)
        lines.extend(tail)
        added = len(extra)

    out = newline.join(lines)
    return out.encode("utf-8"), len(blocks), added, used


def patch_vpk_bytes(blob, names):
    tmp = _tmpfile("patch_in.vpk")
    open(tmp, "wb").write(blob)
    info = vpk_read(tmp)
    entries = info["entries"]
    idxs = [i for i, e in enumerate(entries) if e.path == "botprofile.db"]
    if len(idxs) != 1:
        raise ValueError(f"VPK 里 botprofile.db 条目数 = {len(idxs)}，预期 1")
    i = idxs[0]
    others_before = {k: v.data for k, v in enumerate(entries) if k != i}
    new_db, nblocks, added, used = rewrite_db(entries[i].data, names)
    entries[i] = VpkEntry("botprofile.db", new_db)
    out = vpk_build(entries, info["arch_md5_sz"], info["other_md5_sz"], info["sig_sz"])
    # 自检：其它条目字节不变
    chk = vpk_read(_write_tmp(out))
    others_after = {k: v.data for k, v in enumerate(chk["entries"]) if k != i}
    assert others_before == others_after, "其它条目被改动了，中止"
    return out, nblocks, added, used


def _write_tmp(b):
    p = _tmpfile("check.vpk")
    open(p, "wb").write(b)
    return p


def verify_vpk(path_or_bytes, names, label):
    blob = open(path_or_bytes, "rb").read() if isinstance(path_or_bytes, str) else path_or_bytes
    info = vpk_read(_write_tmp(blob))
    e = [x for x in info["entries"] if x.path == "botprofile.db"][0]
    text = e.data.decode("utf-8")
    got = [m.group(2) for m in (DECL.match(l) for l in text.replace("\r\n", "\n").split("\n")) if m]
    ok_names = all(n in set(names) for n in got)
    print(f"   {label}: 条目 {len(info['entries'])} 个，档案 {len(got)} 个，"
          f"全部来自清单={ok_names}，唯一={len(set(got))}")
    return ok_names


# ───────────────────────── 主流程 ─────────────────────────
DEFAULT_DIR = ("D:/apps/steam/steamapps/common/Counter-Strike Global Offensive/"
               "game/csgo/overrides")


def targets_in_dir(d):
    out = []
    for v in ["", "High", "Medium", "Low"]:
        p = os.path.join(d, v, "botprofile.vpk") if v else os.path.join(d, "botprofile.vpk")
        if os.path.exists(p):
            out.append(("vpk", p))
    for root, _dirs, files in os.walk(d):
        for f in files:
            if f.lower() == "botprofile.db":
                out.append(("db", os.path.join(root, f)))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--dir", default=DEFAULT_DIR)
    ap.add_argument("--zip", default=None)
    ap.add_argument("--names", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                    "01_人机名字清单_1969.txt"))
    a = ap.parse_args()
    if not (a.check or a.apply):
        a.check = True

    names = load_names(a.names)
    print(f"名字清单: {a.names}  ({len(names)} 个, 唯一 {len(set(names))})")
    print(f"模式: {'APPLY（就地改+备份）' if a.apply else 'CHECK（只看不改）'}\n")

    if a.zip:
        import zipfile
        z = zipfile.ZipFile(a.zip)
        out_zip = os.path.splitext(a.zip)[0] + "_中文名.zip"
        replaced = {}
        for n in z.namelist():
            if n.lower().endswith("botprofile.vpk"):
                blob = z.read(n)
                try:
                    new, nb, ad, used = patch_vpk_bytes(blob, names)
                except Exception as ex:
                    print(f"  {n}: 跳过（{ex}）")
                    continue
                replaced[n] = new
                print(f"  {n}: 档案 {nb} +追加 {ad} -> {len(blob)}B → {len(new)}B"
                      + ("  [check]" if not a.apply else ""))
        if a.apply:
            with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zo:
                for item in z.infolist():
                    zo.writestr(item, replaced.get(item.filename, z.read(item.filename)))
            print("\n输出包:", out_zip)
        else:
            print("\n[check] 未写文件")
        return

    tg = targets_in_dir(a.dir)
    if not tg:
        print("没找到目标文件，检查 --dir:", a.dir)
        return
    for kind, p in tg:
        print(f"[{kind}] {p}")
        try:
            if kind == "vpk":
                blob = open(p, "rb").read()
                new, nb, ad, used = patch_vpk_bytes(blob, names)
                print(f"   档案 {nb} 个 + 追加 {ad} 个 -> {len(blob)}B → {len(new)}B"
                      + ("  [check]" if not a.apply else ""))
                if a.apply:
                    bak = p + ".bak"
                    if not os.path.exists(bak):
                        shutil.copy2(p, bak)
                    open(p, "wb").write(new)
                    verify_vpk(p, names, "写入后自检")
            else:
                raw = open(p, "rb").read()
                new, nb, ad, used = rewrite_db(raw, names)
                print(f"   裸露 db 档案 {nb} 个 + 追加 {ad} 个 -> {len(raw)}B → {len(new)}B"
                      + ("  [check]" if not a.apply else ""))
                if a.apply:
                    bak = p + ".bak"
                    if not os.path.exists(bak):
                        shutil.copy2(p, bak)
                    open(p, "wb").write(new)
        except Exception as ex:
            print("   失败:", ex)


if __name__ == "__main__":
    main()
