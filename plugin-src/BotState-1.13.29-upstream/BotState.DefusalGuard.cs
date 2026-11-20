using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private enum GuardPhase { Hold, Out, Back, Shift, Runup, Crouch, Roam }
    private enum GuardAction { StandingPeek, CrouchCover, CrouchAdvance }

    private const ulong GuardDuckButton = (ulong)PlayerButtons.Duck;
    private const float GuardRadius = 225f;
    private static readonly TraceOptions GuardHeadTrace = new() { InteractsWith = Masks.PlayerSolid };
    private static readonly Vector GuardHeadMins = new(-14f, -14f, 0f);
    private static readonly Vector GuardHeadMaxs = new(14f, 14f, 16f);

    private sealed class GuardState
    {
        public GuardPhase Phase;
        public GuardAction Action;
        public uint Pawn;
        public int Site, Peeks;
        public float IdleSince = -1f, NextAt, Until, UpdateAt, StartX, StartY, AnchorX, AnchorY, Dx, Dy, LastSide;
        public float RunAt, DuckAt, DuckProgress, SlowSince = -1f;
        public long StandToken, DuckToken;
        public bool MeasuredCrouch;
        public bool MeasuredPostplantPeek, PostplantPatrol;
        public bool MeasuredSitePeek;
        public float BackBest, BackProgressAt;
        public bool LateralPeek;
        public float NextDiagonalPeek;
        public SiteHold? Assignment;
        public float RoamDistance, BestRoamProgress, RoamProgressAt;
    }

    private readonly Dictionary<int, GuardState> _defusalGuards = new();
    private readonly Dictionary<int, (uint Pawn, float Since, float X, float Y)> _stationaryGuardWatch = new();
    private readonly Dictionary<int, (long Token, float Until)> _staticStandTokens = new();
    private int _guardPeeks, _guardReturns, _guardShifts;
    private int _guardCrouchHolds, _guardCrouchPushes, _guardCrouchAborts, _guardIdleRescues;
    private int _guardRelocations, _guardRelocated;
    private readonly int[] _ctPeekMoved = new int[2], _ctPeekBack = new int[2];
    private readonly int[] _tPeekMoved = new int[2], _tPeekBack = new int[2];

    private void ClearDefusalGuard(int slot)
    {
        ClearSiteHoldLook(slot);
        if (!_defusalGuards.Remove(slot, out var s)) return;
        if (s.PostplantPatrol && s.Assignment is { Postplant: true } hold)
        {
            hold.Returning = true;
            hold.NextReturn = 0f;
        }
        ReleaseGuardPose(slot, s);
        if (s.Assignment != null || s.Phase != GuardPhase.Hold) ReleaseMove(slot, MoveOwner.Peek);
    }

    private void ClearStationaryGuardWatch(int slot)
    {
        _stationaryGuardWatch.Remove(slot);
        if (!_staticStandTokens.Remove(slot, out var pose) || _botController == null || pose.Token <= 0) return;
        try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, pose.Token); } catch { }
    }

    private void ExpireStationaryGuard(float now)
    {
        foreach (var entry in _staticStandTokens.Where(entry => now >= entry.Value.Until).ToArray())
            ClearStationaryGuardWatch(entry.Key);
    }

    private void ResetDefusalGuards()
    {
        foreach (int slot in _defusalGuards.Keys.ToArray()) ClearDefusalGuard(slot);
        foreach (int slot in _staticStandTokens.Keys.ToArray()) ClearStationaryGuardWatch(slot);
        _stationaryGuardWatch.Clear();
    }

    // A native hold or a stale movement intent can strand a guard in either pose.
    // Watch actual displacement and retry a short, safe angle check after a pause.
    private void UpdateStationaryGuard(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        int slot = player.Slot;
        var pos = pawn.AbsOrigin;
        string? weapon = pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        bool duel = _duels.TryGetValue(slot, out var duelState) && duelState.Phase != DuelPhase.None;
        bool traveling = pos != null && _rolePlan.TryGetValue(slot, out var plan)
            && plan.Leg < plan.Legs.Count
            && (KeepOwnPlan(plan) || RetakeDistance(pos, plan.Legs[plan.Leg].goal) > plan.Arrive + 75f);
        var owner = ActiveMoveOwner(slot, now);
        bool exempt = !IsDefusalCombatEnabled() || pos == null || !DuelGrounded(pawn)
            || IsNonGunWeapon(weapon) || DuelFlashActive(pawn, idx, now) || DefusalObjectiveBusy(pawn)
            || _cachedNearLadder.GetValueOrDefault(idx) || traveling || HasDefusalRetakeRoute(slot)
            || _defusalBombPlanted
            || GuardOwnsLocalMovement(slot, pawn, now) || TradeSupportActive(slot, pawn, now) || owner > MoveOwner.GuardRecovery
            || bot.IsEnemyVisible || bot.IsAttacking || duel
            || (_botController != null && BotControllerBridge.IsViewControlled(_botController, slot));
        if (exempt)
        {
            ClearStationaryGuardWatch(slot);
            return;
        }
        if (owner == MoveOwner.GuardRecovery)
        {
            _stationaryGuardWatch.Remove(slot);
            return;
        }

        bool crouched = bot.IsCrouching || (pawn.MovementServices is { Handle: not 0 } && DuelDuckAmount(pawn) >= 0.65f);
        uint identity = pawn.EntityHandle.Raw;
        if (!_stationaryGuardWatch.TryGetValue(slot, out var watch) || watch.Pawn != identity)
        {
            _stationaryGuardWatch[slot] = (identity, now, pos!.X, pos.Y);
            return;
        }
        float dx = pos!.X - watch.X, dy = pos.Y - watch.Y;
        if (dx * dx + dy * dy >= 8f * 8f)
        {
            _stationaryGuardWatch[slot] = (identity, now, pos.X, pos.Y);
            return;
        }
        if (now - watch.Since < (crouched ? 0.38f : 1.0f)) return;

        bool canStand = GuardCanStand(pawn, pos);
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        ClearStationaryGuardWatch(slot);
        if (canStand) bot.IsCrouching = false;
        if (canStand && _botController != null)
        {
            try
            {
                long token = BotControllerBridge.StartUsercmdSuppression(_botController, slot, GuardDuckButton);
                if (token > 0) _staticStandTokens[slot] = (token, now + 0.40f);
            }
            catch { }
        }
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float side = _random.Next(2) == 0 ? 1f : -1f;
        foreach (var move in new[] { (fwd: 0f, left: side), (fwd: 0f, left: -side),
                     (fwd: -0.7f, left: side * 0.7f), (fwd: -0.7f, left: -side * 0.7f),
                     (fwd: -1f, left: 0f), (fwd: 0.45f, left: side * 0.7f),
                     (fwd: 0.45f, left: -side * 0.7f), (fwd: 0.6f, left: 0f) })
        {
            float wx = fx * move.fwd - fy * move.left, wy = fy * move.fwd + fx * move.left;
            float length = MathF.Sqrt(wx * wx + wy * wy);
            wx /= length; wy /= length;
            if (!IsDirectionClear(pawn, wx, wy, 34f)) continue;
            if (player.TeamNum == (byte)CsTeam.CounterTerrorist && !_defusalBombPlanted && _tSpawn != null
                && Dist2D(new Vector(pos.X + wx * 34f, pos.Y + wy * 34f, pos.Z), _tSpawn) + 30f < Dist2D(pos, _tSpawn)) continue;
            if (SetMove(slot, MoveOwner.GuardRecovery, move.fwd * 0.75f, move.left * 0.75f, now + RandF(0.35f, 0.55f)))
            {
                _guardIdleRescues++;
                break;
            }
        }
        ResetLookAroundForBot(player);
    }

    private bool GuardCanStand(CCSPlayerPawn pawn, Vector pos)
    {
        try
        {
            var start = new Vector(pos.X, pos.Y, pos.Z + 54f);
            var end = new Vector(pos.X, pos.Y, pos.Z + 55f);
            var hit = Trace.TraceHullShape(start, end, GuardHeadMins, GuardHeadMaxs, pawn, GuardHeadTrace);
            return !hit.IsAllSolid && hit.Fraction >= 0.96f;
        }
        catch { return false; }
    }

    // Respect live orders and native travel before asking an idle guard to check a nearby angle.
    private bool UpdateDefusalGuard(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        int slot = player.Slot;
        bool ct = player.TeamNum == (byte)CsTeam.CounterTerrorist;
        bool t = player.TeamNum == (byte)CsTeam.Terrorist;
        if (_botController == null || (!t && !ct) || (_defusalBombPlanted && !t))
        {
            ClearDefusalGuard(slot);
            return true;
        }

        var pos = pawn.AbsOrigin;
        uint identity = pawn.EntityHandle.Raw;
        bool inGuardMove = _defusalGuards.TryGetValue(slot, out var currentGuard)
            && currentGuard.Pawn == identity && currentGuard.Phase != GuardPhase.Hold;
        string? gun = pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        bool routed = pos != null && _rolePlan.TryGetValue(slot, out var plan)
            && (!_defusalBombPlanted || IsPostplantRoute(plan))
            && plan.Leg < plan.Legs.Count && (IsCtSiteDelivery(plan) || KeepOwnPlan(plan)
                || RetakeDistance(pos, plan.Legs[plan.Leg].goal) > plan.Arrive + 75f);
        ResolveSites();
        var siteHold = pos is { } sitePos ? ResolveSiteHold(player, pawn, bot, sitePos, routed, now) : null;
        bool postplant = t && _defusalBombPlanted;
        if (postplant && PostplantShouldSave(now))
        {
            DropSiteHold(slot);
            ClearDefusalGuard(slot);
            return true;
        }
        if (siteHold != null && !_defusalGuards.ContainsKey(slot)) { currentGuard = null; inGuardMove = false; }
        bool supporting = TradeSupportActive(slot, pawn, now);
        if ((postplant && siteHold == null) || routed || HasDefusalRetakeRoute(slot) || pos == null || IsNonGunWeapon(gun) || DefusalObjectiveBusy(pawn) || bot.IsEnemyVisible
            || bot.IsAttacking || supporting || _smokeHold.ContainsKey(slot)
            || (siteHold == null && (bot.IsWaitingBehindFriend || (bot.IsStuck && !inGuardMove)))
            || (!_defusalBombPlanted && _rotateUntil.GetValueOrDefault(slot) > now && _rolePlan.ContainsKey(slot))
            || !DuelGrounded(pawn) || _cachedNearLadder.GetValueOrDefault(idx) || DuelFlashActive(pawn, idx, now)
            || (ActiveMoveOwner(slot, now) > MoveOwner.Peek
                && !(siteHold is { Postplant: false } && now < siteHold.EscapeUntil && ActiveMoveOwner(slot, now) == MoveOwner.GuardRecovery))
            || BotControllerBridge.IsViewControlled(_botController, slot))
        {
            ClearDefusalGuard(slot);
            if (siteHold is { Postplant: false } && siteHold.EscapeAttempts > 0)
            {
                ReleaseMove(slot, MoveOwner.GuardRecovery);
                siteHold.EscapeUntil = 0f;
                siteHold.EscapeAttempts = 0;
                siteHold.MotionAt = -1f;
            }
            return true;
        }

        if (t && !_defusalBombPlanted && !inGuardMove && !bot.IsCrouching
            && (bot.IsRunning || bot.LookAtSpotDuration <= 0f
                || now < bot.LookAtSpotTimestamp || now - bot.LookAtSpotTimestamp >= bot.LookAtSpotDuration))
        {
            ClearDefusalGuard(slot);
            return true;
        }

        if (siteHold is { Postplant: false, EscapeAttempts: > 0 } && !InHoldZone(siteHold, pos))
        {
            ReleaseMove(slot, MoveOwner.GuardRecovery);
            siteHold.EscapeUntil = 0f;
            siteHold.EscapeAttempts = 0;
            siteHold.MotionAt = -1f;
        }
        if (siteHold is { Postplant: false, Returning: false, EscapeAttempts: > 0 }
            && UpdateGuardRelocation(player, pawn, bot, siteHold, pos, now)) return true;
        if (siteHold != null && ReturnToSiteHold(slot, bot, siteHold, pos, now)) return true;
        if (siteHold is { Postplant: false } && UpdateGuardRelocation(player, pawn, bot, siteHold, pos, now)) return true;
        int site = siteHold?.Site ?? SiteOf(pos, 1300f);
        if (currentGuard == null || currentGuard.Pawn != identity || currentGuard.Assignment != siteHold)
        {
            ClearDefusalGuard(slot);
            CancelIdlePeek(slot);
            _defusalGuards[slot] = currentGuard = new GuardState { Site = site, Pawn = identity,
                AnchorX = pos.X, AnchorY = pos.Y, Assignment = siteHold };
        }

        var s = currentGuard;
        if (siteHold != null)
        {
            if (siteHold.Postplant && s.Phase == GuardPhase.Hold) RefreshPostplantAngle(slot, pawn, siteHold, pos, now);
            else if (!siteHold.Postplant && s.Phase == GuardPhase.Hold && now >= siteHold.NextLaneCheck)
            {
                siteHold.NextLaneCheck = now + RandF(4.5f, 7.0f);
                PickSiteAngle(slot, pawn, siteHold, change: false);
            }
            WatchSiteAngle(slot, pawn, bot, pos, siteHold, now);
        }
        if (s.Phase != GuardPhase.Hold)
        {
            AdvanceGuardMove(slot, pawn, bot, s, pos, now);
            return true;
        }

        if (siteHold != null)
        {
            // Keep native path input from resuming between local movement legs.
            SetMove(slot, MoveOwner.Peek, 0f, 0f, now + 0.10f);
            if (s.StandToken == 0 && GuardCanStand(pawn, pos)) StartGuardStand(slot, bot, s);
        }
        else if (DuelSpeedSquared(pawn) > 32f * 32f || HasRecoveryMoveIntent(pawn, bot, null, out _, out _))
        {
            s.IdleSince = -1f;
            return true;
        }
        if (s.IdleSince < 0f) s.IdleSince = now;
        bool defuseCheck = siteHold is { Postplant: true } && _postplantDefusing && PostplantChecksBomb(slot, siteHold, pos);
        float nextAt = s.NextAt - (defuseCheck ? 0.25f : 0f);
        if (now < nextAt || now - s.IdleSince < (defuseCheck ? 0f : siteHold == null ? 0.30f : 0.07f)) return true;

        if (s.Site != site || GuardDistanceSquared(pos.X, pos.Y, s.AnchorX, s.AnchorY) > 180f * 180f)
        {
            s.Site = site;
            s.AnchorX = pos.X;
            s.AnchorY = pos.Y;
            s.Peeks = 0;
        }
        if (_defusalGuards.Any(kv => kv.Key != slot && kv.Value.Phase != GuardPhase.Hold
            && GuardDistanceSquared(pos.X, pos.Y, kv.Value.StartX, kv.Value.StartY) < (siteHold == null ? 175f * 175f : 67f * 67f)))
        {
            s.NextAt = now + RandF(0.12f, 0.24f);
            return true;
        }

        if (!StartGuardMove(player, pawn, bot, s, pos, now, IsSniperWeapon(gun)))
        {
            // Geometry blocks a move: check a different line without forcing an unsafe route.
            if (siteHold != null)
            {
                if (!siteHold.Postplant && now + 3.6f >= siteHold.NextLaneCheck)
                {
                    siteHold.NextLaneCheck = now + RandF(4.5f, 6f);
                    PickSiteAngle(slot, pawn, siteHold, change: true);
                }
                WatchSiteAngle(slot, pawn, bot, pos, siteHold, now);
            }
            else ResetLookAroundForBot(player);
            s.NextAt = now + (postplant ? RandF(0.33f, 0.52f) : RandF(0.16f, 0.25f));
        }
        return true;
    }

    private bool StartGuardMove(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, GuardState s,
        Vector pos, float now, bool scoped)
    {
        if (s.Assignment is { Postplant: true }) return StartPostplantPeek(player.Slot, pawn, bot, s, pos, now);
        bool assigned = s.Assignment != null;
        float viewYaw = pawn.EyeAngles.Y;
        float laneYaw = s.Assignment is { } guard ? SiteWatchYaw(player.Slot, pawn, pos, guard, now) : viewYaw;
        float yaw = (viewYaw + Math.Clamp((float)NormalizeAngleDeg(laneYaw - viewYaw), -14f, 14f)) * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        int draw = _random.Next(120); // 98 standing peeks / 8 crouched cover shifts / 14 crouched advances.
        int slot = player.Slot;
        if (s.Assignment is { } hold && now >= hold.NextShift && hold.Pattern != SiteGuardPattern.AllHold)
        {
            hold.NextShift = now + RandF(1.0f, 2.0f);
            bool quiet = now - bot.LastSawEnemyTimestamp > 2.2f
                && (!_reaction.TryGetValue(slot, out var reaction) || now - reaction.LastHurtAt > 2f);
            bool patrol = hold.Pattern == SiteGuardPattern.Patrol;
            if (quiet && now >= _sitePatternNext[hold.Site] && SiteShiftHasCover(slot, hold, now))
            {
                float length = patrol ? RandF(78f, 110f) : RandF(53f, 76f);
                if (TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Roam,
                        GuardAction.StandingPeek, fx, fy, patrol ? -0.50f : -0.24f, length)
                    || TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Roam,
                        GuardAction.StandingPeek, fx, fy, -1.4f, length * 0.79f))
                {
                    hold.NextShift = now + (patrol ? RandF(4.5f, 8f) : RandF(6.5f, 10f));
                    _sitePatternNext[hold.Site] = now + (patrol ? RandF(2.5f, 4.0f) : RandF(4.2f, 6.5f));
                    return true;
                }
            }
        }
        if (draw >= 98)
        {
            var action = draw < 106 ? GuardAction.CrouchCover : GuardAction.CrouchAdvance;
            bool alongCover = action == GuardAction.CrouchCover;
            bool coverNearby = !alongCover || SightDistance(pawn, fx, fy, 0f, 0f, 0f, 620f) < 590f;
            if (coverNearby && TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Runup, action, fx, fy,
                    0f, assigned ? 52f : alongCover ? 96f : 118f)) return true;
        }
        if (s.Peeks >= (scoped ? 4 : 3) && TryGuardLeg(player.Slot, pawn, bot, s, pos, now, GuardPhase.Shift,
                GuardAction.StandingPeek, fx, fy, 0f, assigned ? 35f : 96f))
        {
            ResetLookAroundForBot(player);
            return true;
        }
        if (TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Out, GuardAction.StandingPeek,
                fx, fy, 0f, assigned ? RandF(31f, 41f) : 52f)
            || TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Out, GuardAction.StandingPeek,
                fx, fy, 0f, 24f)) return true;
        if (!RollDiagonalGuardPeek(s, now, urgent: false)) return false;
        // A shallow retreat avoids exposing more of the bot when the direct sides are blocked.
        return TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Out, GuardAction.StandingPeek,
            fx, fy, -0.36f, 28f);
    }

    private bool RollDiagonalGuardPeek(GuardState s, float now, bool urgent)
    {
        if (now < s.NextDiagonalPeek) return false;
        s.NextDiagonalPeek = now + 1.25f;
        return _random.NextDouble() < (urgent ? 0.24 : 0.12);
    }

    private bool TryGuardLeg(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now,
        GuardPhase phase, GuardAction action, float fx, float fy, float along, float clearance)
    {
        float left = s.LastSide == 0f ? (_random.Next(2) == 0 ? -1f : 1f) : -s.LastSide;
        if (s.LastSide == 0f && phase == GuardPhase.Out)
        {
            float leftSight = SightDistance(pawn, fx, fy, -fy, fx, 43f, 1250f);
            float rightSight = SightDistance(pawn, fx, fy, fy, -fx, 43f, 1250f);
            if (MathF.Abs(leftSight - rightSight) > 160f) left = leftSight > rightSight ? 1f : -1f;
        }
        float scale = 1f / MathF.Sqrt(1f + along * along);
        bool started = false;
        for (int attempt = 0; attempt < 2; attempt++, left = -left)
        {
            float dx = (fx * along - fy * left) * scale, dy = (fy * along + fx * left) * scale;
            if (!GuardRouteClear(pawn, s, pos, dx, dy, clearance, standing: true)) continue;
            if (!StartGuardStand(slot, bot, s)) return false;
            s.Action = action;
            s.LateralPeek = phase != GuardPhase.Roam;
            s.LastSide = left;
            s.Dx = dx;
            s.Dy = dy;
            s.RoamDistance = clearance;
            s.Until = now + (phase switch
            {
                GuardPhase.Runup => 0.40f,
                GuardPhase.Roam => clearance / 85f + 0.35f,
                GuardPhase.Shift => s.Assignment == null ? RandF(0.37f, 0.47f) : RandF(0.26f, 0.34f),
                _ => RandF(0.25f, 0.33f),
            });
            if (!DriveGuardLeg(slot, pawn, s, dx, dy, phase == GuardPhase.Runup ? 0.90f : s.Assignment != null && phase != GuardPhase.Roam ? 0.58f : 0.72f))
            {
                ReleaseGuardPose(slot, s);
                return false;
            }
            if (phase == GuardPhase.Roam)
            {
                s.RoamDistance = clearance;
                s.BestRoamProgress = 0f;
                s.RoamProgressAt = now;
            }
            started = true;
            break;
        }
        if (!started) return false;

        s.StartX = pos.X;
        s.StartY = pos.Y;
        s.Phase = phase;
        s.RunAt = now;
        s.UpdateAt = now + 0.06f;
        s.MeasuredCrouch = false;
        s.MeasuredSitePeek = false;
        if (phase is GuardPhase.Shift or GuardPhase.Roam) { _guardShifts++; s.Peeks = 0; }
        if (phase == GuardPhase.Out) { _guardPeeks++; s.Peeks++; }
        return true;
    }

    private void AdvanceGuardMove(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now)
    {
        float backX = s.StartX - pos.X, backY = s.StartY - pos.Y;
        float fromStart = MathF.Sqrt(backX * backX + backY * backY);
        float velocity = pawn.AbsVelocity.X * s.Dx + pawn.AbsVelocity.Y * s.Dy;
        float progress = -(backX * s.Dx + backY * s.Dy);
        bool postplant = s.Assignment is { Postplant: true };
        bool local = s.Assignment != null;
        if (local && s.Phase == GuardPhase.Out)
        {
            if (!s.MeasuredSitePeek && progress >= 14f && s.Assignment!.Site is 0 or 1)
            {
                s.MeasuredSitePeek = true;
                (postplant ? _tPeekMoved : _ctPeekMoved)[s.Assignment.Site]++;
            }
            if (postplant && !s.MeasuredPostplantPeek && progress >= 14f)
            {
                s.MeasuredPostplantPeek = true;
                _postplantPeekMoves++;
            }
            if (progress >= s.RoamDistance - 7f || now - s.RunAt > 0.26f && progress < 6f)
            {
                ReturnGuardToCover(slot, pawn, bot, s, pos, now);
                return;
            }
        }
        if (s.Phase == GuardPhase.Roam && s.Assignment is { } hold)
        {
            if (progress >= s.BestRoamProgress + 8f)
            {
                s.BestRoamProgress = progress;
                s.RoamProgressAt = now;
            }
            if (now < s.UpdateAt && now < s.Until) return;
            if (progress < s.RoamDistance - 14f && now < s.Until && now - s.RoamProgressAt < 0.38f
                && (postplant ? PostplantPatrolCanContinue(slot, s, hold, pos) : SiteShiftHasCover(slot, hold, now))
                && GuardRouteClear(pawn, s, pos, s.Dx, s.Dy, Math.Clamp(s.RoamDistance - progress, 23f, 38f), standing: true)
                && DriveGuardLeg(slot, pawn, s, s.Dx, s.Dy, 0.67f))
            {
                s.UpdateAt = now + 0.09f;
                return;
            }
            if (postplant)
            {
                if (progress >= 55f) _postplantPatrols++;
                ReturnGuardToCover(slot, pawn, bot, s, pos, now);
                return;
            }
            if (progress >= 40f)
            {
                PickSiteAngle(slot, pawn, hold, change: true);
                if (hold.Pattern == SiteGuardPattern.Patrol) _siteHoldPatrols++;
                else _siteHoldShifts++;
            }
            EndGuardMove(slot, s, now);
            return;
        }
        if (s.Phase == GuardPhase.Runup)
        {
            if (now - s.RunAt >= 0.10f && velocity >= 72f && progress >= 9f && DuelDuckAmount(pawn) < 0.12f)
            {
                if (!GuardRouteClear(pawn, s, pos, s.Dx, s.Dy, local ? 32f : 50f, standing: false)
                    || !StartGuardCrouch(slot, pawn, s, now, progress))
                    AbortGuardCrouch(slot, pawn, bot, s, pos, now);
                return;
            }
            if (now >= s.Until)
            {
                AbortGuardCrouch(slot, pawn, bot, s, pos, now);
                return;
            }
        }
        else if (s.Phase == GuardPhase.Crouch)
        {
            if (now - s.DuckAt >= 0.25f && velocity < 18f)
            {
                if (s.SlowSince < 0f) s.SlowSince = now;
                if (now - s.SlowSince >= 0.16f)
                {
                    AbortGuardCrouch(slot, pawn, bot, s, pos, now);
                    return;
                }
            }
            else s.SlowSince = -1f;

            float movedCrouched = progress - s.DuckProgress;
            if (!s.MeasuredCrouch && movedCrouched >= 28f && velocity >= 28f && DuelDuckAmount(pawn) >= 0.65f)
            {
                s.MeasuredCrouch = true;
                if (s.Action == GuardAction.CrouchCover) _guardCrouchHolds++; else _guardCrouchPushes++;
            }
            if (now >= s.Until || (now - s.DuckAt >= 0.54f && movedCrouched >= 76f) || fromStart >= (local ? 55f : 142f))
            {
                if (s.Action == GuardAction.CrouchCover || pawn.TeamNum == (byte)CsTeam.CounterTerrorist) ReturnGuardToCover(slot, pawn, bot, s, pos, now);
                else EndGuardMove(slot, s, now);
                return;
            }
        }
        else if (now >= s.Until || (s.Phase == GuardPhase.Back && (fromStart < 13f || local && now - s.BackProgressAt > 0.34f))
            || (local && s.Phase == GuardPhase.Shift && progress >= s.RoamDistance - 7f))
        {
            if (s.Phase == GuardPhase.Out) ReturnGuardToCover(slot, pawn, bot, s, pos, now);
            else
            {
                if (s.Phase == GuardPhase.Back && fromStart < 24f)
                {
                    if (s.MeasuredSitePeek && s.Assignment is { Site: 0 or 1 } measured && fromStart < 13f)
                        (postplant ? _tPeekBack : _ctPeekBack)[measured.Site]++;
                    if (!postplant) _guardReturns++;
                    else if (s.MeasuredPostplantPeek && !s.PostplantPatrol) _postplantPeekReturns++;
                }
                else if (postplant && s.Phase == GuardPhase.Back && s.Assignment is { } patrolHold
                    && RetakeDistance(pos, patrolHold.Anchor) > 80f) patrolHold.Returning = true;
                EndGuardMove(slot, s, now);
            }
            return;
        }

        bool crouched = s.Phase == GuardPhase.Crouch;
        if (!crouched) bot.IsCrouching = false;
        if (now < s.UpdateAt) return;
        s.UpdateAt = now + 0.06f;
        bool back = s.Phase == GuardPhase.Back;
        if (back && fromStart < s.BackBest - 3f)
        {
            s.BackBest = fromStart;
            s.BackProgressAt = now;
        }
        float dx = back ? backX / fromStart : s.Dx;
        float dy = back ? backY / fromStart : s.Dy;
        float lookAhead = back ? Math.Min(fromStart, 32f) : crouched ? 24f
            : local && s.Phase is (GuardPhase.Out or GuardPhase.Shift) ? Math.Clamp(s.RoamDistance - progress, 16f, 31f) : 35f;
        float power = s.Phase is GuardPhase.Runup or GuardPhase.Crouch ? 0.90f
            : back && local ? 0.93f : local ? 0.58f : 0.72f;
        if ((back ? GuardBackClear(pawn, s, pos, dx, dy, lookAhead) : GuardRouteClear(pawn, s, pos, dx, dy, lookAhead, standing: !crouched))
            && DriveGuardLeg(slot, pawn, s, dx, dy, power)) return;
        if (s.Phase is GuardPhase.Runup or GuardPhase.Crouch) AbortGuardCrouch(slot, pawn, bot, s, pos, now);
        else if (s.Phase == GuardPhase.Out) ReturnGuardToCover(slot, pawn, bot, s, pos, now);
        else
        {
            if (postplant && s.Assignment is { } patrolHold
                && (s.PostplantPatrol || RetakeDistance(pos, patrolHold.Anchor) > 80f)) patrolHold.Returning = true;
            EndGuardMove(slot, s, now);
        }
    }

    private void AbortGuardCrouch(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now)
    {
        _guardCrouchAborts++;
        ReturnGuardToCover(slot, pawn, bot, s, pos, now);
        if (s.Phase != GuardPhase.Hold) return;
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        TryGuardLeg(slot, pawn, bot, s, pos, now, GuardPhase.Shift, GuardAction.StandingPeek,
            MathF.Cos(yaw), MathF.Sin(yaw), 0f, 27f);
    }

    private void ReturnGuardToCover(int slot, CCSPlayerPawn pawn, CCSBot bot, GuardState s, Vector pos, float now)
    {
        // The outward peek is lateral-only; returning to the original cover may use its direct line.
        s.LateralPeek = false;
        float dx = s.StartX - pos.X, dy = s.StartY - pos.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        bool returnSpace = distance >= 13f && GuardBackClear(pawn, s, pos, dx / distance, dy / distance,
            Math.Min(distance, 32f));
        if (returnSpace && StartGuardStand(slot, bot, s))
        {
            s.Until = now + Math.Clamp(distance / 100f + 0.28f, 0.38f, s.PostplantPatrol ? 1.6f : 1.0f);
            if (DriveGuardLeg(slot, pawn, s, dx / distance, dy / distance, s.Assignment != null ? 0.93f : 0.78f))
            {
                s.Phase = GuardPhase.Back;
                s.BackBest = distance;
                s.BackProgressAt = now;
                s.UpdateAt = now + 0.06f;
                return;
            }
        }
        if (s.PostplantPatrol && distance >= 30f && s.Assignment is { } patrolHold) patrolHold.Returning = true;
        EndGuardMove(slot, s, now);
    }


    private bool GuardBackClear(CCSPlayerPawn pawn, GuardState s, Vector pos, float dx, float dy, float distance)
    {
        if (s.Assignment is not { } hold) return GuardRouteClear(pawn, s, pos, dx, dy, distance, standing: true);
        // Returning on a just-traversed line must not be rejected by the peek's outward leash.
        try
        {
            if (!SiteGuardGround(pawn, pos.X + dx * distance, pos.Y + dy * distance, pos.Z)) return false;
            return GuardEscapeClear(pawn, pos, dx, dy, standing: true, distance);
        }
        catch { return false; }
    }

    private bool StartGuardStand(int slot, CCSBot bot, GuardState s)
    {
        if (_botController == null) return false;
        if (s.DuckToken > 0) ReleaseGuardPose(slot, s);
        if (s.StandToken <= 0)
        {
            try { s.StandToken = BotControllerBridge.StartUsercmdSuppression(_botController, slot, GuardDuckButton); }
            catch { return false; }
            if (s.StandToken <= 0) return false;
        }
        bot.IsCrouching = false;
        return true;
    }

    private bool StartGuardCrouch(int slot, CCSPlayerPawn pawn, GuardState s, float now, float progress)
    {
        if (_botController == null) return false;
        ReleaseGuardPose(slot, s);
        float duration = s.Action == GuardAction.CrouchCover ? RandF(0.58f, 0.70f) : RandF(0.62f, 0.79f);
        s.Until = now + duration;
        if (!DriveGuardLeg(slot, pawn, s, s.Dx, s.Dy, 0.90f)) return false;
        try { s.DuckToken = BotControllerBridge.InjectUsercmd(_botController, slot, GuardDuckButton, (int)MathF.Ceiling(duration * 1000f)); }
        catch { return false; }
        if (s.DuckToken <= 0) return false;
        s.DuckAt = now;
        s.DuckProgress = progress;
        s.SlowSince = -1f;
        s.UpdateAt = now + 0.06f;
        s.Phase = GuardPhase.Crouch;
        return true;
    }

    private void ReleaseGuardPose(int slot, GuardState s)
    {
        if (_botController != null)
        {
            if (s.StandToken > 0)
                try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, s.StandToken); } catch { }
            if (s.DuckToken > 0)
                try { BotControllerBridge.CancelUsercmdInjection(_botController, slot, s.DuckToken); } catch { }
        }
        s.StandToken = s.DuckToken = 0;
    }

    private bool GuardRouteClear(CCSPlayerPawn pawn, GuardState s, Vector pos, float dx, float dy, float distance, bool standing)
    {
        float x = pos.X + dx * distance, y = pos.Y + dy * distance;
        if (s.Assignment is { } hold && !SiteGuardPathClear(pawn, hold, pos, dx, dy, distance)) return false;
        if (GuardDistanceSquared(x, y, s.AnchorX, s.AnchorY) > GuardRadius * GuardRadius) return false;
        if (s.Site >= 0 && s.Assignment is not { Postplant: true })
        {
            var center = SiteCenter(s.Site);
            if (GuardDistanceSquared(x, y, center.X, center.Y) > 1340f * 1340f) return false;
        }
        if (!IsDirectionClear(pawn, dx, dy, distance)) return false;
        if (!standing) return true;
        // Native crouch sometimes means there is no standing clearance above this route.
        try
        {
            var a = new Vector(pos.X, pos.Y, pos.Z + 54f);
            var b = new Vector(x, y, a.Z);
            var hit = Trace.TraceHullShape(a, b, GuardHeadMins, GuardHeadMaxs, pawn, GuardHeadTrace);
            return !hit.IsAllSolid && hit.Fraction >= 0.96f;
        }
        catch { return false; }
    }

    private bool DriveGuardLeg(int slot, CCSPlayerPawn pawn, GuardState s, float dx, float dy, float magnitude)
    {
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float forward = fx * dx + fy * dy, lateral = fx * dy - fy * dx;
        // A turn during a peek may make its old world direction look like a forward step.
        if (s.LateralPeek && MathF.Abs(forward) > 0.78f * MathF.Abs(lateral)) return false;
        return SetMove(slot, MoveOwner.Peek, forward * magnitude, lateral * magnitude, s.Until);
    }

    private static float GuardDistanceSquared(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    private void EndGuardMove(int slot, GuardState s, float now)
    {
        ReleaseGuardPose(slot, s);
        s.PostplantPatrol = false;
        if (s.Assignment == null || !SetMove(slot, MoveOwner.Peek, 0f, 0f, now + 0.10f))
            ReleaseMove(slot, MoveOwner.Peek);
        s.Phase = GuardPhase.Hold;
        s.IdleSince = now - 0.30f;
        s.NextAt = now + (s.Assignment is { Postplant: true } ? RandF(0.28f, 0.54f) : RandF(0.12f, 0.23f));
    }

    private string DefusalGuardStatus() => $"[Smarter-Bot] guardPeeks={_guardPeeks} guardReturns={_guardReturns} guardShifts={_guardShifts}"
        + $" guardDuckHold={_guardCrouchHolds} guardDuckPush={_guardCrouchPushes} guardDuckAbort={_guardCrouchAborts} idleRescues={_guardIdleRescues}"
        + $" guardRelocate={_guardRelocated}/{_guardRelocations}";
}
