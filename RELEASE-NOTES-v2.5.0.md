# XueRong CS2 Bot Manager — v2.5.0

**Uninstall is now a real cleanup.** No more backup folders piling up inside your game directory, and Steam no longer asks to re-download files after you remove the plugins.

---

## Fixed

### 1. Steam asked to re-download files after uninstalling

The plugin pack overwrites 15 official game files — `gameinfo.gi` plus 14 `cfg/gamemode_*.cfg`. The old uninstaller deleted the plugin folders but never put those back, so Steam saw modified official files and wanted to repair the install.

Now:
- Every install first snapshots the official originals into `%LOCALAPPDATA%\XueRongCS2\pristine\` (outside the game folder).
- Uninstall restores all of them before finishing.
- If no snapshot exists (installed with an older build), the app falls back to the pack's own official `gameinfo.gi` and tells you to run Steam → Verify integrity once.

### 2. Backup folders accumulated inside the game directory

The old uninstaller moved everything to `<game>\..\_xuerong_backup\uninstall_<timestamp>\`. On this machine that had already reached **854 MB** across 8 folders.

Now uninstall **deletes outright** — no backup is created. If you want the plugins back, just run *Install / Repair suite* again; the payload is already inside the app.

### 3. "31 of 55 missing" after uninstalling looked like a failure

It wasn't. Of the 55 checks, **15 are official game files** (`gameinfo.gi` + 14 gamemode configs) that always exist and must never be deleted. The other 40 are plugin files that only exist once installed.

The status card now separates the two:

```
Not installed (clean state)
55 checks: 15 are official game files (always present), 40 are plugin files
```

A red "missing N items" state only appears when the install is genuinely broken — i.e. some plugin files are there and some aren't.

## Improved

- **Permission errors are readable.** If the game folder is not writable (typical when Steam lives under `C:\Program Files`), the error now explains the cause and suggests running as administrator or moving the Steam library, instead of dumping a raw OS error.
- **Icon assets are deleted safely.** `panorama/images/icons/equipment` is only removed when every file inside is a `.vsvg_eq`/`.vsvg_c` asset we added — never if official content is mixed in.
- **`autoexec.cfg` is cleaned properly.** Only the marked block is removed; if the file ends up empty it is deleted, otherwise your own lines are preserved.

## Uninstall behaviour summary

| Action | Old | New |
|---|---|---|
| `addons/`, `overrides/`, `backup/` | moved to `_xuerong_backup` | deleted |
| Killfeed icon assets | left behind | deleted (only if ours) |
| `xuerong_manager.cfg` | left behind | deleted |
| `autoexec.cfg` marker block | left behind | removed |
| Official files overwritten by the pack | **never restored** | restored from snapshot |
| Disk usage after uninstall | grows every time | returns to baseline |

## Notes

- Auto-update is unchanged. This build still checks the repository baked into it.
- Still offline / private use only. Plugin mode runs with `-insecure`, which disables VAC. Never use in official matchmaking.
- Third-party components and licenses: see [THIRD-PARTY.md](https://github.com/hanhanmi/xuerong-manager/blob/main/THIRD-PARTY.md).
