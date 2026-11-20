using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    // Knife while travelling; back to the gun when an enemy is noticed.
    // Noticing is human-like: seen in front, heard running, or very close. A shift-walking
    // enemy coming from behind can catch a knifing bot, more so for "careless" bots.
    private sealed class KnifeRunState
    {
        public bool Runner;        // rolled once per round
        public bool Careless;
        public bool Active;
        public float MovingSince = -1f;
        public float StillSince = -1f;
        public float NextCheck;
        public float NoticedAt = -1f;
        public float SwitchBackAt = -1f;
        public float Cooldown;
    }

    private readonly Dictionary<int, KnifeRunState> _knifeRun = new();

    private KnifeRunState GetKnifeRun(int slot)
    {
        if (!_knifeRun.TryGetValue(slot, out var s))
        {
            s = new KnifeRunState
            {
                Runner = _random.NextDouble() < 0.55,
                Careless = _random.NextDouble() < 0.22,
            };
            _knifeRun[slot] = s;
        }
        return s;
    }

    private bool IsKnifeRunning(int slot) => _knifeRun.TryGetValue(slot, out var s) && s.Active;

    private void OnBotHurtKnifeRun(int slot)
    {
        if (_knifeRun.TryGetValue(slot, out var s) && s.Active) s.SwitchBackAt = Server.CurrentTime;
    }

    private void UpdateKnifeRun(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int slot, float now, bool curIsAttacking)
    {
        if (_botController == null || _isFreezeTime || IsDeathmatch()) return;
        if (_knifeLockedBotSlots.Contains(slot)) return;
        var s = GetKnifeRun(slot);
        if (GuardOwnsLocalMovement(slot, pawn, now))
        {
            if (s.Active) EndKnifeRun(pawn, slot, s, now);
            else s.MovingSince = -1f;
            return;
        }
        var o = pawn.AbsOrigin;
        if (o == null) return;

        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        float sp2 = vx * vx + vy * vy;

        if (s.Active)
        {
            // Hard triggers: fighting or hurt -> gun now (the draw time is the punishment).
            if (curIsAttacking || bot.IsEnemyVisible || pawn.IsDefusing) { EndKnifeRun(pawn, slot, s, now); return; }
            if (s.SwitchBackAt >= 0f && now >= s.SwitchBackAt) { EndKnifeRun(pawn, slot, s, now); return; }
            // The knife lock would block switching to C4 for the plant.
            if (NearSiteWithC4(pawn)) { EndKnifeRun(pawn, slot, s, now); return; }

            // Arrived / holding: stop knifing after ~1.2 s standing still.
            if (sp2 < 60f * 60f)
            {
                if (s.StillSince < 0f) s.StillSince = now;
                else if (now - s.StillSince > 1.2f) { EndKnifeRun(pawn, slot, s, now); return; }
            }
            else s.StillSince = -1f;

            if (now >= s.NextCheck)
            {
                s.NextCheck = now + 0.2f;
                if (s.SwitchBackAt < 0f && EnemyNoticed(player, pawn, s.Careless, 1300f))
                {
                    // Small human delay between noticing and pulling the gun.
                    s.SwitchBackAt = now + (s.Careless ? RandF(0.35f, 0.8f) : RandF(0.08f, 0.3f));
                }
            }
            return;
        }

        if (!s.Runner || now < s.Cooldown) return;
        if (curIsAttacking || bot.IsEnemyVisible || pawn.IsDefusing || pawn.IsScoped) { s.MovingSince = -1f; return; }
        if (sp2 < 170f * 170f) { s.MovingSince = -1f; return; }
        if (s.MovingSince < 0f) { s.MovingSince = now; return; }
        if (now - s.MovingSince < 1.0f || now < s.NextCheck) return;
        s.NextCheck = now + 0.3f;

        if (now - bot.LastSawEnemyTimestamp < 6f) return;
        if (_reaction.TryGetValue(slot, out var rs) && now - rs.LastHurtAt < 5f) return;
        if (_smokeHold.ContainsKey(slot)) return;
        if (NearSiteWithC4(pawn)) return;

        var active = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (active == null || !active.IsValid) return;
        var gearSlot = new CCSWeaponBase(active.Handle).VData?.GearSlot;
        if (gearSlot != gear_slot_t.GEAR_SLOT_RIFLE && gearSlot != gear_slot_t.GEAR_SLOT_PISTOL) return;

        if (EnemyNoticed(player, pawn, false, 2000f)) return;  // larger start radius = hysteresis

        try
        {
            if (BotControllerBridge.SwitchBotWeapon(_botController, slot, KnifeDefinitionIndex)
                && BotControllerBridge.LockKnife(_botController, slot))
            {
                s.Active = true;
                s.StillSince = -1f;
                s.SwitchBackAt = -1f;
                s.NoticedAt = -1f;
            }
        }
        catch { }
    }

    private void EndKnifeRun(CCSPlayerPawn pawn, int slot, KnifeRunState s, float now)
    {
        s.Active = false;
        s.MovingSince = -1f;
        s.SwitchBackAt = -1f;
        s.Cooldown = now + RandF(3f, 6f);
        if (_botController == null) return;
        try
        {
            if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                BotControllerBridge.UnlockWeapon(_botController, slot);
            if (GetReequipWeapon(pawn, out int def))
                BotControllerBridge.SwitchBotWeapon(_botController, slot, def);
        }
        catch { }
    }

    // Seen in a forward cone with line of sight, heard running/shooting nearby, or within touch range.
    private bool EnemyNoticed(CCSPlayerController self, CCSPlayerPawn pawn, bool careless, float sightRange)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return false;
        float touch = careless ? 280f : 450f;
        float hear = careless ? 750f : 1100f;
        float cone = careless ? 50f : 75f; // half-angle
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum == self.TeamNum) continue;
            if (p.TeamNum != (byte)CsTeam.Terrorist && p.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var ep = p.PlayerPawn.Value;
            if (ep?.AbsOrigin == null || ep.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var e = ep.AbsOrigin;
            float dx = e.X - o.X, dy = e.Y - o.Y, dz = e.Z - o.Z;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > sightRange) continue;
            if (d < touch) return true;

            float evx = ep.AbsVelocity.X, evy = ep.AbsVelocity.Y;
            bool running = evx * evx + evy * evy > 150f * 150f && (ep.Flags & 1) != 0;
            if (running && d < hear) return true;

            float yawTo = MathF.Atan2(dy, dx) * 180f / MathF.PI;
            if (MathF.Abs((float)NormalizeAngleDeg(yawTo - pawn.EyeAngles.Y)) > cone) continue;
            if (BotCanSee(pawn, new Vector(e.X, e.Y, e.Z + 56f))) return true;
        }
        return false;
    }

    private bool NearSiteWithC4(CCSPlayerPawn pawn)
    {
        if (pawn.AbsOrigin == null || !HasC4(pawn)) return false;
        ResolveSites();
        return SiteOf(pawn.AbsOrigin, 1500f) >= 0;
    }

    private void ResetKnifeRunForSlot(int slot)
    {
        if (_knifeRun.TryGetValue(slot, out var s) && s.Active && _botController != null)
        {
            try
            {
                if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                    BotControllerBridge.UnlockWeapon(_botController, slot);
            }
            catch { }
        }
        _knifeRun.Remove(slot);
    }

    private void ResetKnifeRunRoundState()
    {
        foreach (var slot in _knifeRun.Keys.ToList()) ResetKnifeRunForSlot(slot);
        _knifeRun.Clear();
    }
}
