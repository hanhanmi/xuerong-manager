using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

// Round context shared by the T director, CT rotations / site fill and the dropped-bomb logic:
// time left, economy, alive counts, and "gather groups" (bots wait at a point, go together).
public partial class BotState
{
    private sealed class GroupState
    {
        public float Deadline;
        public int Need;
        public bool Released;
        public int FlankerSlot = -1;   // group also waits for this bot to finish its first leg
        public bool ReleaseOnLowTime = true;
        public float NotBefore;        // gathering alone cannot release before this time
    }

    private readonly Dictionary<string, GroupState> _groups = new();

    private static IEnumerable<CCSPlayerController> GetMatchPlayers()
    {
        // Utilities.GetPlayers can reuse the main menu's cached one-client limit.
        int maxClients = NativeAPI.GetMaxClients();
        for (int slot = 0; slot < maxClients; slot++)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true, Connected: PlayerConnectedState.Connected })
                yield return player;
        }
    }

    private static CCSGameRules? Rules()
        => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    // Seconds until the round timer runs out (999 when unknown).
    private static float TimeLeft()
    {
        var r = Rules();
        if (r == null || r.RoundTime <= 0) return 999f;
        return r.RoundStartTime + r.RoundTime - Server.CurrentTime;
    }

    private static int AliveCount(CsTeam team)
    {
        int n = 0;
        var seen = new HashSet<uint>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)team) continue;
            var pw = p.PlayerPawn.Value;
            if (pw != null && pw.IsValid && pw.LifeState == (byte)LifeState_t.LIFE_ALIVE && seen.Add(pw.EntityHandle.Raw)) n++;
        }
        return n;
    }

    private static bool IsPrimaryWeapon(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("weapon_")) return false;
        if (name.StartsWith("weapon_knife") || name == "weapon_bayonet") return false;
        return name switch
        {
            "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_p250"
            or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" or "weapon_deagle"
            or "weapon_revolver" or "weapon_elite" or "weapon_c4" or "weapon_taser"
            or "weapon_hegrenade" or "weapon_flashbang" or "weapon_smokegrenade"
            or "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy" or "weapon_healthshot" => false,
            _ => true,
        };
    }

    private static bool HasPrimary(CCSPlayerPawn pawn)
    {
        var ws = pawn.WeaponServices;
        if (ws == null) return false;
        foreach (var h in ws.MyWeapons)
            if (IsPrimaryWeapon(h.Value?.DesignerName)) return true;
        return false;
    }
    // Eco / save round for a team: most of its alive players have no primary weapon.
    private static bool IsEcoRound(CsTeam team)
    {
        int alive = 0, armed = 0;
        var seen = new HashSet<uint>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)team) continue;
            var pw = p.PlayerPawn.Value;
            if (pw == null || !pw.IsValid || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || !seen.Add(pw.EntityHandle.Raw)) continue;
            alive++;
            if (HasPrimary(pw)) armed++;
        }
        return alive > 0 && armed * 2 < alive;
    }

    private void OpenGroup(string tag, int need, float deadline, int flankerSlot = -1, bool lowTimeRelease = true)
        => _groups[tag] = new GroupState { Need = Math.Max(1, need), Deadline = deadline, FlankerSlot = flankerSlot, ReleaseOnLowTime = lowTimeRelease };

    private bool GroupReleased(string tag) => !_groups.TryGetValue(tag, out var g) || g.Released;

    private void ReleaseGroup(string tag, string why)
    {
        if (!_groups.TryGetValue(tag, out var g) || g.Released) return;
        g.Released = true;
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Group] {tag} go ({why})");
    }

    // Twice a second: release groups whose members have gathered, whose wait timed out,
    // whose flanker is in position, that ran low on time, or that got into a fight.
    private void UpdateGroups(float now)
    {
        if (_groups.Count == 0) return;
        float left = TimeLeft();
        foreach (var (tag, g) in _groups)
        {
            if (g.Released) continue;
            int members = 0, arrived = 0; bool fighting = false;
            foreach (var (slot, plan) in _rolePlan)
            {
                if (plan.Group != tag) continue;
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!IsAliveBot(p, out var pawn, out var bot)) continue;
                members++;
                if (plan.Leg > plan.GatherLeg) { arrived++; continue; }
                if (plan.Leg == plan.GatherLeg && Dist2D(pawn.AbsOrigin!, plan.Legs[plan.Leg].goal) < plan.Arrive + 120f) arrived++;
                if (bot.IsAttacking) fighting = true;
            }
            if (members == 0) { g.Released = true; continue; }

            bool flankReady = true;
            if (g.FlankerSlot >= 0)
            {
                var fp = Utilities.GetPlayerFromSlot(g.FlankerSlot);
                flankReady = !IsAliveBot(fp, out _, out _)
                          || !_rolePlan.TryGetValue(g.FlankerSlot, out var fplan)
                          || fplan.Leg >= 1;
            }

            if (arrived >= Math.Min(g.Need, members) && flankReady && now >= g.NotBefore) ReleaseGroup(tag, $"gathered {arrived}/{members}");
            else if (now > g.Deadline) ReleaseGroup(tag, "wait timeout");
            else if (g.ReleaseOnLowTime && left < 30f) ReleaseGroup(tag, $"time {left:F0}s");
            else if (fighting && _random.NextDouble() < 0.5) ReleaseGroup(tag, "contact");
        }
    }

    private void ResetMatchRoundState() => _groups.Clear();
}
