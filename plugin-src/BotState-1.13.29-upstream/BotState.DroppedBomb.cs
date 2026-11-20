using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

// Bomb on the ground: T bots split into fetch / cover / guard / solo hunt, and when the
// round is lost on numbers some of them save instead. A CT who sees it may camp it.
public partial class BotState
{
    private uint _droppedC4 = uint.MaxValue;   // entity index of the bomb being handled
    private float _droppedAt;
    private bool _ctBombGuardSent;
    private float _nextBombScan;
    private int _bombFetcherSlot = -1, _bombBlockedSlot = -1;
    private uint _bombFetcherPawn;
    private float _bombProgressAt, _bombBestDistance, _bombBlockedUntil, _bombHumanNearSince = -1f;
    private float _bombFightSince = -1f, _nextBombSupport;
    private bool _bombSaving;

    private Vector? FindDroppedC4(out uint idx)
    {
        idx = uint.MaxValue;
        foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CC4>("weapon_c4"))
        {
            if (c4 == null || !c4.IsValid || c4.AbsOrigin == null) continue;
            var owner = c4.OwnerEntity.Value;
            if (owner != null && owner.IsValid) continue;   // carried
            idx = c4.Index;
            return new Vector(c4.AbsOrigin.X, c4.AbsOrigin.Y, c4.AbsOrigin.Z);
        }
        return null;
    }

    private void UpdateDroppedBomb(float now)
    {
        if (now < _nextBombScan) return;
        _nextBombScan = now + 0.5f;
        if (_roundLiveAt < 0f || !TacticsEnabled())
        {
            if (_droppedC4 != uint.MaxValue) ClearDroppedBombRoles();
            _droppedC4 = uint.MaxValue;
            return;
        }

        var pos = FindDroppedC4(out uint idx);
        if (pos == null)
        {
            if (_droppedC4 != uint.MaxValue) EndDroppedBomb();
            return;
        }
        if (idx != _droppedC4)
        {
            if (_droppedC4 != uint.MaxValue) ClearDroppedBombRoles();
            _droppedC4 = idx; _droppedAt = now; _ctBombGuardSent = false;
            ResetBombRecoveryState();
            AssignDroppedBombRoles(pos, now);
        }
        UpdateBombRecovery(pos, now);
        if (IsDefusalCombatEnabled()) CheckVisibleDroppedBomb(pos, now);
        if (!_ctBombGuardSent && now - _droppedAt > 1.5f) TryCtBombGuard(pos, now);
    }

    private void CheckVisibleDroppedBomb(Vector pos, float now)
    {
        int site = SiteOf(pos, 1250f);
        if (site < 0 || now - _lastBombRotationCheck[site] < 3f) return;
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist || !IsAliveBot(player, out var pawn, out _)
                || pawn.AbsOrigin == null || pawn.BlindUntilTime > now) continue;
            var origin = pawn.AbsOrigin;
            float dx = pos.X - origin.X, dy = pos.Y - origin.Y;
            if (dx * dx + dy * dy > 1500f * 1500f) continue;
            float yaw = MathF.Atan2(dy, dx) * 180f / MathF.PI;
            if (MathF.Abs((float)NormalizeAngleDeg(yaw - pawn.EyeAngles.Y)) > 55f
                || SegmentThroughSmoke(origin, pos, SmokeEdge) || !BotCanSee(pawn, new Vector(pos.X, pos.Y, pos.Z + 8f))) continue;
            _c4SeenAt[site] = now;
            _lastBombRotationCheck[site] = now;
            TriggerDefusalPreplantRotate(site, heavy: true);
            return;
        }
    }
    private void AssignDroppedBombRoles(Vector pos, float now)
    {
        var bots = BombRecoveryBots(pos);
        if (bots.Count == 0) return;
        float left = TimeLeft();
        int tAlive = AliveCount(CsTeam.Terrorist), ctAlive = AliveCount(CsTeam.CounterTerrorist);
        bool humanAlive = GetMatchPlayers().Any(p => p.TeamNum == (byte)CsTeam.Terrorist
            && (!p.IsBot || p.HasBeenControlledByPlayerThisRound) && BombPlayerPawn(p) != null);
        bool outnumbered = tAlive <= 2 && ctAlive >= tAlive + 2;
        bool tooLate = left < Math.Max(7f, bots[0].Distance / 220f + 5f);
        if (!humanAlive && bots.All(b => HasPrimary(b.Pawn)) && (tooLate || outnumbered && left < 30f)
            && _random.NextDouble() < (tooLate ? 0.65 : 0.30))
        {
            _bombSaving = true;
            foreach (var bot in bots)
            {
                var save = new RolePlan { Role = "bomb-save", StartAt = now, Sticky = true, Walk = true };
                save.Legs.Add((Jitter(_tSpawn ?? bot.Pawn.AbsOrigin!, 200f), 2, now + 120f));
                _rolePlan[bot.Player.Slot] = save;
            }
            return;
        }
    }

    private static CCSPlayerPawn? BombPlayerPawn(CCSPlayerController player)
    {
        var current = player.Pawn?.Value;
        if (current is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: not null })
            return current.As<CCSPlayerPawn>();
        var pawn = player.PlayerPawn.Value;
        return pawn is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: not null } ? pawn : null;
    }

    private List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, float Distance)> BombRecoveryBots(Vector pos)
    {
        var bots = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, float Distance)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(player, out var pawn, out var bot)) continue;
            bots.Add((player, pawn, bot, RetakeDistance(pawn.AbsOrigin!, pos)));
        }
        return bots.OrderBy(b => b.Distance).ToList();
    }

    private bool HumanNearDroppedBomb(Vector pos)
    {
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.Terrorist || (player.IsBot && !player.HasBeenControlledByPlayerThisRound)) continue;
            var pawn = BombPlayerPawn(player);
            if (pawn?.AbsOrigin == null || MathF.Abs(pawn.AbsOrigin.Z - pos.Z) > 90f) continue;
            if (Dist2D(pawn.AbsOrigin, pos) < 190f) return true;
        }
        return false;
    }

    private bool KnownThreatNearBomb(Vector pos, float now)
    {
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(player, out _, out var bot)
                || now - bot.LastSawEnemyTimestamp > 4f || bot.LastEnemyPosition == null) continue;
            if (RetakeDistance(bot.LastEnemyPosition, pos) < 850f) return true;
        }
        return false;
    }

    private void UpdateBombRecovery(Vector pos, float now)
    {
        if (_botController == null) return;
        if (_bombSaving)
        {
            // A human taking over the recovery cancels the save decision immediately.
            bool humanAlive = GetMatchPlayers().Any(p => p.TeamNum == (byte)CsTeam.Terrorist
                && (!p.IsBot || p.HasBeenControlledByPlayerThisRound)
                && BombPlayerPawn(p) != null);
            if (!humanAlive) return;
            ClearDroppedBombRoles();
            _bombSaving = false;
        }
        bool humanNear = HumanNearDroppedBomb(pos);
        if (humanNear && _bombHumanNearSince < 0f) _bombHumanNearSince = now;
        if (!humanNear) _bombHumanNearSince = -1f;
        bool humanTaking = humanNear && now - _bombHumanNearSince < 2.5f && TimeLeft() > 12f;
        var bots = BombRecoveryBots(pos);
        var fetcher = _bombFetcherSlot >= 0 ? Utilities.GetPlayerFromSlot(_bombFetcherSlot) : null;
        bool hasFetcher = _bombFetcherSlot >= 0 && IsAliveBot(fetcher, out var current, out var currentBot)
            && current.EntityHandle.Raw == _bombFetcherPawn
            && _rolePlan.TryGetValue(_bombFetcherSlot, out var currentPlan) && currentPlan.Role == "bomb-fetch";
        if (hasFetcher)
        {
            var pawn = fetcher!.PlayerPawn.Value!;
            var bot = pawn.Bot!;
            float distance = RetakeDistance(pawn.AbsOrigin!, pos);
            if (distance < _bombBestDistance - 55f)
            {
                _bombBestDistance = distance;
                _bombProgressAt = now;
            }
            bool fighting = bot.IsEnemyVisible || bot.IsAttacking;
            if (fighting)
            {
                if (_bombFightSince < 0f) _bombFightSince = now;
                _bombProgressAt = now;
            }
            else _bombFightSince = -1f;
            bool alternate = bots.Any(b => b.Player.Slot != _bombFetcherSlot
                && (b.Player.Slot != _bombBlockedSlot || now >= _bombBlockedUntil)
                && !b.Bot.IsEnemyVisible && !b.Bot.IsAttacking);
            if ((now - _bombProgressAt > 5f && (distance > 18f || MathF.Abs(pawn.AbsOrigin!.Z - pos.Z) > 45f))
                || (fighting && alternate && now - _bombFightSince > 2.5f)
                || (humanTaking && distance > 300f))
            {
                int oldSlot = _bombFetcherSlot;
                ReleaseMove(oldSlot, MoveOwner.BombPickup);
                if (_rolePlan.TryGetValue(oldSlot, out var old) && old.Role == "bomb-fetch") _rolePlan.Remove(oldSlot);
                _bombBlockedSlot = oldSlot;
                _bombBlockedUntil = now + 6f;
                _bombFetcherSlot = -1;
                _bombFightSince = -1f;
                hasFetcher = false;
            }
            else if (_rolePlan.TryGetValue(_bombFetcherSlot, out var plan) && plan.Legs.Count > 0
                     && RetakeDistance(plan.Legs[0].goal, pos) > 75f)
            {
                plan.Legs[0] = (pos, plan.Legs[0].route, 0f);
                plan.LegIssued = 0f;
                _bombProgressAt = now;
                _bombBestDistance = distance;
            }
            if (hasFetcher && !fighting && distance is > 18f and < 100f && MathF.Abs(pawn.AbsOrigin!.Z - pos.Z) < 70f
                && !DuelFlashActive(pawn, (int)fetcher.Index, now) && !_cachedNearLadder.GetValueOrDefault((int)fetcher.Index))
            {
                float dx = pos.X - pawn.AbsOrigin.X, dy = pos.Y - pawn.AbsOrigin.Y;
                float length = MathF.Sqrt(dx * dx + dy * dy);
                if (length > 1f && IsDirectionClear(pawn, dx / length, dy / length, Math.Min(length, 30f)))
                {
                    float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float forward = (dx * MathF.Cos(yaw) + dy * MathF.Sin(yaw)) / length;
                    float left = (-dx * MathF.Sin(yaw) + dy * MathF.Cos(yaw)) / length;
                    ClearDefusalGuard(fetcher.Slot);
                    SetMove(fetcher.Slot, MoveOwner.BombPickup, forward * 0.8f, left * 0.8f, now + 0.55f);
                }
            }
        }
        else
        {
            if (_bombFetcherSlot >= 0)
            {
                ReleaseMove(_bombFetcherSlot, MoveOwner.BombPickup);
                if (_rolePlan.TryGetValue(_bombFetcherSlot, out var old) && old.Role == "bomb-fetch")
                    _rolePlan.Remove(_bombFetcherSlot);
            }
            _bombFetcherSlot = -1;
        }

        if (_bombFetcherSlot < 0 && !humanTaking)
        {
            var candidate = bots.FirstOrDefault(b => (b.Player.Slot != _bombBlockedSlot || now >= _bombBlockedUntil)
                && !b.Bot.IsEnemyVisible && !b.Bot.IsAttacking && !DefusalObjectiveBusy(b.Pawn)
                && !DuelFlashActive(b.Pawn, (int)b.Player.Index, now)
                && !_cachedNearLadder.GetValueOrDefault((int)b.Player.Index));
            if (candidate.Player != null)
            {
                int slot = candidate.Player.Slot;
                var plan = new RolePlan { Role = "bomb-fetch", StartAt = now, Sticky = true, Arrive = 12f,
                    Hurry = TimeLeft() < 45f ? 7f : 3f };
                plan.Legs.Add((pos, KnownThreatNearBomb(pos, now) && TimeLeft() > 35f ? 2 : 1, 0f));
                _rolePlan[slot] = plan;
                _bombFetcherSlot = slot;
                _bombFetcherPawn = candidate.Pawn.EntityHandle.Raw;
                _bombProgressAt = now;
                _bombBestDistance = candidate.Distance;
                _bombFightSince = -1f;
                ClearDefusalGuard(slot);
                CancelIdlePeek(slot);
                if (BotMoveTo(candidate.Bot, pos, plan.Legs[0].route))
                {
                    plan.LegIssued = plan.BestAt = now;
                    SetHurry(candidate.Bot, plan.Hurry);
                }
            }
        }
        if (now < _nextBombSupport) return;
        _nextBombSupport = now + 2f;
        int desired = bots.Count >= 4 && KnownThreatNearBomb(pos, now) ? 2 : bots.Count >= 2 ? 1 : 0;
        int covered = _rolePlan.Count(kv => kv.Value.Role == "bomb-cover" && IsAliveBot(Utilities.GetPlayerFromSlot(kv.Key), out _, out _));
        foreach (var helper in bots)
        {
            if (covered >= desired) break;
            int slot = helper.Player.Slot;
            if (slot == _bombFetcherSlot || helper.Distance > 1800f || helper.Bot.IsEnemyVisible || helper.Bot.IsAttacking
                || TradeSupportActive(slot, helper.Pawn, now)) continue;
            if (_rolePlan.TryGetValue(slot, out var existing) && existing.Role is "bomb-cover" or "bomb-save") continue;
            var coverPoint = RotationApproach(helper.Pawn, pos, covered, 220f)
                ?? Lerp(pos, helper.Pawn.AbsOrigin!, Math.Clamp(220f / Math.Max(helper.Distance, 1f), 0.1f, 0.9f));
            var cover = new RolePlan { Role = "bomb-cover", StartAt = now, Arrive = 120f, Hurry = TimeLeft() < 30f ? 5f : 0f };
            cover.Legs.Add((coverPoint, 2, now + 10f));
            _rolePlan[slot] = cover;
            covered++;
        }
    }

    // A CT that has eyes on the dropped bomb may camp it from a distance.
    private void TryCtBombGuard(Vector pos, float now)
    {
        _ctBombGuardSent = true;
        if (_random.NextDouble() > 0.4 || AliveCount(CsTeam.CounterTerrorist) < 3) return;
        var eye = new Vector(pos.X, pos.Y, pos.Z + 8f);
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != (byte)CsTeam.CounterTerrorist || !IsAliveBot(p, out var pawn, out var bot)) continue;
            if (bot.IsAttacking || _rolePlan.ContainsKey(p.Slot) || pawn.BlindUntilTime > now) continue;
            float d = Dist2D(pawn.AbsOrigin!, pos);
            float yaw = MathF.Atan2(pos.Y - pawn.AbsOrigin!.Y, pos.X - pawn.AbsOrigin.X) * 180f / MathF.PI;
            if (d > 1800f || MathF.Abs((float)NormalizeAngleDeg(yaw - pawn.EyeAngles.Y)) > 55f
                || SegmentThroughSmoke(pawn.AbsOrigin, pos, SmokeEdge) || !BotCanSee(pawn, eye)) continue;
            // Hold at a distance on the CT side of the bomb.
            var spot = Lerp(pos, pawn.AbsOrigin!, Math.Clamp(500f / Math.Max(d, 1f), 0.2f, 0.9f));
            var plan = new RolePlan { Role = "ct-bombcamp", StartAt = now + RandF(0.3f, 1f), Arrive = 150f };
            plan.Legs.Add((Jitter(spot, 60f), 2, now + RandF(10f, 20f)));
            _rolePlan[p.Slot] = plan;
            if (_debugTactics) BroadcastDebug($"[Smarter-Bot/C4] {p.PlayerName} camps dropped bomb");
            return;
        }
    }

    private void ClearDroppedBombRoles()
    {
        foreach (var slot in _rolePlan.Where(kv => kv.Value.Role is "bomb-fetch" or "bomb-cover"
                     or "bomb-guard" or "bomb-save" or "ct-bombcamp").Select(kv => kv.Key).ToList())
            _rolePlan.Remove(slot);
        if (_bombFetcherSlot >= 0) ReleaseMove(_bombFetcherSlot, MoveOwner.BombPickup);
    }

    private void ResetBombRecoveryState()
    {
        _bombFetcherSlot = _bombBlockedSlot = -1;
        _bombFetcherPawn = 0;
        _bombProgressAt = _bombBestDistance = _bombBlockedUntil = _nextBombSupport = 0f;
        _bombHumanNearSince = _bombFightSince = -1f;
        _bombSaving = false;
    }

    private void EndDroppedBomb()
    {
        _droppedC4 = uint.MaxValue;
        ClearDroppedBombRoles();
        ResetBombRecoveryState();
        if (!TacticsEnabled()) return;
        var carrier = GetMatchPlayers().FirstOrDefault(player => player.TeamNum == (byte)CsTeam.Terrorist
            && BombPlayerPawn(player) is { } pawn && HasC4(pawn));
        var position = carrier == null ? null : BombPlayerPawn(carrier)?.AbsOrigin;
        if (position == null) return;
        float now = Server.CurrentTime;
        int nearSite = SiteOf(position, 1000f);
        int humanSite = HumanCarrierPreferredSite();
        int site = humanSite >= 0 ? humanSite : nearSite >= 0 ? nearSite
            : _tExecSite >= 0 ? _tExecSite : ChooseExecSite(TimeLeft() < 40f);
        _fakeReleaseAt = -1f;
        _tProbeDecisionAt = -1f;
        _ecoStealCarrier = -1;
        _groups.Remove(TExecGroup);
        StartTExecute(site, "bomb recovered", gather: false, hurry: TimeLeft() < 35f ? 8f : 5f);
    }

    private void ResetDroppedBombRoundState()
    {
        _droppedC4 = uint.MaxValue;
        _ctBombGuardSent = false;
        _nextBombScan = 0f;
        ResetBombRecoveryState();
    }
}
