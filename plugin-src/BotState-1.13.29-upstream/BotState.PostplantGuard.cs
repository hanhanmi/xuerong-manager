using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private const float PostplantOuterRange = 1250f;
    private const float PostplantEarshot = 650f;
    private const float PostplantNearRange = 720f;
    private uint _postplantBomb;
    private Vector? _postplantTarget;
    private int _postplantSite = -1, _postplantPeekMoves, _postplantPeekReturns, _postplantArrivals, _postplantPatrols;
    private int _postplantLiving, _postplantReady, _postplantPatrolBudget;
    private int _postplantBombWatcher = -1;
    private float _postplantNextScan, _postplantBlowAt, _postplantNextOrders, _postplantNextPatrol;
    private bool _postplantDefusing;
    private double _postplantPatrolRoll;
    private readonly Dictionary<int, (uint Pawn, float Next, int Attempts)> _postplantRallyRetry = new();

    private static bool IsPostplantRoute(RolePlan plan) => plan.Role is "eco-postplant" or "t-postplant-return";

    private void ResetPostplantGuard()
    {
        foreach (int slot in _siteHolds.Where(entry => entry.Value.Postplant).Select(entry => entry.Key).ToArray())
            DropSiteHold(slot);
        foreach (int slot in _rolePlan.Where(entry => IsPostplantRoute(entry.Value)).Select(entry => entry.Key).ToArray())
            _rolePlan.Remove(slot);
        _postplantRallyRetry.Clear();
        _postplantBomb = 0;
        _postplantTarget = null;
        _postplantSite = -1;
        _postplantNextScan = _postplantBlowAt = 0f;
        _postplantNextOrders = _postplantNextPatrol = 0f;
        _postplantLiving = _postplantReady = _postplantPatrolBudget = 0;
        _postplantBombWatcher = -1;
        _postplantDefusing = false;
    }

    private void RefreshPostplantGuard(float now)
    {
        if (!IsDefusalCombatEnabled() || !_defusalBombPlanted)
        {
            if (_postplantBomb != 0 || _postplantRallyRetry.Count > 0) ResetPostplantGuard();
            return;
        }
        if (now < _postplantNextScan) return;
        var planted = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(b => b is { IsValid: true, BombTicking: true, BombDefused: false, AbsOrigin: not null });
        if (planted?.AbsOrigin is not { } pos || planted.C4Blow <= now)
        {
            if (_postplantBomb != 0) ResetPostplantGuard();
        }
        else
        {
            if (_postplantBomb != planted.EntityHandle.Raw)
            {
                if (_postplantBomb != 0) ResetPostplantGuard();
                ResolveSites();
                _postplantSite = SiteOf(pos, float.MaxValue);
                _postplantBomb = planted.EntityHandle.Raw;
                _postplantTarget = new Vector(pos.X, pos.Y, pos.Z + 12f);
                _postplantPatrolRoll = _random.NextDouble();
                _postplantNextPatrol = now + RandF(3.0f, 6.0f);
            }
            _postplantBlowAt = planted.C4Blow;
            _postplantDefusing = planted.BeingDefused;
            UpdatePostplantOrders(now);
        }
        _postplantNextScan = now + 0.16f;
    }

    private bool CanHoldPostplant(CCSPlayerPawn pawn, Vector pos, Vector target)
    {
        if (RetakeDistance(pos, target) > PostplantNearRange) return false;
        int nearby = SiteOf(pos, 850f);
        return (MathF.Abs(pos.Z - target.Z) < 165f && (nearby < 0 || _postplantSite < 0 || nearby == _postplantSite))
            || ClearPostplantLine(pawn, pos, target);
    }

    private void UpdatePostplantOrders(float now)
    {
        if (now < _postplantNextOrders || _postplantTarget is not { } target) return;
        _postplantNextOrders = now + 0.35f;
        _postplantLiving = AliveCount(CsTeam.Terrorist);
        _postplantReady = 0;
        _postplantBombWatcher = -1;
        float bestWatch = float.MaxValue;
        _postplantPatrolBudget = _postplantLiving switch
        {
            <= 2 => 0,
            3 => _postplantPatrolRoll < 0.27 ? 1 : 0,
            4 => _postplantPatrolRoll < 0.63 ? 1 : 0,
            _ => _postplantPatrolRoll < 0.19 ? 2 : _postplantPatrolRoll < 0.75 ? 1 : 0,
        };
        if (_postplantDefusing || _postplantBlowAt - now < 10f) _postplantPatrolBudget = 0;

        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(player, out var pawn, out var bot)) continue;
            int slot = player.Slot;
            var pos = pawn.AbsOrigin!;
            if (_rolePlan.TryGetValue(slot, out var attack) && !IsPostplantRoute(attack)) _rolePlan.Remove(slot);
            if (PostplantShouldSave(now))
            {
                if (_rolePlan.TryGetValue(slot, out var saved) && IsPostplantRoute(saved)) _rolePlan.Remove(slot);
                continue;
            }
            if (_siteHolds.TryGetValue(slot, out var hold) && hold.Postplant && hold.Bomb == _postplantBomb
                && hold.Pawn == pawn.EntityHandle.Raw)
            {
                if (GuardOwnsLocalMovement(slot, pawn, now) && RetakeDistance(pos, target) <= PostplantNearRange
                    && !PostplantPatrolling(slot) && !RetakeBusy(player, pawn, bot, now)
                    && !IsNonGunWeapon(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName))
                {
                    _postplantReady++;
                    float score = MathF.Abs(RetakeDistance(pos, target) - 300f);
                    if (hold.WatchBomb && score < bestWatch)
                    {
                        bestWatch = score;
                        _postplantBombWatcher = slot;
                    }
                }
                continue;
            }
            if (CanHoldPostplant(pawn, pos, target))
            {
                // Preserve the planned approach from the steal tactic until its final leg is reached.
                if (_rolePlan.TryGetValue(slot, out var local) && local.Role == "t-postplant-return") _rolePlan.Remove(slot);
                continue;
            }
            if (!_moveToReady || SiteHoldTravelActive(slot, postplant: true)) continue;
            var retry = _postplantRallyRetry.GetValueOrDefault(slot);
            if (retry.Pawn != pawn.EntityHandle.Raw) retry = default;
            if (now < retry.Next) continue;

            var ground = new Vector(target.X, target.Y, target.Z - 12f);
            var goal = retry.Attempts % 2 == 0 ? RotationApproach(pawn, ground, slot + retry.Attempts, 310f) ?? ground : ground;
            var plan = new RolePlan { Role = "t-postplant-return", Site = _postplantSite, StartAt = now, Arrive = 155f, Hurry = 6f };
            plan.Legs.Add((goal, 1, 0f));
            _rolePlan[slot] = plan;
            _postplantRallyRetry[slot] = (pawn.EntityHandle.Raw, now + Math.Min(9f, 3.5f + retry.Attempts * 1.5f), retry.Attempts + 1);
            ClearDefusalGuard(slot);
            CancelIdlePeek(slot);
        }
    }

    private SiteHold? ResolvePostplantGuard(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, Vector pos, bool routed, float now)
    {
        int slot = player.Slot;
        if (routed || SiteHoldTravelActive(slot, postplant: true) || _postplantTarget is not { } target
            || !DuelGrounded(pawn) || (now < _siteHoldRetry.GetValueOrDefault(slot))
            || TradeSupportActive(slot, pawn, now) || RetakeBusy(player, pawn, bot, now)
            || PostplantShouldSave(now)) return null;

        if (!CanHoldPostplant(pawn, pos, target))
        {
            _siteHoldEntries.Remove(slot);
            return null;
        }
        if (!_siteHoldEntries.TryGetValue(slot, out var entry) || entry.Pawn != pawn.EntityHandle.Raw || entry.Site != _postplantSite)
        {
            _siteHoldEntries[slot] = (pawn.EntityHandle.Raw, _postplantSite, now);
            return null;
        }
        if (now - entry.Since < 0.28f) return null;

        bool watchingBomb = ClearPostplantLine(pawn, pos, target);
        float bombYaw = MathF.Atan2(target.Y - pos.Y, target.X - pos.X) * 180f / MathF.PI;
        var hold = new SiteHold
        {
            Pawn = pawn.EntityHandle.Raw, Postplant = true, Bomb = _postplantBomb, Site = _postplantSite,
            Anchor = new Vector(pos.X, pos.Y, pos.Z), Yaw = watchingBomb ? bombYaw : pawn.EyeAngles.Y,
            WatchBomb = watchingBomb, NextShift = now + RandF(2.5f, 5f), NextPatrol = now + RandF(3f, 6f),
            Pattern = SiteGuardPattern.AllHold,
        };
        if (!watchingBomb) PickSiteAngle(slot, pawn, hold, change: false);
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        ClearStationaryGuardWatch(slot);
        ResetDefusalRecoveryForSlot(slot);
        _siteHolds[slot] = hold;
        _siteHoldEntries.Remove(slot);
        _postplantRallyRetry.Remove(slot);
        _postplantArrivals++;
        return hold;
    }

    private bool PostplantShouldSave(float now) => !_postplantDefusing && _postplantTarget != null
        && _postplantBlowAt - now <= 4.8f;

    private bool PostplantPatrolling(int slot) => _defusalGuards.TryGetValue(slot, out var guard) && guard.PostplantPatrol;

    private bool PostplantPatrolCanContinue(int slot, GuardState guard, SiteHold hold, Vector pos)
    {
        if (_postplantDefusing || _postplantPatrolBudget == 0 || _postplantReady < 2 || _postplantBombWatcher < 0
            || _postplantTarget is not { } target || RetakeDistance(pos, target) > PostplantNearRange + 155f
            || RetakeDistance(pos, hold.Anchor) >= HoldLeash(hold)) return false;
        int earlier = _defusalGuards.Count(p => p.Key != slot && p.Value.PostplantPatrol
            && (p.Value.RunAt < guard.RunAt || p.Value.RunAt == guard.RunAt && p.Key < slot));
        return earlier < _postplantPatrolBudget;
    }

    private static readonly TraceOptions PostplantSight = new() { InteractsWith = Masks.SolidBrushOnly };

    private bool ClearPostplantLine(CCSPlayerPawn pawn, Vector pos, Vector target)
    {
        var eye = new Vector(pos.X, pos.Y, pos.Z + 62f);
        if (SegmentThroughSmoke(eye, target, SmokeEdge)) return false;
        try
        {
            var hit = Trace.TraceEndShape(eye, target, pawn, PostplantSight);
            return !hit.IsAllSolid && hit.Fraction >= 0.985f;
        }
        catch { return false; }
    }

    private bool PostplantChecksBomb(int slot, SiteHold hold, Vector pos) => !PostplantPatrolling(slot)
        && ((hold.WatchBomb && (_postplantBombWatcher < 0 || _postplantBombWatcher == slot))
            || (_postplantDefusing && _postplantTarget != null && RetakeDistance(pos, _postplantTarget) <= PostplantEarshot));

    private void RefreshPostplantAngle(int slot, CCSPlayerPawn pawn, SiteHold hold, Vector pos, float now)
    {
        if (_postplantTarget is not { } target) return;
        if (now >= hold.NextShift)
        {
            hold.NextShift = now + RandF(2.3f, 4.0f);
            hold.WatchBomb = ClearPostplantLine(pawn, pos, target);
            if (slot != _postplantBombWatcher || !hold.WatchBomb) PickSiteAngle(slot, pawn, hold, change: true);
        }
    }

    private bool StartPostplantPatrol(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now)
    {
        var hold = s.Assignment!;
        if (_postplantTarget is not { } target || now < hold.NextPatrol || now < _postplantNextPatrol
            || _postplantPatrolBudget == 0 || _postplantDefusing || _postplantReady < 3
            || now - bot.LastSawEnemyTimestamp < 3f || RetakeDistance(pos, target) >= PostplantNearRange
            || (_reaction.TryGetValue(slot, out var hurt) && now - hurt.LastHurtAt < 3f)
            || _defusalGuards.Values.Count(g => g.PostplantPatrol) >= _postplantPatrolBudget)
            return false;

        // Leave the bomb watcher in place and sweep only the nearby approach.
        if (_postplantBombWatcher < 0 || _postplantBombWatcher == slot) return false;
        float yaw = MathF.Atan2(target.Y - pos.Y, target.X - pos.X);
        if (RetakeDistance(pos, target) < 70f) yaw = hold.Yaw * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float length = RandF(96f, 137f);
        if (!TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Roam, GuardAction.StandingPeek, fx, fy, -0.7f, length)
            && !TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Roam, GuardAction.StandingPeek, fx, fy, 0f, 101f))
        {
            hold.NextPatrol = now + RandF(1.3f, 2.1f);
            return false;
        }
        s.PostplantPatrol = true;
        _postplantReady--;
        hold.NextPatrol = now + RandF(8f, 13f);
        _postplantNextPatrol = now + (_postplantPatrolBudget > 1 ? RandF(0.55f, 1f) : RandF(2.0f, 3.8f));
        return true;
    }

    private bool StartPostplantPeek(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now)
    {
        var hold = s.Assignment!;
        var target = _postplantTarget!;
        if (StartPostplantPatrol(slot, pawn, bot, s, pos, now)) return true;
        bool checkingC4 = PostplantChecksBomb(slot, hold, pos);
        bool urgent = checkingC4 && _postplantDefusing;
        float viewYaw = pawn.EyeAngles.Y;
        float laneYaw = checkingC4 ? MathF.Atan2(target.Y - pos.Y, target.X - pos.X) * 180f / MathF.PI
            : SiteWatchYaw(slot, pawn, pos, hold, now);
        float yaw = (viewYaw + Math.Clamp((float)NormalizeAngleDeg(laneYaw - viewYaw), -14f, 14f)) * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float side = s.LastSide == 0f ? (_random.Next(2) == 0 ? 1f : -1f) : -s.LastSide;
        float best = float.NegativeInfinity, dxBest = 0f, dyBest = 0f, lengthBest = 0f, sideBest = side;
        for (int pass = 0; pass < 2 && lengthBest == 0f; pass++)
        {
            if (pass == 1 && !RollDiagonalGuardPeek(s, now, urgent)) break;
            float along = pass == 0 ? 0f : urgent ? 0.36f : -0.32f;
            float scale = 1f / MathF.Sqrt(1f + along * along);
            for (int size = 0; size < 3; size++)
            for (int i = 0; i < 2; i++)
            {
                float length = size == 0 ? urgent ? 55f : 43f : size == 1 ? 31f : 23f;
                float lateral = i == 0 ? side : -side;
                float dx = (fx * along - fy * lateral) * scale, dy = (fy * along + fx * lateral) * scale;
                if (!GuardRouteClear(pawn, s, pos, dx, dy, length + 8f, standing: true)) continue;
                var edge = new Vector(pos.X + dx * length, pos.Y + dy * length, pos.Z);
                bool line = checkingC4 && ClearPostplantLine(pawn, edge, target);
                if (checkingC4 && hold.WatchBomb && !urgent && !line) continue;
                float score = (line ? 100f : 0f) + length * 0.36f + (i == 0 ? 11f : 0f)
                    - RetakeDistance(edge, hold.Anchor) * 0.11f;
                if (urgent && !line) score += (RetakeDistance(pos, target) - RetakeDistance(edge, target)) * 0.7f;
                if (score <= best) continue;
                best = score;
                dxBest = dx;
                dyBest = dy;
                lengthBest = length;
                sideBest = lateral;
            }
        }
        if (lengthBest == 0f || !StartGuardStand(slot, bot, s)) return false;
        s.LateralPeek = true;
        s.Until = now + 0.20f + lengthBest / 125f;
        if (!DriveGuardLeg(slot, pawn, s, dxBest, dyBest, 0.58f))
        {
            ReleaseGuardPose(slot, s);
            return false;
        }
        s.Phase = GuardPhase.Out;
        s.StartX = pos.X;
        s.StartY = pos.Y;
        s.Dx = dxBest;
        s.Dy = dyBest;
        s.LastSide = sideBest;
        s.RoamDistance = lengthBest;
        s.RunAt = now;
        s.UpdateAt = now + 0.06f;
        s.MeasuredPostplantPeek = false;
        s.MeasuredSitePeek = false;
        return true;
    }

    private string PostplantGuardStatus()
        => $"[Smarter-Bot] T-postplant site={RetakeSiteLabel(_postplantSite)} alive={_postplantLiving} guards={_postplantReady} rallying={_rolePlan.Values.Count(IsPostplantRoute)}"
            + $" moving={_defusalGuards.Values.Count(g => g.Assignment is { Postplant: true } && g.Phase is GuardPhase.Out or GuardPhase.Back)}"
            + $" outside={_defusalGuards.Values.Count(g => g.PostplantPatrol)}/{_postplantPatrolBudget} patrols={_postplantPatrols}"
            + $" arrived={_postplantArrivals} peeksMoved={_postplantPeekMoves} returned={_postplantPeekReturns}";
}
