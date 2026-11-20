using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Modules.Commands.CommandInfo;
using static CounterStrikeSharp.API.Core.Listeners;

namespace MapRanks;

/// <summary>
/// XP badge half of the former PremierBadgeMvp plugin. MapRanks remains the
/// only writer for rank netvars; this partial only writes the inventory XP trail.
/// </summary>
public sealed partial class MapRanksPlugin
{
    private sealed class XpBadgeSettings
    {
        [JsonPropertyName("EnableXpBadge")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("XpMin")] public int Min { get; set; } = 1;
        [JsonPropertyName("XpMax")] public int Max { get; set; } = 8;
        [JsonPropertyName("XpMode")] public string Mode { get; set; } = "slot";
        [JsonPropertyName("SelfXp")] public int SelfXp { get; set; } = 7;
        [JsonPropertyName("XpToHumans")] public bool ToHumans { get; set; } = true;
    }

    private static readonly JsonSerializerOptions XpBadgeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private XpBadgeSettings _xpBadge = new();
    private readonly Dictionary<int, int> _xpBadgeLevels = new();
    private readonly HashSet<int> _xpBadgeLogged = new();

    private void InitializeXpBadge()
    {
        var moduleDir = ((BasePlugin)this).ModuleDirectory;
        var configPath = Path.GetFullPath(Path.Combine(moduleDir, "..", "PremierBadgeMvp", "config.json"));

        try
        {
            if (File.Exists(configPath))
            {
                _xpBadge = JsonSerializer.Deserialize<XpBadgeSettings>(File.ReadAllText(configPath), XpBadgeJsonOptions)
                    ?? new XpBadgeSettings();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, JsonSerializer.Serialize(_xpBadge, XpBadgeJsonOptions));
            }

            _xpBadge.Min = Math.Clamp(_xpBadge.Min, 1, 8);
            _xpBadge.Max = Math.Clamp(_xpBadge.Max, _xpBadge.Min, 8);
            _xpBadge.SelfXp = Math.Clamp(_xpBadge.SelfXp, 0, 8);
            _xpBadge.Mode = string.Equals(_xpBadge.Mode, "random", StringComparison.OrdinalIgnoreCase)
                ? "random"
                : "slot";
        }
        catch (Exception ex)
        {
            _xpBadge = new XpBadgeSettings();
            Logger.LogWarning(ex, "[MapRanks] Could not load the legacy XP badge config; using defaults.");
        }

        ((BasePlugin)this).RegisterEventHandler<EventPlayerConnectFull>(OnXpBadgeConnectFull, (HookMode)1);
        ((BasePlugin)this).RegisterEventHandler<EventPlayerSpawn>(OnXpBadgeSpawn, (HookMode)1);
        ((BasePlugin)this).RegisterEventHandler<EventRoundStart>(OnXpBadgeRoundStart, (HookMode)1);
        ((BasePlugin)this).RegisterListener<OnClientDisconnect>(OnXpBadgeDisconnect);
        ((BasePlugin)this).AddCommand(
            "css_badge_mvp",
            "Show XP badge status (rank presentation is owned by MapRanks)",
            (CommandCallback)OnXpBadgeCommand);

        Logger.LogInformation(
            "[MapRanks] Merged XP badge loaded: enabled={Enabled}, range={Min}-{Max}, humans={Humans}; rank fields remain MapRanks-owned.",
            _xpBadge.Enabled, _xpBadge.Min, _xpBadge.Max, _xpBadge.ToHumans);
    }

    private void ShutdownXpBadge()
    {
        _xpBadgeLevels.Clear();
        _xpBadgeLogged.Clear();
    }

    private HookResult OnXpBadgeConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsHLTV: false } player)
            ApplyXpBadge(player);
        return (HookResult)0;
    }

    private HookResult OnXpBadgeSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsHLTV: false } player)
            ApplyXpBadge(player);
        return (HookResult)0;
    }

    private HookResult OnXpBadgeRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid && !player.IsHLTV)
                ApplyXpBadge(player);
        }
        return (HookResult)0;
    }

    private void OnXpBadgeDisconnect(int slot)
    {
        _xpBadgeLevels.Remove(slot);
        _xpBadgeLogged.Remove(slot);
    }

    private void ApplyXpBadge(CCSPlayerController player)
    {
        if (!_xpBadge.Enabled || !player.IsValid || player.IsHLTV || (!player.IsBot && !_xpBadge.ToHumans))
            return;

        try
        {
            var inventory = player.InventoryServices;
            if (inventory is null || inventory.Handle == nint.Zero)
                return;

            int level;
            if (!player.IsBot && _xpBadge.SelfXp > 0)
            {
                level = _xpBadge.SelfXp;
            }
            else if (!_xpBadgeLevels.TryGetValue(player.Slot, out level))
            {
                level = _xpBadge.Mode == "random"
                    ? Random.Shared.Next(_xpBadge.Min, _xpBadge.Max + 1)
                    : SpreadXpBadge(player.Slot, _xpBadge.Min, _xpBadge.Max);
                _xpBadgeLevels[player.Slot] = level;
            }

            // InventoryServices owns this field. The engine replicates it; do not
            // send a CCSPlayerController SetStateChanged for the child object.
            inventory.PersonaDataXpTrailLevel = level;

            if (_xpBadgeLogged.Add(player.Slot))
            {
                Logger.LogInformation("[MapRanks] XP badge applied: {Name} ({Kind}), level={Level}.",
                    player.PlayerName, player.IsBot ? "BOT" : "HUMAN", level);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MapRanks] XP badge could not be applied to {Name}.", player.PlayerName);
        }
    }

    private static int SpreadXpBadge(int slot, int min, int max)
    {
        int span = max - min + 1;
        if (span <= 1)
            return min;

        uint hash = unchecked((uint)slot * 2654435761u) & 0x7FFFFFFFu;
        hash ^= hash >> 13;
        return min + (int)(hash % (uint)span);
    }

    private void OnXpBadgeCommand(CCSPlayerController? player, CounterStrikeSharp.API.Modules.Commands.CommandInfo command)
    {
        command.ReplyToCommand(
            $"[MapRanks] XP badge={_xpBadge.Enabled}, range={_xpBadge.Min}-{_xpBadge.Max}, mode={_xpBadge.Mode}, humans={_xpBadge.ToHumans}, self={_xpBadge.SelfXp}. Rank fields are owned by MapRanks.");
    }
}
