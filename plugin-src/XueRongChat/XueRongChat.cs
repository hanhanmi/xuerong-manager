/*---------------------------------------------------------------------------------------------
 * XueRongChat v2.0.0 — FACEIT-style match presentation for local CS2 servers.
 * CounterStrikeSharp 1.0.378 / net10.0. Pure PrintToChat + PrintToCenterHtml, no netvars.
 *--------------------------------------------------------------------------------------------*/

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace XueRongChat;

public sealed class ChatConfig : BasePluginConfig
{
    [JsonPropertyName("platform_name")] public string PlatformName { get; set; } = "FACEIT";

    [JsonPropertyName("enable_chat_format")] public bool EnableChatFormat { get; set; } = true;
    [JsonPropertyName("show_level")] public bool ShowLevel { get; set; } = true;
    [JsonPropertyName("level_min")] public int LevelMin { get; set; } = 7;
    [JsonPropertyName("level_max")] public int LevelMax { get; set; } = 10;
    [JsonPropertyName("show_team_tag")] public bool ShowTeamTag { get; set; } = true;
    [JsonPropertyName("chat_prefix")] public bool ChatPrefix { get; set; } = true;

    [JsonPropertyName("enable_chat_banner")] public bool EnableChatBanner { get; set; } = true;


    [JsonPropertyName("enable_welcome")] public bool EnableWelcome { get; set; } = true;
    [JsonPropertyName("welcome_delay")] public float WelcomeDelay { get; set; } = 3.0f;
    [JsonPropertyName("welcome_lines")] public List<string> WelcomeLines { get; set; } = new()
    {
        "Welcome to the match, {name}.",
        "Match ID: {matchid}  |  Region: EU  |  Server: {server}",
        "FACEIT Anti-Cheat is active. This match is being recorded.",
        "Type !help for match commands. GLHF.",
    };

    [JsonPropertyName("enable_match_end")] public bool EnableMatchEnd { get; set; } = true;
    [JsonPropertyName("match_end_lines")] public List<string> MatchEndLines { get; set; } = new()
    {
        "MATCH FINISHED  |  {score}",
        "ELO {elo_delta}  |  {elo} ELO  |  Level {level}",
        "GG WP. Match results are available on your FACEIT profile.",
    };

    [JsonPropertyName("enable_round_info")] public bool EnableRoundInfo { get; set; } = true;

    [JsonPropertyName("enable_multikill")] public bool EnableMultiKill { get; set; } = true;
    [JsonPropertyName("enable_headshot_tag")] public bool EnableHeadshotTag { get; set; } = true;

    [JsonPropertyName("enable_clutch")] public bool EnableClutch { get; set; } = true;
    [JsonPropertyName("clutch_delay")] public float ClutchDelay { get; set; } = 2.0f;

    [JsonPropertyName("enable_periodic_notice")] public bool EnablePeriodicNotice { get; set; } = true;
    [JsonPropertyName("notice_every_rounds")] public int NoticeEveryRounds { get; set; } = 6;
    [JsonPropertyName("notice_line")] public string NoticeLine { get; set; } = "FACEIT Anti-Cheat active  |  Match recorded  |  Play fair.";

    [JsonPropertyName("enable_elo_persist")] public bool EnableEloPersist { get; set; } = true;
    [JsonPropertyName("elo_start")] public int EloStart { get; set; } = 1842;
    [JsonPropertyName("elo_gain")] public int EloGain { get; set; } = 24;
    [JsonPropertyName("elo_loss")] public int EloLoss { get; set; } = 25;
    [JsonPropertyName("min_players")] public int MinPlayers { get; set; } = 1;
}

public sealed class XueRongChat : BasePlugin, IPluginConfig<ChatConfig>
{
    public override string ModuleName => "XueRongChat";
    public override string ModuleVersion => "2.6.1";
    public override string ModuleAuthor => "XueRong";
    public override string ModuleDescription => "FACEIT-style match chat, banners and match presentation";

    public ChatConfig Config { get; set; } = new();
    public void OnConfigParsed(ChatConfig config) => Config = config;

    private readonly Dictionary<int, int> _killStreak = new();
    private readonly Dictionary<ulong, int> _levels = new();
    private readonly Dictionary<ulong, int> _elo = new();
    private int _roundNumber;   // 只作 fallback，真实回合数从 GameRules 读
    private bool _matchEnded;
    private string _matchId = string.Empty;
    private string _currentMap = string.Empty;

