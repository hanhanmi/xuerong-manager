using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

// CT rotations with human variety, plus filling an empty site and eco stacks ("赌点").
public partial class BotState
{
    private enum RotateStyle { None, One, All, Gather, WaitFlank }

    private readonly float[] _c4SeenAt = { -99f, -99f };
    private readonly RotateStyle?[] _siteStyle = new RotateStyle?[2];
    private readonly float[] _fillCd = new float[2];
    private int _ctGambleSite = -1;
    private int _ctGambleMode, _ctGambleKeep;
    private float _ctGambleDecisionAt, _ctGambleNextMove;
    private int _groupSeq;
    private float _nextFillScan;
    private int _ctFormation;
    private bool _ctFormationApplied;

    private void TriggerRotate(int site, bool heavy)
    {
        if (IsDefusalCombatEnabled())
        {
            TriggerDefusalPreplantRotate(site, heavy);
            return;
        }

        float now = Server.CurrentTime;
        if (now < _siteTriggerCooldown[site] || _ctSpawn == null) return;
        var center = SiteCenter(site);
        int other = 1 - site;

        var cands = new List<(CCSPlayerController p, CCSPawnPos x, float dist, int at)>();
        int defendersAtOther = 0, alive = 0;
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pw = p.PlayerPawn.Value;
            if (pw == null || !pw.IsValid || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || pw.AbsOrigin == null) continue;
            alive++;
            int at = SiteOf(pw.AbsOrigin);
            if (at == site) continue;
            if (at == other) defendersAtOther++;
            if (!IsAliveBot(p, out var pawn, out var bot)) continue;
            if (bot.IsAttacking || bot.IsEnemyVisible || pawn.IsDefusing) continue;
            if (_rotateUntil.GetValueOrDefault(p.Slot) > now) continue;
            if (_rolePlan.TryGetValue(p.Slot, out var rp) && rp.Role.StartsWith("ct-bomb")) continue;
            cands.Add((p, new CCSPawnPos(pawn.AbsOrigin!, bot), Dist2D(pawn.AbsOrigin!, center), at));
        }
        if (cands.Count == 0) return;

        bool confirmed = heavy || now - _c4SeenAt[site] < 8f;
        double r = _random.NextDouble();
        RotateStyle style;
        if (alive <= 2) style = RotateStyle.All;
        else if (confirmed)
            style = r < 0.42 ? RotateStyle.All : r < 0.70 ? RotateStyle.Gather
                  : r < 0.97 ? RotateStyle.One : RotateStyle.WaitFlank;
        else
            style = r < 0.22 ? RotateStyle.None : r < 0.55 ? RotateStyle.One
                  : r < 0.70 ? RotateStyle.All : r < 0.97 ? RotateStyle.Gather : RotateStyle.WaitFlank;
        // A site that already got an answer only escalates, it does not re-roll downwards.
        if (_siteStyle[site] is RotateStyle prev && prev != RotateStyle.None && style is RotateStyle.None or RotateStyle.One && confirmed)
            style = RotateStyle.All;
        _siteStyle[site] = style;

