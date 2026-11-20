using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

// T-side "IGL": reacts during the round instead of only rolling a plan at freeze end.
//  - early pick on a default        -> group up and hit one site
//  - CT sniper gets a kill on site  -> fall back and hit the other site (if time allows)
//  - running out of time            -> execute anyway, then a last-second all-in
//  - fake: a pair makes noise on one site, the rest hit the other
public partial class BotState
{
    private const string TExecGroup = "tExec";
    private bool _tExecuted, _tFallbackDone, _tFinalPush;
    private int _tExecSite = -1;
    private float _tExecAtLeft = 50f;      // execute when this many seconds are left
    private float _tSniperAt = -99f; private int _tSniperSite = -1;
    private float _fakeReleaseAt = -1f; private readonly List<int> _fakers = new();
    private readonly Dictionary<int, float> _tSeenCt = new(); // site*128+ctSlot -> time
    private float _nextDirectorScan, _tProbeDecisionAt = -1f;
    private int _ecoStealCarrier = -1, _ecoStealSite = -1, _ecoFakeSite = -1;
    private float _ecoStealStart;
    private bool _ecoStealReleased;

    private static bool IsSniper(string? w) => w is "awp" or "ssg08" or "g3sg1" or "scar20"
        or "weapon_awp" or "weapon_ssg08" or "weapon_g3sg1" or "weapon_scar20";

    private Vector StagingFor(int site)
        => Jitter(Lerp(_tSpawn!, SiteCenter(site), RandF(0.55f, 0.66f)), 150f);

    private int HumanCarrierPreferredSite()
    {
        var carrier = GetMatchPlayers().FirstOrDefault(p => p.TeamNum == (byte)CsTeam.Terrorist
            && (!p.IsBot || p.HasBeenControlledByPlayerThisRound)
            && BombPlayerPawn(p) is { } pawn && HasC4(pawn));
        var position = carrier == null ? null : BombPlayerPawn(carrier)?.AbsOrigin;
        if (position == null || _tSpawn == null) return -1;
        int near = SiteOf(position, 1250f);
        if (near >= 0) return near;
        float a = RetakeDistance(position, SiteCenter(0));
        float b = RetakeDistance(position, SiteCenter(1));
        if (MathF.Abs(a - b) > 450f && MathF.Min(a, b) < 1800f
            && RetakeDistance(position, _tSpawn) > 650f) return a < b ? 0 : 1;
        return -1;
    }

    // Prefer the human bomb carrier's apparent destination; otherwise use recent sightings.
    private int ChooseExecSite(bool ignoreSniper)
    {
        float now = Server.CurrentTime;
        int[] seen = new int[2];
        foreach (var (key, time) in _tSeenCt)
            if (now - time < 20f) seen[key / 128]++;
        int humanSite = HumanCarrierPreferredSite();
        if (humanSite >= 0) return humanSite;
        if (!ignoreSniper && _tSniperSite >= 0 && now - _tSniperAt < 25f) return 1 - _tSniperSite;
        if (seen[0] != seen[1] && _random.NextDouble() < 0.75) return seen[0] < seen[1] ? 0 : 1;
        return _tTargetSite >= 0 ? _tTargetSite : _random.Next(2);
    }

    private static bool KeepOwnPlan(RolePlan? p)
        => p != null && (p.Role.StartsWith("bomb-fetch") || p.Role.StartsWith("bomb-save")
            || p.Role is "bomb-cover" or "bomb-guard");

