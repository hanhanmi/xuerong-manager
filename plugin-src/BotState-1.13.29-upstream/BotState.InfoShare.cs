using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    // Probabilistic "callouts": a bot that sees an enemy shares an APPROXIMATE position
    // with some teammates. It is deliberately loose (noise on the position, random subset
    // of teammates, random chance to fire at all, per-bot cooldowns) so it reads like radio
    // chatter, not a shared aimbot. Receivers investigate / glance, they do not snap-aim.
    private float _nextShareScan;
    private readonly Dictionary<int, float> _shareCd = new();      // sharer slot -> next allowed
    private readonly Dictionary<int, float> _shareRecvCd = new();  // receiver slot -> next allowed

    private void UpdateInfoShare(float now)
    {
        if (now < _nextShareScan) return;
        _nextShareScan = now + 0.4f;
        if (_isFreezeTime || IsDeathmatch()) return;

        var players = GetMatchPlayers().ToArray();
        if (IsDefusalCombatEnabled())
        {
            foreach (var player in players)
            {
                if (_shareCd.GetValueOrDefault(player.Slot) > now || !IsAliveBot(player, out var pawn, out var bot)
                    || !bot.IsEnemyVisible || DuelFlashActive(pawn, (int)player.Index, now)) continue;
                var enemy = bot.Enemy.Value;
                if (enemy is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                    || enemy.TeamNum == player.TeamNum) continue;
                _shareCd[player.Slot] = now + RandF(1.5f, 2.7f);
                if (_random.NextDouble() < 0.78) ShareAwareness(player, players, pos, CueKind.Visual, now, now);
            }
            return;
        }
        foreach (var sp in players)
        {
            if (sp == null || !IsAliveBot(sp, out var spawn, out var sbot)) continue;
            if (!sbot.IsEnemyVisible) continue;
            int sslot = sp.Slot;
            if (_shareCd.GetValueOrDefault(sslot) > now) continue;

            var enemy = sbot.Enemy.Value;
            if (enemy == null || !enemy.IsValid || enemy.AbsOrigin == null) continue;
            if (enemy.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            // Not every sighting becomes a callout.
            if (_random.NextDouble() > 0.55) { _shareCd[sslot] = now + RandF(1.5f, 3f); continue; }

            var epos = new Vector(enemy.AbsOrigin.X, enemy.AbsOrigin.Y, enemy.AbsOrigin.Z);
            byte team = sp.TeamNum;

            var recv = new List<(CCSPlayerController p, CCSPlayerPawn pw, CCSBot b, float d)>();
            foreach (var rp in players)
            {
                if (rp == null || rp.Slot == sslot || rp.TeamNum != team) continue;
                if (!IsAliveBot(rp, out var rpw, out var rb)) continue;
                if (rb.IsEnemyVisible || rb.IsAttacking) continue;
                if (now - rb.LastSawEnemyTimestamp < 2f) continue; // already has fresh info
                if (_shareRecvCd.GetValueOrDefault(rp.Slot) > now) continue;
                float d = Dist2D(rpw.AbsOrigin!, epos);
                if (d > 4000f) continue;
                recv.Add((rp, rpw, rb, d));
            }
            if (recv.Count == 0) { _shareCd[sslot] = now + RandF(1.5f, 3f); continue; }

            // Share with a random subset (1..3) — never the whole team at once.
            recv = recv.OrderBy(_ => _random.Next()).ToList();
            int shared = 0, cap = Math.Min(recv.Count, _random.Next(1, 4));
            foreach (var r in recv)
            {
                if (shared >= cap) break;
                if (_random.NextDouble() > 0.7) continue; // some teammates miss the call

                float noise = Math.Clamp(90f + r.d * 0.06f, 90f, 430f);
                var shaped = new Vector(epos.X + RandF(-noise, noise), epos.Y + RandF(-noise, noise), epos.Z);
                ApplyCallout(r.pw, r.b, shaped, now);
                _shareRecvCd[r.p.Slot] = now + RandF(3f, 6f);
                shared++;
            }
            _shareCd[sslot] = now + RandF(2.5f, 5f);
        }
    }

    // Feeds one teammate an APPROXIMATE enemy position: a remembered spot to clear and,
    // for some, a brief glance toward it. Never sets IsEnemyVisible or forces aim.
    private void ApplyCallout(CCSPlayerPawn pawn, CCSBot bot, Vector pos, float now)
    {
        try
        {
            var lep = bot.LastEnemyPosition;
            lep.X = pos.X; lep.Y = pos.Y; lep.Z = pos.Z;
            ref float saw = ref bot.LastSawEnemyTimestamp;
            if (now - saw > 3f) saw = now - RandF(1.5f, 2.5f); // "heard about", not "just saw"
            ref bool deadMem = ref bot.IsLastEnemyDead; deadMem = false;

            var np = bot.NoisePosition;
            np.X = pos.X; np.Y = pos.Y; np.Z = pos.Z;
            ref float nts = ref bot.NoiseTimestamp; nts = now;

            if (_random.NextDouble() < 0.45 && pawn.AbsOrigin != null)
            {
                var spot = bot.LookAtSpot;
                spot.X = pos.X; spot.Y = pos.Y; spot.Z = pos.Z + 40f;
                ref float dur = ref bot.LookAtSpotDuration; dur = RandF(0.4f, 0.9f);
                ref float ts = ref bot.LookAtSpotTimestamp; ts = now;
                ref bool atk = ref bot.LookAtSpotAttack; atk = false;
            }
        }
        catch { }
    }

    private void ResetInfoShareRoundState()
    {
        ResetAwareness();
        _shareCd.Clear();
        _shareRecvCd.Clear();
        _nextShareScan = 0f;
    }
}
