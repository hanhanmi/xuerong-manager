using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private enum RecoveryStep { Watch, Bypass, Repath, Climb, Cooldown }

    private sealed class DefusalRecovery
    {
        public uint Pawn;
        public RecoveryStep Step;
        public float X, Y, Z, Until;
    }

    private readonly Dictionary<int, DefusalRecovery> _defusalRecovery = new();
    private static readonly TraceOptions DefusalLedgeGeometry = new() { InteractsWith = Masks.PlayerSolidBrushOnly };
    private const ulong NativeMoveButtons = (ulong)(PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Moveleft | PlayerButtons.Moveright);
    private int _statRecoveryBypasses, _statRecoveryRepaths, _statRecoveryClimbs, _statRecoveryRecovered;

    private bool InDefusalRecoveryClimb(int slot, float now)
        => _defusalRecovery.TryGetValue(slot, out var state) && state.Step == RecoveryStep.Climb && now < state.Until;

    private void ResetDefusalRecoveryForSlot(int slot)
    {
        if (_defusalRecovery.Remove(slot, out var state) && state.Step is RecoveryStep.Bypass or RecoveryStep.Climb)
            ReleaseMove(slot, MoveOwner.Unstuck);
    }

    private void ResetDefusalRecoveryRoundState()
    {
        foreach (int slot in _defusalRecovery.Keys.ToArray()) ResetDefusalRecoveryForSlot(slot);
    }

    private static bool HasRecoveryMoveIntent(CCSPlayerPawn pawn, CCSBot bot, MoveToken? move, out float fwd, out float left)
    {
        if (move != null)
        {
            fwd = move.Fwd;
            left = move.Left;
        }
        else
        {
            ulong flags = bot.ButtonFlags & NativeMoveButtons;
            fwd = ((flags & (ulong)PlayerButtons.Forward) != 0 ? 1f : 0f) - ((flags & (ulong)PlayerButtons.Back) != 0 ? 1f : 0f);
            left = ((flags & (ulong)PlayerButtons.Moveleft) != 0 ? 1f : 0f) - ((flags & (ulong)PlayerButtons.Moveright) != 0 ? 1f : 0f);
            if (flags == 0 && !bot.IsWaitingBehindFriend && pawn.MovementServices is { Handle: not 0 } services)
            {
                var movement = services.As<CCSPlayer_MovementServices>();
                fwd = movement.CmdForwardMove;
                left = movement.CmdLeftMove;
            }
        }
        float length2 = fwd * fwd + left * left;
        if (!float.IsFinite(length2) || length2 < 0.04f) return false;
        float inverseLength = 1f / MathF.Sqrt(length2);
        fwd *= inverseLength;
        left *= inverseLength;
        return true;
    }

    private void UpdateDefusalRecovery(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        int slot = player.Slot;
        _moveTokens.TryGetValue(slot, out var move);
        if (move != null && now >= move.Until) move = null;
        var pos = pawn.AbsOrigin;
        bool airborne = _cachedInAir.GetValueOrDefault(idx);
        // Quiet site guards choose their own alternate directions when a peek is blocked.
        if (!airborne && !bot.IsEnemyVisible && !bot.IsAttacking && GuardOwnsLocalMovement(slot, pawn, now)
            && ActiveMoveOwner(slot, now) < MoveOwner.Combat)
        {
            ResetDefusalRecoveryForSlot(slot);
            return;
        }
        if (_botController == null || pos == null || pawn.IsDefusing || _cachedNearLadder.GetValueOrDefault(idx)
            || now - _ladderExitTime.GetValueOrDefault(idx, -999f) < 0.35f
            || move?.Owner == MoveOwner.Flash || pawn.BlindUntilTime > now || InCommittedBlind(idx, now)
            || (_flashTurn.TryGetValue(idx, out var turn) && now >= turn.Start && now < turn.Until)
            || IsRecoveryPlanting(pawn))
        {
            ResetDefusalRecoveryForSlot(slot);
            return;
        }

        uint handle = pawn.EntityHandle.Raw;
        _defusalRecovery.TryGetValue(slot, out var state);
        if (state != null && state.Pawn != handle)
        {
            ResetDefusalRecoveryForSlot(slot);
            state = null;
            if (move?.Owner == MoveOwner.Unstuck) move = null;
        }

        // A zero movement command is intentional (settling for a shot, holding smoke, or waiting at cover).
        bool wantsMove = HasRecoveryMoveIntent(pawn, bot, move, out float fwd, out float left);
        if (!wantsMove && move == null && bot.IsStuck && !bot.IsEnemyVisible && !bot.IsAttacking)
        {
            var goal = bot.GoalPosition;
            float gx = goal.X - pos.X, gy = goal.Y - pos.Y;
            float distance = MathF.Sqrt(gx * gx + gy * gy);
            if (float.IsFinite(distance) && distance > 40f && distance < 4000f)
            {
                float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                fwd = (gx * MathF.Cos(yaw) + gy * MathF.Sin(yaw)) / distance;
                left = (-gx * MathF.Sin(yaw) + gy * MathF.Cos(yaw)) / distance;
                wantsMove = true;
            }
        }
        if ((move != null && !wantsMove) || (airborne && state?.Step != RecoveryStep.Climb))
        {
            ResetDefusalRecoveryForSlot(slot);
            return;
        }
        if (state == null)
        {
            if (!wantsMove) return;
            _defusalRecovery[slot] = NewRecoveryWatch(handle, pos, now, bot.IsStuck);
            return;
        }
        if (state.Step == RecoveryStep.Cooldown)
        {
            if (now >= state.Until)
            {
                if (wantsMove) _defusalRecovery[slot] = NewRecoveryWatch(handle, pos, now, bot.IsStuck);
                else _defusalRecovery.Remove(slot);
            }
            return;
        }

        float dx = pos.X - state.X, dy = pos.Y - state.Y;
        bool progressed = dx * dx + dy * dy >= 24f * 24f || MathF.Abs(pos.Z - state.Z) >= 18f;
        if (state.Step == RecoveryStep.Watch)
        {
            if (!wantsMove) { _defusalRecovery.Remove(slot); return; }
            if (progressed)
            {
                SetRecoveryWatch(state, pos, now, bot.IsStuck);
                return;
            }
        }
        else if (!airborne && progressed && (state.Step != RecoveryStep.Climb || now >= state.Until - 0.15f))
        {
            StopDefusalRecovery(player, state, now, recovered: true);
            return;
        }

        if (now < state.Until) return;

        if (state.Step == RecoveryStep.Watch && wantsMove && TryDefusalLedgeClimb(pawn, slot, idx, fwd, left, now))
        {
            state.Step = RecoveryStep.Climb;
            state.Until = now + 0.85f;
            _statRecoveryClimbs++;
            LogDefusalRecovery(player, "ledge climb");
            return;
        }

        if (state.Step == RecoveryStep.Watch && TryDefusalBypass(pawn, slot, fwd, left, now))
        {
            state.Step = RecoveryStep.Bypass;
            state.Until = now + 0.35f;
            _statRecoveryBypasses++;
            LogDefusalRecovery(player, "bypass");
            return;
        }

        if (state.Step is RecoveryStep.Watch or RecoveryStep.Bypass)
        {
            // Release the override so the new route can supply its own movement direction.
            ReleaseMove(slot, MoveOwner.Unstuck);
            ForceRepath(bot);
            state.Step = RecoveryStep.Repath;
            state.Until = now + 0.55f;
            _statRecoveryRepaths++;
            LogDefusalRecovery(player, "repath");
            return;
        }

        if (state.Step == RecoveryStep.Repath && wantsMove && TryDefusalLedgeClimb(pawn, slot, idx, fwd, left, now))
        {
            state.Step = RecoveryStep.Climb;
            state.Until = now + 0.85f;
            _statRecoveryClimbs++;
            LogDefusalRecovery(player, "ledge climb");
            return;
        }

        if (state.Step == RecoveryStep.Climb)
        {
            ReleaseMove(slot, MoveOwner.Unstuck);
            ForceRepath(bot);
            state.Step = RecoveryStep.Repath;
            state.Until = now + 0.35f;
            return;
        }
        StopDefusalRecovery(player, state, now, recovered: false);
    }

    private static DefusalRecovery NewRecoveryWatch(uint handle, Vector pos, float now, bool nativeStuck)
    {
        var state = new DefusalRecovery { Pawn = handle };
        SetRecoveryWatch(state, pos, now, nativeStuck);
        return state;
    }

    private static void SetRecoveryWatch(DefusalRecovery state, Vector pos, float now, bool nativeStuck)
    {
        state.X = pos.X;
        state.Y = pos.Y;
        state.Z = pos.Z;
        state.Until = now + (nativeStuck ? 0.48f : 0.72f);
    }

    private void StopDefusalRecovery(CCSPlayerController player, DefusalRecovery state, float now, bool recovered)
    {
        ReleaseMove(player.Slot, MoveOwner.Unstuck);
        state.Step = RecoveryStep.Cooldown;
        state.Until = now + (recovered ? 0.65f : 1.35f);
        if (recovered) _statRecoveryRecovered++;
        LogDefusalRecovery(player, recovered ? "movement restored" : "resume native path");
    }

    private void LogDefusalRecovery(CCSPlayerController player, string action)
    {
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Recovery] {player.PlayerName}: {action}");
    }

    private static bool IsRecoveryPlanting(CCSPlayerPawn pawn)
    {
        var weapon = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (weapon == null || weapon.DesignerName != "weapon_c4") return false;
        var bomb = new CC4(weapon.Handle);
        return bomb.StartedArming || bomb.IsPlantingViaUse;
    }

    private static (float X, float Y) RecoveryDirection(CCSPlayerPawn pawn, float fwd, float left)
    {
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
        return (cos * fwd - sin * left, sin * fwd + cos * left);
    }

    private bool TryDefusalBypass(CCSPlayerPawn pawn, int slot, float fwd, float left, float now)
    {
        float side = _random.Next(2) == 0 ? 1f : -1f;
        // Arc around either side of the blocked direction, then back out if neither side is clear.
        return TryRecoveryMove(pawn, slot, fwd * 0.2f - left * side * 0.95f, left * 0.2f + fwd * side * 0.95f, now)
            || TryRecoveryMove(pawn, slot, fwd * 0.2f + left * side * 0.95f, left * 0.2f - fwd * side * 0.95f, now)
            || TryRecoveryMove(pawn, slot, -fwd * 0.8f, -left * 0.8f, now);
    }

    private bool TryRecoveryMove(CCSPlayerPawn pawn, int slot, float fwd, float left, float now)
    {
        var dir = RecoveryDirection(pawn, fwd, left);
        float length = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        return IsDirectionClear(pawn, dir.X / length, dir.Y / length, 76f)
            && SetMove(slot, MoveOwner.Unstuck, fwd, left, now + 0.35f);
    }

    private bool TryDefusalLedgeClimb(CCSPlayerPawn pawn, int slot, int idx, float fwd, float left, float now)
    {
        if (_botController == null || now - _lastStuckJump.GetValueOrDefault(idx, -999f) < 1.8f) return false;
        var (dx, dy) = RecoveryDirection(pawn, fwd, left);
        if (!(IsDefusalJumpableLedge(pawn, dx, dy)
                || (JumpableObstacle(pawn, dx, dy) && GroundAhead(pawn, dx, dy, 70f)))
            || !SetMove(slot, MoveOwner.Unstuck, fwd, left, now + 0.85f)) return false;

        _jumpAllowUntil[idx] = now + 0.45f;
        ReleaseJumpGate(slot);
        try
        {
            if (BotControllerBridge.InjectUsercmd(_botController, slot, JumpButtonMask, 110) > 0)
            {
                _lastStuckJump[idx] = now;
                _forceJumpAssist[idx] = now;
                return true;
            }
        }
        catch { }
        ReleaseMove(slot, MoveOwner.Unstuck);
        return false;
    }

    private static bool IsDefusalJumpableLedge(CCSPlayerPawn pawn, float dx, float dy)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return false;
        const float distance = 48f;
        float x = o.X + dx * distance, y = o.Y + dy * distance;
        try
        {
            var mins = new Vector(-14f, -14f, 0f);
            var foot = Trace.TraceHullShape(new(o.X, o.Y, o.Z + 20f), new(x, y, o.Z + 20f), mins,
                new(14f, 14f, 18f), pawn, DefusalLedgeGeometry);
            if (foot.IsAllSolid || foot.Fraction >= 0.95f) return false;
            var clearance = Trace.TraceHullShape(new(o.X, o.Y, o.Z + 63f), new(x, y, o.Z + 63f), mins,
                new(14f, 14f, 36f), pawn, DefusalLedgeGeometry);
            if (clearance.IsAllSolid || clearance.Fraction < 0.95f) return false;
            var landing = Trace.TraceEndShape(new(x, y, o.Z + 62f), new(x, y, o.Z - 16f), pawn, DefusalLedgeGeometry);
            return !landing.IsAllSolid && landing.Fraction is > 0.03f and < 0.95f;
        }
        catch { return false; }
    }
}
