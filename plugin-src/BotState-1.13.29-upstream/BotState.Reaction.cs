using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    // Human-like reaction before the first shot at a newly seen enemy.
    // The bot still turns and aims during the delay; only IN_ATTACK is held back.
    private const ulong AttackButtonMask = 1UL; // IN_ATTACK

    private sealed class ReactionState
    {
        public bool PrevVisible;
        public uint EnemyHandle;
        public float LostAt = -999f;
        public float LastHurtAt = -999f;
        public uint LastHurtBy;
        public float HoldUntil;
        public long DefusalFireBlock;
        public float AwaitingShotSince = -1f;
    }

    private readonly Dictionary<int, ReactionState> _reaction = new();
    private int _statReactions;
    private float _statReactionMsSum;

    private ReactionState GetReaction(int slot)
    {
        if (!_reaction.TryGetValue(slot, out var r)) { r = new ReactionState(); _reaction[slot] = r; }
        return r;
    }

    // True while a reaction delay is still holding fire for this bot.
    private bool InReactionDelay(int slot, float now)
        => _reaction.TryGetValue(slot, out var r) && now < r.HoldUntil;

    private void NoteBotHurtForReaction(int victimSlot, CCSPlayerController? attacker)
    {
        var r = GetReaction(victimSlot);
        r.LastHurtAt = Server.CurrentTime;
        r.LastHurtBy = attacker?.PlayerPawn?.Raw ?? 0;
    }

    private void UpdateReaction(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int slot, float now)
    {
        if (_botController == null) return;
        var r = GetReaction(slot);
        bool tuned = IsDefusalCombatEnabled() && _defusalTier.HasValue;
        if (r.DefusalFireBlock > 0 && (!tuned || now >= r.HoldUntil)) ReleaseDefusalReaction(slot, r);
        if (tuned && DefusalObjectiveBusy(pawn))
        {
            ReleaseDefusalReaction(slot, r);
            r.PrevVisible = false;
            r.EnemyHandle = 0;
            r.AwaitingShotSince = -1f;
            return;
        }

        var enemy = bot.Enemy.Value;
        bool vis = bot.IsEnemyVisible && enemy != null && enemy.IsValid
            && enemy.LifeState == (byte)LifeState_t.LIFE_ALIVE;
        uint handle = vis ? bot.Enemy.Raw : 0u;

        if (!vis)
        {
            if (r.PrevVisible) r.LostAt = now;
            r.PrevVisible = false;
            if (now - r.LostAt >= 0.25f)
            {
                ReleaseDefusalReaction(slot, r);
                r.AwaitingShotSince = -1f;
            }
            return;
        }

        bool newContact = !r.PrevVisible || handle != r.EnemyHandle;
        bool sameEnemy = handle == r.EnemyHandle;
        float lostFor = now - r.LostAt;
        r.PrevVisible = true;
        r.EnemyHandle = handle;
        if (!newContact) return;

        // Flicker (one or two ticks of lost sight) is not a new reaction.
        if (sameEnemy && lostFor < 0.25f) return;

        if (tuned)
        {
            ReleaseDefusalReaction(slot, r);
            r.AwaitingShotSince = now;
            float hold = ComputeDefusalReactionMs(pawn, bot, enemy!, r, now, sameEnemy, lostFor, _defusalTier!.Value);
            try
            {
                r.DefusalFireBlock = BotControllerBridge.StartUsercmdSuppression(_botController, slot, AttackButtonMask);
                if (r.DefusalFireBlock > 0)
                {
                    r.HoldUntil = now + hold / 1000f;
                    _statReactions++;
                    _statReactionMsSum += hold;
                }
            }
            catch { }
            return;
        }

        float ms = ComputeReactionMs(pawn, bot, enemy!, r, now, sameEnemy, lostFor);
        int dur = (int)ms;
        try
        {
            if (BotControllerBridge.SuppressUsercmd(_botController, slot, AttackButtonMask, dur))
            {
                r.HoldUntil = now + ms / 1000f;
                _statReactions++;
                _statReactionMsSum += ms;
            }
        }
        catch { }
    }

    private float ComputeDefusalReactionMs(CCSPlayerPawn pawn, CCSBot bot, CCSPlayerPawn enemy,
        ReactionState r, float now, bool sameEnemy, float lostFor, DefusalTier tier)
    {
        var o = pawn.AbsOrigin;
        var e = enemy.AbsOrigin;
        if (o == null || e == null) return tier switch { DefusalTier.Easy => 260f, DefusalTier.Normal => 235f, _ => 185f };

        float dx = e.X - o.X, dy = e.Y - o.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float turn = MathF.Abs((float)NormalizeAngleDeg(MathF.Atan2(dy, dx) * 180f / MathF.PI - pawn.EyeAngles.Y));
        var last = bot.LastEnemyPosition;
        bool expected = turn < 26f && sameEnemy && lostFor < 1.45f;
        // Native last-seen coordinates can already have updated to this new enemy.
        // Only the same, genuinely tracked opponent earns the early window.
        float ms = tier switch
        {
            DefusalTier.Easy => expected ? RandF(180f, 235f) : RandF(235f, 290f),
            DefusalTier.Normal => expected ? RandF(145f, 202f) : RandF(205f, 258f),
            _ => expected ? RandF(104f, 158f) : RandF(155f, 220f),
        };
        if (distance > 1350f) ms += RandF(22f, 52f);
        if (turn > 25f) ms += MathF.Min(196f, (turn - 25f) * 1.31f);
        if (DuelSpeedSquared(pawn) > 145f * 145f) ms += 24f;
        if (_smokes.Count > 0 && (SmokeNear(e, SmokeEdge) != null || SmokeNear(o, SmokeEdge) != null
                                  || SegmentThroughSmoke(o, e, SmokeEdge))) ms += RandF(90f, 149f);
        if (pawn.BlindUntilTime > 0f && pawn.BlindUntilTime > now - 0.78f) ms += RandF(68f, 116f);
        if (expected && last != null && r.LastHurtBy == enemy.EntityHandle.Raw && now - r.LastHurtAt < 0.55f)
            ms -= 10f;
        return Math.Clamp(ms, 100f, 620f);
    }

    private void ReleaseDefusalReaction(int slot, ReactionState r)
    {
        if (r.DefusalFireBlock <= 0) return;
        if (_botController != null)
        {
            try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, r.DefusalFireBlock); }
            catch { }
        }
        r.DefusalFireBlock = 0;
        r.HoldUntil = 0f;
    }

    private void ClearDefusalReaction(int slot)
    {
        if (!_reaction.TryGetValue(slot, out var r) || (r.DefusalFireBlock <= 0 && r.AwaitingShotSince < 0f)) return;
        ReleaseDefusalReaction(slot, r);
        _reaction.Remove(slot);
    }

    private void NoteDefusalReactionShot(int slot, float now)
    {
        if (!_reaction.TryGetValue(slot, out var r) || r.AwaitingShotSince < 0f) return;
        float delay = now - r.AwaitingShotSince;
        r.AwaitingShotSince = -1f;
        if (delay < 0f || delay > 2f || (!r.PrevVisible && now - r.LostAt > 0.15f)) return;
        _firstShotMs[_firstShotCount % _firstShotMs.Length] = delay * 1000f;
        _firstShotCount++;
    }

    private float ComputeReactionMs(CCSPlayerPawn pawn, CCSBot bot, CCSPlayerPawn enemy,
                                    ReactionState r, float now, bool sameEnemy, float lostFor)
    {
        var o = pawn.AbsOrigin;
        var e = enemy.AbsOrigin;
        if (o == null || e == null) return RandF(220f, 360f);

        float dx = e.X - o.X, dy = e.Y - o.Y, dz = e.Z - o.Z;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        // Base by distance: close targets are big but surprising, far ones are small.
        float ms = dist < 500f ? RandF(190f, 310f)
                 : dist < 1500f ? RandF(220f, 360f)
                 : RandF(260f, 420f);

        // How far the bot has to turn to face the enemy.
        float yawTo = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        float off = MathF.Abs((float)NormalizeAngleDeg(yawTo - pawn.EyeAngles.Y));
        if (off > 110f) ms += RandF(240f, 380f);
        else if (off > 60f) ms += RandF(140f, 250f);
        else if (off > 25f) ms += RandF(60f, 130f);

        // Bot running needs to stop / settle.
        float bvx = pawn.AbsVelocity.X, bvy = pawn.AbsVelocity.Y;
        if (bvx * bvx + bvy * bvy > 130f * 130f) ms += RandF(30f, 70f);

        // Fast lateral movers are harder to pick up.
        float evx = enemy.AbsVelocity.X, evy = enemy.AbsVelocity.Y;
        if (dist > 1f)
        {
            float lat = MathF.Abs((evx * -dy + evy * dx) / MathF.Sqrt(dx * dx + dy * dy));
            if (lat > 150f) ms += RandF(25f, 70f);
        }

        // Smoke: enemy popping out of / standing at a smoke edge, or the bot leaving one.
        if (_smokes.Count > 0)
        {
            if (SmokeNear(e, SmokeEdge) != null) ms += RandF(150f, 280f);
            else if (SmokeNear(o, SmokeEdge) != null) ms += RandF(110f, 220f);
            else if (SegmentThroughSmoke(o, e, SmokeEdge)) ms += RandF(80f, 160f);
        }

        // Just recovered from a flash.
        if (pawn.BlindUntilTime > now - 1.2f && pawn.BlindUntilTime > 0f) ms += RandF(120f, 220f);

        // Expected contact: same enemy just seen, or the bot was already watching that spot.
        if (sameEnemy && lostFor < 1.5f) ms *= 0.35f;
        else
        {
            var last = bot.LastEnemyPosition;
            if (last != null && now - bot.LastSawEnemyTimestamp < 6f)
            {
                float lx = last.X - e.X, ly = last.Y - e.Y;
                if (lx * lx + ly * ly < 300f * 300f) ms *= 0.65f;
            }
        }

        // Already under fire from this enemy: more alert.
        if (now - r.LastHurtAt < 0.8f) ms -= r.LastHurtBy == enemy.EntityHandle.Raw ? 90f : 40f;

        float min = sameEnemy && lostFor < 1.5f ? 60f : 120f;
        return Math.Clamp(ms, min, 820f);
    }

    private void ResetReactionRoundState()
    {
        foreach (var (slot, r) in _reaction) ReleaseDefusalReaction(slot, r);
        _reaction.Clear();
    }
}