    private string P => Config.PlatformName;

    public override void Load(bool hotReload)
    {
        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSayTeam, HookMode.Pre);

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchWinPanel, HookMode.Post);
        RegisterEventHandler<EventGameEnd>(OnGameEnd, HookMode.Post);

        _matchId = "1-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        LoadElo();
        Server.PrintToConsole($"[XueRongChat] {P} match presentation loaded (v2.3.0, elo persisted={Config.EnableEloPersist})");
    }

    // ── 聊天格式化 ───────────────────────────────────────────────
    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
        => FormatSay(player, info, teamOnly: false);

    private HookResult OnSayTeam(CCSPlayerController? player, CommandInfo info)
        => FormatSay(player, info, teamOnly: true);


    private HookResult FormatSay(CCSPlayerController? player, CommandInfo info, bool teamOnly)
    {
        if (!Config.EnableChatFormat || player == null || !player.IsValid)
            return HookResult.Continue;

        string text = CleanSayText(info.ArgString);
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('!') || text.StartsWith('/'))
            return HookResult.Continue;

        string line = BuildChatLine(player, text, teamOnly);
        if (!teamOnly)
        {
            Server.PrintToChatAll(line);
        }
        else
        {
            foreach (CCSPlayerController p in Utilities.GetPlayers())
                if (p != null && p.IsValid && !p.IsBot && p.Team == player.Team)
                    p.PrintToChat(line);
        }
        return HookResult.Stop;
    }

    private string BuildChatLine(CCSPlayerController player, string text, bool teamOnly)
    {
        var sb = new StringBuilder();
        if (Config.ChatPrefix)
            sb.Append($"{ChatColors.Orange}[{P}]{ChatColors.Default} ");
        if (Config.ShowLevel)
        {
            int lvl = LevelOf(player);
            sb.Append($"{LevelColor(lvl)}LVL {lvl}{ChatColors.Default} ");
        }
        if (teamOnly && Config.ShowTeamTag)
            sb.Append($"{ChatColors.Grey}[TEAM]{ChatColors.Default} ");
        else if (Config.ShowTeamTag)
            sb.Append($"{ChatColors.Grey}{TeamTag(player)}{ChatColors.Default} ");

        sb.Append($"{ChatColors.ForPlayer(player)}{player.PlayerName}{ChatColors.Default}"
                + $"{ChatColors.Grey}:{ChatColors.Default} {ChatColors.White}{text}");
        return sb.ToString();
    }

    private static string TeamTag(CCSPlayerController p) =>
        p.Team == CsTeam.CounterTerrorist ? "[CT]" : "[T]";

    private static char LevelColor(int lvl) => lvl switch
    {
        <= 3 => ChatColors.Silver,
        <= 6 => ChatColors.LightBlue,
        <= 9 => ChatColors.LightPurple,
        _ => ChatColors.Orange,
    };

    private int LevelOf(CCSPlayerController player)
    {
        ulong key = player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1000000);
        if (_levels.TryGetValue(key, out int cached)) return cached;
        int lo = Math.Clamp(Config.LevelMin, 1, 10);
        int hi = Math.Clamp(Config.LevelMax, lo, 10);
        int lvl = lo + (int)(key % (ulong)(hi - lo + 1));
        _levels[key] = lvl;
        return lvl;
    }

    private int EloOf(CCSPlayerController player)
    {
        ulong key = player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1000000);
        return _elo.TryGetValue(key, out int v) ? v : Config.EloStart;
    }

    private static string CleanSayText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }

    // ── 横幅 ────────────────────────────────────────────────────
    private void ChatBanner(params string[] lines)
    {
        if (!Config.EnableChatBanner) return;
        Server.PrintToChatAll($"{ChatColors.Orange}━━━━━━━━━━━━━━  {ChatColors.Default}{ChatColors.Gold}{P}"
                            + $"{ChatColors.Default}{ChatColors.Orange}  ━━━━━━━━━━━━━━{ChatColors.Default}");
        foreach (string l in lines)
            Server.PrintToChatAll($"{ChatColors.Orange}  {ChatColors.Default}{ChatColors.White}{l}{ChatColors.Default}");
        Server.PrintToChatAll($"{ChatColors.Orange}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{ChatColors.Default}");
    }

    private void Sys(string text) =>
        Server.PrintToChatAll($"{ChatColors.Orange}[{P}]{ChatColors.Default} {ChatColors.White}{text}{ChatColors.Default}");


    // ── 进服欢迎 ────────────────────────────────────────────────
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        if (!Config.EnableWelcome) return HookResult.Continue;
        CCSPlayerController? player = ev.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        AddTimer(Config.WelcomeDelay, () =>
        {
            if (!player.IsValid) return;
            foreach (string raw in Config.WelcomeLines)
            {
                string line = raw.Replace("{name}", player.PlayerName)
                                 .Replace("{matchid}", _matchId)
                                 .Replace("{server}", _currentMap.Length > 0 ? _currentMap : "EU West #1")
                                 .Replace("{level}", LevelOf(player).ToString())
                                 .Replace("{elo}", EloOf(player).ToString());
                player.PrintToChat($"{ChatColors.Orange}[{P}]{ChatColors.Default} {ChatColors.White}{line}{ChatColors.Default}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    /// <summary>从引擎的 cs_team_manager 读真实比分，绝不自己累加（半场换边/热身/插件重载都会算错）。</summary>
    private static (int T, int CT) ReadScore()
    {
        int t = 0, ct = 0;
        try
        {
            foreach (CCSTeam team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
            {
                if (team == null || !team.IsValid) continue;
                int tn = team.TeamNum;
                if (tn == 2) t = team.Score;
                else if (tn == 3) ct = team.Score;
            }
        }
        catch { /* schema 变动时退回 0:0，不抛异常 */ }
        return (t, ct);
    }

    /// <summary>从 GameRules 读已打完回合数，+1 即当前回合号。</summary>
    private int ReadRoundNumber()
    {
        try
        {
            foreach (CCSGameRulesProxy proxy in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
            {
                CCSGameRules? rules = proxy?.GameRules;
                if (rules != null) return Math.Max(1, rules.TotalRoundsPlayed + 1);
            }
        }
        catch { }
        return _roundNumber;
    }

    // ── 回合 ────────────────────────────────────────────────────
    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        _killStreak.Clear();
        ResetIfMapChanged();
        if (_matchEnded) return HookResult.Continue;

        _roundNumber = ReadRoundNumber();
        var (t, ct) = ReadScore();

        if (Config.EnableRoundInfo && HumanCount() >= Config.MinPlayers)
            Sys($"ROUND {_roundNumber}  ·  {ChatColors.LightRed}T {t}{ChatColors.Default}"
                + $"{ChatColors.Grey} : {ChatColors.Default}{ChatColors.LightBlue}{ct} CT{ChatColors.Default}"
                + $"{ChatColors.Grey}  ·  LIVE{ChatColors.Default}");

        if (Config.EnablePeriodicNotice && Config.NoticeEveryRounds > 0 &&
            _roundNumber % Config.NoticeEveryRounds == 0)
            Sys(Config.NoticeLine);

        if (Config.EnableClutch)
            AddTimer(Config.ClutchDelay, CheckClutch, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void ResetIfMapChanged()
    {
        string map = Server.MapName ?? string.Empty;
        if (map.Length == 0 || map == _currentMap) return;
        _currentMap = map;
        _roundNumber = 0;
        _matchEnded = false;
        _matchId = "1-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }

    private void CheckClutch()
    {
        int tAlive = 0, ctAlive = 0;
        string last = "";
        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid) continue;
            CBasePlayerPawn? pawn = p.Pawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;
            if (p.Team == CsTeam.Terrorist) { tAlive++; last = p.PlayerName; }
            else if (p.Team == CsTeam.CounterTerrorist) { ctAlive++; last = p.PlayerName; }
        }
        if (tAlive == 1 && ctAlive >= 2)
            Sys($"CLUTCH  ·  {ChatColors.LightRed}T{ChatColors.Default} 1v{ctAlive}  ·  {last}");
        else if (ctAlive == 1 && tAlive >= 2)
            Sys($"CLUTCH  ·  {ChatColors.LightBlue}CT{ChatColors.Default} 1v{tAlive}  ·  {last}");
    }

    private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        if (ev.Winner is not (2 or 3)) return HookResult.Continue;
        // round_end 时引擎可能还没写比分，延迟一小段再读，保证播报与记分板一致
        int winner = ev.Winner;
        AddTimer(0.4f, () =>
        {
            var (t, ct) = ReadScore();
            if (Config.EnableRoundInfo)
            {
                string w = winner == 2 ? "TERRORISTS WIN" : "COUNTER-TERRORISTS WIN";
                Sys($"{w}  ·  {ChatColors.LightRed}T {t}{ChatColors.Default}"
                    + $"{ChatColors.Grey} : {ChatColors.Default}{ChatColors.LightBlue}{ct} CT{ChatColors.Default}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    // ── 连杀 ────────────────────────────────────────────────────
    private HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!Config.EnableMultiKill) return HookResult.Continue;
        CCSPlayerController? killer = ev.Attacker, victim = ev.Userid;
        if (killer == null || victim == null || !killer.IsValid || killer.Slot == victim.Slot)
            return HookResult.Continue;

        int slot = killer.Slot;
        int streak = _killStreak.TryGetValue(slot, out int n) ? n + 1 : 1;
        _killStreak[slot] = streak;

        string? label = streak switch { 2 => "DOUBLE KILL", 3 => "TRIPLE KILL", 4 => "QUADRA KILL", 5 => "ACE", _ => null };
        if (label == null && (streak < 6 || streak % 5 != 0)) return HookResult.Continue;
        label ??= $"{streak} KILL STREAK";

        string hs = (Config.EnableHeadshotTag && ev.Headshot) ? "  ·  HEADSHOT" : "";
        Sys($"{ChatColors.Gold}{label}{ChatColors.Default}  ·  {killer.PlayerName}{hs}");
        // 连杀只在聊天里播报，不放中心横幅（挡视野）
        return HookResult.Continue;
    }

    // ── 比赛结束 ────────────────────────────────────────────────
    private HookResult OnMatchWinPanel(EventCsWinPanelMatch ev, GameEventInfo info)
    {
        FinishMatch();
        return HookResult.Continue;
    }

    private HookResult OnGameEnd(EventGameEnd ev, GameEventInfo info)
    {
        FinishMatch();
        return HookResult.Continue;
    }

    private void FinishMatch()
    {
        if (!Config.EnableMatchEnd || _matchEnded) return;
        _matchEnded = true;
        var (finalT, finalCt) = ReadScore();
        string score = $"{finalT} : {finalCt}";

        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot) continue;
            bool won = (p.Team == CsTeam.Terrorist && finalT > finalCt)
                    || (p.Team == CsTeam.CounterTerrorist && finalCt > finalT);
            int delta = won ? Config.EloGain : -Config.EloLoss;
            ulong key = p.SteamID != 0 ? p.SteamID : (ulong)(p.Slot + 1000000);
            int elo = Math.Max(0, EloOf(p) + delta);
            _elo[key] = elo;

            foreach (string raw in Config.MatchEndLines)
            {
                string line = raw.Replace("{score}", score)
                                 .Replace("{elo_delta}", (delta >= 0 ? "+" : "") + delta)
                                 .Replace("{elo}", elo.ToString())
                                 .Replace("{level}", LevelOf(p).ToString())
                                 .Replace("{name}", p.PlayerName)
                                 .Replace("{matchid}", _matchId);
                p.PrintToChat($"{ChatColors.Orange}[{P}]{ChatColors.Default} {ChatColors.White}{line}{ChatColors.Default}");
            }
        }

        SaveElo();
        ChatBanner($"MATCH FINISHED   ·   {score}", "GG WP  ·  Results are available on your profile");
    }

    // 雪绒定制：ELO 落盘，重开游戏不重置
    private string EloStorePath => Path.Combine(ModuleDirectory, "elo.json");

    private void LoadElo()
    {
        if (!Config.EnableEloPersist) return;
        try
        {
            string path = EloStorePath;
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
            if (saved == null) return;
            foreach (var kv in saved)
                if (ulong.TryParse(kv.Key, out ulong id)) _elo[id] = kv.Value;
        }
        catch { /* 存档损坏就按起始 ELO 走 */ }
    }

    private void SaveElo()
    {
        if (!Config.EnableEloPersist) return;
        try
        {
            var data = _elo.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            File.WriteAllText(EloStorePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private int HumanCount()
    {
        int n = 0;
        foreach (CCSPlayerController p in Utilities.GetPlayers())
            if (p != null && p.IsValid && !p.IsBot) n++;
        return n;
    }

    // ── 控制台命令 ──────────────────────────────────────────────

    [ConsoleCommand("css_faceit_status", "Show match presentation status")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var (st, sct) = ReadScore();
        command.ReplyToCommand($"[{P}] match={_matchId} map={_currentMap} round={ReadRoundNumber()} score=T {st} : {sct} CT");
        command.ReplyToCommand($"[{P}] chat_format={Config.EnableChatFormat} level={Config.ShowLevel}");
    }
}
