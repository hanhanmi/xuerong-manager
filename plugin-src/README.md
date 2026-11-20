# Plugin source index

This folder separates editable plugins from the upstream body package. The game installer consumes the built binaries in `src-tauri/resources/CS2BotImprover.zip`; it does not compile these projects at install time.

## XueRong maintained plugins

| Folder | Purpose | Build target |
|---|---|---|
| `BotFreezeActions/` | Short random knife / crouch actions for bots during freeze time; no yaw or netvar writes | .NET 10, CounterStrikeSharp API 1.0.380 |
| `Killfeed_Icons/` | Killfeed icon packs, player preferences, and per-round bot pack rerolls | .NET 10, CounterStrikeSharp API 1.0.380 |
| `MapRanks/` | Bot rank / MVP presentation and merged XP overload badge; passive rank-message diagnostics are off by default | .NET 10, CounterStrikeSharp API 1.0.380 |
| `MVP-Music-Lite/` | Custom MVP music and manager music page integration | .NET 10, CounterStrikeSharp API 1.0.380 |
| `RoundDamageRecap/` | End-of-round damage summary; rebuilt from the upstream plugin source | .NET 10, CounterStrikeSharp API 1.0.380 |
| `XueRongChat/` | FACEIT-style chat and match announcements | .NET 10, CounterStrikeSharp API 1.0.380 |

Build a maintained plugin from the workspace root with:

```powershell
dotnet build plugin-src\<Folder>\<Project>.csproj -c Release
```

The generated `bin/` and `obj/` directories are ignored by Git.

After building all six maintained plugins, rebuild the bundle with the latest
body ZIP, the previous manager bundle as an overlay source, and the bundled
official InventorySimulator release:

```powershell
python tools\assemble_resource_bundle.py `
  --base "C:\path\to\CS2BotImprover_中文名.zip" `
  --overlay "src-tauri\resources\CS2BotImprover.pre-manifest-sync-1.0.8.old.zip" `
  --output "src-tauri\resources\CS2BotImprover.zip"
```

The assembler verifies the InventorySimulator release hash, synchronizes the
installer manifest and the music page's separate MVP DLL, excludes per-user
state, and rejects a packaged Panel.exe.

For the exact 1,969-name list and VPK rewrite instructions, see `tools/bot_names/`.

## Upstream maintained source

| Folder | Upstream | Packaged version | Notes |
|---|---|---|---|
| `third-party/InventorySimulator-3.8.2/` | [ianlucas/cs2-css-inventory-simulator](https://github.com/ianlucas/cs2-css-inventory-simulator) | 3.8.2 | Unmodified upstream source archive and MIT license. The resource bundle uses the matching official v3.8.2 release binary and gamedata. |

CS2-Bot-Improver's other plugins are maintained by the upstream body-package project and are distributed as its v1.4.9 release binaries. Their standalone source projects are not duplicated here.

## Source and binary notes

- `MapRanks/native/win64/MapRanksWire.dll` is a paired native component in the package. Its C++ source was not present in the handoff files, so keep that binary paired with the matching `MapRanks.dll` build.
- The supplied Killfeed RAR contains source files but no license file. Its 1,593 icon files match the current workspace resources byte for byte. Confirm the original author's redistribution terms before public redistribution.
- `InventorySimulator` is upstream-maintained; do not patch or rebuild it as a XueRong fork unless that is explicitly intended.
- Keep distribution updates in `src-tauri/resources/CS2BotImprover.zip`. Do not copy plugin DLLs directly into an installed game folder.
