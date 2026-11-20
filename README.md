# AEMEATH

A desktop manager for offline CS2 bot matches. It bundles the CS2-Bot-Improver plugin stack and gives you a GUI to install it, switch difficulty, tune server rules, and manage bot cosmetics.

Built with Tauri 2 (Rust + React). Windows x64 only.

---

## What it does

| Tab | Purpose |
| --- | --- |
| Overview | Install / repair the kit, launch in bot or skin-only mode, uninstall |
| Plugins | File-by-file check of the whole addon tree, category toggles |
| Bots | Three difficulty presets, bot gear, aim mode, grenade intensity |
| Rules | Overtime, friendly fire, stacking, bunnyhop, C4 timer, molotov scale |
| Cosmetics | XP overload badge, MVP card, 22 kill-icon packs, skin inventory |
| Commands | 15 frequently used console commands, one-click copy |
| Walkthrough | Nine-step interactive guide |
| Settings | Game path, background, music, compatibility flags, diagnostics |

Other things worth knowing:

- CS2 refuses to let the manager touch files while the game is running, so it blocks those actions and tells you why.
- The diagnostic bundle collects logs, config snapshots and (optionally) a crash dump into a zip you can hand to someone for help. Paths and SteamIDs are masked.
- The bot kit itself ships as a separate archive inside the installer. You do not need to assemble anything by hand.

---

## Bot AI (Smarter-Bot 1.13.29)

The bundled bot stack was upgraded to Smarter-Bot 1.13.29. Highlights from the last three upstream releases:

- **CT site holding** — bots take up positions without being pulled away by the old pathfinder; each round picks between full hold, partial rotation, or corner swapping. A and B can be set up differently.
- **Post-plant defence** — one bot watches the C4 while others cover entrances; bots rotate back from far positions and fall back to the vanilla retreat logic when the bomb is about to explode and nobody is defusing.
- **Sound awareness** — bots hear footsteps, gunfire and landing, report rough directions to teammates, and keep a short memory (~4s) used for pre-aiming.
- **Peeking** — side-to-side peeks are now the default for both holding and post-plant; front-back peeks were removed. Bots fall back to unstuck corner swapping when they cannot move.
- **Flash handling** — flash return prefers standing and keeps moving; no forced crouch or hard stop.
- **Aim feel** — view-angle parameters were retuned across 1369+ profiles for smoother tracking, and reaction time is now a realistic 50–150 ms instead of near-instant.

Bots keep three difficulty tiers. Press `css_smartbot_status` in the console to see the live version, site assignments, peek counts and sound/reaction stats.

The upstream notes state the package was file-verified but not play-tested. Treat the in-game feel as something to confirm yourself. A full feature breakdown is in [BOT-AI-FEATURES.md](BOT-AI-FEATURES.md).

---

## Install

Download `AEMEATH_x.y.z_x64-setup.exe` and run it.

- Installs to `%LOCALAPPDATA%\Programs`, no administrator rights needed.
- SmartScreen will warn on first run because the binary is not code-signed. Choose "More info" → "Run anyway".
- After install, confirm the CS2 path in Settings, then click "Install / Repair kit" on the Overview tab and launch in plugin mode.

### Requirements

- Windows 10 / 11 x64
- CS2 from Steam
- WebView2 runtime (included in Windows 11; Windows 10 usually has it via Edge)

### Localisation

Bot names in the shipped profile database are Chinese. If you want a different set, the name list lives in `tools/bot_names/`.

---

## Building from source

```bash
npm install
npx tauri build
```

You need Node 18+, Rust 1.77+ and the MSVC toolchain. The installer ends up in `src-tauri/target/release/bundle/nsis/`.

Two things are intentionally not in this repository:

- `src-tauri/resources/CS2BotImprover.zip` (~80 MB) — the plugin kit, containing third-party binaries. Grab an upstream CS2-Bot-Improver release, repack `addons / backup / cfg / gameinfo.gi / overrides / panorama` into a zip and place it there. Without it the build succeeds but "Install / Repair kit" reports a missing archive.
- `src/assets/bgm.mp3` and `src/assets/bg.mp4` — third-party media. Drop any mp3/mp4 in their place; the code does not depend on specific content, but the bundler needs the files to exist.

---

## Authorship and licensing

The installer bundles software by other authors. Bot plugins come from ed0ard's CS2-Bot-Improver ecosystem (AGPL-3.0), native plugins from XBribo, InventorySimulator from ianlucas (MIT), and the frameworks from alliedmodders and roflmuffin (GPL-3.0). Full list with source links: [THIRD-PARTY.md](THIRD-PARTY.md).

Written for this project: the desktop manager, `XueRongChat` (FACEIT-style presentation), and the XP overload badge layer in MapRanks. MapRanks itself is upstream work.

Manager code is [MIT](LICENSE). Bundled AGPL components keep their own licences; see THIRD-PARTY.md before redistributing.

---

## Scope

Offline bot matches and private servers only. Not for Valve matchmaking or FACEIT. Plugin mode starts CS2 with `-insecure`, which disables VAC — do not use it in official matchmaking.

---

## Credits

[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) ·
[Metamod:Source](https://github.com/alliedmodders/metamod-source) ·
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) ·
[Harmony](https://github.com/pardeike/Harmony) ·
[cs2-lib](https://github.com/ianlucas/cs2-lib) ·
[Tauri](https://github.com/tauri-apps/tauri)
