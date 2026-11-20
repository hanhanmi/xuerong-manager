# Bot Freeze Actions

Small CounterStrikeSharp plugin that gives bots occasional short knife-switch or crouch actions during the actual buy freeze period.

## Compatibility and conflict boundaries

- Built for CounterStrikeSharp API `1.0.380` and .NET 10, matching the manager bundle.
- Checks `CCSGameRules.FreezePeriod`; it stops immediately when freeze time ends.
- Targets connected fake clients only. It does not change view angles, yaw, movement, rank, identity, or networked fields.
- Each action is temporary and has a cleanup path on freeze end, bot disconnect, and plugin unload.
- The plugin does not hook bot AI events and has no configuration or shared state with other plugins.

## Build

```powershell
dotnet build .\BotFreezeActions.csproj -c Release
```

Copy `bin/Release/net10.0/BotFreezeActions.dll` and `BotFreezeActions.deps.json` into the bundle's `addons/counterstrikesharp/plugins/BotFreezeActions/` directory.
