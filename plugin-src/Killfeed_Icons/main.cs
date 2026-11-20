using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;

public partial class Plugin : BasePlugin, IPluginConfig<Config>
{
    // Keep the existing manager's path so its !weaponkill selection stays in sync.
    private const string SkinDataPath = "addons/counterstrikesharp/configs/plugins/KillfeedIcons/player_skins.json";

    private readonly Dictionary<int, int> _killStreak = new();
    private readonly Dictionary<ulong, string> _playerSkinPack = new();
    private readonly Dictionary<int, string> _botSkinPack = new();
    private readonly Random _random = new();

    public override string ModuleName => "Killfeed Icons";
    public override string ModuleVersion => "1.0.26";
    public override string ModuleAuthor => "exkludera, hanasuki futaba & XueRong";
    public override string ModuleDescription => "Killfeed icon packs with a fresh random bot pack each round.";

    public Config Config { get; set; } = new();
    public void OnConfigParsed(Config config) => Config = config ?? new Config();

    public override void Load(bool hotReload)
    {
        AddCommand("css_weaponkill", "选择击杀图标", OnWeaponKillCommand);
        AddCommand("weaponkill", "选择击杀图标", OnWeaponKillCommand);
        LoadPlayerSkinData();

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterListener<OnClientDisconnect>(OnClientDisconnect);
    }

    public override void Unload(bool hotReload)
    {
        SavePlayerSkinData();
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        DeregisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        DeregisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RemoveCommand("css_weaponkill", OnWeaponKillCommand);
        RemoveCommand("weaponkill", OnWeaponKillCommand);
        _killStreak.Clear();
        _botSkinPack.Clear();
    }

    private HookResult OnRoundEnd(EventRoundEnd gameEvent, GameEventInfo info)
    {
        _killStreak.Clear();
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart gameEvent, GameEventInfo info)
    {
        // Re-roll once per round. The previous version kept an existing pack forever.
        foreach (var player in Utilities.GetPlayers())
        {
            if (IsConnectedBot(player))
                AssignRandomBotPack(player);
        }
        _killStreak.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (player is not { IsValid: true })
            return HookResult.Continue;

        _killStreak.Remove(player.Slot);
        if (IsConnectedBot(player) && !_botSkinPack.ContainsKey(player.Slot))
            AssignRandomBotPack(player);

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int slot)
    {
        _killStreak.Remove(slot);
        _botSkinPack.Remove(slot);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath gameEvent, GameEventInfo info)
    {
        var attacker = gameEvent.Attacker;
        if (attacker is not { IsValid: true }
            || attacker.Connected != PlayerConnectedState.Connected
            || gameEvent.Userid?.Slot == attacker.Slot)
            return HookResult.Continue;

        int streak = _killStreak.GetValueOrDefault(attacker.Slot) + 1;
        _killStreak[attacker.Slot] = streak;

        string skinPackId;
        if (attacker.IsBot)
        {
            if (!_botSkinPack.TryGetValue(attacker.Slot, out skinPackId!) || string.IsNullOrWhiteSpace(skinPackId))
            {
                skinPackId = GetRandomSkinPack();
                if (!string.IsNullOrWhiteSpace(skinPackId))
                    _botSkinPack[attacker.Slot] = skinPackId;
            }
        }
        else
        {
            skinPackId = GetPlayerSkinPack(attacker);
        }

        if (string.IsNullOrWhiteSpace(skinPackId) || string.IsNullOrWhiteSpace(gameEvent.Weapon))
            return HookResult.Continue;

        string weaponBase = gameEvent.Weapon.StartsWith("weapon_", StringComparison.Ordinal)
            ? gameEvent.Weapon["weapon_".Length..]
            : gameEvent.Weapon;

        bool allowAce = Config.SkinPackAceSupport is null
            || !Config.SkinPackAceSupport.TryGetValue(skinPackId, out bool aceValue)
            || aceValue;
        gameEvent.Weapon = streak >= 5 && allowAce
            ? $"{weaponBase}_ace_{skinPackId}"
            : $"{weaponBase}_{skinPackId}";

        return HookResult.Continue;
    }

    private static bool IsConnectedBot(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: true, IsHLTV: false }
        && player.Connected == PlayerConnectedState.Connected;

    private void AssignRandomBotPack(CCSPlayerController player)
    {
        var pack = GetRandomSkinPack();
        if (string.IsNullOrWhiteSpace(pack))
            _botSkinPack.Remove(player.Slot);
        else
            _botSkinPack[player.Slot] = pack;
    }

    private string GetRandomSkinPack()
    {
        var packs = (Config.AvailableSkinPacks ?? [])
            .Where(pack => !string.IsNullOrWhiteSpace(pack))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return packs.Length == 0 ? string.Empty : packs[_random.Next(packs.Length)];
    }

    private string GetPlayerSkinPack(CCSPlayerController player) =>
        _playerSkinPack.GetValueOrDefault(player.SteamID) ?? string.Empty;

    private void SetPlayerSkinPack(CCSPlayerController player, string packId)
    {
        string newPack = packId == "default" ? string.Empty : packId;
        _playerSkinPack[player.SteamID] = newPack;
        SavePlayerSkinData();
        player.PrintToChat($"击杀图标已切换为: {(newPack.Length == 0 ? "默认" : newPack)}");
    }

    private void OnWeaponKillCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true })
            return;

        var menu = new CenterHtmlMenu("选择击杀图标", this);
        menu.AddMenuOption("默认 (官方)", (p, option) => SetPlayerSkinPack(p, "default"));
        foreach (var pack in Config.AvailableSkinPacks ?? [])
        {
            var packId = pack;
            menu.AddMenuOption(packId, (p, option) => SetPlayerSkinPack(p, packId));
        }
        MenuManager.OpenCenterHtmlMenu(this, player, menu);
    }

    private void LoadPlayerSkinData()
    {
        string root = Server.GameDirectory;
        string path = Path.Combine(root, SkinDataPath);
        if (!File.Exists(path))
            return;

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<ulong, string>>(File.ReadAllText(path));
            _playerSkinPack.Clear();
            if (data is null)
                return;
            foreach (var (steamId, pack) in data)
                _playerSkinPack[steamId] = pack ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read saved player killfeed icon choices.");
        }
    }

    private void SavePlayerSkinData()
    {
        try
        {
            string fullPath = Path.Combine(Server.GameDirectory, SkinDataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(
                _playerSkinPack,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not save player killfeed icon choices.");
        }
    }
}
