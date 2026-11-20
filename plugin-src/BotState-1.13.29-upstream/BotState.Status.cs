using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using System;

namespace BotState;

public partial class BotState
{
    private bool _debugTactics;
    private bool _announcedThisMap;

    // Short lines: the in-game console cuts long ones off.
    private string[] StatusLines()
    {
        int knife = _knifeRun.Values.Count(k => k.Active);
        float avg = _statReactions > 0 ? _statReactionMsSum / _statReactions : 0f;
        var roles = string.Join(" ", _rolePlan.Values.GroupBy(r => r.Role).Select(g => $"{g.Key}x{g.Count()}"));
        var lines = new[]
        {
            $"[Smarter-Bot] v{ModuleVersion} controller={(_botController != null ? "ok" : "MISSING")} moveTo={(_moveToReady ? "ok" : "off")} tactics={(TacticsEnabledQuiet() ? "on" : "off")}",
            $"[Smarter-Bot] plan={_lastStrategyText}",
            $"[Smarter-Bot] roles={_rolePlan.Count} {roles}",
            $"[Smarter-Bot] groups={_groups.Count(g => !g.Value.Released)} c4Dropped={(_droppedC4 != uint.MaxValue ? "yes" : "no")} smokes={_smokes.Count} knifeRun={knife}",
            $"[Smarter-Bot] reactions={_statReactions} avgReact={avg:F0}ms jumpAssists={_statJumpAssists} jumpsBlocked={_statJumpsBlocked}",
            $"[Smarter-Bot] flashUpgraded={_statFlashUpgraded} flashTurns={_statFlashTurns} flashBlinds={_statFlashBlinds} flashReturned={_statFlashReturned} flashMoving={_statFlashMoving} flashInterrupts={_statFlashFireInterrupted}",
        };
        return _defusalMapSeen
            ? lines.Concat(DefusalDuelStatus()).Append(DefusalReactionStatus()).Append(DefusalGuardStatus())
                   .Append($"[Smarter-Bot] defusal={(IsDefusalCombatEnabled() ? "on" : "off")} phase={(_defusalBombPlanted ? "postplant" : "preplant")}")
                   .Append(SiteHoldStatus())
                   .Append($"[Smarter-Bot] sitePeek out/back CT A={_ctPeekMoved[0]}/{_ctPeekBack[0]} B={_ctPeekMoved[1]}/{_ctPeekBack[1]} T A={_tPeekMoved[0]}/{_tPeekBack[0]} B={_tPeekMoved[1]}/{_tPeekBack[1]}")
                   .Append(PostplantGuardStatus())
                   .Append(AwarenessStatus())
                   .Append(DefusalRotationStatus())
                   .Append($"[Smarter-Bot] recoveryBypass={_statRecoveryBypasses} repath={_statRecoveryRepaths} climb={_statRecoveryClimbs} recovered={_statRecoveryRecovered}").ToArray()
            : lines;
    }

    private string StatusLine() => string.Join(" | ", StatusLines());

    private bool TacticsEnabledQuiet()
    {
        try { return TacticsEnabled(); } catch { return false; }
    }

    [ConsoleCommand("css_smartbot_status", "Show Smarter-Bot module status")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnStatusCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        foreach (var l in StatusLines()) cmd.ReplyToCommand(l);
        Logger.LogInformation(StatusLine());
    }

    [ConsoleCommand("css_smartbot_debug", "Toggle Smarter-Bot tactics debug")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnTacticsDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        _debugTactics = !_debugTactics;
        cmd.ReplyToCommand($"[Smarter-Bot] tactics debug = {_debugTactics}");
    }

    // One visible notice per map so it is obvious in game that the plugin runs.
    private void AnnounceOnce()
    {
        if (_announcedThisMap) return;
        _announcedThisMap = true;
        string line = StatusLine();
        Logger.LogInformation(line);
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            p.PrintToConsole(line);
            p.PrintToChat($" \x04[Smarter-Bot]\x01 已加载：反应延迟/烟雾/闪光/角色/切刀 {(_botController != null ? "正常" : "缺少BotController")}");
        }
    }
}
