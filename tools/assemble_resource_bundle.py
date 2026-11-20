#!/usr/bin/env python3
"""Assemble the manager's distributable resource ZIP from the latest body release."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
INVENTORY_RELEASE_SHA256 = "7E310228AB4852E54C82D8C21A21F913CDE946CC7AA149DAB7C224CE6781D1AB"
CUSTOM_PLUGIN_DIRS = {
    "BotFreezeActions",
    "Killfeed_Icons",
    "MapRanks",
    "MVP-Music-Lite",
    "RoundDamageRecap",
    "XueRongChat",
    "PremierBadgeMvp",  # legacy XP badge config only; never its old DLL
}
PERSONAL_OR_TRANSIENT = {
    "elo.json",
    "human_ratings.json",
    "player_skins.json",
    "volumes.json",
    "stattrak_local.json",
}


def read_files(archive: zipfile.ZipFile) -> dict[str, tuple[zipfile.ZipInfo, bytes]]:
    return {
        item.filename: (item, archive.read(item.filename))
        for item in archive.infolist()
        if not item.is_dir()
    }


def include_manager_overlay(name: str) -> bool:
    if name == "插件检测清单.json" or name.startswith("panorama/"):
        return True
    if name in {
        "addons/BotHider/map_whitelist.json",
        "cfg/xuerong_invsim.cfg",
    }:
        return True
    if name.startswith("addons/MVP-Music-Lite/"):
        return True
    if name.startswith("addons/counterstrikesharp/configs/"):
        return Path(name).name not in PERSONAL_OR_TRANSIENT
    if name.startswith("addons/counterstrikesharp/gamedata/"):
        return name.endswith("/inventory-simulator.json") is False
    prefix = "addons/counterstrikesharp/plugins/"
    if name.startswith(prefix):
        parts = name[len(prefix):].split("/")
        plugin = parts[0]
        if plugin not in CUSTOM_PLUGIN_DIRS:
            return False
        if Path(name).name in PERSONAL_OR_TRANSIENT or "/logs/" in name:
            return False
        if plugin == "PremierBadgeMvp":
            return name.endswith("/config.json")
        return True
    # Panel binaries and internal reverse-engineering notes do not belong in
    # the game resource bundle. The clean upstream package supplies gameinfo.gi.
    return False


def put_bytes(
    entries: dict[str, tuple[zipfile.ZipInfo, bytes]],
    name: str,
    data: bytes,
    template: zipfile.ZipInfo | None = None,
) -> None:
    if template is not None:
        info = copy.copy(template)
        info.filename = name
    else:
        info = zipfile.ZipInfo(name, datetime.now().timetuple()[:6])
        info.create_system = 3
        info.external_attr = 0o100644 << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    entries[name] = (info, data)


def add_plugin_build(entries: dict[str, tuple[zipfile.ZipInfo, bytes]], folder: str, dll: str) -> None:
    built = ROOT / "plugin-src" / folder / "bin" / "Release" / "net10.0"
    target = f"addons/counterstrikesharp/plugins/{folder}"
    for suffix in (".dll", ".deps.json"):
        source = built / f"{dll}{suffix}"
        if not source.is_file():
            raise FileNotFoundError(f"Build output missing: {source}")
        put_bytes(entries, f"{target}/{source.name}", source.read_bytes())


def update_manifest(entries: dict[str, tuple[zipfile.ZipInfo, bytes]]) -> None:
    raw = entries.get("插件检测清单.json")
    if raw is None:
        raise ValueError("Manager overlay does not contain 插件检测清单.json")
    manifest = json.loads(raw[1].decode("utf-8-sig"))

    rust = (ROOT / "src-tauri" / "src" / "lib.rs").read_text(encoding="utf-8")
    block = rust.split("const MANIFEST_FILES: &[(&str, bool)] = &[", 1)[1].split("];", 1)[0]
    files = re.findall(r'\("([^"\n]+)",\s*(?:true|false)\)', block)
    if not files:
        raise ValueError("Could not read MANIFEST_FILES from lib.rs")
    manifest["files"] = files
    manifest["bundleVersion"] = "1.4.9"
    manifest["pluginMeta"] = [
        {
            "name": "BotFreezeActions",
            "displayName": "准备阶段人机动作",
            "version": "1.0.0",
            "type": "css_plugin",
            "source": "雪绒自制源码；CounterStrikeSharp API 1.0.380 / .NET 10",
            "status": "冻结时间里机器人随机短暂切刀或下蹲；不改朝向、不写网络变量",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/BotFreezeActions/BotFreezeActions.dll",
                "addons/counterstrikesharp/plugins/BotFreezeActions/BotFreezeActions.deps.json",
            ],
            "optionalFiles": [],
        },
        {
            "name": "InventorySimulator",
            "displayName": "本地皮肤库存（库存模拟器）",
            "version": "3.8.2-gloves",
            "type": "css_plugin",
            "source": "ianlucas/cs2-css-inventory-simulator，按用户实测保留可显示手套的构建",
            "status": "用户实测手套可显示；资源包重组时不再强制替换",
            "configLocation": "addons/counterstrikesharp/configs/plugins/InventorySimulator/invsim.cfg; cfg/xuerong_invsim.cfg",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.dll",
                "addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.deps.json",
                "addons/counterstrikesharp/gamedata/inventory-simulator.json",
            ],
            "optionalFiles": [
                "addons/counterstrikesharp/plugins/InventorySimulator/InventorySimulator.pdb",
                "addons/counterstrikesharp/plugins/InventorySimulator/lang/en.json",
                "addons/counterstrikesharp/plugins/InventorySimulator/lang/zh-Hans.json",
                "addons/counterstrikesharp/plugins/InventorySimulator/lang/pt-BR.json",
            ],
        },
        {
            "name": "Killfeed_Icons",
            "displayName": "击杀图标",
            "version": "1.0.26",
            "type": "css_plugin",
            "source": "雪绒维护源码（基于用户提供的 Killfeed_Icons 源码）；CounterStrikeSharp API 1.0.380 / .NET 10",
            "status": "!weaponkill 打开选包菜单；机器人图标包每回合重抽；保留旧玩家设置路径",
            "configLocation": "addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json; addons/counterstrikesharp/configs/plugins/KillfeedIcons/player_skins.json",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/Killfeed_Icons/Killfeed_Icons.dll",
                "addons/counterstrikesharp/plugins/Killfeed_Icons/Killfeed_Icons.deps.json",
            ],
            "optionalFiles": [],
            "sideEffect": "改写 EventPlayerDeath.Weapon 图标名；不写 netvar。gameinfo.gi 挂载 panorama 图标目录。",
        },
        {
            "name": "MapRanks",
            "displayName": "BOT 随机段位 + XP 过载徽章",
            "version": "0.2.7-xuerong-xp-merged",
            "type": "css_plugin",
            "source": "雪绒定制 MapRanks；XP 徽章已合并；CounterStrikeSharp API 1.0.380 / .NET 10",
            "status": "段位 / 动态 MVP / XP 徽章由单一插件管理；只读 Rank 消息观察钩子默认关闭",
            "configLocation": "addons/counterstrikesharp/plugins/MapRanks/MapRanks.json; addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/MapRanks/MapRanks.dll",
                "addons/counterstrikesharp/plugins/MapRanks/MapRanks.deps.json",
                "addons/counterstrikesharp/plugins/MapRanks/native/win64/MapRanksWire.dll",
            ],
            "optionalFiles": [
                "addons/counterstrikesharp/plugins/MapRanks/MapRanks.json",
                "addons/counterstrikesharp/plugins/PremierBadgeMvp/config.json",
            ],
            "sideEffect": "唯一写段位 netvar 和 RankUpdate 消息；合并 XP InventoryServices 字段；不加载独立 PremierBadgeMvp DLL。",
        },
        {
            "name": "MVP-Music-Lite",
            "displayName": "MVP Music Lite",
            "version": "1.3.4",
            "type": "css_plugin",
            "source": "雪绒维护源码；CounterStrikeSharp API 1.0.380 / .NET 10",
            "configLocation": "addons/counterstrikesharp/configs/plugins/MVP-Music-Lite/MVP-Music-Lite.json",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.dll",
                "addons/counterstrikesharp/plugins/MVP-Music-Lite/MVP-Music-Lite.deps.json",
                "addons/MVP-Music-Lite/plugin/MVP-Music-Lite.dll",
            ],
            "optionalFiles": [
                "addons/counterstrikesharp/configs/plugins/MVP-Music-Lite/MVP-Music-Lite.json",
                "addons/counterstrikesharp/plugins/MVP-Music-Lite/volumes.json",
            ],
            "depsJsonRequired": True,
        },
        {
            "name": "RoundDamageRecap",
            "displayName": "回合伤害回顾",
            "version": "1.3.0",
            "type": "css_plugin",
            "source": "CS2-Bot-Improver 上游源码；按 CounterStrikeSharp API 1.0.380 / .NET 10 重建",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.dll",
                "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.deps.json",
            ],
            "optionalFiles": [],
            "depsJsonRequired": True,
        },
        {
            "name": "XueRongChat",
            "displayName": "FACEIT 平台聊天仿真（雪绒自制）",
            "version": "2.6.1",
            "type": "css_plugin",
            "source": "雪绒维护源码；CounterStrikeSharp API 1.0.380 / .NET 10",
            "status": "FACEIT 风格聊天与播报、等级前缀、连杀/残局/回合提示、ELO 存档",
            "configLocation": "addons/counterstrikesharp/configs/plugins/XueRongChat/XueRongChat.json",
            "requiredFiles": [
                "addons/counterstrikesharp/plugins/XueRongChat/XueRongChat.dll",
                "addons/counterstrikesharp/plugins/XueRongChat/XueRongChat.deps.json",
            ],
            "optionalFiles": [],
            "sideEffect": "监听 say / say_team 并按配置重发聊天消息；不写 netvar。",
        },
    ]
    manifest.setdefault("cleanupPaths", [])
    freeze_path = "addons/counterstrikesharp/plugins/BotFreezeActions"
    if freeze_path not in manifest["cleanupPaths"]:
        manifest["cleanupPaths"].append(freeze_path)
    manifest.setdefault("preservePaths", [])
    legacy_skin_path = "addons/counterstrikesharp/configs/plugins/KillfeedIcons"
    if legacy_skin_path not in manifest["preservePaths"]:
        manifest["preservePaths"].append(legacy_skin_path)
    ownership = manifest.get("netvarOwnership")
    if isinstance(ownership, dict):
        notes = ownership.setdefault("notes", [])
        note = "BotFreezeActions / Killfeed_Icons / XueRongChat 不写 netvar。"
        if note not in notes:
            notes.append(note)

    encoded = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    put_bytes(entries, "插件检测清单.json", encoded, raw[0])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, type=Path, help="Latest CS2-Bot-Improver ZIP with the supplied bot-name VPK update applied")
    parser.add_argument("--overlay", required=True, type=Path, help="Previous manager resource ZIP, used only for manager-specific assets/configs")
    parser.add_argument(
        "--inventory-release",
        type=Path,
        default=ROOT / "plugin-src/third-party/InventorySimulator-3.8.2/InventorySimulator-v3.8.2-release.zip",
    )
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    inv_hash = hashlib.sha256(args.inventory_release.read_bytes()).hexdigest().upper()
    if inv_hash != INVENTORY_RELEASE_SHA256:
        raise ValueError(f"InventorySimulator release ZIP hash mismatch: {inv_hash}")

    entries: dict[str, tuple[zipfile.ZipInfo, bytes]] = {}
    with zipfile.ZipFile(args.base) as archive:
        entries.update(read_files(archive))
    # The upstream archive carries Panel.exe at its ZIP root. The manager has
    # its own controls and never invokes that helper, so do not redistribute it.
    for name in list(entries):
        if Path(name).name.casefold() == "panel.exe":
            del entries[name]
    with zipfile.ZipFile(args.overlay) as archive:
        for name, (info, data) in read_files(archive).items():
            if include_manager_overlay(name):
                put_bytes(entries, name, data, info)

    # 保留用户实测有效的库存插件版本（手套能正常显示）；只有在基底完全没有库存插件时
    # 才用官方 release 补齐，避免每次重组资源包都把手套版本换回官方 3.8.2。
    inv_prefix = "addons/counterstrikesharp/plugins/InventorySimulator/"
    inv_gamedata = "addons/counterstrikesharp/gamedata/inventory-simulator.json"
    if not any(n.startswith(inv_prefix) for n in entries):
        with zipfile.ZipFile(args.inventory_release) as archive:
            for info, data in read_files(archive).values():
                put_bytes(entries, info.filename, data, info)
    elif inv_gamedata not in entries:
        with zipfile.ZipFile(args.inventory_release) as archive:
            for info, data in read_files(archive).values():
                if info.filename == inv_gamedata:
                    put_bytes(entries, info.filename, data, info)

    # Use the user-supplied killfeed configuration without including their local
    # player_skins.json (the manager recreates that file from local settings).
    killfeed_config = ROOT / "plugin-src/Killfeed_Icons/Killfeed_Icons.json"
    put_bytes(
        entries,
        "addons/counterstrikesharp/configs/plugins/Killfeed_Icons/Killfeed_Icons.json",
        killfeed_config.read_bytes(),
    )

    # Keep the merged XP badge configuration and remove only its obsolete message observer.
    mapranks_name = "addons/counterstrikesharp/plugins/MapRanks/MapRanks.json"
    if mapranks_name in entries:
        config = json.loads(entries[mapranks_name][1].decode("utf-8-sig"))
        config["observe_rank_messages"] = False
        put_bytes(entries, mapranks_name, (json.dumps(config, ensure_ascii=False, indent=2) + "\n").encode("utf-8"), entries[mapranks_name][0])

    for folder, dll in (
        ("BotFreezeActions", "BotFreezeActions"),
        ("Killfeed_Icons", "Killfeed_Icons"),
        ("MapRanks", "MapRanks"),
        ("MVP-Music-Lite", "MVP-Music-Lite"),
        ("RoundDamageRecap", "RoundDamageRecap"),
        ("XueRongChat", "XueRongChat"),
    ):
        add_plugin_build(entries, folder, dll)

    mvp_dll = ROOT / "plugin-src/MVP-Music-Lite/bin/Release/net10.0/MVP-Music-Lite.dll"
    mvp_data = mvp_dll.read_bytes()
    put_bytes(entries, "addons/MVP-Music-Lite/plugin/MVP-Music-Lite.dll", mvp_data)
    # The music page also installs this plugin from Tauri's separate resources.
    (ROOT / "src-tauri/resources/mvp-music/MVP-Music-Lite.dll").write_bytes(mvp_data)
    update_manifest(entries)

    missing = [name for name in json.loads(entries["插件检测清单.json"][1])["files"] if name not in entries]
    if missing:
        raise ValueError("Manifest file(s) missing from output: " + ", ".join(missing))
    forbidden = [name for name in entries if name == "Panel.exe" or name.endswith("/Panel.exe")]
    if forbidden:
        raise ValueError("Panel executable must not be redistributed: " + ", ".join(forbidden))
    personal = [name for name in entries if Path(name).name in PERSONAL_OR_TRANSIENT]
    if personal:
        raise ValueError("Personal / transient state must not be packaged: " + ", ".join(personal))
    if "addons/counterstrikesharp/plugins/PremierBadgeMvp/PremierBadgeMvp.dll" in entries:
        raise ValueError("The obsolete standalone PremierBadgeMvp DLL must not be packaged")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix="resource_bundle_", suffix=".zip", dir=args.output.parent, delete=False) as temp:
        temp_path = Path(temp.name)
    try:
        with zipfile.ZipFile(temp_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for name, (info, data) in entries.items():
                archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
        with zipfile.ZipFile(temp_path) as archive:
            if archive.testzip() is not None:
                raise ValueError("ZIP CRC verification failed")
        temp_path.replace(args.output)
    finally:
        temp_path.unlink(missing_ok=True)

    print(f"Created {args.output} ({len(entries)} files; bundleVersion 1.4.9)")
    print(f"InventorySimulator release SHA256: {inv_hash}")


if __name__ == "__main__":
    main()
