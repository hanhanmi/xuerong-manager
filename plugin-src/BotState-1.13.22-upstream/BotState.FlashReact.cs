using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace BotState;

public partial class BotState
{
    private const float FlashTurnSpeed = 960f;
    private const float FlashReturnSpeed = 1080f;
    private const float FlashAfterPop = 0.055f;

    private sealed class FlashTurn
    {
        public uint Flash, Pawn;
        public int Slot;
        public bool ManagedView, OwnsAim, Returning;
        public Vector3 Position;
        public float Start, Until, DetonateAt, PoppedAt = -1f;
        public float OriginalPitch, OriginalYaw, Pitch, Yaw, LastUpdate;
        public long FireBlock;
    }

    private readonly Dictionary<int, FlashTurn> _flashTurn = new();
    private int _statFlashReturned;
    private bool _warnedFlashView;

    private bool StartFlashTurn(CCSPlayerController player, int bidx, uint flashIndex, Vector pos, float readyAt, float detonateAt)
    {
        bool managed = IsDefusalCombatEnabled();
        if (managed && (_botController == null || _flashTurn.ContainsKey(bidx)
            || BotControllerBridge.IsViewControlled(_botController, player.Slot))) return false;
        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true }) return false;

        _flashTurn[bidx] = new FlashTurn
        {
            Flash = flashIndex,
            Pawn = pawn.EntityHandle.Raw,
            Slot = player.Slot,
            Position = new Vector3(pos.X, pos.Y, pos.Z),
            ManagedView = managed,
            Start = managed ? MathF.Max(readyAt, detonateAt - RandF(0.28f, 0.40f))
                            : MathF.Max(readyAt + 0.08f, detonateAt - RandF(0.15f, 0.35f)),
            Until = detonateAt + (managed ? 0.27f : RandF(0.35f, 0.65f)),
            DetonateAt = detonateAt,
        };
        return true;
    }

    private void UpdateFlashTurnPos(uint flashIndex, Vector pos)
    {
        foreach (var turn in _flashTurn.Values)
            if (turn.Flash == flashIndex && turn.PoppedAt < 0f)
                turn.Position = new Vector3(pos.X, pos.Y, pos.Z);
    }

    private void MarkFlashTurnDetonated(uint flashIndex, float now)
    {
        foreach (var turn in _flashTurn.Values)
        {
            if (!turn.ManagedView || turn.Flash != flashIndex || turn.PoppedAt >= 0f) continue;
            turn.PoppedAt = now;
            turn.Until = now + 0.36f;
        }
    }

    private bool FlashViewControlledByOther(int slot, int bidx)
    {
        if (_botController == null) return false;
        return _flashTurn.TryGetValue(bidx, out var turn) && turn.OwnsAim
            ? BotControllerBridge.HasFullViewLock(_botController, slot) : BotControllerBridge.IsViewControlled(_botController, slot);
    }

    private void PruneFlashTurns()
    {
        if (_flashTurn.Count == 0) return;
        foreach (var (idx, turn) in _flashTurn.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(turn.Slot);
            if (!IsAliveBot(player, out var pawn, out _) || pawn.EntityHandle.Raw != turn.Pawn)
                StopFlashTurn(idx);
        }
    }

    private void ResetFlashTurns()
    {
        foreach (var idx in _flashTurn.Keys.ToArray()) StopFlashTurn(idx);
    }

    private void StopFlashTurn(int idx)
    {
        if (!_flashTurn.Remove(idx, out var turn) || _botController == null) return;
        if (turn.FireBlock > 0)
        {
            try { BotControllerBridge.CancelUsercmdSuppression(_botController, turn.Slot, turn.FireBlock); }
            catch (Exception error) { LogFlashViewFailure(error); }
        }
        if (turn.OwnsAim)
        {
            try { BotControllerBridge.ReleaseFlashAim(_botController, turn.Slot); }
            catch (Exception error) { LogFlashViewFailure(error); }
        }
    }

    private void UpdateFlashTurn(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        int idx = (int)player.Index;
        if (!_flashTurn.TryGetValue(idx, out var turn)) return;
        try
        {
            var origin = pawn.AbsOrigin;
            if (origin == null || pawn.EntityHandle.Raw != turn.Pawn) { StopFlashTurn(idx); return; }
            if (!turn.ManagedView)
            {
                if (now > turn.Until) StopFlashTurn(idx);
                else if (now >= turn.Start) ApplyLegacyFlashLook(bot, origin, turn, now);
                return;
            }

            if (_botController == null || !IsDefusalCombatEnabled() || FlashViewControlledByOther(turn.Slot, idx))
            {
                StopFlashTurn(idx);
                return;
            }
            if (now < turn.Start) return;

            bool objectiveOrLadder = DefusalObjectiveBusy(pawn) || pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
            if (objectiveOrLadder)
            {
                if (turn.OwnsAim) CompleteFlashReturn(player, pawn, bot, turn, idx);
                else StopFlashTurn(idx);
                return;
            }

            float pop = turn.PoppedAt >= 0f ? turn.PoppedAt : turn.DetonateAt + 0.13f;
            if (!turn.OwnsAim)
            {
                if (now >= pop || InCommittedBlind(idx, now) || !BotControllerBridge.TryAcquireFlashAim(_botController, turn.Slot))
                {
                    StopFlashTurn(idx);
                    return;
                }
                turn.OwnsAim = true;
                var view = pawn.EyeAngles;
                turn.OriginalPitch = turn.Pitch = Math.Clamp(view.X, -89f, 89f);
                turn.OriginalYaw = turn.Yaw = (float)NormalizeAngleDeg(view.Y);
                turn.LastUpdate = now - Server.TickInterval;
                ClearDefusalDuel(turn.Slot);
                ClearDefusalGuard(turn.Slot);
                CancelIdlePeek(turn.Slot);
                turn.FireBlock = BotControllerBridge.StartUsercmdSuppression(_botController, turn.Slot, AttackButtonMask | (ulong)PlayerButtons.Attack2);
            }

            if (now >= pop + FlashAfterPop && !turn.Returning)
            {
                turn.Returning = true;
                turn.Until = now + 0.28f;
            }

            float targetYaw;
            float targetPitch;
            if (turn.Returning)
            {
                targetYaw = turn.OriginalYaw;
                targetPitch = turn.OriginalPitch;
            }
            else
            {
                float dx = origin.X - turn.Position.X;
                float dy = origin.Y - turn.Position.Y;
                targetYaw = dx * dx + dy * dy > 16f ? MathF.Atan2(dy, dx) * 180f / MathF.PI : turn.OriginalYaw + 180f;
                targetPitch = pawn.ViewOffset.Z + origin.Z <= turn.Position.Z ? 18f : -12f;
            }

            float maxStep = Math.Clamp(now - turn.LastUpdate, 0f, 0.05f) * (turn.Returning ? FlashReturnSpeed : FlashTurnSpeed);
            turn.LastUpdate = now;
            float remainingYaw = (float)NormalizeAngleDeg(targetYaw - turn.Yaw);
            turn.Yaw = (float)NormalizeAngleDeg(turn.Yaw + Math.Clamp(remainingYaw, -maxStep, maxStep));
            turn.Pitch += Math.Clamp(targetPitch - turn.Pitch, -maxStep, maxStep);
            bool returned = turn.Returning && MathF.Abs((float)NormalizeAngleDeg(targetYaw - turn.Yaw)) < 1f
                && MathF.Abs(targetPitch - turn.Pitch) < 1f;
            if (turn.Returning && (returned || now >= turn.Until))
            {
                CompleteFlashReturn(player, pawn, bot, turn, idx);
                return;
            }

            WriteFlashView(pawn, turn.Pitch, turn.Yaw);
        }
        catch (Exception error)
        {
            try
            {
                if (turn.OwnsAim) SeedNativeFlashLook(bot, turn.OriginalPitch, turn.OriginalYaw);
            }
            catch (Exception resetError) { LogFlashViewFailure(resetError); }
            finally { StopFlashTurn(idx); }
            LogFlashViewFailure(error);
        }
    }

    private void CompleteFlashReturn(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, FlashTurn turn, int idx)
    {
        pawn.Teleport(angles: new Vector3(0f, turn.OriginalYaw, 0f));
        WriteFlashView(pawn, turn.OriginalPitch, turn.OriginalYaw);
        SeedNativeFlashLook(bot, turn.OriginalPitch, turn.OriginalYaw);
        ResetLookAroundForBot(player);
        StopFlashTurn(idx);
        _statFlashReturned++;
        if (_debugFlash) BroadcastDebug($"[Smarter-Bot/Flash] bot={player.PlayerName} flash#{turn.Flash} returned yaw={pawn.EyeAngles.Y:F0}");
    }

    private static void SeedNativeFlashLook(CCSBot bot, float pitch, float yaw)
    {
        bot.LookYaw = yaw;
        bot.LookPitch = pitch;
        bot.LookYawVel = 0f;
        bot.LookPitchVel = 0f;
        bot.LookAtSpotDuration = 0f;
        bot.InhibitLookAroundTimestamp = 0f;
    }

    private void LogFlashViewFailure(Exception error)
    {
        if (_warnedFlashView) return;
        _warnedFlashView = true;
        Logger.LogWarning(error, "[Smarter-Bot] flash view control encountered an error");
    }

    private static void WriteFlashView(CCSPlayerPawn pawn, float pitch, float yaw)
    {
        var eyeAngles = pawn.EyeAngles;
        var viewAngles = pawn.V_angle;
        if (MathF.Abs((float)NormalizeAngleDeg(yaw - eyeAngles.Y)) < 0.25f && MathF.Abs(pitch - eyeAngles.X) < 0.25f
            && MathF.Abs((float)NormalizeAngleDeg(yaw - viewAngles.Y)) < 0.25f && MathF.Abs(pitch - viewAngles.X) < 0.25f) return;
        pawn.Teleport(angles: new Vector3(0f, yaw, 0f));
        // Keep both the simulated and observer-facing angles in sync while native look is paused.
        viewAngles.X = eyeAngles.X = pitch;
        viewAngles.Y = eyeAngles.Y = yaw;
        viewAngles.Z = eyeAngles.Z = 0f;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");
    }

    private static void BriefNativeLook(CCSPlayerPawn pawn, CCSBot bot, float yaw, float duration, float now)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null) return;
        float radians = yaw * MathF.PI / 180f;
        float pitch = Math.Clamp(pawn.EyeAngles.X, -60f, 60f) * MathF.PI / 180f;
        var spot = bot.LookAtSpot;
        spot.X = origin.X + MathF.Cos(radians) * 450f;
        spot.Y = origin.Y + MathF.Sin(radians) * 450f;
        spot.Z = origin.Z + pawn.ViewOffset.Z - MathF.Tan(pitch) * 450f;
        bot.LookAtSpotTimestamp = now;
        bot.LookAtSpotDuration = duration;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.InhibitLookAroundTimestamp = now + duration;
    }
    private static void ApplyLegacyFlashLook(CCSBot bot, Vector origin, FlashTurn turn, float now)
    {
        float yaw = MathF.Atan2(origin.Y - turn.Position.Y, origin.X - turn.Position.X);
        bot.LookYaw = yaw * 180f / MathF.PI;
        bot.LookPitch = 12f;
        var spot = bot.LookAtSpot;
        spot.X = origin.X + MathF.Cos(yaw) * 300f;
        spot.Y = origin.Y + MathF.Sin(yaw) * 300f;
        spot.Z = origin.Z + 30f;
        bot.LookAtSpotDuration = turn.Until - now;
        bot.LookAtSpotTimestamp = now;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.InhibitLookAroundTimestamp = turn.Until;
    }

    // After an actual flash: back off / strafe, spray blind at the last contact, or crouch and hold.
    private void StartBlindReaction(CCSPlayerController player, float blindDur)
    {
        if (_botController == null || blindDur < 0.7f) return;
        int slot = player.Slot;
        Server.NextFrame(() =>
        {
            try
            {
                if (!IsAliveBot(player, out var pawn, out var bot)) return;
                float now = Server.CurrentTime;
                float window = MathF.Min(blindDur, RandF(0.9f, 1.6f));
                double r = _random.NextDouble();
                bool recentContact = now - bot.LastSawEnemyTimestamp < 2f && bot.LastEnemyPosition != null;
                if (IsDefusalCombatEnabled())
                {
                    if (recentContact && r < 0.3)
                    {
                        string? weapon = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                        if (!IsNonGunWeapon(weapon))
                            BotControllerBridge.InjectUsercmd(_botController, slot, AttackButtonMask, (int)RandF(250f, 600f));
                    }
                    float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw), lx = -fy, ly = fx;
                    float side = _random.Next(2) == 0 ? 0.7f : -0.7f;
                    if (pawn.AbsOrigin != null && GuardCanStand(pawn, pawn.AbsOrigin)) bot.IsCrouching = false;
                    foreach (var move in new[] { (fwd: -0.8f, left: side), (fwd: -0.8f, left: -side),
                                 (fwd: 0f, left: side), (fwd: 0f, left: -side), (fwd: -1f, left: 0f) })
                    {
                        if (!DirClear(pawn, fx, fy, lx, ly, move.fwd, move.left)) continue;
                        SetMove(slot, MoveOwner.Flash, move.fwd, move.left, now + window);
                        break;
                    }
                    return;
                }

                if (recentContact && r < 0.3)
                {
                    // Panic spray where the enemy was.
                    string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                    if (!IsNonGunWeapon(wpn))
                        BotControllerBridge.InjectUsercmd(_botController, slot, AttackButtonMask, (int)RandF(250f, 600f));
                    return;
                }
                if (r < 0.7)
                {
                    // Back off, usually diagonally.
                    float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float fx = MathF.Cos(yaw), fy = MathF.Sin(yaw), lx = -fy, ly = fx;
                    float side = _random.Next(2) == 0 ? 0.6f : -0.6f;
                    float fwd = -0.9f;
                    if (!DirClear(pawn, fx, fy, lx, ly, fwd, side)) side = -side;
                    if (!DirClear(pawn, fx, fy, lx, ly, fwd, side)) { side = 0f; }
                    if (!DirClear(pawn, fx, fy, lx, ly, fwd, side)) { fwd = 0f; side = side == 0f ? 1f : side; }
                    SetMove(slot, MoveOwner.Flash, fwd, side, now + window);
                    return;
                }
                // Crouch and hold still.
                ref bool crouch = ref bot.IsCrouching; crouch = true;
                SetMove(slot, MoveOwner.Flash, 0f, 0f, now + window * 0.8f);
            }
            catch { }
        });
    }
}
