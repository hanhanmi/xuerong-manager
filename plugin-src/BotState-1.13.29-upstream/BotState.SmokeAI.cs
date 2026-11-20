using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    private enum SmokeChoice { Push = 0, Wait = 1, Reroute = 2, Hide = 3 }

    private sealed class SmokeHold
    {
        public int SmokeId;
        public SmokeChoice Mode;
        public float Until, NextStepAt, DirX, DirY, LastX, LastY, LastProgressAt;
        public bool Holding;     // Hide: true once inside the smoke
    }

    private sealed class SpamState
    {
        public float NextRoll;
        public int Bursts;
        public float ContactStamp;
        public Vector? HurtTarget;
        public float HurtTargetUntil;
    }

    private readonly Dictionary<(int slot, int smoke), SmokeChoice> _smokeDecisions = new();
    private readonly Dictionary<int, SmokeHold> _smokeHold = new();
    private readonly Dictionary<int, float> _smokeNextCheck = new();
    private readonly Dictionary<int, SpamState> _smokeSpam = new();

    // Decides how a non-fighting bot treats a smoke on its path: wait, reroute, hide or push.
    private void UpdateSmokeApproach(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot,
                                     int slot, float now, bool curIsAttacking)
    {
        bool threat = curIsAttacking || bot.IsEnemyVisible || pawn.IsDefusing;

        if (_smokeHold.TryGetValue(slot, out var hold))
        {
            bool smokeGone = !_smokes.Exists(s => s.Id == hold.SmokeId);
            if (threat || smokeGone || ActiveMoveOwner(slot, now) > MoveOwner.Smoke)
            {
                EndSmokeHold(slot, hold);
                return;
            }
            if (hold.Mode == SmokeChoice.Wait)
            {
                if (now >= hold.Until)
                {
                    // After waiting: mostly go through, sometimes take another way.
                    EndSmokeHold(slot, hold);
                    _smokeDecisions[(slot, hold.SmokeId)] =
                        _random.NextDouble() < 0.65 ? SmokeChoice.Push : SmokeChoice.Reroute;
                    if (_smokeDecisions[(slot, hold.SmokeId)] == SmokeChoice.Reroute)
                        TrySmokeReroute(pawn, bot);
                }
                else if (IsDefusalCombatEnabled()) MoveWhileWatchingSmoke(slot, pawn, hold, now);
                else SetMove(slot, MoveOwner.Smoke, 0f, 0f, now + 0.3f);
                return;
            }
            if (hold.Mode == SmokeChoice.Hide)
            {
                var s = _smokes.Find(x => x.Id == hold.SmokeId);
                var o = pawn.AbsOrigin;
                if (s == null || o == null) { EndSmokeHold(slot, hold); return; }
                float dx = o.X - s.X, dy = o.Y - s.Y;
                if (!hold.Holding)
                {
                    if (dx * dx + dy * dy < 90f * 90f)
                    {
                        hold.Holding = true;
                        hold.Until = now + RandF(2f, 4.5f);
                    }
                    else if (now > hold.Until) { EndSmokeHold(slot, hold); return; }
                }
                if (hold.Holding)
                {
                    if (now >= hold.Until) { EndSmokeHold(slot, hold); return; }
                    if (IsDefusalCombatEnabled()) MoveWhileWatchingSmoke(slot, pawn, hold, now);
                    else SetMove(slot, MoveOwner.Smoke, 0f, 0f, now + 0.3f);
                }
                return;
            }
            EndSmokeHold(slot, hold);
            return;
        }

        if (threat || _smokes.Count == 0 || _botController == null) return;
        if (now < _smokeNextCheck.GetValueOrDefault(slot)) return;
        _smokeNextCheck[slot] = now + 0.2f;

        var pos = pawn.AbsOrigin;
        if (pos == null) return;
        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        float sp = MathF.Sqrt(vx * vx + vy * vy);
        if (sp < 60f) return;
        if (SmokeNear(pos, SmokeRadius) != null) return; // already inside

        var ahead = new Vector(pos.X + vx / sp * 220f, pos.Y + vy / sp * 220f, pos.Z);
        var smoke = SmokeNear(ahead, SmokeRadius + 30f);
        if (smoke == null || now - smoke.Start < 1.2f) return;

        var key = (slot, smoke.Id);
        if (_smokeDecisions.TryGetValue(key, out var decided))
        {
            return; // already decided for this smoke (push / reroute done)
        }

        bool ctRetake = player.TeamNum == (byte)CsTeam.CounterTerrorist && IsBombPlanted();
        double r = _random.NextDouble();
        SmokeChoice choice;
        if (ctRetake) choice = r < 0.3 ? SmokeChoice.Wait : SmokeChoice.Push;
        else choice = r < 0.38 ? SmokeChoice.Wait
                    : r < 0.60 ? SmokeChoice.Reroute
                    : r < 0.75 ? SmokeChoice.Hide
                    : SmokeChoice.Push;
        if (choice == SmokeChoice.Reroute && !TrySmokeReroute(pawn, bot)) choice = SmokeChoice.Wait;
        _smokeDecisions[key] = choice;

        if (choice == SmokeChoice.Wait)
        {
            var h = new SmokeHold { SmokeId = smoke.Id, Mode = SmokeChoice.Wait, Until = now + RandF(2f, 5f) };
            _smokeHold[slot] = h;
            if (IsDefusalCombatEnabled()) MoveWhileWatchingSmoke(slot, pawn, h, now);
            else SetMove(slot, MoveOwner.Smoke, 0f, 0f, now + 0.3f);
            ResetLookAroundForBot(player);
        }
        else if (choice == SmokeChoice.Hide)
        {
            _smokeHold[slot] = new SmokeHold { SmokeId = smoke.Id, Mode = SmokeChoice.Hide, Until = now + 3f };
        }
    }

    private void MoveWhileWatchingSmoke(int slot, CCSPlayerPawn pawn, SmokeHold hold, float now)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null || !DuelGrounded(pawn) || pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            ReleaseMove(slot, MoveOwner.Smoke);
            return;
        }
        var smoke = _smokes.Find(s => s.Id == hold.SmokeId);
        if (smoke == null) return;
        float dx = smoke.X - origin.X, dy = smoke.Y - origin.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
        {
            float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
            dx = MathF.Cos(yaw); dy = MathF.Sin(yaw); length = 1f;
        }
        float x = dx / length, y = dy / length;
        if (hold.LastProgressAt > 0f)
        {
            float movedX = origin.X - hold.LastX, movedY = origin.Y - hold.LastY;
            if (movedX * movedX + movedY * movedY >= 9f)
            {
                hold.LastX = origin.X; hold.LastY = origin.Y; hold.LastProgressAt = now;
            }
        }
        bool blocked = hold.DirX != 0f || hold.DirY != 0f;
        blocked = blocked && (!IsDirectionClear(pawn, hold.DirX, hold.DirY, 24f)
            || (hold.LastProgressAt > 0f && now - hold.LastProgressAt >= 0.32f));
        if (now >= hold.NextStepAt || blocked)
        {
            float side = hold.DirX == 0f && hold.DirY == 0f ? (_random.Next(2) == 0 ? 1f : -1f)
                : MathF.Sign(hold.DirX * -y + hold.DirY * x) * -1f;
            if (side == 0f) side = 1f;
            float along = hold.Holding ? -0.10f : -0.22f;
            float sx = -y * side + x * along, sy = x * side + y * along;
            float norm = MathF.Sqrt(sx * sx + sy * sy);
            sx /= norm; sy /= norm;
            if (!IsDirectionClear(pawn, sx, sy, 34f)) { sx = y * side + x * along; sy = -x * side + y * along; }
            norm = MathF.Sqrt(sx * sx + sy * sy);
            sx /= norm; sy /= norm;
            if (!IsDirectionClear(pawn, sx, sy, 28f))
            {
                ReleaseMove(slot, MoveOwner.Smoke);
                hold.NextStepAt = now + 0.15f;
                return;
            }
            hold.DirX = sx; hold.DirY = sy;
            hold.LastX = origin.X; hold.LastY = origin.Y; hold.LastProgressAt = now;
            hold.NextStepAt = now + RandF(0.38f, 0.65f);
        }
        float view = pawn.EyeAngles.Y * MathF.PI / 180f;
        float fx = MathF.Cos(view), fy = MathF.Sin(view);
        float forward = (hold.DirX * fx + hold.DirY * fy) * 0.68f;
        float left = (-hold.DirX * fy + hold.DirY * fx) * 0.68f;
        SetMove(slot, MoveOwner.Smoke, forward, left, now + 0.12f);
    }
    private void EndSmokeHold(int slot, SmokeHold hold)
    {
        _smokeHold.Remove(slot);
        ReleaseMove(slot, MoveOwner.Smoke);
    }

    // Sends the bot somewhere that does not go through the smoke ahead.
    private bool TrySmokeReroute(CCSPlayerPawn pawn, CCSBot bot)
    {
        if (!TacticsEnabled() || pawn.AbsOrigin == null) return false;
        var pos = pawn.AbsOrigin;
        var a = SiteCenter(0); var b = SiteCenter(1);
        Vector goal;
        var ctrl = pawn.OriginalController.Value;
        bool isT = ctrl != null && ctrl.TeamNum == (byte)CsTeam.Terrorist;
        // Prefer the site whose straight line is not smoked; T otherwise picks the other site.
        bool aSmoked = SegmentThroughSmoke(pos, a, SmokeEdge);
        bool bSmoked = SegmentThroughSmoke(pos, b, SmokeEdge);
        if (aSmoked && !bSmoked) goal = b;
        else if (bSmoked && !aSmoked) goal = a;
        else if (isT) goal = Dist2D(pos, a) < Dist2D(pos, b) ? b : a;
        else return false;
        var g = new Vector(goal.X + RandF(-120f, 120f), goal.Y + RandF(-120f, 120f), goal.Z);
        return BotMoveTo(bot, g, 2);
    }

    private static bool IsBombPlanted()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        return rules != null && rules.BombPlanted;
    }

    // Remembers where an unseen attacker shot from, so the bot may spray back into the smoke.
    private void NoteHurtThroughSmoke(CCSPlayerController victim, CCSPlayerController attacker)
    {
        if (_smokes.Count == 0) return;
        var vp = victim.PlayerPawn.Value; var ap = attacker.PlayerPawn.Value;
        if (vp?.AbsOrigin == null || ap?.AbsOrigin == null) return;
        if (!SegmentThroughSmoke(vp.AbsOrigin, ap.AbsOrigin, SmokeEdge)) return;
        var st = GetSpam(victim.Slot);
        st.HurtTarget = new Vector(ap.AbsOrigin.X + RandF(-140f, 140f), ap.AbsOrigin.Y + RandF(-140f, 140f), ap.AbsOrigin.Z + 40f);
        st.HurtTargetUntil = Server.CurrentTime + 2.5f;
    }

    private SpamState GetSpam(int slot)
    {
        if (!_smokeSpam.TryGetValue(slot, out var s)) { s = new SpamState(); _smokeSpam[slot] = s; }
        return s;
    }

    // Occasionally fires a short burst into a smoke where an enemy was last seen.
    private void UpdateSmokeSpam(CCSPlayerPawn pawn, CCSBot bot, int slot, float now)
    {
        if (_botController == null || _smokes.Count == 0 || bot.IsEnemyVisible || pawn.IsDefusing) return;
        var st = GetSpam(slot);
        if (now < st.NextRoll) return;
        st.NextRoll = now + 0.2f;

        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        if (IsNonGunWeapon(wpn) || IsSniperWeapon(wpn)) return;
        var active = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (active == null || !active.IsValid || new CCSWeaponBase(active.Handle).Clip1 <= 3) return;

        var o = pawn.AbsOrigin;
        if (o == null) return;
        Vector? target = null;
        float since = now - bot.LastSawEnemyTimestamp;
        if (st.HurtTarget != null && now < st.HurtTargetUntil) target = st.HurtTarget;
        else if (since > 0.3f && since < 3.5f && bot.LastEnemyPosition != null)
        {
            target = bot.LastEnemyPosition;
            if (st.ContactStamp != bot.LastSawEnemyTimestamp)
            {
                st.ContactStamp = bot.LastSawEnemyTimestamp;
                st.Bursts = 0;
            }
        }
        if (target == null || st.Bursts >= 3) return;

        var eye = new Vector(o.X, o.Y, o.Z + 64f);
        if (!SegmentThroughSmoke(eye, target)) return;

        // Only shoot where the bot is already looking.
        float yawTo = MathF.Atan2(target.Y - o.Y, target.X - o.X) * 180f / MathF.PI;
        if (MathF.Abs((float)NormalizeAngleDeg(yawTo - pawn.EyeAngles.Y)) > 12f) return;

        if (_random.NextDouble() > 0.30) { st.NextRoll = now + RandF(0.5f, 1.0f); return; }

        bool pistol = wpn is "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_p250"
            or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" or "weapon_deagle" or "weapon_elite" or "weapon_revolver";
        int ms = pistol ? 60 : (int)RandF(150f, 420f);
        try { BotControllerBridge.InjectUsercmd(_botController, slot, AttackButtonMask, ms); } catch { return; }
        st.Bursts++;
        st.NextRoll = now + RandF(1.0f, 2.5f);
    }

    private void ResetSmokeForSlot(int slot)
    {
        if (_smokeHold.TryGetValue(slot, out var h)) EndSmokeHold(slot, h);
        _smokeSpam.Remove(slot);
    }
}
