using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace MvpMusicLite;

public sealed class LiteConfig : BasePluginConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>音效包根目录（相对 game/csgo）。</summary>
    public string PacksDirectory { get; set; } = "sounds/mymvp/packs";

    /// <summary>包内文件名。</summary>
    public string SoundFileName { get; set; } = "mvpanthem.vsnd";

    /// <summary>兜底包（只有 UnboundMvp=default 时才用）。</summary>
    public string DefaultPack { get; set; } = "default";

    /// <summary>SteamID64 -> 包名。谁拿 MVP 就放谁的包。</summary>
    public Dictionary<string, string> Packs { get; set; } = new();

    /// <summary>可选：写死的完整音效路径（优先级低于 Packs）。</summary>
    public string FallbackSound { get; set; } = "";

    /// <summary>MVP 没绑定包时：silent 不放 / default 放 DefaultPack。</summary>
    public string UnboundMvp { get; set; } = "silent";

    /// <summary>人机拿 MVP 是否也放（默认 false）。</summary>
    public bool IncludeBots { get; set; } = false;

    /// <summary>非空时只有名单内的玩家能听到。</summary>
    public string[] OnlyForSteamIds { get; set; } = Array.Empty<string>();

    // ---------------- 音量 ----------------

    /// <summary>没设置过的玩家的默认音量（0~1）。</summary>
    public float DefaultVolume { get; set; } = 1.0f;

    /// <summary>是否允许玩家用 !mvpvol 自己调。</summary>
    public bool AllowPlayersToAdjust { get; set; } = true;

    /// <summary>播完之后是否把 snd_toolvolume 恢复成 1（避免影响别的插件用 play）。</summary>
    public bool RestoreToolVolume { get; set; } = true;

    /// <summary>多久后恢复 snd_toolvolume。</summary>
    public float ToolVolumeRestoreSeconds { get; set; } = 12f;

    // ---------------- 其它 ----------------

    public bool MuteVanillaOnMvp { get; set; } = false;
    public float MuteRestoreSeconds { get; set; } = 9f;
    public float RestoreVolume { get; set; } = 1.0f;
}

public sealed class MvpMusicLitePlugin : BasePlugin, IPluginConfig<LiteConfig>
{
    public override string ModuleName => "MVP Music Lite";
    public override string ModuleVersion => "1.3.4";
    public override string ModuleAuthor => "local";
    public override string ModuleDescription => "只在回合 MVP 时播放该 MVP 本人的自定义音乐，支持玩家在游戏内调音量。";

    public LiteConfig Config { get; set; } = new();

    private readonly Dictionary<int, int> _muteTokens = new();
    private readonly Dictionary<int, int> _volTokens = new();
    private Dictionary<string, float> _volumes = new();

    private string VolumeFile => Path.Combine(ModuleDirectory, "volumes.json");

    private string ConfigFile =>
        Path.Combine(CsgoDir(), "addons", "counterstrikesharp", "configs", "plugins", "MVP-Music-Lite", "MVP-Music-Lite.json");

    private DateTime _cfgStamp = DateTime.MinValue;

    private void Info(string m)
    {
        Logger.LogInformation(m);
        Server.PrintToConsole("[MVP Music Lite] " + m);
    }

    // ------------------------------------------------------------------ 生命周期

