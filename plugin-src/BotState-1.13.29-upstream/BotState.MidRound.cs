using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private enum MidRoundGear { Pistol, Economy, Rifle }

    private bool _midRoundGearCaptured, _midTDecided, _midCtDecided;
    private MidRoundGear _midTGear, _midCtGear;
    private float _midTDecisionAt, _midCtDecisionAt, _nextMidScan;
    private readonly Dictionary<int, (int Site, uint Pawn, float Until)> _midAttackPending = new();

    private void ResetMidRoundState()
    {
        _midRoundGearCaptured = _midTDecided = _midCtDecided = false;
        _midTDecisionAt = _midCtDecisionAt = _nextMidScan = 0f;
        _midAttackPending.Clear();
    }

    private static MidRoundGear CaptureMidRoundGear(CsTeam team)
    {
        int rounds = Rules()?.TotalRoundsPlayed ?? -1;
        int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        if (rounds == 0 || (maxRounds > 1 && rounds == maxRounds / 2)) return MidRoundGear.Pistol;

        int alive = 0, rifleUsers = 0;
        var counted = new HashSet<uint>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)team) continue;
            var pawn = BombPlayerPawn(player);
            if (pawn == null || !counted.Add(pawn.EntityHandle.Raw)) continue;
            alive++;
            if (pawn.WeaponServices?.MyWeapons.Any(h => h.Value?.DesignerName is
                "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_galilar"
                or "weapon_famas" or "weapon_aug" or "weapon_sg556" or "weapon_awp"
                or "weapon_ssg08" or "weapon_g3sg1" or "weapon_scar20" or "weapon_negev"
                or "weapon_m249") == true) rifleUsers++;
        }
        return alive > 0 && rifleUsers * 2 >= alive ? MidRoundGear.Rifle : MidRoundGear.Economy;
    }

    private void UpdateMidRound(float now)
    {
        if (now < _nextMidScan) return;
        _nextMidScan = now + 0.25f;
        if (!IsDefusalCombatEnabled() || !TacticsEnabled() || !_rolesAssigned || _roundLiveAt < 0f
            || _tSpawn == null || _ctSpawn == null || _defusalBombPlanted) return;
        if (!_midRoundGearCaptured)
        {
            if (now - _roundLiveAt < 2.5f) return;
            _midTGear = CaptureMidRoundGear(CsTeam.Terrorist);
            _midCtGear = CaptureMidRoundGear(CsTeam.CounterTerrorist);
            _midTDecisionAt = _roundLiveAt + RandF(22f, 32f);
            _midCtDecisionAt = _roundLiveAt + RandF(25f, 37f);
            _midRoundGearCaptured = true;
        }
        UpdatePendingMidAttack(now);
        if (TimeLeft() < 42f)
        {
            _midTDecided = _midCtDecided = true;
            return;
        }
        if (!_midTDecided && now >= _midTDecisionAt) TryMidAttack(now);
        if (!_midCtDecided && now >= _midCtDecisionAt) TryMidDefense(now);
    }

    private bool MidAttackBusy(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
        => bot.IsEnemyVisible || bot.IsAttacking || now - bot.LastSawEnemyTimestamp < 0.7f
            || DefusalObjectiveBusy(pawn) || DuelFlashActive(pawn, (int)player.Index, now)
            || _cachedNearLadder.GetValueOrDefault((int)player.Index)
            || TradeSupportActive(player.Slot, pawn, now)
            || ActiveMoveOwner(player.Slot, now) > MoveOwner.Peek;

    private void TryMidAttack(float now)
    {
        if (_droppedC4 != uint.MaxValue || _ecoStealCarrier >= 0 || _fakeReleaseAt > 0f
            || _tProbeDecisionAt > 0f || _tFinalPush || AliveCount(CsTeam.Terrorist) < 2) return;
        _midTDecided = true;
        int humanSite = HumanCarrierPreferredSite();
        int site = humanSite >= 0 ? humanSite : ChooseExecSite(ignoreSniper: false);
        int[] sightings = new int[2];
        foreach (var (key, time) in _tSeenCt)
            if (now - time <= 12f) sightings[key / 128]++;

        // Break away from a congested attack only on information the bots saw.
        // Fighters keep their current orders until the engagement ends.
        if (_tExecuted && _tExecSite >= 0 && !_tFallbackDone && TimeLeft() > 55f
            && (humanSite < 0 || humanSite != _tExecSite))
        {
            int other = 1 - _tExecSite;
            double chance = _midTGear switch { MidRoundGear.Pistol => 0.40, MidRoundGear.Economy => 0.52, _ => 0.48 };
            if (sightings[_tExecSite] >= 2 && sightings[other] + 1 < sightings[_tExecSite]
                && _random.NextDouble() < chance)
            {
                var skip = GetMatchPlayers().Where(p => p.TeamNum == (byte)CsTeam.Terrorist
                    && IsAliveBot(p, out var pawn, out var bot) && MidAttackBusy(p, pawn, bot, now))
                    .Select(p => p.Slot).ToArray();
                if (GetMatchPlayers().Any(p => p.TeamNum == (byte)CsTeam.Terrorist
                    && IsAliveBot(p, out _, out _) && !skip.Contains(p.Slot)))
                {
                    _tFallbackDone = true;
                    int destination = humanSite >= 0 ? humanSite : other;
                    StartTExecute(destination, "mid-round rotation", gather: false, hurry: 7f, skip: skip);
                    QueueMidAttackAfterCombat(skip, destination, now);
                    return;
                }
            }
        }

        double roll = _random.NextDouble();
        if (!_tExecuted)
        {
            double attack = _midTGear switch { MidRoundGear.Pistol => 0.40, MidRoundGear.Economy => 0.46, _ => 0.22 };
            if (roll < attack)
            {
                var skip = GetMatchPlayers().Where(p => p.TeamNum == (byte)CsTeam.Terrorist
                    && IsAliveBot(p, out var pawn, out var bot) && MidAttackBusy(p, pawn, bot, now))
                    .Select(p => p.Slot).ToArray();
                StartTExecute(site, "mid-round regroup", gather: false,
                    hurry: _midTGear == MidRoundGear.Rifle ? 4f : 7f, skip: skip);
                if (_tExecSite == site) QueueMidAttackAfterCombat(skip, site, now);
                return;
            }
            if (_midTGear == MidRoundGear.Rifle && roll < 0.44 && TryMidSplit(site, now)) return;
        }
        double supportChance = _midTGear switch { MidRoundGear.Pistol => 0.28, MidRoundGear.Economy => 0.34, _ => 0.24 };
        if (_random.NextDouble() < supportChance) TryMidEscort(site, now);
    }

    private void QueueMidAttackAfterCombat(IEnumerable<int> slots, int site, float now)
    {
        foreach (int slot in slots)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (IsAliveBot(player, out var pawn, out _))
                _midAttackPending[slot] = (site, pawn.EntityHandle.Raw, now + 20f);
        }
    }

    private void UpdatePendingMidAttack(float now)
    {
        if (_midAttackPending.Count == 0 || _droppedC4 != uint.MaxValue) return;
        foreach (var (slot, pending) in _midAttackPending.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (now >= pending.Until || _tExecSite != pending.Site
                || !IsAliveBot(player, out var pawn, out var bot) || pawn.EntityHandle.Raw != pending.Pawn
                || player!.TeamNum != (byte)CsTeam.Terrorist)
            {
                _midAttackPending.Remove(slot);
                continue;
            }
            if (MidAttackBusy(player, pawn, bot, now) || KeepOwnPlan(_rolePlan.GetValueOrDefault(slot))) continue;
            var plan = new RolePlan { Role = "t-mid-follow", Site = pending.Site, StartAt = now, Hurry = 6f };
            plan.Legs.Add((Jitter(SiteCenter(pending.Site), 140f), 1, 0f));
            _rolePlan[slot] = plan;
            ClearDefusalGuard(slot);
            CancelIdlePeek(slot);
            _midAttackPending.Remove(slot);
        }
    }

    private bool TryMidSplit(int site, float now)
    {
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(player, out var pawn, out var bot)
                || HasC4(pawn) || MidAttackBusy(player, pawn, bot, now)
                || KeepOwnPlan(_rolePlan.GetValueOrDefault(player.Slot))) continue;
            candidates.Add((player, pawn, bot));
        }
        if (candidates.Count < 2) return false;
        var flanker = candidates.OrderBy(_ => _random.Next()).First();
        var skip = GetMatchPlayers().Where(p => p.TeamNum == (byte)CsTeam.Terrorist
            && IsAliveBot(p, out var pawn, out var bot) && MidAttackBusy(p, pawn, bot, now))
            .Select(p => p.Slot).Append(flanker.Player.Slot).ToArray();
        StartTExecute(site, "mid-round split", gather: false, hurry: 5f, skip: skip);
        QueueMidAttackAfterCombat(skip.Where(slot => slot != flanker.Player.Slot), site, now);
        var approach = Lerp(SiteCenter(1 - site), SiteCenter(site), 0.48f);
        var plan = new RolePlan { Role = "t-mid-flank", Site = site, StartAt = now, Arrive = 200f };
        plan.Legs.Add((Jitter(approach, 100f), 2, 0f));
        plan.Legs.Add((Jitter(SiteCenter(site), 120f), 2, 0f));
        _rolePlan[flanker.Player.Slot] = plan;
        return true;
    }

    private void TryMidEscort(int site, float now)
    {
        var carrier = GetMatchPlayers().FirstOrDefault(p => p.TeamNum == (byte)CsTeam.Terrorist
            && BombPlayerPawn(p) is { } pawn && HasC4(pawn));
        var carrierPos = carrier == null ? null : BombPlayerPawn(carrier)?.AbsOrigin;
        if (carrierPos == null) return;
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, float Distance)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.Slot == carrier!.Slot || player.TeamNum != (byte)CsTeam.Terrorist
                || !IsAliveBot(player, out var pawn, out var bot) || MidAttackBusy(player, pawn, bot, now)
                || KeepOwnPlan(_rolePlan.GetValueOrDefault(player.Slot))) continue;
            float distance = RetakeDistance(pawn.AbsOrigin!, carrierPos);
            if (distance is > 240f and < 1300f) candidates.Add((player, pawn, bot, distance));
        }
        var helper = candidates.OrderBy(c => c.Distance).FirstOrDefault();
        if (helper.Player == null) return;
        var cover = RotationApproach(helper.Pawn, carrierPos, 0, 220f) ?? carrierPos;
        var plan = new RolePlan { Role = "t-mid-support", Site = site, StartAt = now, Arrive = 180f };
        plan.Legs.Add((cover, 2, 0f));
        plan.Legs.Add((Jitter(SiteCenter(site), 140f), 2, 0f));
        _rolePlan[helper.Player.Slot] = plan;
    }

    private void TryMidDefense(float now)
    {
        if (_ctGambleSite >= 0 || _droppedC4 != uint.MaxValue
            || (_rotationSite >= 0 && now - _rotationConfirmedAt < 10f)
            || _lossSupport.Any(request => request != null && now < request.Until)
            || _siteContacts.Any(contact => now - contact.Value < 5f)) return;
        _midCtDecided = true;
        var defenders = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot? Bot, int Site)>();
        var counted = new HashSet<uint>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = BombPlayerPawn(player);
            if (pawn?.AbsOrigin == null || !counted.Add(pawn.EntityHandle.Raw)) continue;
            bool controlled = IsAliveBot(player, out _, out var bot);
            int at = SiteOf(pawn.AbsOrigin, 1100f);
            if (controlled && _rolePlan.TryGetValue(player.Slot, out var old) && old.Site >= 0 && old.Leg < old.Legs.Count)
                at = old.Site;
            defenders.Add((player, pawn, controlled ? bot : null, at));
        }
        if (defenders.Count < 2) return;
        var available = defenders.Where(d => d.Bot != null && d.Site >= 0
            && !_rolePlan.ContainsKey(d.Player.Slot) && _rotateUntil.GetValueOrDefault(d.Player.Slot) <= now
            && !RetakeBusy(d.Player, d.Pawn, d.Bot, now) && !TradeSupportActive(d.Player.Slot, d.Pawn, now)
            && now - d.Bot.LastSawEnemyTimestamp > 4f).ToList();
        if (available.Count == 0) return;
        double roll = _random.NextDouble();
        double reposition = _midCtGear switch { MidRoundGear.Pistol => 0.38, MidRoundGear.Economy => 0.42, _ => 0.36 };
        double support = _midCtGear switch { MidRoundGear.Pistol => 0.62, MidRoundGear.Economy => 0.68, _ => 0.66 };
        if (roll < support)
        {
            bool nearTeammate = roll >= reposition;
            var order = nearTeammate
                ? available.OrderByDescending(d => defenders.Any(h => h.Site == d.Site && h.Bot == null)).ThenBy(_ => _random.Next())
                : available.OrderBy(_ => _random.Next());
            foreach (var candidate in order)
                if (!SiteHoldActive(candidate.Player.Slot, candidate.Pawn)
                    && TryMidDefensiveAngle(candidate.Player, candidate.Pawn, candidate.Site, now)) return;
        }
        else if (roll < 0.76)
        {
            // Quietly rebalance only a genuinely uneven setup and retain a defender on both sides.
            int a = defenders.Count(d => d.Site == 0), b = defenders.Count(d => d.Site == 1);
            int donor = a > b ? 0 : 1, target = 1 - donor;
            int donorCount = donor == 0 ? a : b, targetCount = target == 0 ? a : b;
            if (donorCount < 3 || donorCount - targetCount < 2) return;
            var pick = available.Where(d => d.Site == donor)
                .OrderBy(d => RetakeDistance(d.Pawn.AbsOrigin!, SiteCenter(target))).FirstOrDefault();
            if (pick.Player != null) QueueDefusalRotation(pick.Player, pick.Pawn, target, 0, now, confirmed: false);
        }
    }

    private bool TryMidDefensiveAngle(CCSPlayerController player, CCSPlayerPawn pawn, int site, float now)
    {
        var pos = pawn.AbsOrigin!;
        var center = SiteCenter(site);
        float towardSpawn = MathF.Atan2(_ctSpawn!.Y - pos.Y, _ctSpawn.X - pos.X);
        float[] offsets = { 0f, 0.65f, -0.65f, 1.20f, -1.20f };
        float distance = RandF(140f, 230f);
        foreach (float offset in offsets.OrderBy(_ => _random.Next()))
        {
            float angle = towardSpawn + offset;
            float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            var goal = new Vector(pos.X + dx * distance, pos.Y + dy * distance, pos.Z);
            if (SiteOf(goal, 1300f) != site || !IsDirectionClear(pawn, dx, dy, distance)) continue;
            var plan = new RolePlan { Role = "ct-mid-angle", Site = site, StartAt = now, Arrive = 65f };
            plan.Legs.Add((goal, 2, 0f));
            _rolePlan[player.Slot] = plan;
            ClearDefusalGuard(player.Slot);
            CancelIdlePeek(player.Slot);
            return true;
        }
        return false;
    }
}
