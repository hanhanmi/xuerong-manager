# Components and Attribution

The installer ships third-party binaries and game assets. This file records where each piece comes from and under which licence it is distributed.

The desktop manager itself and the two plugins noted in section 3 were written for this project. Everything else in the installer comes from the projects listed below.

---

## 1. Runtime frameworks

| Component | Version | Project | Licence |
| --- | --- | --- | --- |
| Metamod:Source | 2.0.0-git1483 | [alliedmodders/metamod-source](https://github.com/alliedmodders/metamod-source) | GPL-3.0 |
| CounterStrikeSharp | 1.0.380 | [roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | GPL-3.0 |
| 0Harmony | 2.x | [pardeike/Harmony](https://github.com/pardeike/Harmony) | MIT |

## 2. Bot plugins from upstream projects

These are compiled from projects by their respective authors. We package them and, where noted, maintain a local fork.

| Plugin | Author / upstream | Source | Licence |
| --- | --- | --- | --- |
| BotAI | ed0ard (forked from Austin) | [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI) | AGPL-3.0 |
| BotAimImprover | ed0ard | [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot) | AGPL-3.0 |
| BotBuy | ed0ard | [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy) | AGPL-3.0 |
| BotRandomizer | ed0ard | [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer) | AGPL-3.0 |
| NadeSystem | ed0ard | [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem) | AGPL-3.0 |
| BotState (Smarter-Bot) | ed0ard | [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot) | AGPL-3.0 |
| RoundDamageRecap | YuGeYu | [LBTV-CS2-Bot-Enhancer](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer) | AGPL-3.0 |
| Killfeed_Icons | source supplied by a user, adapted to API 1.0.380 | `plugin-src/Killfeed_Icons/` | no licence file supplied — check with the original author before redistributing |
| MVP-Music-Lite | local fork of an upstream MVP music plugin | `plugin-src/MVP-Music-Lite/` | see source tree |
| BotFreezeActions | small local plugin, currently disabled | `plugin-src/BotFreezeActions/` | see source tree |
| InventorySimulator | ianlucas | [cs2-css-inventory-simulator](https://github.com/ianlucas/cs2-css-inventory-simulator) v3.8.2, official release; source under `plugin-src/third-party/` | MIT |

Native Metamod plugins:

| Plugin | Author | Source |
| --- | --- | --- |
| BotController | XBribo | [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) |
| BotHider | XBribo | [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) |
| BotVision | XBribo | [CS2-Bot-Vision](https://github.com/XBribo/CS2-Bot-Vision) |

The base bundle is [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) by ed0ard, AGPL-3.0 overall. Its `Panel/` subdirectory is PolyForm Strict 1.0.0 and is not included here.

## 3. Written for this project

| Part | What it is |
| --- | --- |
| `src/` , `src-tauri/src/` | The desktop manager: Tauri shell, plugin checks, config editing, installer |
| `XueRongChat` | FACEIT-style chat and round presentation for local servers — see `plugin-src/XueRongChat/` |
| XP overload badge in `MapRanks` | The XP badge layer added on top of upstream MapRanks — see `plugin-src/MapRanks/MapRanksXpBadge.cs` |

MapRanks itself is upstream work. Only the XP badge part was written here.

## 4. Game assets

- `overrides/*/botprofile.vpk` — bot profile database derived from upstream CS2-Bot-Improver; names localised to Chinese.
- `panorama/images/icons/equipment/*.vsvg_c` — 1593 kill icons shipped with Killfeed_Icons; provenance unverified.

## 5. Licensing notes

AGPL-3.0 components require source availability when you distribute the installer. Keeping this repository public with links to the upstream projects satisfies that for the unmodified upstream parts. Modified AGPL components remain under AGPL-3.0.

The manager shell is MIT licensed; it talks to the plugins through files on disk, so it is distributed alongside them rather than as a derivative work.

Two items lack upstream licence files: Killfeed_Icons and the XP badge layer's original MapRanks source. Confirm redistribution terms with the respective authors before publishing a public build.