        if (style == RotateStyle.None)
        {
            _siteTriggerCooldown[site] = now + RandF(5f, 8f); // hold position, look again later
            if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Rotate] {(site == 0 ? 'A' : 'B')}: hold (no rotate)");
            return;
        }

        // Mid / rotation-path players first, then the closest from the other site.
        cands.Sort((x, y) =>
        {
            int px = x.at == -1 ? 0 : 1, py = y.at == -1 ? 0 : 1;
            return px != py ? px.CompareTo(py) : x.dist.CompareTo(y.dist);
        });
        var go = new List<(CCSPlayerController p, CCSPawnPos x, float dist, int at)>();
        bool keepAnchor = alive > 3 && _random.NextDouble() < 0.6;
        int want = style == RotateStyle.One ? 1 : cands.Count;
        foreach (var c in cands)
        {
            if (go.Count >= want) break;
            if (c.at == other && keepAnchor && defendersAtOther <= 1) continue;
            if (c.at == other) defendersAtOther--;
            go.Add(c);
        }
        if (go.Count == 0) return;
        if (go.Count < 2 && style is RotateStyle.Gather or RotateStyle.WaitFlank) style = RotateStyle.All;

        string tag = $"ctRot{site}_{++_groupSeq}";
        int flanker = -1;
        Vector? meet = null;
        if (style is RotateStyle.Gather or RotateStyle.WaitFlank)
        {
            var near = go.OrderBy(c => c.dist).First();
            meet = Jitter(Lerp(near.x.Pos, center, RandF(0.25f, 0.4f)), 80f);
            if (style == RotateStyle.WaitFlank) flanker = go.OrderByDescending(c => c.dist).First().p.Slot;
            OpenGroup(tag, go.Count - (flanker >= 0 ? 1 : 0), now + RandF(7f, 13f), flanker);
        }

        foreach (var c in go)
        {
            int slot = c.p.Slot;
            var plan = new RolePlan { StartAt = now + RandF(0f, 0.6f), Sticky = true, Site = site, Hurry = 8f };
            if (slot == flanker && _tSpawn != null)
            {
                // Comes in from the attackers' side to catch them in the back.
                plan.Role = "ct-flank"; plan.Hurry = 5f;
                plan.Legs.Add((Jitter(Lerp(center, _tSpawn, RandF(0.32f, 0.45f)), 120f), 2, 0f));
                plan.Legs.Add((Jitter(center, 150f), 2, 0f));
            }
            else if (meet != null)
            {
                plan.Role = "ct-gather"; plan.Hurry = 6f; plan.Group = tag; plan.GatherLeg = 0; plan.Arrive = 260f;
                plan.Legs.Add((Jitter(meet, 90f), 1, 0f));
                plan.Legs.Add((Jitter(center, 150f), 1, 0f));
            }
            else
            {
                plan.Role = "ct-rotate";
                plan.Legs.Add((Jitter(center, 150f), 1, 0f));
            }
            _rolePlan[slot] = plan;
            _rotateUntil[slot] = now + 30f;
            _rotateSite[slot] = site;
            _rotateIssued[slot] = now;
            _rotateReissues[slot] = 0;
        }
        _siteTriggerCooldown[site] = now + (style == RotateStyle.One ? RandF(5f, 8f) : RandF(9f, 14f));
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Rotate] {(site == 0 ? 'A' : 'B')}: {style} x{go.Count}{(confirmed ? " (confirmed)" : "")}");
    }
    // At round start on a CT eco: often stack one site and gamble on it.
    private void AssignCtRound(float now)
    {
        _ctGambleSite = -1;
        double formationRoll = _random.NextDouble();
        _ctFormation = formationRoll < 0.70 ? 1 : formationRoll < 0.80 ? 2 : formationRoll < 0.90 ? 3 : 0;
        _ctFormationApplied = false;
        if (!IsEcoRound(CsTeam.CounterTerrorist) || AliveCount(CsTeam.CounterTerrorist) < 3) return;
        int rounds = Rules()?.TotalRoundsPlayed ?? -1;
        int maxRounds = CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        bool pistolRound = rounds == 0 || (maxRounds > 1 && rounds == maxRounds / 2);
        double gambleChance = pistolRound ? 0.02 : 0.10;
        if (_random.NextDouble() >= gambleChance) return;
        _ctGambleSite = _random.Next(2);
        double followup = _random.NextDouble();
        _ctGambleMode = followup < 0.45 ? 0 : followup < 0.80 ? 1 : 2;
        _ctGambleKeep = _random.NextDouble() < 0.5 ? 1 : 2;
        _ctGambleDecisionAt = now + RandF(20f, 32f);
        _ctGambleNextMove = _ctGambleDecisionAt;
        var center = SiteCenter(_ctGambleSite);
        var bots = GetMatchPlayers()
            .Where(p => p != null && p.TeamNum == (byte)CsTeam.CounterTerrorist && IsAliveBot(p, out _, out _))
            .OrderBy(_ => _random.Next()).ToList();
        if (bots.Count == 0) { _ctGambleSite = -1; return; }
        // Usually one stays mid / on the other site for info.
        int stay = bots.Count >= 4 && _random.NextDouble() < 0.5 ? 1 : 0;
        for (int i = stay; i < bots.Count; i++)
        {
            var plan = new RolePlan { Role = "ct-stack", StartAt = now + RandF(0f, 1.5f), Site = _ctGambleSite, Hurry = 6f };
            plan.Legs.Add((Jitter(center, 260f), 1, 0f));
            _rolePlan[bots[i].Slot] = plan;
        }
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/CT] eco stack {(_ctGambleSite == 0 ? 'A' : 'B')} x{bots.Count - stay}");
    }

    private void UpdateCtGamble(float now)
    {
        if (_ctGambleSite < 0 || _defusalBombPlanted || !IsDefusalCombatEnabled()
            || now < _ctGambleDecisionAt || now < _ctGambleNextMove) return;
        int gambleSite = _ctGambleSite, other = 1 - gambleSite;
        if (_ctGambleMode == 2) return;
        if (_rotationSite >= 0 && now - _rotationConfirmedAt < 8f) return;
        if (_siteContacts.Any(kv => kv.Key / 128 == gambleSite && now - kv.Value < 7f)
            || now - _ctLastLossAt[gambleSite] < 7f) return;

        int alive = 0, onGamble = 0, onOther = 0, roamingHumans = 0;
        var seen = new HashSet<uint>();
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, int At, float Cost)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !seen.Add(pawn.EntityHandle.Raw)) continue;
            alive++;
            bool isBot = IsAliveBot(player, out _, out var bot);
            int at = SiteOf(pos, 1250f);
            _rolePlan.TryGetValue(player.Slot, out var plan);
            if (isBot && plan is { Site: >= 0 } && plan.Leg < plan.Legs.Count) at = plan.Site;
            if (at == gambleSite) onGamble++;
            else if (at == other) onOther++;
            else if (!isBot) roamingHumans++;
            if (!isBot || at != gambleSite || RetakeBusy(player, pawn, bot, now)
                || TradeSupportActive(player.Slot, pawn, now) || bot.IsEnemyVisible || bot.IsAttacking
                || now - bot.LastSawEnemyTimestamp < 5f) continue;
            if (plan != null && plan.Role != "ct-stack") continue;
            candidates.Add((player, pawn, bot, at, RetakeDistance(pos, SiteCenter(other))));
        }
        int keep = _ctGambleMode == 1 ? Math.Min(2, Math.Max(1, alive - 1)) : Math.Min(_ctGambleKeep, Math.Max(1, alive - 1));
        int desiredOther = _ctGambleMode == 1 ? Math.Min(2, Math.Max(1, alive - keep - roamingHumans))
            : Math.Max(1, alive - keep - roamingHumans);
        if (onOther >= desiredOther || onGamble <= keep)
        {
            _ctGambleSite = -1;
            _ctFormationApplied = true;
            if (_ctGambleMode == 1) _ctFormation = 1;
            return;
        }
        var pick = candidates.OrderBy(c => c.Cost).FirstOrDefault();
        if (pick.Player == null)
        {
            _ctGambleNextMove = now + 1f;
            return;
        }
        int slot = pick.Player.Slot;
        var route = new RolePlan { Role = "ct-gamble-expand", Site = other, Arrive = 260f,
            StartAt = now, Hurry = _ctGambleMode == 0 ? 5f : 0f };
        route.Legs.Add((Jitter(SiteCenter(other), 180f), _ctGambleMode == 0 ? 1 : 2, 0f));
        _rolePlan[slot] = route;
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        if (BotMoveTo(pick.Bot, route.Legs[0].goal, route.Legs[0].route))
        {
            route.LegIssued = route.BestAt = now;
            if (route.Hurry > 0f) SetHurry(pick.Bot, route.Hurry);
        }
        _ctGambleNextMove = now + (_ctGambleMode == 0 ? RandF(0.7f, 1.5f) : RandF(5f, 8f));
    }
    private void ApplyCtFormation(float now)
    {
        if (_ctFormationApplied || _ctFormation == 0 || _ctGambleSite >= 0 || !IsDefusalCombatEnabled()
            || _defusalBombPlanted || now - _roundLiveAt < 7f || !TacticsEnabled()) return;
        _ctFormationApplied = true;
        if (_siteContacts.Count > 0 || _rotationSite >= 0) return;
        int[] at = new int[2];
        int alive = 0, roamingHumans = 0;
        var seen = new HashSet<uint>();
        var bots = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, int Site)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !seen.Add(pawn.EntityHandle.Raw)) continue;
            alive++;
            int site = CtCoverageSite(player, pawn, 1100f);
            bool controlledBot = IsAliveBot(player, out var livePawn, out var bot);
            int reserved = controlledBot ? CtInboundSite(player.Slot) : -1;
            if (reserved >= 0) at[reserved]++;
            else if (site >= 0) at[site]++;
            else if (!controlledBot) roamingHumans++;
            if (controlledBot && !_rolePlan.ContainsKey(player.Slot) && !RetakeBusy(player, livePawn, bot, now)
                && now - bot.LastSawEnemyTimestamp > 3f)
                bots.Add((player, livePawn, bot, site));
        }
        if (alive < 2) return;
        int[] target = alive >= 4 ? new[] { 2, 2 } : new[] { 1, 1 };
        if (alive >= 4 && _ctFormation == 2) target = new[] { 3, 1 };
        if (alive >= 4 && _ctFormation == 3) target = new[] { 1, 3 };
        int budget = Math.Min(target[0] + target[1], Math.Max(2, alive - Math.Min(roamingHumans, 1)));
        while (bots.Count > 0)
        {
            var choices = new List<(int Site, int Bot, float Cost)>();
            for (int site = 0; site < 2; site++)
            {
                if (at[site] >= target[site]) continue;
                for (int i = 0; i < bots.Count; i++)
                {
                    var bot = bots[i];
                    if (bot.Site == site || (bot.Site < 0 && at[0] + at[1] >= budget)
                        || (bot.Site >= 0 && at[bot.Site] <= target[bot.Site])) continue;
                    float cost = RetakeDistance(bot.Pawn.AbsOrigin!, SiteCenter(site))
                        + (bot.Site >= 0 ? 700f : 0f) + at[site] * 350f;
                    choices.Add((site, i, cost));
                }
            }
            if (choices.Count == 0) break;
            var choice = choices.OrderBy(c => c.Cost).First();
            var selected = bots[choice.Bot];
            var plan = new RolePlan { Role = "ct-formation", Site = choice.Site, Arrive = 250f,
                StartAt = now + RandF(0.05f, 0.4f), Hurry = 4f };
            plan.Legs.Add((Jitter(SiteCenter(choice.Site), 220f), 1, 0f));
            _rolePlan[selected.Player.Slot] = plan;
            if (selected.Site >= 0) at[selected.Site]--;
            at[choice.Site]++;
            bots.RemoveAt(choice.Bot);
        }
    }
    // Every 2 s: a site with no CT on it (and no rotation going on) gets one bot sent to it.
    private void UpdateSiteFill(float now)
    {
        if (now < _nextFillScan) return;
        _nextFillScan = now + 2f;
        if (_roundLiveAt < 0f || now - _roundLiveAt < 8f || !TacticsEnabled() || _ctGambleSite >= 0) return;
        if (_defusalBombPlanted) return;
        if (TimeLeft() < 20f) return;

        int[] at = new int[2]; int alive = 0;
        int[] inbound = new int[2];
        var seen = new HashSet<uint>();
        var free = new List<(CCSPlayerController p, CCSBot bot, Vector pos, int site)>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pw = p.PlayerPawn.Value;
            if (pw?.AbsOrigin == null || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || !seen.Add(pw.EntityHandle.Raw)) continue;
            alive++;
            int s = CtCoverageSite(p, pw, 1100f);
            int reserved = IsAliveBot(p, out _, out _) ? CtInboundSite(p.Slot) : -1;
            if (reserved >= 0) inbound[reserved]++;
            if (s >= 0) at[s]++;
            if (!IsAliveBot(p, out var pawn, out var bot) || bot.IsAttacking || bot.IsEnemyVisible) continue;
            if (_rolePlan.ContainsKey(p.Slot) || RetakeBusy(p, pawn, bot, now)
                || TradeSupportActive(p.Slot, pawn, now) || now - bot.LastSawEnemyTimestamp < 4f) continue;
            free.Add((p, bot, pawn.AbsOrigin!, s));
        }
        if (alive < 2) return;

        for (int site = 0; site < 2; site++)
        {
            int wanted = at[site] > 0 && IsDefusalCombatEnabled() && _ctFormation == 1 && alive >= 4 && at[1 - site] >= 2 ? 2 : 1;
            if (at[site] + inbound[site] >= wanted || now < _fillCd[site]) continue;
            if (_rotationSite == 1 - site && _rotationConfirmed[1 - site]
                && now - _rotationConfirmedAt < 8f) continue;

            if (!IsDefusalCombatEnabled() && _random.NextDouble() < 0.2) continue;
            var center = SiteCenter(site);
            // Take from mid first, else from a site that still keeps one defender.
            var pick = free.Where(f => f.site == -1 || (f.site != site && at[f.site] > wanted))
                           .OrderBy(f => (f.site == -1 ? 0f : 3000f) + Dist2D(f.pos, center))
                           .FirstOrDefault();
            if (pick.p == null) continue;
            _fillCd[site] = now + RandF(12f, 20f);
            var plan = new RolePlan { Role = $"ct-fill{(site == 0 ? 'A' : 'B')}", StartAt = now + RandF(0.2f, 1.5f), Site = site };
            plan.Legs.Add((Jitter(center, 220f), 2, 0f));
            _rolePlan[pick.p.Slot] = plan;
            if (pick.site >= 0) at[pick.site]--;
            inbound[site]++;
            free.Remove(pick);
            if (_debugTactics) BroadcastDebug($"[Smarter-Bot/CT] {pick.p.PlayerName} fills empty {(site == 0 ? 'A' : 'B')}");
        }
    }

    private void ResetCtRotateRoundState()
    {
        ResetDefusalRetakeRoundState();
        _c4SeenAt[0] = _c4SeenAt[1] = -99f;
        _siteStyle[0] = _siteStyle[1] = null;
        _fillCd[0] = _fillCd[1] = 0f;
        _ctGambleSite = -1;
        _ctFormation = 0;
        _ctFormationApplied = false;
        _nextFillScan = 0f;
    }
}
