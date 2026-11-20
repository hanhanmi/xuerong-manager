using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    // Owners of the single per-slot usercmd movement override; higher wins.
    private enum MoveOwner { None = 0, Peek = 1, GuardRecovery = 2, Smoke = 3, BombPickup = 4, Combat = 5, Flash = 6, Unstuck = 7 }

    private sealed class MoveToken
    {
        public long Id;
        public MoveOwner Owner;
        public float Until;
        public float Fwd;
        public float Left;
        public float LastVerified;
    }

    private sealed class StrafeState
    {
        public float SegUntil;
        public float Dir = 1f;
        public float HoldStart = -1f;
        public float HoldNeed;
        public float NextPeek;
        public int PeekPhase;        // 0 none, 1 out, 2 back to cover, 3 one-way move
        public float PeekUntil;
        public float PeekDir;
        public float PeekOutTime;
        public bool PeekRepeated;
        // Combat "walk-stop-fire" rhythm.
        public bool CombatMoving;    // true = committed strafe leg, false = planted to shoot
        public int MoveStreak;       // consecutive move legs (for occasional long swings)
        // Hold-crouch cadence: brief crouch, mostly standing and peeking.
        public float CrouchUntil;
        public float CrouchCycleNext;
    }

    private readonly Dictionary<int, MoveToken> _moveTokens = new();
    private readonly Dictionary<int, StrafeState> _strafe = new();

    private Vector? _traceA, _traceB, _hullMins, _hullMaxs;
    private TraceOptions? _moveTraceOpts;

    private float RandF(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    private StrafeState GetStrafe(int slot)
    {
        if (!_strafe.TryGetValue(slot, out var s))
        {
            s = new StrafeState { Dir = _random.Next(2) == 0 ? 1f : -1f };
            _strafe[slot] = s;
        }
        return s;
    }
    // Requests a movement override; refused while a higher owner is active.
    private bool SetMove(int slot, MoveOwner owner, float fwd, float left, float until)
    {
        if (_botController == null) return false;
        fwd = Math.Clamp(fwd, -1f, 1f);
        left = Math.Clamp(left, -1f, 1f);
        float now = Server.CurrentTime;
        try
        {
            if (_moveTokens.TryGetValue(slot, out var tok))
            {
                if (tok.Owner > owner && now < tok.Until) return false;
                tok.Owner = owner;
                tok.Until = until;
                if (tok.Fwd == fwd && tok.Left == left && now - tok.LastVerified < 0.2f) return true;
                if (BotControllerBridge.UpdateUsercmdMovement(_botController, slot, tok.Id, fwd, left))
                {
                    tok.Fwd = fwd; tok.Left = left; tok.LastVerified = now;
                    return true;
                }
                _moveTokens.Remove(slot);
            }
            long id = BotControllerBridge.StartUsercmdMovement(_botController, slot, fwd, left);
            if (id <= 0) return false;
            _moveTokens[slot] = new MoveToken { Id = id, Owner = owner, Until = until, Fwd = fwd, Left = left, LastVerified = now };
            return true;
        }
        catch { return false; }
    }

    // Releases the override if it belongs to the owner (or any owner when None).
    private void ReleaseMove(int slot, MoveOwner owner = MoveOwner.None)
    {
        if (!_moveTokens.TryGetValue(slot, out var tok)) return;
        if (owner != MoveOwner.None && tok.Owner != owner) return;
        _moveTokens.Remove(slot);
        if (_botController == null) return;
        try { BotControllerBridge.CancelUsercmdMovement(_botController, slot, tok.Id); } catch { }
    }

    private MoveOwner ActiveMoveOwner(int slot, float now)
        => _moveTokens.TryGetValue(slot, out var t) && now < t.Until ? t.Owner : MoveOwner.None;

    // Cancels overrides whose time ran out so path following resumes.
    private void ExpireMoveTokens(float now)
    {
        if (_moveTokens.Count == 0) return;
        List<int>? expired = null;
        foreach (var kv in _moveTokens)
            if (now >= kv.Value.Until) (expired ??= new()).Add(kv.Key);
        if (expired == null) return;
        foreach (int slot in expired) ReleaseMove(slot);
    }

    private void CancelAllMoveTokens()
    {
        foreach (int slot in _moveTokens.Keys.ToList()) ReleaseMove(slot);
        _moveTokens.Clear();
    }

    // True when a player hull can move `dist` along (dx,dy) without hitting
    // geometry and without stepping off a drop deeper than ~70 units.
    private bool IsDirectionClear(CCSPlayerPawn pawn, float dx, float dy, float dist)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return false;
        _traceA ??= new Vector(); _traceB ??= new Vector();
        _hullMins ??= new Vector(-14f, -14f, 0f);
        _hullMaxs ??= new Vector(14f, 14f, 36f);
        _moveTraceOpts ??= new TraceOptions { InteractsWith = Masks.PlayerSolid };
        try
        {
            // Hull raised by step height so stairs and small lips do not block.
            _traceA.X = o.X; _traceA.Y = o.Y; _traceA.Z = o.Z + 20f;
            _traceB.X = o.X + dx * dist; _traceB.Y = o.Y + dy * dist; _traceB.Z = o.Z + 20f;
            var r = Trace.TraceHullShape(_traceA, _traceB, _hullMins, _hullMaxs, pawn, _moveTraceOpts);
            if (r.IsAllSolid || r.Fraction < 0.95f) return false;

            // Ledge check at the far end.
            _traceA.X = _traceB.X; _traceA.Y = _traceB.Y; _traceA.Z = o.Z + 20f;
            _traceB.Z = o.Z - 70f;
            var d = Trace.TraceEndShape(_traceA, _traceB, pawn, _moveTraceOpts);
            return d.Fraction < 1f;
        }
        catch { return false; }
    }

    // Free distance from the eye along (dx,dy), shifted sideways by (sx,sy)*shift.
    private float SightDistance(CCSPlayerPawn pawn, float dx, float dy, float sx, float sy, float shift, float range)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return 0f;
        _traceA ??= new Vector(); _traceB ??= new Vector();
        float eyeZ = o.Z + (pawn.ViewOffset?.Z ?? 64f);
        _traceA.X = o.X + sx * shift; _traceA.Y = o.Y + sy * shift; _traceA.Z = eyeZ;
        _traceB.X = _traceA.X + dx * range; _traceB.Y = _traceA.Y + dy * range; _traceB.Z = eyeZ;
        try
        {
            var r = Trace.TraceEndShape(_traceA, _traceB, pawn, new TraceOptions { InteractsWith = Masks.SolidBrushOnly });
            return r.Fraction * range;
        }
        catch { return 0f; }
    }
    private static bool IsSniperWeapon(string? w)
        => w is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1";

    private static bool IsCloseRangeWeapon(string? w)
        => w is "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45"
             or "weapon_p90" or "weapon_bizon" or "weapon_nova" or "weapon_xm1014"
             or "weapon_sawedoff" or "weapon_mag7";

    private static bool IsNonGunWeapon(string? w)
        => w is null or "weapon_c4" or "weapon_taser" or "weapon_healthshot"
             || w.StartsWith("weapon_knife") || w == "weapon_bayonet"
             || w.EndsWith("grenade") || w is "weapon_flashbang" or "weapon_molotov" or "weapon_decoy";

    // Jiggle / diagonal strafing while trading shots with a visible enemy.
    private void UpdateCombatStrafe(CCSPlayerPawn pawn, CCSBot bot, int slot, int idx, float now, bool curIsAttacking)
    {
        var s = GetStrafe(slot);
        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        bool canStrafe = curIsAttacking && bot.IsEnemyVisible
            && !pawn.IsDefusing && !bot.IsCrouching && !pawn.IsScoped
            && !_cachedInAir.GetValueOrDefault(idx) && !_cachedNearLadder.GetValueOrDefault(idx)
            && !IsNonGunWeapon(wpn) && !IsSniperWeapon(wpn);
        if (!canStrafe)
        {
            ReleaseMove(slot, MoveOwner.Combat);
            s.SegUntil = 0f;
            s.CombatMoving = false;
            s.MoveStreak = 0;
            return;
        }
        if (now < s.SegUntil) return;

        float dist = 800f;
        var enemy = bot.Enemy.Value;
        var o = pawn.AbsOrigin;
        if (enemy != null && enemy.IsValid && enemy.AbsOrigin != null && o != null)
        {
            float ex = enemy.AbsOrigin.X - o.X, ey = enemy.AbsOrigin.Y - o.Y;
            dist = MathF.Sqrt(ex * ex + ey * ey);
        }
        bool close = dist < 600f, far = dist > 1600f;
        bool cqWeapon = IsCloseRangeWeapon(wpn);

        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float lx = -fy, ly = fx;

        bool wasMoving = s.CombatMoving;
        // Walk-stop-fire rhythm. Plant to shoot accurately, then commit a clear strafe leg.
        // Long range favors planting (accuracy); close range favors constant movement.
        double plantChance = far ? 0.55 : close ? 0.22 : 0.4;
        if (wasMoving) plantChance += 0.28; else plantChance -= 0.18;
        plantChance = Math.Clamp(plantChance, 0.08, 0.85);

        if (_random.NextDouble() < plantChance)
        {
            // PLANT: stop and shoot. Crisp, short.
            s.CombatMoving = false;
            s.MoveStreak = 0;
            s.SegUntil = now + (far ? RandF(0.28f, 0.5f) : RandF(0.16f, 0.32f));
            SetMove(slot, MoveOwner.Combat, 0f, 0f, s.SegUntil);
            return;
        }

        // MOVE: commit a decisive strafe leg. Usually flip from the last direction.
        if (wasMoving && _random.NextDouble() < 0.82) s.Dir = -s.Dir;
        s.MoveStreak++;
        float left = s.Dir; // full-amplitude strafe so the shuffle is visible

        // Mostly pure side-strafe; sometimes a diagonal push/back for depth.
        double r = _random.NextDouble();
        float fwd;
        if (close && cqWeapon) fwd = r < 0.4 ? 0.8f : r < 0.6 ? -0.5f : 0f;
        else fwd = r < 0.18 ? 0.5f : r < 0.33 ? -0.5f : 0f;

        if (!DirClear(pawn, fx, fy, lx, ly, fwd, left))
        {
            left = -left; s.Dir = left;
            if (!DirClear(pawn, fx, fy, lx, ly, fwd, left))
            {
                left = 0f;
                if (fwd == 0f) fwd = -0.6f;
                if (!DirClear(pawn, fx, fy, lx, ly, fwd, 0f)) fwd = 0f;
            }
        }

        // Longer legs = bigger lateral travel. Every few legs, an extra-wide swing.
        bool wideSwing = s.MoveStreak >= 2 && _random.NextDouble() < 0.3;
        float legMin = close ? 0.3f : 0.38f, legMax = close ? 0.5f : 0.62f;
        if (wideSwing) { legMin += 0.2f; legMax += 0.25f; }
        s.CombatMoving = true;
        s.SegUntil = now + RandF(legMin, legMax);
        SetMove(slot, MoveOwner.Combat, fwd, left, s.SegUntil);
    }

    // True while the bot is in a committed combat strafe leg (used to relax counter-strafe).
    private bool InCombatMovePhase(int slot)
        => _strafe.TryGetValue(slot, out var s) && s.CombatMoving && Server.CurrentTime < s.SegUntil;

    private bool DirClear(CCSPlayerPawn pawn, float fx, float fy, float lx, float ly, float fwd, float left)
    {
        float wx = fx * fwd + lx * left, wy = fy * fwd + ly * left;
        float len = MathF.Sqrt(wx * wx + wy * wy);
        if (len < 0.01f) return true;
        return IsDirectionClear(pawn, wx / len, wy / len, 56f);
    }
    // Replaces long static holds with shoulder peeks, wide swings and small repositions.
    private void UpdateHoldPeek(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int slot, int idx, float now, bool curIsAttacking)
    {
        var s = GetStrafe(slot);
        bool threat = curIsAttacking || bot.IsEnemyVisible || pawn.IsDefusing;

        if (s.PeekPhase != 0)
        {
            if (threat || ActiveMoveOwner(slot, now) > MoveOwner.Peek)
            {
                s.PeekPhase = 0;
                ReleaseMove(slot, MoveOwner.Peek);
                s.NextPeek = now + RandF(1.5f, 3f);
                s.HoldStart = -1f;
                return;
            }
            // Keep the bot standing for the whole peek so the lateral move is a real,
            // visible strafe (native hiding logic otherwise re-crouches it mid-peek and
            // the movement shrinks to nothing).
            if (bot.IsCrouching) { ref bool c = ref bot.IsCrouching; c = false; }
            if (now < s.PeekUntil) return;

            if (s.PeekPhase == 1)
            {
                // Step back behind cover (a touch shorter than the way out).
                s.PeekPhase = 2;
                s.PeekUntil = now + s.PeekOutTime * 0.85f;
                if (!SetMove(slot, MoveOwner.Peek, 0f, -s.PeekDir, s.PeekUntil)) s.PeekPhase = 0;
                return;
            }
            if (s.PeekPhase == 2 && !s.PeekRepeated && _random.NextDouble() < 0.45)
            {
                // Second, wider jiggle out (double-peek).
                s.PeekRepeated = true;
                s.PeekPhase = 1;
                s.PeekOutTime = RandF(0.28f, 0.45f);
                s.PeekUntil = now + s.PeekOutTime;
                if (!SetMove(slot, MoveOwner.Peek, 0f, s.PeekDir, s.PeekUntil)) s.PeekPhase = 0;
                return;
            }
            s.PeekPhase = 0;
            ReleaseMove(slot, MoveOwner.Peek);
            s.NextPeek = now + RandF(2f, 4.5f);
            s.HoldStart = -1f;
            return;
        }

        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        bool eligible = !threat && !_isFreezeTime && _botController != null
            && !_cachedInAir.GetValueOrDefault(idx) && !_cachedNearLadder.GetValueOrDefault(idx)
            && !bot.IsWaitingBehindFriend && !bot.IsStuck && wpn != "weapon_c4"
            && ActiveMoveOwner(slot, now) == MoveOwner.None
            && vx * vx + vy * vy < 20f * 20f;
        if (!eligible) { s.HoldStart = -1f; return; }

        bool sniper = IsSniperWeapon(wpn);
        if (s.HoldStart < 0f)
        {
            s.HoldStart = now;
            s.HoldNeed = sniper ? RandF(2.5f, 5f) : RandF(1.0f, 2.2f);
            s.CrouchCycleNext = now; // decide the first crouch window immediately
            return;
        }

        // Crouch cadence: a camper crouches only briefly, then stands and peeks.
        // Crouching is the minority of the hold; standing / peeking dominates.
        if (now >= s.CrouchCycleNext)
        {
            double crouchChance = sniper ? 0.5 : 0.35;
            if (_random.NextDouble() < crouchChance)
            {
                s.CrouchUntil = now + (sniper ? RandF(0.9f, 1.8f) : RandF(0.5f, 1.1f));
                s.CrouchCycleNext = s.CrouchUntil + RandF(2.2f, 4.5f);
            }
            else { s.CrouchUntil = now; s.CrouchCycleNext = now + RandF(2.2f, 4.5f); }
        }
        bool wantCrouch = now < s.CrouchUntil;
        { ref bool crouch = ref bot.IsCrouching; crouch = wantCrouch; }

        if (now - s.HoldStart < s.HoldNeed || now < s.NextPeek || wantCrouch) return;

        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw);
        float lx = -fy, ly = fx;

        bool leftClear = IsDirectionClear(pawn, lx, ly, 70f);
        bool rightClear = IsDirectionClear(pawn, -lx, -ly, 70f);
        if (!leftClear && !rightClear)
        {
            s.NextPeek = now + RandF(1.5f, 3.5f);
            s.HoldStart = -1f;
            return;
        }

        float dir;
        if (leftClear && rightClear)
        {
            // Prefer the side where the sightline opens up, i.e. the edge of cover.
            float center = SightDistance(pawn, fx, fy, 0f, 0f, 0f, 1500f);
            float l = SightDistance(pawn, fx, fy, lx, ly, 48f, 1500f);
            float r = SightDistance(pawn, fx, fy, -lx, -ly, 48f, 1500f);
            if (l > center + 120f && l >= r) dir = 1f;
            else if (r > center + 120f) dir = -1f;
            else dir = _random.Next(2) == 0 ? 1f : -1f;
        }
        else dir = leftClear ? 1f : -1f;

        // Room on the chosen side scales how far the bot commits (no peeking into a wall).
        float rlx = dir > 0f ? lx : -lx, rly = dir > 0f ? ly : -ly;
        bool roomy = IsDirectionClear(pawn, rlx, rly, 150f);

        s.PeekDir = dir;
        s.PeekRepeated = false;
        double roll = _random.NextDouble();
        if (sniper || roll < 0.45)
        {
            // Shoulder / jiggle peek and return — wider than before so it reads as a peek.
            s.PeekPhase = 1;
            float lo = roomy ? 0.3f : 0.2f, hi = roomy ? 0.5f : 0.34f;
            s.PeekOutTime = sniper ? RandF(0.32f, 0.5f) : RandF(lo, hi);
            s.PeekUntil = now + s.PeekOutTime;
            if (!SetMove(slot, MoveOwner.Peek, 0f, dir, s.PeekUntil)) s.PeekPhase = 0;
        }
        else if (roll < 0.72 && roomy)
        {
            // Wide swing out with a forward lean, then it steps back behind cover.
            s.PeekPhase = 1;
            s.PeekOutTime = RandF(0.55f, 0.9f);
            s.PeekUntil = now + s.PeekOutTime;
            if (!SetMove(slot, MoveOwner.Peek, 0.35f, dir, s.PeekUntil)) s.PeekPhase = 0;
            else ResetLookAroundForBot(player);
        }
        else if (roll < 0.9)
        {
            // Committed swing that stays out to take the angle / trade.
            s.PeekPhase = 3;
            s.PeekUntil = now + RandF(0.6f, 1.1f);
            float fwd = roomy ? 0.4f : 0.15f;
            if (!SetMove(slot, MoveOwner.Peek, fwd, dir, s.PeekUntil)) s.PeekPhase = 0;
            else ResetLookAroundForBot(player);
        }
        else
        {
            // Small reposition on a diagonal, then let the bot re-plan its look.
            float fwd = _random.Next(2) == 0 ? 0.7f : -0.7f;
            if (!DirClear(pawn, fx, fy, lx, ly, fwd, dir * 0.7f)) fwd = 0f;
            s.PeekPhase = 3;
            s.PeekUntil = now + RandF(0.7f, 1.1f);
            if (!SetMove(slot, MoveOwner.Peek, fwd, dir * 0.7f, s.PeekUntil)) s.PeekPhase = 0;
            else ResetLookAroundForBot(player);
        }
    }

    // Drops all movement overrides and strafe state for a slot.
    private void ResetStrafeForSlot(int slot)
    {
        ReleaseMove(slot);
        _strafe.Remove(slot);
    }

    private void ResetStrafeRoundState()
    {
        CancelAllMoveTokens();
        _strafe.Clear();
    }
}