    public void OnConfigParsed(LiteConfig config)
    {
        Config = config ?? new LiteConfig();
        Normalize();
        LoadVolumes();
        var ids = string.Join(", ", Config.Packs.Select(kv => kv.Key + "->" + kv.Value));
        Info($"loaded v1.3.3 Enabled={Config.Enabled} UnboundMvp={Config.UnboundMvp} IncludeBots={Config.IncludeBots} DefaultVolume={Config.DefaultVolume:0.##} Packs={(ids.Length == 0 ? "(空)" : ids)}");
        foreach (var kv in Config.Packs)
        {
            var p = ResolveForMvp(Config, kv.Key, false);
            Info("[check] " + kv.Key + " -> " + kv.Value + " " + (FileExistsOnDisk(CsgoDir(), p) ? "OK" : "缺文件(会没声音)"));
        }
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        RegisterEventHandler<EventPlayerConnectFull>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } p) Info("vol: " + p.PlayerName + " = " + VolumeOf(SteamIdOf(p)).ToString("0.##", CultureInfo.InvariantCulture));
            return HookResult.Continue;
        });

        AddCommand("css_mvptest", "只给自己试播一次（用你当前的音量）", OnTestCommand);
        AddCommand("css_mvpwhoami", "显示自己的 SteamID64 和对应音效包", OnWhoAmICommand);
        AddCommand("css_mvpsound", "打印你当 MVP 时会放的音效路径", OnSoundCommand);
        AddCommand("css_mvpvol", "查看/设置 MVP 音乐音量：!mvpvol 0-100 | on | off | reset", OnVolumeCommand);
        AddCommand("css_mvppacks", "列出所有音效包（更新时间 / 大小 / 绑定 SteamID）", OnPacksCommand);
        AddCommand("css_mvpreload", "重新读取配置（改完 Packs 不用重启 CS2）", OnReloadConfigCommand);

        // 配置自动重载：文件被改（管理器/记事本）后几秒内自动生效，不用重启 CS2
        try { _cfgStamp = File.GetLastWriteTimeUtc(ConfigFile); } catch { }
        AddTimer(5.0f, WatchConfigFile, TimerFlags.REPEAT);
        Info("ready (音量命令: !mvpvol 0-100)");
    }

    // ------------------------------------------------------------------ 核心

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (!Config.Enabled) return HookResult.Continue;

        var mvp = @event.Userid;
        if (mvp is not { IsValid: true }) return HookResult.Continue;

        var mvpId = SteamIdOf(mvp);
        var isBot = mvp.IsBot;
        Info("round_mvp: '" + mvp.PlayerName + "' steamid=" + mvpId + " bot=" + isBot);

        // 用【MVP 本人】的 SteamID 决定放谁的歌，全场统一放这一首
        var path = ResolveForMvp(Config, mvpId, isBot);
        if (string.IsNullOrEmpty(path))
        {
            Info(isBot && !Config.IncludeBots
                ? "  -> 跳过：人机拿 MVP（IncludeBots=false）"
                : "  -> 跳过：该 MVP 未绑定音效包（UnboundMvp=silent）"
                  + "   [如果是刚加的绑定，用 css_mvpreload 或重启 CS2 让配置生效]");
            return HookResult.Continue;
        }

        if (!FileExistsOnDisk(CsgoDir(), path))
            Info("  !! 警告：服务端缺少文件 " + path + ".vsnd_c");

        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsEligible(p)) continue;
            var vol = VolumeOf(SteamIdOf(p));
            if (vol <= 0.001f) { Info("  -> " + p.PlayerName + " 音量为 0，跳过"); continue; }
            if (Config.MuteVanillaOnMvp) MuteVolume(p);
            Play(p, path!, vol);
            var vtxt = vol.ToString("0.##", CultureInfo.InvariantCulture);
            var vwarn = vol < 0.2f ? "   <== 音量只有 " + (int)Math.Round(vol * 100) + "%，他几乎听不到" : "";
            Info("  -> " + p.PlayerName + " vol=" + vtxt + " play \"" + path + "\"" + vwarn);
        }
        return HookResult.Continue;
    }

    /// <summary>CS2 里 playvol 已经失效；play 的音量由客户端 snd_toolvolume 决定。</summary>
    private void Play(CCSPlayerController p, string path, float vol)
    {
        p.ExecuteClientCommand("snd_toolvolume " + Fmt(vol));
        p.ExecuteClientCommand("play \"" + path + "\"");

        if (!Config.RestoreToolVolume || Config.ToolVolumeRestoreSeconds <= 0) return;

        var slot = p.Slot;
        var token = (_volTokens.TryGetValue(slot, out var t) ? t : 0) + 1;
        _volTokens[slot] = token;
        AddTimer(Config.ToolVolumeRestoreSeconds, () =>
        {
            if (_volTokens.TryGetValue(slot, out var cur) && cur == token)
            {
                var q = Utilities.GetPlayerFromSlot(slot);
                if (q is { IsValid: true }) q.ExecuteClientCommand("snd_toolvolume 1");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static string Fmt(float v) => Math.Clamp(v, 0f, 1f).ToString("0.##", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ 音量存储

    private void LoadVolumes()
    {
        try
        {
            if (File.Exists(VolumeFile))
            {
                var txt = File.ReadAllText(VolumeFile);
                _volumes = JsonSerializer.Deserialize<Dictionary<string, float>>(txt) ?? new();
                Info("已载入 " + _volumes.Count + " 个玩家的音量设置");
            }
        }
        catch (Exception ex)
        {
            Info("volumes.json 读取失败（忽略）: " + ex.Message);
            _volumes = new();
        }
    }

    private void SaveVolumes()
    {
        try
        {
            File.WriteAllText(VolumeFile, JsonSerializer.Serialize(_volumes,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Info("volumes.json 写入失败: " + ex.Message);
        }
    }

    private float VolumeOf(string steamId)
        => _volumes.TryGetValue(steamId, out var v) ? Math.Clamp(v, 0f, 1f) : Math.Clamp(Config.DefaultVolume, 0f, 1f);

    // ------------------------------------------------------------------ 纯函数

    /// <summary>给定这一回合 MVP 的 SteamID，算出要播的音效路径（不含 .vsnd_c）。null = 不放。</summary>
    public static string? ResolveForMvp(LiteConfig? cfg, string? mvpSteamId, bool mvpIsBot)
    {
        if (cfg == null) return null;
        if (mvpIsBot && !cfg.IncludeBots) return null;

        var file = string.IsNullOrWhiteSpace(cfg.SoundFileName) ? "mvpanthem.vsnd" : cfg.SoundFileName.Trim();

        if (cfg.Packs != null && !string.IsNullOrWhiteSpace(mvpSteamId)
            && cfg.Packs.TryGetValue(mvpSteamId!, out var pack) && !string.IsNullOrWhiteSpace(pack))
        {
            return Join(cfg.PacksDirectory, pack, file);
        }

        if (!string.IsNullOrWhiteSpace(cfg.FallbackSound))
            return cfg.FallbackSound.Trim();

        if (string.Equals(cfg.UnboundMvp, "default", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(cfg.DefaultPack))
        {
            return Join(cfg.PacksDirectory, cfg.DefaultPack, file);
        }

        return null;
    }

    /// <summary>把 "60" / "0.6" / "on" / "off" / "reset" 解析成音量；失败返回 null。</summary>
    public static float? ParseVolume(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant().TrimEnd('%');
        switch (s)
        {
            case "on": return 1.0f;
            case "off":
            case "0": return 0f;
            case "reset":
            case "default": return -1f;   // 特殊值：删掉个人设置
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
        if (d > 1.0) d /= 100.0;
        return (float)Math.Clamp(d, 0.0, 1.0);
    }

    private static string Join(string dir, string pack, string file)
        => dir.Trim().Trim('/').TrimEnd('/') + "/" + pack.Trim().Trim('/') + "/" + file.Trim().TrimStart('/');

    private void Normalize()
    {
        if (Config.Packs == null) Config.Packs = new();
        var mode = (Config.UnboundMvp ?? "silent").Trim().ToLowerInvariant();
        Config.UnboundMvp = mode == "default" ? "default" : "silent";
        if (Config.MuteRestoreSeconds < 0) Config.MuteRestoreSeconds = 0;
        if (Config.ToolVolumeRestoreSeconds < 0) Config.ToolVolumeRestoreSeconds = 0;
        Config.DefaultVolume = (float)Math.Clamp(Config.DefaultVolume, 0.0, 1.0);
    }

    private string CsgoDir() => Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", ".."));

    private static bool FileExistsOnDisk(string csgoDir, string? soundPath)
    {
        if (string.IsNullOrEmpty(soundPath)) return false;
        try
        {
            var p = Path.Combine(csgoDir, soundPath!.Replace('/', Path.DirectorySeparatorChar) + "_c");
            return File.Exists(p);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ 资格

    private bool IsEligible(CCSPlayerController p)
    {
        if (p == null || !p.IsValid || p.IsHLTV) return false;
        if (p.IsBot) return Config.IncludeBots;
        var id = SteamIdOf(p);
        if (id == "0") return false;
        if (Config.OnlyForSteamIds is { Length: > 0 } && !Config.OnlyForSteamIds.Contains(id)) return false;
        return true;
    }

    private static string SteamIdOf(CCSPlayerController p) => p.SteamID.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ 可选静音

    private void MuteVolume(CCSPlayerController p)
    {
        p.ExecuteClientCommand("snd_musicvolume 0");
        var secs = Config.MuteRestoreSeconds;
        if (secs <= 0) return;
        var slot = p.Slot;
        var token = (_muteTokens.TryGetValue(slot, out var t) ? t : 0) + 1;
        _muteTokens[slot] = token;
        AddTimer(secs, () =>
        {
            if (_muteTokens.TryGetValue(slot, out var cur) && cur == token)
            {
                var q = Utilities.GetPlayerFromSlot(slot);
                if (q is { IsValid: true })
                    q.ExecuteClientCommand("snd_musicvolume " + Fmt(Config.RestoreVolume));
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ------------------------------------------------------------------ 命令

    private void Say(CCSPlayerController? caller, CommandInfo info, string msg)
    {
        info.ReplyToCommand(msg);
        try { caller?.PrintToChat(msg); } catch { }
    }

    private void OnVolumeCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true }) { info.ReplyToCommand("[MVP Music Lite] 请在游戏内使用。"); return; }

        var id = SteamIdOf(caller);
        var cur = VolumeOf(id);
        var arg = info.ArgCount >= 2 ? info.GetArg(1) : null;

        if (string.IsNullOrWhiteSpace(arg))
        {
            var lowTip = cur < 0.2f ? "   ！低于 20% 基本听不到，打 !mvpvol 100 恢复" : "";
            Say(caller, info, "[MVP Music Lite] MVP 音乐音量 = " + (int)Math.Round(cur * 100) + "%" + lowTip +
                             "   （!mvpvol 0-100 调整 / off 关闭 / on 100% / reset 恢复默认）");
            return;
        }

        if (!Config.AllowPlayersToAdjust)
        {
            Say(caller, info, "[MVP Music Lite] 服务器已关闭玩家自行调整音量。");
            return;
        }

        var parsed = ParseVolume(arg);
        if (parsed == null)
        {
            Say(caller, info, "[MVP Music Lite] 看不懂 \"" + arg + "\"，用法：!mvpvol 0-100 | on | off | reset");
            return;
        }

        if (parsed.Value < 0f)
        {
            _volumes.Remove(id);
            SaveVolumes();
            Say(caller, info, "[MVP Music Lite] 已恢复默认音量 " + (int)Math.Round(Config.DefaultVolume * 100) + "%");
            return;
        }

        var v = parsed.Value;
        if (v <= 0.001f)
        {
            _volumes[id] = 0f;
            SaveVolumes();
            Say(caller, info, "[MVP Music Lite] MVP 音乐已关闭（!mvpvol 100 打开）");
            return;
        }

        _volumes[id] = v;
        SaveVolumes();
        var setTip = v < 0.2f ? "   ！=" + (int)Math.Round(v * 100) + "% 时基本听不到，确定要这么低？" : "";
        Say(caller, info, "[MVP Music Lite] MVP 音乐音量 = " + (int)Math.Round(v * 100) + "%" + setTip + "   （!mvptest 试听）");
    }

    private void OnTestCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true }) { info.ReplyToCommand("[MVP Music Lite] 请在游戏内使用。"); return; }

        var path = ResolveForMvp(Config, SteamIdOf(caller), false);
        if (string.IsNullOrEmpty(path))
        {
            info.ReplyToCommand("[MVP Music Lite] 你没有绑定音效包（配置 Packs 里没有你的 SteamID）。");
            return;
        }
        var vol = VolumeOf(SteamIdOf(caller));
        if (vol <= 0.001f) { Say(caller, info, "[MVP Music Lite] 你的音量是 0，先 !mvpvol 100"); return; }

        Play(caller, path!, vol);
        Say(caller, info, "[MVP Music Lite] 试听 vol=" + (int)Math.Round(vol * 100) + "%  " + path);
    }

    private void OnWhoAmICommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true }) { info.ReplyToCommand("[MVP Music Lite] 请在游戏内使用。"); return; }
        var id = SteamIdOf(caller);
        var pack = Config.Packs.TryGetValue(id, out var p) ? p : "(未绑定 -> 你拿 MVP 时不会放歌)";
        Say(caller, info, "[MVP Music Lite] steamid=" + id + "  pack=" + pack +
                         "  vol=" + (int)Math.Round(VolumeOf(id) * 100) + "%");
    }

    /// <summary>从磁盘重读配置；成功返回绑定摘要，失败抛异常。</summary>
    private string ReloadConfigCore()
    {
        var cfgPath = ConfigFile;
        if (!File.Exists(cfgPath)) throw new FileNotFoundException("找不到配置: " + cfgPath);

        var json = File.ReadAllText(cfgPath);
        var fresh = JsonSerializer.Deserialize<LiteConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (fresh == null) throw new InvalidDataException("配置解析为空");

        Config = fresh;
        Normalize();
        LoadVolumes();

        try { _cfgStamp = File.GetLastWriteTimeUtc(cfgPath); } catch { }

        var ids = string.Join(", ", Config.Packs.Select(kv => kv.Key + "->" + kv.Value));
        Info("reload: Packs=(" + (ids.Length == 0 ? "空" : ids) + ") DefaultPack=" + Config.DefaultPack
             + " UnboundMvp=" + Config.UnboundMvp + " DefaultVolume=" + Config.DefaultVolume.ToString("0.##", CultureInfo.InvariantCulture));
        foreach (var kv in Config.Packs)
        {
            var p = ResolveForMvp(Config, kv.Key, false);
            Info("[check] " + kv.Key + " -> " + kv.Value + " " + (FileExistsOnDisk(CsgoDir(), p) ? "OK" : "缺文件(会没声音)"));
        }
        return ids;
    }

    /// <summary>每 5 秒瞄一眼配置文件有没有被改，改了自动重载。</summary>
    private void WatchConfigFile()
    {
        try
        {
            var p = ConfigFile;
            if (!File.Exists(p)) return;
            var st = File.GetLastWriteTimeUtc(p);
            if (st == _cfgStamp) return;
            _cfgStamp = st;
            var ids = ReloadConfigCore();
            Info("配置自动重载（检测到文件变化）: " + Config.Packs.Count + " 个绑定  " + ids);
        }
        catch (Exception ex)
        {
            Info("配置自动重载失败（保持旧配置）: " + ex.Message);
        }
    }

    private void OnReloadConfigCommand(CCSPlayerController? caller, CommandInfo info)
    {
        try
        {
            var ids = ReloadConfigCore();
            info.ReplyToCommand("[MVP Music Lite] 配置已重载：" + Config.Packs.Count + " 个绑定  " + ids);
            Server.PrintToConsole("[MVP Music Lite] 配置已重载：" + Config.Packs.Count + " 个绑定  " + ids);
        }
        catch (Exception ex)
        {
            info.ReplyToCommand("[MVP Music Lite] 重载失败: " + ex.Message);
            Info("reload failed: " + ex);
        }
    }

    private void OnPacksCommand(CCSPlayerController? caller, CommandInfo info)
    {
        var dir = Path.Combine(CsgoDir(), "sounds", "mymvp", "packs");
        if (!Directory.Exists(dir))
        {
            info.ReplyToCommand("[MVP Music Lite] 还没有任何音效包: " + dir);
            return;
        }

        // 反查：包名 -> 绑定的 SteamID
        var byPack = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Config.Packs)
        {
            if (!byPack.TryGetValue(kv.Value, out var list)) byPack[kv.Value] = list = new List<string>();
            list.Add(kv.Key);
        }

        var dirs = Directory.GetDirectories(dir);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        info.ReplyToCommand("[MVP Music Lite] 音效包 " + dirs.Length + " 个  （默认音量 " + (int)Math.Round(Config.DefaultVolume * 100) + "%）");

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            var f = Path.Combine(d, Config.SoundFileName);
            var exists = File.Exists(f.Length > 0 ? f + "_c" : f) || Directory.GetFiles(d, "*.vsnd_c").Length > 0;
            var sizeKb = 0L;
            var when = "";
            try
            {
                var any = Directory.GetFiles(d, "*.vsnd_c");
                if (any.Length > 0) { var fi = new FileInfo(any[0]); sizeKb = fi.Length / 1024; when = fi.LastWriteTime.ToString("MM-dd HH:mm"); }
            }
            catch { }

            var ids = byPack.TryGetValue(name, out var list) && list.Count > 0 ? string.Join(",", list) : "(未绑定)";
            var star = name.Equals(Config.DefaultPack, StringComparison.OrdinalIgnoreCase) ? " *默认" : "";
            info.ReplyToCommand("  " + name.PadRight(14) + " " + when.PadRight(12) + (sizeKb + "KB").PadLeft(8) + "  " + ids + star + (exists ? "" : "  [缺文件]"));
        }
    }

    private void OnSoundCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true }) { info.ReplyToCommand("[MVP Music Lite] 请在游戏内使用。"); return; }
        var path = ResolveForMvp(Config, SteamIdOf(caller), false);
        if (string.IsNullOrEmpty(path))
        {
            Say(caller, info, "[MVP Music Lite] 你拿 MVP 时不会放歌（未绑定）");
            return;
        }
        var ok = FileExistsOnDisk(CsgoDir(), path);
        Say(caller, info, "[MVP Music Lite] " + path + "   服务端文件" + (ok ? "存在" : "缺失") +
                          "   音量=" + (int)Math.Round(VolumeOf(SteamIdOf(caller)) * 100) + "%");
        Server.PrintToConsole("[MVP Music Lite] " + caller.PlayerName + " -> " + path + " exists=" + ok);
    }
}