    // Gives every T bot (except bomb fetch / saving) a plan onto `site`.
    // gather: meet at a staging point first and go together.
    private void StartTExecute(int site, string why, bool gather, float hurry = 5f, IEnumerable<int>? skip = null)
    {
        float now = Server.CurrentTime, left = TimeLeft();
        var skipSet = skip == null ? new HashSet<int>() : new HashSet<int>(skip);
        var bots = new List<(CCSPlayerController p, CCSPlayerPawn pawn, CCSBot bot)>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(p, out var pawn, out var bot)) continue;
            if (skipSet.Contains(p.Slot) || KeepOwnPlan(_rolePlan.GetValueOrDefault(p.Slot))) continue;
            bots.Add((p, pawn, bot));
        }
        if (bots.Count == 0) return;

        _tExecuted = true; _tExecSite = site; _tTargetSite = site;
        var center = SiteCenter(site);
        var stage = StagingFor(site);
        // Bots already standing on the site do not walk back to the staging point.
        gather &= bots.Count >= 2 && left > 35f;
        if (gather)
            OpenGroup(TExecGroup, Math.Max(2, bots.Count - 1), now + Math.Clamp(left - 30f, 5f, 14f));
        else
            _groups.Remove(TExecGroup);

        foreach (var (p, pawn, bot) in bots)
        {
            var old = _rolePlan.GetValueOrDefault(p.Slot);
            var plan = new RolePlan { StartAt = now + RandF(0f, 0.8f), Hurry = hurry, Sticky = true, Site = site };
            bool lurker = old?.Role == "lurker";
            if (lurker && _random.NextDouble() < 0.45)
            {
                // Lurker stays on the other side and cuts the rotation instead of joining.
                plan.Role = "lurk-cut";
                plan.Hurry = 0f;
                var cut = Jitter(Lerp(SiteCenter(1 - site), center, RandF(0.3f, 0.45f)), 150f);
                plan.Legs.Add((cut, 2, now + RandF(10f, 20f)));
                plan.Legs.Add((Jitter(center, 200f), 2, 0f));
            }
            else if (gather && SiteOf(pawn.AbsOrigin!) != site && !lurker)
            {
                plan.Role = $"exec{(site == 0 ? 'A' : 'B')}-stage";
                plan.Group = TExecGroup; plan.GatherLeg = 0; plan.Arrive = 320f;
                plan.Legs.Add((Jitter(stage, 120f), 2, 0f));
                plan.Legs.Add((Jitter(center, 180f), 1, 0f));
            }
            else
            {
                plan.Role = $"exec{(site == 0 ? 'A' : 'B')}-direct";
                plan.Legs.Add((Jitter(center, 180f), lurker ? 2 : 1, 0f));
            }
            _rolePlan[p.Slot] = plan;
        }
        _lastStrategyText = $"{_tStrategy}->exec{(site == 0 ? 'A' : 'B')} ({why}{(gather ? ", gather" : "")}, {left:F0}s)";
        if (_debugTactics) BroadcastDebug("[Smarter-Bot/TDir] " + _lastStrategyText);
    }
    private bool StartEcoSplitAttack(int site, List<CCSPlayerController> bots, float now)
    {
        var flanker = bots.Where(p => IsAliveBot(p, out var pawn, out _) && !HasC4(pawn))
            .OrderBy(_ => _random.Next()).FirstOrDefault();
        if (flanker == null) return false;
        _rolePlan.Remove(flanker.Slot);
        StartTExecute(site, "eco split", gather: false, hurry: 10f, skip: new[] { flanker.Slot });
        var middle = Lerp(_tSpawn!, _ctSpawn!, 0.5f);
        var flank = new RolePlan { Role = "eco-flank", Site = site, StartAt = now + RandF(0.2f, 0.6f),
            Hurry = 5f, Arrive = 240f };
        flank.Legs.Add((Jitter(Lerp(middle, SiteCenter(1 - site), 0.35f), 80f), 2, 0f));
        flank.Legs.Add((Jitter(SiteCenter(site), 140f), 2, 0f));
        _rolePlan[flanker.Slot] = flank;
        _tFallbackDone = true;
        _lastStrategyText = $"Eco split {(site == 0 ? 'A' : 'B')} flank=1";
        return true;
    }

    private bool StartEcoSteal(int fakeSite, List<CCSPlayerController> bots, float now)
    {
        var carrier = bots.FirstOrDefault(p => IsAliveBot(p, out var pawn, out _) && HasC4(pawn));
        if (carrier == null) return false;
        int target = 1 - fakeSite;
        _rolePlan.Remove(carrier.Slot);
        StartTExecute(fakeSite, "eco distraction", gather: false, hurry: 8f, skip: new[] { carrier.Slot });
        _ecoStealCarrier = carrier.Slot;
        _ecoStealSite = target;
        _ecoFakeSite = fakeSite;
        _ecoStealStart = now;
        _ecoStealReleased = false;
        _tFallbackDone = true;
        var stage = Lerp(_tSpawn!, SiteCenter(target), 0.48f);
        var plan = new RolePlan { Role = "eco-steal", Site = target, Walk = true, StartAt = now + RandF(0.1f, 0.4f), Arrive = 230f };
        plan.Legs.Add((Jitter(stage, 80f), 2, now + 25f));
        plan.Legs.Add((SiteCenter(target), 2, 0f));
        _rolePlan[carrier.Slot] = plan;
        _lastStrategyText = $"Eco fake {(fakeSite == 0 ? 'A' : 'B')} steal {(target == 0 ? 'A' : 'B')}";
        return true;
    }

    private void UpdateEcoSteal(float now)
    {
        if (_ecoStealCarrier < 0 || _ecoStealReleased) return;
        var carrier = Utilities.GetPlayerFromSlot(_ecoStealCarrier);
        if (!IsAliveBot(carrier, out var pawn, out _) || !HasC4(pawn))
        {
            _ecoStealCarrier = -1;
            return;
        }
        if (!_rolePlan.TryGetValue(carrier!.Slot, out var plan) || plan.Role != "eco-steal") return;
        bool pressure = _tSeenCt.Any(kv => kv.Key / 128 == _ecoFakeSite && now - kv.Value < 5f);
        bool danger = _tSeenCt.Any(kv => kv.Key / 128 == _ecoStealSite && now - kv.Value < 5f);
        bool waited = now - _ecoStealStart >= 12f;
        if ((!danger && pressure && now - _ecoStealStart >= 4f) || (!danger && waited) || TimeLeft() < 35f || now - _ecoStealStart >= 22f)
        {
            var leg = plan.Legs[0];
            plan.Legs[0] = (leg.goal, leg.route, now);
            plan.Hurry = TimeLeft() < 35f ? 8f : 0f;
            _ecoStealReleased = true;
            _lastStrategyText += " (go)";
        }
    }

    private void SupportEcoPlant(CCSPlayerController? planter)
    {
        if (_ecoStealCarrier < 0 || planter == null || planter.Slot != _ecoStealCarrier) return;
        int site = _ecoStealSite;
        _ecoStealCarrier = -1;
        if (site is < 0 or > 1) return;
        float now = Server.CurrentTime;
        var center = SiteCenter(site);
        int lane = 0;
        foreach (var player in GetMatchPlayers())
        {
            if (player.Slot == planter.Slot || player.TeamNum != (byte)CsTeam.Terrorist
                || !IsAliveBot(player, out var pawn, out _) || pawn.AbsOrigin == null) continue;
            var goal = RotationApproach(pawn, center, lane++, 350f) ?? center;
            var plan = new RolePlan { Role = "eco-postplant", Site = site, Arrive = 230f,
                StartAt = now, Hurry = 8f };
            plan.Legs.Add((goal, 1, 0f));
            _rolePlan[player.Slot] = plan;
            ClearDefusalGuard(player.Slot);
            CancelIdlePeek(player.Slot);
        }
    }
    // Called from OnPlayerDeath (before the victim's plan is dropped).
    private void DirectorOnDeath(CCSPlayerController? victim, CCSPlayerController? attacker, string weapon)
    {
        if (victim == null || !victim.IsValid || !TacticsEnabled() || _tSpawn == null) return;
        var vp = victim.PlayerPawn.Value;
        if (vp?.AbsOrigin == null) return;
        var pos = new Vector(vp.AbsOrigin.X, vp.AbsOrigin.Y, vp.AbsOrigin.Z);
        float left = TimeLeft();
        int tAlive = AliveCount(CsTeam.Terrorist) - (victim.TeamNum == (byte)CsTeam.Terrorist ? 1 : 0);

        Server.NextFrame(() =>
        {
            try
            {
                if (!TacticsEnabled() || tAlive <= 0) return;
                if (victim.TeamNum == (byte)CsTeam.CounterTerrorist && !_tExecuted)
                {
                    // Early pick: hit the site the kill opened up (or the weaker one) together.
                    double chance = _tStrategy is TStrategy.Default or TStrategy.SlowDefault or TStrategy.Contact or TStrategy.Probe ? 0.65 : 0.35;
                    if (left > 35f && _random.NextDouble() < chance)
                    {
                        int s = SiteOf(pos, 1700f);
                        if (s < 0) s = ChooseExecSite(false);
                        StartTExecute(s, "pick", gather: _random.NextDouble() < 0.7, hurry: 4f);
                    }
                }
                else if (victim.TeamNum == (byte)CsTeam.Terrorist && attacker != null && attacker.IsValid
                         && attacker.TeamNum == (byte)CsTeam.CounterTerrorist && IsSniper(weapon))
                {
                    int s = SiteOf(pos, 1600f);
                    if (s < 0) s = _tExecSite >= 0 ? _tExecSite : SiteOf(pos, 2200f);
                    if (s < 0) return;
                    _tSniperAt = Server.CurrentTime; _tSniperSite = s;
                    // Plenty of time: back off and go the other way. Low time: keep going.
                    bool towardSniper = !_tExecuted || _tExecSite == s;
                    if (towardSniper && !_tFallbackDone && left > 42f && tAlive >= 2 && _random.NextDouble() < 0.7)
                    {
                        _tFallbackDone = true;
                        StartTExecute(1 - s, "sniper fallback", gather: left > 55f && _random.NextDouble() < 0.6, hurry: 5f);
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("[Smarter-Bot] director: " + e.Message); }
        });
    }

    private void UpdateTDirector(float now)
    {
        if (now < _nextDirectorScan) return;
        _nextDirectorScan = now + 0.5f;
        if (!_rolesAssigned || _roundLiveAt < 0f || !TacticsEnabled() || _tSpawn == null) return;
        float left = TimeLeft();

        // What the T side has seen: which site has CTs on it.
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(p, out _, out var bot)) continue;
            if (!bot.IsEnemyVisible) continue;
            var e = bot.Enemy.Value;
            if (e?.AbsOrigin == null) continue;
            int s = SiteOf(e.AbsOrigin, 1500f);
            if (s >= 0) _tSeenCt[s * 128 + (int)(e.OriginalController.Value?.Slot ?? 0)] = now;
        }

        if (_droppedC4 != uint.MaxValue) return;
        UpdateEcoSteal(now);
        if (_tProbeDecisionAt > 0f && now >= _tProbeDecisionAt)
        {
            _tProbeDecisionAt = -1f;
            if (!_tExecuted)
                StartTExecute(ChooseExecSite(ignoreSniper: left < 40f), "probe regroup", gather: left > 50f, hurry: 5f);
        }

        // Fake: once the fakers made noise (or the timer says so) the real hit goes.
        if (_fakeReleaseAt > 0f)
        {
            bool fakeContact = _fakers.Any(s => { var fp = Utilities.GetPlayerFromSlot(s);
                return IsAliveBot(fp, out _, out var fb) && (fb.IsAttacking || now - fb.LastSawEnemyTimestamp < 2f); });
            if (now >= _fakeReleaseAt || fakeContact || left < 40f)
            {
                _fakeReleaseAt = -1f;
                if (_groups.TryGetValue(TExecGroup, out var g)) g.NotBefore = 0f;
                ReleaseGroup(TExecGroup, fakeContact ? "fake drew them" : "fake timer");
            }
        }

        // Clock: default rounds turn into an execute; the sniper is ignored late.
        if (!_tExecuted && left < _tExecAtLeft)
            StartTExecute(ChooseExecSite(ignoreSniper: left < 40f), "time", gather: left > 42f && _random.NextDouble() < 0.6, hurry: 6f);

        // Last seconds: everyone still alive goes in fast, no waiting, no fallbacks.
        if (!_tFinalPush && left < 22f)
        {
            _tFinalPush = true;
            int s = _tExecSite >= 0 ? _tExecSite : ChooseExecSite(true);
            ReleaseGroup(TExecGroup, "final push");
            StartTExecute(s, "final push", gather: false, hurry: 12f);
        }
    }

    private void ResetTDirectorRoundState()
    {
        _ecoStealCarrier = _ecoStealSite = _ecoFakeSite = -1;
        _ecoStealReleased = false;
        _tExecuted = _tFallbackDone = _tFinalPush = false;
        _tExecSite = -1; _tSniperSite = -1; _tSniperAt = -99f;
        _fakeReleaseAt = -1f; _fakers.Clear(); _tSeenCt.Clear();
        _nextDirectorScan = 0f;
        _tProbeDecisionAt = -1f;
    }
}
