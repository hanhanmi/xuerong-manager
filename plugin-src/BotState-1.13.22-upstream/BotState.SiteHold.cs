using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private enum SiteGuardPattern { AllHold, Patrol, CrossAngles }

    private const float SiteHoldAcquireRadius = 650f;
    private const float SiteHoldLeash = 285f;
    private const float SiteHoldBoundary = 1040f;

    private sealed class SiteHold
    {
        public uint Pawn;
        public int Site;
        public bool Returning;
        public Vector Anchor = null!;
        public float NextReturn, NextLook, Yaw, LookIssued = -1f;
        public SiteGuardPattern Pattern;
        public float NextShift, BestReturn = float.MaxValue;
        public int ReturnAttempts;
    }

    private readonly Dictionary<int, SiteHold> _siteHolds = new();
    private readonly Dictionary<int, float> _siteHoldRetry = new();
    private readonly Dictionary<int, (uint Pawn, int Site, float Since)> _siteHoldEntries = new();
    private readonly SiteGuardPattern[] _siteGuardPatterns = new SiteGuardPattern[2];
    private readonly float[] _sitePatternNext = new float[2];
    private int _siteHoldArrivals, _siteHoldReturns, _siteHoldShifts, _siteHoldPatrols;

    private bool TrySiteHold(int slot, CCSPlayerPawn pawn, out SiteHold hold)
    {
        if (_siteHolds.TryGetValue(slot, out hold!) && hold.Pawn == pawn.EntityHandle.Raw
            && !_defusalBombPlanted
            && (!_rolePlan.TryGetValue(slot, out var active) || active.Leg >= active.Legs.Count)) return true;
        DropSiteHold(slot);
        return false;
    }

    private void ResetSiteHoldRoundState()
    {
        ClearSiteHoldAssignments();
        double roll = _random.NextDouble();
        var pattern = roll < 0.40 ? SiteGuardPattern.AllHold
            : roll < 0.73 ? SiteGuardPattern.Patrol : SiteGuardPattern.CrossAngles;
        _siteGuardPatterns[0] = _siteGuardPatterns[1] = pattern;
        if (pattern != SiteGuardPattern.AllHold && _random.NextDouble() < 0.35)
            _siteGuardPatterns[_random.Next(2)] = SiteGuardPattern.AllHold;
    }

    private void ClearSiteHoldAssignments()
    {
        foreach (int slot in _siteHolds.Keys.ToArray()) DropSiteHold(slot);
        _siteHoldRetry.Clear();
        _siteHoldEntries.Clear();
        Array.Clear(_sitePatternNext);
    }

    private void DropSiteHold(int slot)
    {
        if (!_siteHolds.ContainsKey(slot)) return;
        ClearDefusalGuard(slot);
        _siteHolds.Remove(slot);
    }

    private void ClearSiteHoldLook(int slot)
    {
        if (!_siteHolds.TryGetValue(slot, out var hold) || hold.LookIssued < 0f) return;
        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player
            && player.PlayerPawn.Value is { IsValid: true } pawn && pawn.EntityHandle.Raw == hold.Pawn
            && pawn.Bot is { } bot && bot.LookAtSpotTimestamp == hold.LookIssued)
            bot.LookAtSpotDuration = 0f;
        hold.LookIssued = -1f;
        hold.NextLook = 0f;
    }
    private bool SiteHoldActive(int slot, CCSPlayerPawn pawn)
        => TrySiteHold(slot, pawn, out _);

    private static bool IsCtSiteDelivery(RolePlan plan) => plan.Site >= 0 && (plan.Role is
        "ct-formation" or "ct-reposition" or "ct-rotate" or "ct-gather" or "ct-flank"
        or "ct-gamble-expand" or "ct-stack" or "ct-crossfire" or "ct-mid-angle"
        || plan.Role.StartsWith("ct-fill", StringComparison.Ordinal));

    private void BeginSiteHold(int slot, CCSPlayerPawn pawn, int site, float now)
    {
        if (_defusalBombPlanted || pawn.TeamNum != (byte)CsTeam.CounterTerrorist
            || site is < 0 or > 1 || pawn.AbsOrigin is not { } pos || !DuelGrounded(pawn)
            || SiteOf(pos, SiteHoldBoundary) != site) return;
        DropSiteHold(slot);
        var hold = new SiteHold
        {
            Pawn = pawn.EntityHandle.Raw, Site = site,
            Anchor = new Vector(pos.X, pos.Y, pos.Z), Yaw = pawn.EyeAngles.Y,
            Pattern = _siteGuardPatterns[site],
            NextReturn = now, NextLook = now, NextShift = now + RandF(3.2f, 7.2f),
        };
        PickSiteAngle(slot, pawn, hold, change: false);
        _siteHolds[slot] = hold;
        _ctHomeSite[slot] = site;
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        ClearStationaryGuardWatch(slot);
        ResetDefusalRecoveryForSlot(slot);
        ReleaseMove(slot, MoveOwner.Unstuck);
        _siteHoldRetry.Remove(slot);
        _siteHoldEntries.Remove(slot);
        _siteHoldArrivals++;
    }

    private SiteHold? ResolveSiteHold(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, Vector pos, bool routed, float now)
    {
        int slot = player.Slot;
        if (TrySiteHold(slot, pawn, out var held)) return held;
        if (player.TeamNum != (byte)CsTeam.CounterTerrorist || _defusalBombPlanted || routed
            || !_rolesAssigned || now - _roundLiveAt < 2f || !DuelGrounded(pawn)
            || now < _siteHoldRetry.GetValueOrDefault(slot) || RetakeBusy(player, pawn, bot, now)
            || _rolePlan.ContainsKey(slot) || TradeSupportActive(slot, pawn, now))
        {
            _siteHoldEntries.Remove(slot);
            return null;
        }

        int site = SiteOf(pos, SiteHoldAcquireRadius);
        // Leave deliberate gamble and rotation assignments alone.
        if (site < 0 || (_ctGambleSite >= 0 && site != _ctGambleSite)
            || (_ctScreenSlot == slot && now < _ctResponseUntil)
            || (_rotationSite >= 0 && site != _rotationSite && _rotationConfirmed[_rotationSite]
                && now - _rotationConfirmedAt < 7f))
        {
            _siteHoldEntries.Remove(slot);
            return null;
        }
        uint identity = pawn.EntityHandle.Raw;
        if (!_siteHoldEntries.TryGetValue(slot, out var entry) || entry.Pawn != identity || entry.Site != site)
        {
            _siteHoldEntries[slot] = (identity, site, now);
            return null;
        }
        if (now - entry.Since < 0.24f) return null;
        BeginSiteHold(slot, pawn, site, now);
        return _siteHolds.GetValueOrDefault(slot);
    }

    private bool ReturnToSiteHold(int slot, CCSBot bot, SiteHold hold, Vector pos, float now)
    {
        float distance = RetakeDistance(pos, hold.Anchor);
        if (distance <= (hold.Returning ? 130f : SiteHoldLeash)
            && SiteOf(pos, SiteHoldBoundary) == hold.Site)
        {
            if (hold.Returning) ResetDefusalRecoveryForSlot(slot);
            hold.Returning = false;
            hold.ReturnAttempts = 0;
            hold.BestReturn = float.MaxValue;
            return false;
        }
        if (!hold.Returning) ClearDefusalGuard(slot);
        hold.Returning = true;
        if (distance + 55f < hold.BestReturn)
        {
            hold.BestReturn = distance;
            hold.ReturnAttempts = 0;
        }
        if (now < hold.NextReturn) return true;
        if (hold.ReturnAttempts >= 3)
        {
            DropSiteHold(slot);
            _siteHoldRetry[slot] = now + 4f;
            return true;
        }
        hold.NextReturn = now + 2f;
        hold.ReturnAttempts++;
        if (BotMoveTo(bot, hold.Anchor, 2)) _siteHoldReturns++;
        return true;
    }

    private bool GuardOwnsLocalMovement(int slot, CCSPlayerPawn pawn, float now)
        => _siteHolds.TryGetValue(slot, out var hold) && hold.Pawn == pawn.EntityHandle.Raw
            && !_defusalBombPlanted && !hold.Returning && pawn.AbsOrigin is { } pos
            && RetakeDistance(pos, hold.Anchor) <= SiteHoldLeash
            && SiteOf(pos, SiteHoldBoundary) == hold.Site && !_smokeHold.ContainsKey(slot)
            && !TradeSupportActive(slot, pawn, now)
            && (!_rolePlan.TryGetValue(slot, out var plan) || plan.Leg >= plan.Legs.Count
                || (_defusalBombPlanted && plan.Role != "eco-postplant"));

    private int CtInboundSite(int slot) => _rolePlan.TryGetValue(slot, out var plan)
        && plan.Leg < plan.Legs.Count && IsCtSiteDelivery(plan) ? plan.Site : -1;

    private int CtCoverageSite(CCSPlayerController player, CCSPlayerPawn pawn, float radius = 900f)
    {
        if (IsAliveBot(player, out _, out _))
        {
            if (_rolePlan.TryGetValue(player.Slot, out var plan) && plan.Leg < plan.Legs.Count)
            {
                if (IsCtSiteDelivery(plan)) return -1;
                if (plan.Role is "ct-screen" or "ct-reserve" or "ct-scout") return -1;
            }
            if (TrySiteHold(player.Slot, pawn, out var hold))
                return !hold.Returning && pawn.AbsOrigin is { } physical && RetakeDistance(physical, hold.Anchor) <= SiteHoldLeash
                    && SiteOf(physical, SiteHoldBoundary) == hold.Site ? hold.Site : -1;
        }
        return pawn.AbsOrigin is { } pos ? SiteOf(pos, radius) : -1;
    }

    private bool SiteShiftHasCover(int slot, SiteHold hold, float now)
    {
        foreach (var entry in _siteHolds)
        {
            int other = entry.Key;
            if (other == slot || entry.Value.Site != hold.Site) continue;
            var player = Utilities.GetPlayerFromSlot(other);
            if (!IsAliveBot(player, out var pawn, out var bot) || !GuardOwnsLocalMovement(other, pawn, now)
                || RetakeBusy(player!, pawn, bot, now) || IsNonGunWeapon(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName)
                || (_defusalGuards.TryGetValue(other, out var guard) && guard.Phase == GuardPhase.Roam)) continue;
            return true;
        }
        return false;
    }

    private bool SiteGuardPathClear(CCSPlayerPawn pawn, SiteHold hold, Vector pos, float dx, float dy, float distance)
    {
        var goal = new Vector(pos.X + dx * distance, pos.Y + dy * distance, pos.Z);
        if (RetakeDistance(goal, hold.Anchor) > SiteHoldLeash - 12f || SiteOf(goal, SiteHoldBoundary) != hold.Site) return false;
        if (_tSpawn != null && Dist2D(goal, _tSpawn) + 112f < Dist2D(hold.Anchor, _tSpawn)) return false;
        try
        {
            return SiteGuardGround(pawn, goal.X, goal.Y, pos.Z)
                && (distance < 65f || SiteGuardGround(pawn, pos.X + dx * distance * 0.5f, pos.Y + dy * distance * 0.5f, pos.Z));
        }
        catch { return false; }
    }

    private static bool SiteGuardGround(CCSPlayerPawn pawn, float x, float y, float z)
    {
        var hit = Trace.TraceEndShape(new Vector(x, y, z + 23f), new Vector(x, y, z - 36f), pawn, RetakeGeometry);
        return !hit.IsAllSolid && hit.Fraction is > 0f and < 0.92f && hit.Normal.Z >= 0.56f;
    }

    private void PickSiteAngle(int slot, CCSPlayerPawn pawn, SiteHold hold, bool change)
    {
        float old = hold.Yaw;
        float best = old, bestScore = float.NegativeInfinity;
        Span<float> offsets = stackalloc float[] { 0f, -35f, 35f, -69f, 69f, 105f, -105f };
        foreach (float offset in offsets)
        {
            float yaw = old + offset;
            float rad = yaw * MathF.PI / 180f;
            float sight = SightDistance(pawn, MathF.Cos(rad), MathF.Sin(rad), 0f, 0f, 0f, 980f);
            if (sight < 110f) continue;
            float score = MathF.Min(730f, sight) * 0.35f + RandF(0f, 40f) + (change == (offset != 0f) ? 70f : 0f);
            foreach (var other in _siteHolds)
                if (other.Key != slot && other.Value.Site == hold.Site && !other.Value.Returning
                    && MathF.Abs((float)NormalizeAngleDeg(yaw - other.Value.Yaw)) < 28f) score -= 130f;
            if (score > bestScore) { bestScore = score; best = yaw; }
        }
        hold.Yaw = best;
        hold.NextLook = 0f;
    }

    private string SiteHoldStatus()
    {
        int a = _siteHolds.Values.Count(h => h.Site == 0 && !h.Returning);
        int b = _siteHolds.Values.Count(h => h.Site == 1 && !h.Returning);
        return $"[Smarter-Bot] siteGuard A={a}/{_siteGuardPatterns[0]} B={b}/{_siteGuardPatterns[1]} arrivals={_siteHoldArrivals} patrols={_siteHoldPatrols} shifts={_siteHoldShifts} returns={_siteHoldReturns}";
    }
    private void WatchSiteAngle(CCSBot bot, Vector pos, SiteHold hold, float now)
    {
        if (now < hold.NextLook) return;
        hold.NextLook = now + RandF(0.28f, 0.40f);
        if ((bot.NoiseTimestamp > 0f && now - bot.NoiseTimestamp < 0.65f) || bot.LookAtSpotTimestamp != hold.LookIssued
            && bot.LookAtSpotTimestamp > 0f && now - bot.LookAtSpotTimestamp < Math.Min(0.55f, bot.LookAtSpotDuration)) return;
        // Use the bot's normal look controller so combat can take over the view.
        float yaw = hold.Yaw * MathF.PI / 180f;
        var spot = bot.LookAtSpot;
        spot.X = pos.X + MathF.Cos(yaw) * 550f;
        spot.Y = pos.Y + MathF.Sin(yaw) * 550f;
        spot.Z = pos.Z + 62f;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.LookAtSpotTimestamp = hold.LookIssued = now;
        bot.LookAtSpotDuration = 0.58f;
        bot.LookAtSpotAngleTolerance = 6f;
        bot.LookAtSpotClearIfClose = false;
        bot.LookAtSpotAttack = false;
    }
}
