using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{

    private readonly Dictionary<int, bool> _prevInAir = new();
    private readonly Dictionary<int, float> _lastForwardDir = new();
    private readonly Dictionary<int, float> _ladderExitTime = new();
    private readonly Dictionary<int, float> _doorEventCooldown = new();

    private readonly Dictionary<int, float> _stuckStartTime = new();
    private readonly Dictionary<int, (float X, float Y)> _stuckStartPos = new();
    private readonly Dictionary<int, bool> _stuckJumpDone = new();
    private readonly Dictionary<int, int> _stuckJumpCount = new();
    private readonly Dictionary<int, float> _stuckMaxSpeed = new();
    private readonly Dictionary<int, int> _stuckStage = new();
    private readonly Dictionary<int, float> _lastStuckJump = new();

    // Makes the bot recompute its path on the next update.
    private static void ForceRepath(CCSBot bot)
    {
        CountdownTimer repathTimer = bot.RepathTimer;
        repathTimer.Duration = 0f;
        repathTimer.Timestamp = Server.CurrentTime;
        repathTimer.Timescale = 1f;
    }
    private readonly Dictionary<int, float> _idleStartTime = new();
    private readonly Dictionary<int, float> _lastRepathTime = new();

    private readonly Dictionary<int, bool> _cachedInAir = new();
    private readonly Dictionary<int, bool> _cachedNearLadder = new();

    // Temporarily leaves movement to the engine after a Bot opens a door.
    private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    // Temporarily leaves movement to the engine after a Bot closes a door.
    private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    // Updates ladder and air movement; door cooldown skips all later movement work.
    private bool UpdateBotMovement(CCSPlayerPawn pawn, CCSBot bot, int slot, int idx, bool curIsAttacking, bool inDoorCooldown)
    {
        // Ladder Stuck Issue Fix
        var movementWrapper = pawn.MovementServices;
        var moveServices = movementWrapper is { Handle: not 0 }
            ? movementWrapper.As<CCSPlayer_MovementServices>() : null;
        var ladderNormal = moveServices?.LadderNormal;

        bool nearLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                    || (ladderNormal != null
                        && (ladderNormal.X != 0f || ladderNormal.Y != 0f || ladderNormal.Z != 0f));

        if (nearLadder) _ladderExitTime[idx] = Server.CurrentTime;

        bool inLadderCooldown = nearLadder
            || (_ladderExitTime.TryGetValue(idx, out float exitTime)
                && Server.CurrentTime - exitTime < 5.0f);

        bool inAir = !inLadderCooldown
                && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);

        _prevInAir.TryGetValue(idx, out bool prevInAir);
        // Door Stuck Issue Fix
        if (inDoorCooldown)
        {
            ResetDefusalRecoveryForSlot(slot);
            _prevInAir[idx] = inAir;
            return false;
        }
        // Traversal jump assist (replaces the old always-on 215 u/s air push that caused bhops).
        UpdateJumpAssist(pawn, idx, inAir, prevInAir, curIsAttacking);
        // Jump gate: block open-area hops; only allow obstacle/gap/ladder/stuck jumps.
        UpdateJumpGate(pawn, bot, slot, idx, Server.CurrentTime, inAir, nearLadder, curIsAttacking);

        // Crouch-jump (helps clearing ledges after a real jump).
        if (inAir && (!curIsAttacking || InDefusalRecoveryClimb(slot, Server.CurrentTime)) && !pawn.IsDefusing)
        {
            ref bool isCrouching = ref bot.IsCrouching;
            isCrouching = true;
        }
        // Cancel Crouch
        if (prevInAir && !inAir)
        {
            ref bool isCrouching = ref bot.IsCrouching;
            isCrouching = false;
        }
        _prevInAir[idx] = inAir;
        // cache the parameters for counter-strafe
        _cachedInAir[idx] = inAir;
        _cachedNearLadder[idx] = nearLadder;
        return true;
    }

    // Recovers stuck bots and periodically repaths idle bots.
    private void RecoverStuckBot(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if (IsDefusalCombatEnabled())
        {
            UpdateDefusalRecovery(player, pawn, bot, idx, now);
            return;
        }
        ResetDefusalRecoveryForSlot(player.Slot);

        // Normal Un-Stuck Process
        ref bool isStuck = ref bot.IsStuck;
        if (isStuck)
        {
            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;

            int slot = player.Slot;
            var curPos = pawn.AbsOrigin!;
            if (!_stuckStartTime.ContainsKey(idx))
            {
                _stuckStartTime[idx] = now;
                _stuckStartPos[idx] = (curPos.X, curPos.Y);
                _stuckStage[idx] = 0;
            }
            float elapsed = now - _stuckStartTime[idx];
            int stage = _stuckStage.GetValueOrDefault(idx);

            // Jump-free escape first: hold the native stuck-jump timer closed so the
            // engine does not hop every tick; only stage 4 opens it once (3 s cooldown).
            CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
            // A low obstacle right ahead (box, step, window ledge) gets an early jump.
            bool ledgeAhead = elapsed >= 0.5f && stage < 3 && JumpableObstacleAhead(pawn);
            bool allowJump = (ledgeAhead || (stage >= 3 && elapsed >= 2.4f))
                && now - _lastStuckJump.GetValueOrDefault(idx, -999f) >= 3.0f
                && !curIsAttacking && !pawn.IsDefusing;
            if (allowJump)
            {
                _lastStuckJump[idx] = now;
                stuckJumpTimer.Duration = 0f;
                stuckJumpTimer.Timestamp = now - 0.01f;
                stuckJumpTimer.Timescale = 1f;
                ref float jumpTimestamp = ref bot.JumpTimestamp;
                jumpTimestamp = 0f;
                SetMove(slot, MoveOwner.Unstuck, 1f, 0f, now + 0.5f);
                // Restart the escape cycle.
                _stuckStartTime[idx] = now - 0.2f;
                _stuckStage[idx] = 0;
                _forceJumpAssist[idx] = now;
            }
            else if (now - _lastStuckJump.GetValueOrDefault(idx, -999f) > 0.15f)
            {
                // Keep the jump window open briefly so the engine's bot update sees it.
                stuckJumpTimer.Duration = 0.5f;
                stuckJumpTimer.Timestamp = now + 0.5f;
                stuckJumpTimer.Timescale = 1f;
            }

            if (!allowJump && !curIsAttacking && !pawn.IsDefusing)
            {
                float side = (_stuckJumpCount.GetValueOrDefault(idx) % 2 == 0) ? 1f : -1f;
                float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
                float lx = -fy, ly = fx;

                float fwd = 0f, left = 0f, dur = 0f;
                bool repath = false;
                if (stage == 0 && elapsed >= 0.25f)
                {
                    // Pick the freer side once per stuck episode.
                    bool lc = IsDirectionClear(pawn, lx, ly, 48f), rc = IsDirectionClear(pawn, -lx, -ly, 48f);
                    if (lc != rc) side = lc ? 1f : -1f;
                    _stuckJumpCount[idx] = side > 0 ? 0 : 1;
                    fwd = -0.7f; left = side; dur = 0.45f; repath = true;
                }
                else if (stage == 1 && elapsed >= 0.9f) { fwd = 0.8f; left = -side; dur = 0.45f; }
                else if (stage == 2 && elapsed >= 1.6f) { fwd = 0f; left = -side; dur = 0.35f; repath = true; }

                if (dur > 0f)
                {
                    _stuckStage[idx] = stage + 1;
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;
                    if (!SetMove(slot, MoveOwner.Unstuck, fwd, left, now + dur))
                    {
                        // Fallback without BotController: one velocity nudge.
                        float wx = fx * fwd + lx * left, wy = fy * fwd + ly * left;
                        float len = MathF.Max(0.01f, MathF.Sqrt(wx * wx + wy * wy));
                        pawn.AbsVelocity.X = wx / len * 130f;
                        pawn.AbsVelocity.Y = wy / len * 130f;
                    }
                    if (repath) ForceRepath(bot);
                }
            }
        }
        else
        {
            // Clear
            _stuckStartTime.Remove(idx);
            _stuckStartPos.Remove(idx);
            _stuckJumpDone.Remove(idx);
            _stuckMaxSpeed.Remove(idx);
            _stuckStage.Remove(idx);
            if (ActiveMoveOwner(player.Slot, now) == MoveOwner.Unstuck)
                ReleaseMove(player.Slot, MoveOwner.Unstuck);

            // Idle repath: if speed < 5 for 5s, force a repath
            float speed2DIdle = MathF.Sqrt(
                pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

            if (speed2DIdle < 5f)
            {
                if (!_idleStartTime.ContainsKey(idx))
                    _idleStartTime[idx] = now;

                float idleElapsed = now - _idleStartTime[idx];
                float lastRepath = _lastRepathTime.GetValueOrDefault(idx, -999f);

                if (idleElapsed >= 5f && now - lastRepath >= 5f && !curIsAttacking && !pawn.IsDefusing)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;

                    _lastRepathTime[idx] = now;

                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;

                    ResetLookAroundForBot(player);
                }
            }
            else
            {
                _idleStartTime.Remove(idx);
            }
        }

    }

    // Retains the Inferno sewer-specific repath adjustment.
    private static void ApplyMapMovementFix(CCSPlayerPawn pawn, CCSBot bot)
    {
        //Inferno Sewer Stuck Fix
        if (pawn.AbsOrigin != null)
        {
            Vector pos = pawn.AbsOrigin;
            bool isInferno = string.Equals(Server.MapName, "de_inferno", StringComparison.OrdinalIgnoreCase);
            float dx = pos.X - 285f;
            float dy = pos.Y - 450f;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (isInferno && dist < 50f)
            {
                CountdownTimer repathTimer = bot.RepathTimer;

                ref float repathduration = ref repathTimer.Duration;
                repathduration = 0.0f;

                ref float repathtimestamp = ref repathTimer.Timestamp;
                repathtimestamp = Server.CurrentTime;

                ref float repathtimescale = ref repathTimer.Timescale;
                repathtimescale = 1.0f;
            }
        }
    }

    // Clears movement state owned by the completed round.
    private void ResetMovementRoundState()
    {
        ResetDefusalRecoveryRoundState();
        // Player indices are reused, so do not carry movement state into the next round.
        _prevInAir.Clear();
        _lastForwardDir.Clear();
        _ladderExitTime.Clear();
        _doorEventCooldown.Clear();
        _stuckStartTime.Clear();
        _stuckStartPos.Clear();
        _stuckJumpDone.Clear();
        _stuckJumpCount.Clear();
        _stuckMaxSpeed.Clear();
        _stuckStage.Clear();
        _lastStuckJump.Clear();
        _idleStartTime.Clear();
        _lastRepathTime.Clear();
        _cachedInAir.Clear();
        _cachedNearLadder.Clear();
        _jumpState.Clear();
        _lastLanding.Clear();
        _forceJumpAssist.Clear();
        foreach (int slot in _jumpSuppressId.Keys.ToList()) ReleaseJumpGate(slot);
        _jumpSuppressId.Clear();
        _jumpAllowUntil.Clear();
    }

    private sealed class JumpState
    {
        public float Start;
        public float DirX, DirY;
        public float Target;
        public bool Assist;
    }

    private readonly Dictionary<int, JumpState> _jumpState = new();
    private readonly Dictionary<int, float> _lastLanding = new();
    private readonly Dictionary<int, float> _forceJumpAssist = new();
    private int _statJumpAssists;

    // --- Jump gate: stops bots hopping in the open (telegraphs position) ---
    private const ulong JumpButtonMask = 1UL << 1; // IN_JUMP
    private readonly Dictionary<int, long> _jumpSuppressId = new(); // active persistent IN_JUMP suppression per slot
    private readonly Dictionary<int, float> _jumpAllowUntil = new(); // allowance window end per idx
    private int _statJumpsBlocked;

    // Only lets a native jump through for real traversal: an obstacle to hop, a gap to
    // clear, a ladder, or stuck recovery. Any other jump (open-area hops) is stripped at
    // the usercmd level so the bot stops bunny-revealing its position.
    private void UpdateJumpGate(CCSPlayerPawn pawn, CCSBot bot, int slot, int idx, float now, bool inAir, bool nearLadder, bool curIsAttacking)
    {
        if (_botController == null) return;

        // Never fight an in-progress jump or a ladder climb.
        bool wantAllow = inAir || nearLadder || (bot.IsStuck && !IsDefusalCombatEnabled());
        if (!wantAllow && !pawn.IsDefusing)
        {
            float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
            float hs = MathF.Sqrt(vx * vx + vy * vy);
            float dx, dy;
            if (hs > 50f) { dx = vx / hs; dy = vy / hs; }
            else { float yaw = pawn.EyeAngles.Y * MathF.PI / 180f; dx = MathF.Cos(yaw); dy = MathF.Sin(yaw); }

            // A box / ledge the bot must hop, or a gap with ground on the far side.
            if (JumpableObstacle(pawn, dx, dy)) wantAllow = true;
            else if (hs > 110f && !GroundAhead(pawn, dx, dy, 95f) && GroundAhead(pawn, dx, dy, 190f))
                wantAllow = true;
        }

        if (wantAllow) _jumpAllowUntil[idx] = now + 0.35f;
        bool allow = now < _jumpAllowUntil.GetValueOrDefault(idx, -999f);

        bool suppressed = _jumpSuppressId.ContainsKey(slot);
        if (allow && suppressed)
        {
            try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, _jumpSuppressId[slot]); } catch { }
            _jumpSuppressId.Remove(slot);
        }
        else if (!allow && !suppressed)
        {
            try
            {
                long id = BotControllerBridge.StartUsercmdSuppression(_botController, slot, JumpButtonMask);
                if (id > 0) { _jumpSuppressId[slot] = id; _statJumpsBlocked++; }
            }
            catch { }
        }
    }

    // Lifts any active jump suppression (bot died / taken over / round reset).
    private void ReleaseJumpGate(int slot)
    {
        if (_jumpSuppressId.TryGetValue(slot, out long id))
        {
            if (_botController != null)
                try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, id); } catch { }
            _jumpSuppressId.Remove(slot);
        }
    }

    // Decides at take-off whether this jump is a traversal jump (ledge / box / gap) and, if so,
    // keeps or gently builds horizontal speed in the air. Never boosts chained hops, never
    // snaps a standing bot to full speed (ramps at ~700 u/s^2, capped at 160 from standstill).
    private void UpdateJumpAssist(CCSPlayerPawn pawn, int idx, bool inAir, bool prevInAir, bool curIsAttacking)
    {
        float now = Server.CurrentTime;
        if (!inAir)
        {
            if (prevInAir) _lastLanding[idx] = now;
            _jumpState.Remove(idx);
            return;
        }

        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        float hs = MathF.Sqrt(vx * vx + vy * vy);

        if (!prevInAir)
        {
            // Take-off.
            var js = new JumpState { Start = now };
            float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
            if (hs > 40f) { js.DirX = vx / hs; js.DirY = vy / hs; }
            else { js.DirX = MathF.Cos(yaw); js.DirY = MathF.Sin(yaw); }

            bool chainedHop = now - _lastLanding.GetValueOrDefault(idx, -999f) < 0.35f && hs > 180f;
            bool forced = now - _forceJumpAssist.GetValueOrDefault(idx, -999f) < 0.3f;
            bool rising = pawn.AbsVelocity.Z > 150f; // a real jump, not walking off an edge
            bool obstacle = rising && JumpableObstacle(pawn, js.DirX, js.DirY);
            bool gap = rising && hs >= 120f && !GroundAhead(pawn, js.DirX, js.DirY, 90f);

            if (!curIsAttacking && !chainedHop && (obstacle || gap || forced))
            {
                js.Assist = true;
                // From standstill: ramp toward 160. Running: keep take-off speed (max 230).
                js.Target = hs < 120f ? 160f : MathF.Min(MathF.Max(hs, 150f), 230f);
                _statJumpAssists++;
            }
            _jumpState[idx] = js;
            return;
        }

        if (!_jumpState.TryGetValue(idx, out var st) || !st.Assist) return;
        if (curIsAttacking || now - st.Start > 0.75f) { st.Assist = false; return; }

        float along = vx * st.DirX + vy * st.DirY;
        if (along >= st.Target) return;
        float add = MathF.Min(700f * 0.015625f, st.Target - along);
        float nx = vx + st.DirX * add, ny = vy + st.DirY * add;
        float ns = MathF.Sqrt(nx * nx + ny * ny);
        if (ns > 240f) { nx *= 240f / ns; ny *= 240f / ns; }
        pawn.AbsVelocity.X = nx;
        pawn.AbsVelocity.Y = ny;
    }

    // Blocked at foot level within 40 u, but clear ~54 u higher: something a jump clears.
    private bool JumpableObstacle(CCSPlayerPawn pawn, float dx, float dy)
        => !ClearAtHeight(pawn, dx, dy, 40f, 20f, 18f) && ClearAtHeight(pawn, dx, dy, 48f, 66f, 18f);

    private bool JumpableObstacleAhead(CCSPlayerPawn pawn)
    {
        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float dx = MathF.Cos(yaw), dy = MathF.Sin(yaw);
        return JumpableObstacle(pawn, dx, dy);
    }

    private bool ClearAtHeight(CCSPlayerPawn pawn, float dx, float dy, float dist, float zOff, float hullH)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return false;
        _moveTraceOpts ??= new TraceOptions { InteractsWith = Masks.PlayerSolid };
        try
        {
            var a = new Vector(o.X, o.Y, o.Z + zOff);
            var b = new Vector(o.X + dx * dist, o.Y + dy * dist, o.Z + zOff);
            var r = Trace.TraceHullShape(a, b, new Vector(-14f, -14f, 0f), new Vector(14f, 14f, hullH), pawn, _moveTraceOpts);
            return !r.IsAllSolid && r.Fraction >= 0.95f;
        }
        catch { return true; }
    }

    private bool GroundAhead(CCSPlayerPawn pawn, float dx, float dy, float dist)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return true;
        _moveTraceOpts ??= new TraceOptions { InteractsWith = Masks.PlayerSolid };
        try
        {
            var a = new Vector(o.X + dx * dist, o.Y + dy * dist, o.Z + 10f);
            var b = new Vector(a.X, a.Y, o.Z - 80f);
            return Trace.TraceEndShape(a, b, pawn, _moveTraceOpts).Fraction < 1f;
        }
        catch { return true; }
    }
}
