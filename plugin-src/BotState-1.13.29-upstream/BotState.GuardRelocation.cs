using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private static readonly Vector GuardEscapeMins = new(-13f, -13f, 0f);
    private static readonly Vector GuardEscapeDuckHull = new(13f, 13f, 35f);
    private static readonly Vector GuardEscapeStandHull = new(13f, 13f, 55f);

    private bool UpdateGuardRelocation(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot,
        SiteHold hold, Vector pos, float now)
    {
        if (now < hold.EscapeUntil) return true;
        if (hold.MotionAt < 0f || GuardDistanceSquared(hold.MotionX, hold.MotionY, pos.X, pos.Y) >= 14f * 14f)
        {
            if (hold.EscapeAttempts > 0)
            {
                hold.EscapeAttempts = 0;
                hold.Anchor = new Vector(pos.X, pos.Y, pos.Z);
                hold.NextLaneCheck = now + RandF(3.5f, 5.5f);
                PickSiteAngle(player.Slot, pawn, hold, change: true);
                _guardRelocated++;
                if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Guard] {player.PlayerName} relocated at {RetakeSiteLabel(hold.Site)}");
            }
            hold.MotionX = pos.X;
            hold.MotionY = pos.Y;
            hold.MotionAt = now;
            return false;
        }
        if (hold.EscapeAttempts == 0 && now - hold.MotionAt < 1.35f) return false;

        int slot = player.Slot;
        ReleaseMove(slot, MoveOwner.GuardRecovery);
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        ResetLookAroundForBot(player);
        hold.EscapeUntil = now + 1.25f;
        _guardRelocations++;
        bool standing = GuardCanStand(pawn, pos);
        if (standing) bot.IsCrouching = false;

        // A shallow sweep works even when a full standing peek cannot start in a tight corner.
        var center = SiteCenter(hold.Site);
        float inward = MathF.Atan2(center.Y - pos.Y, center.X - pos.X);
        int attempt = hold.EscapeAttempts++;
        if (attempt % 3 != 2)
        {
            float view = pawn.EyeAngles.Y * MathF.PI / 180f;
            float offset = attempt % 3 == 0 ? 0f : MathF.PI / 8f;
            ReadOnlySpan<float> angles = [0f, 0.79f, -0.79f, 1.57f, -1.57f, 2.36f, -2.36f, 3.14f];
            foreach (float angle in angles)
            {
                float yaw = inward + angle + offset;
                float dx = MathF.Cos(yaw), dy = MathF.Sin(yaw);
                if (!SiteGuardPathClear(pawn, hold, pos, dx, dy, 42f) || !GuardEscapeClear(pawn, pos, dx, dy, standing)) continue;
                if (SetMove(slot, MoveOwner.GuardRecovery, MathF.Cos(yaw - view) * 0.82f,
                    MathF.Sin(yaw - view) * 0.82f, now + 0.55f))
                {
                    if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Guard] {player.PlayerName} escape at {RetakeSiteLabel(hold.Site)} via short move");
                    return true;
                }
            }
        }

        // If local input cannot get it out, give navigation a route deeper into the same site.
        float distance = Math.Min(220f, Dist2D(pos, center));
        var goal = distance >= 75f
            ? new Vector(pos.X + MathF.Cos(inward) * distance, pos.Y + MathF.Sin(inward) * distance, center.Z)
            : center;
        if (!BotMoveTo(bot, goal, 2)) ForceRepath(bot);
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Guard] {player.PlayerName} escape at {RetakeSiteLabel(hold.Site)} via navigation");
        return true;
    }

    private static bool GuardEscapeClear(CCSPlayerPawn pawn, Vector pos, float dx, float dy, bool standing, float distance = 42f)
    {
        try
        {
            var start = new Vector(pos.X, pos.Y, pos.Z + 12f);
            var end = new Vector(pos.X + dx * distance, pos.Y + dy * distance, start.Z);
            var hit = Trace.TraceHullShape(start, end, GuardEscapeMins, standing ? GuardEscapeStandHull : GuardEscapeDuckHull,
                pawn, RetakeGeometry);
            return !hit.IsAllSolid && hit.Fraction >= 0.95f;
        }
        catch { return false; }
    }
}
