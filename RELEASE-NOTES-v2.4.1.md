# XueRong CS2 Bot Manager — v2.4.1

A Windows desktop app that installs, configures and updates CS2 offline bot-match plugins for you. No more copying DLLs by hand or editing `gameinfo.gi` — pick a mode, click launch.

---

## What it does

- **One-click install / repair** of the full plugin suite: Metamod:Source + CounterStrikeSharp + 13 plugins, with automatic backup of every file it overwrites.
- **55-item integrity check** — 51 required files, the icon asset directory, two `gameinfo.gi` markers and the killfeed config. Tells you exactly what is missing instead of "something went wrong".
- **Two run modes** — *Full bots* (AI, cosmetics, rules, badges) or *Skins only* (inventory simulator alone, everything else disabled).
- **Rules editor** — overtime, friendly fire, boosting, bunny-hop, C4 timer, molotov damage. Written straight into cfg, effective next match.
- **Difficulty switching** — swaps the `overrides/*/botprofile.vpk` profile *and* syncs `bot_difficulty`, so the game actually reflects it.
- **Badges & killfeed** — XP overload badge, dynamic MVP nameplate, 22 killfeed icon packs.
- **Built-in auto-update** — checks GitHub Releases on launch, downloads with a progress bar, installs silently.

## Requirements

- Windows 10 / 11 x64
- Counter-Strike 2 installed via Steam
- WebView2 runtime (bundled with Windows 11; usually present on Windows 10)

## Install

1. Run `雪绒人机管理器_2.4.1_x64-setup.exe`. No administrator rights needed — it installs per-user.
2. Windows SmartScreen will warn you ("Windows protected your PC"). Click **More info → Run anyway**. The installer is not code-signed.
3. Open the app → **Settings** → confirm your CS2 folder (auto-detect usually works).
4. **Overview → Install / Repair suite**. Takes 30–60 seconds; the mod payload is ~81 MB.
5. **Overview → Plugin mode** to launch. Plugins only load in this mode.
6. In game, open the console (`~`) and run `exec my_bot_normal_config` to apply rules and cosmetics.

## Important notes

- **Offline and private use only.** Plugin mode launches CS2 with `-insecure`, which disables VAC. Never use this in Valve official matchmaking or on third-party competitive platforms (FACEIT etc.).
- **Third-party components.** The installer bundles Metamod:Source (GPL-3.0), CounterStrikeSharp (GPL-3.0), Harmony (MIT) and plugins from the CS2-Bot-Improver ecosystem (**AGPL-3.0**). None of them are original work. Full attribution and license terms: [THIRD-PARTY.md](https://github.com/hanhanmi/xuerong-manager/blob/main/THIRD-PARTY.md).
- **Not fully original.** The manager itself (Rust backend + React frontend) is written from scratch; the bundled plugins are not. Please read THIRD-PARTY.md before redistributing.
- **Uninstall is non-destructive.** Files are moved to a backup folder, not deleted.

## Known limitations

- Badge colour is driven by a single global `RankType` value — all bots share one colour per match. This is a plugin limitation, not a bug.
- Killfeed icons need a one-time in-game selection: send `!weaponkill`, then `!<number>`.
- If the game updates and plugins stop loading, run **Install / Repair suite** then **Re-apply configs**. If it still fails, the upstream Metamod / CounterStrikeSharp build has not caught up yet.

## Update

This build has the repository baked in — no configuration needed. Open **Settings → Software Update → Check for updates**. New versions are downloaded and installed in place.

---

**Full changelog / docs:** see the repository README.
**License:** MIT for the manager's own code; bundled third-party components keep their original licenses.
