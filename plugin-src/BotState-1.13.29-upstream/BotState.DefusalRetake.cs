using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private sealed class RetakeRoute
    {
        public uint Pawn;
        public Vector Goal = null!;
        public bool Support, Fallback, Paused, Done, Arrived;
        public int Attempts;
        public float At, LastOrder, BestDist = float.MaxValue, ProgressAt;
    }

    private readonly Dictionary<int, RetakeRoute> _retakeRoutes = new();
    private readonly bool[] _rotationConfirmed = new bool[2];
    private readonly HashSet<int> _ctLossVictims = new();
    private readonly float[] _ctLastLossAt = { -99f, -99f };
    private static readonly TraceOptions RetakeGeometry = new() { InteractsWith = Masks.PlayerSolidBrushOnly };
    private uint _retakeBomb;
    private int _retakeLead = -1, _retakeSite = -1, _rotationSite = -1, _rotationOrders, _retakeOrders, _retakeRetries;
    private int _casualtyRolls, _casualtyOrders, _rotationArrivals;
    private int _ctResponseMode, _ctResponseSite = -1, _ctScreenSlot = -1;
    private float _ctResponseUntil;
    private float _nextRetakeScan, _rotationConfirmedAt = -99f, _defuseRequestAt;
    private sealed class LossSupport
    {
        public float Until, Next;
        public int Wanted;
        public bool Human;
        public string LastDecision = "";
        public readonly HashSet<int> Sent = new();
    }

    private readonly LossSupport?[] _lossSupport = new LossSupport?[2];
    private int _humanLossSupportOrders, _lossSupportFilled, _lossSupportExpired;

    private void RequestLossSupport(int site, int victimSlot, float now, bool human)
    {
        if (site is < 0 or > 1 || !IsDefusalCombatEnabled() || _defusalBombPlanted || _botController == null
            || !_moveToReady || _siteA == null || _siteB == null || !_ctLossVictims.Add(victimSlot)) return;

        bool repeated = now - _ctLastLossAt[site] <= 8f;
        _ctLastLossAt[site] = now;
        bool bombSeen = now - _c4SeenAt[site] < 8f;
        if (bombSeen) TriggerDefusalPreplantRotate(site, true);
        if (!human && !bombSeen)
        {
            _casualtyRolls++;
            if (_random.NextDouble() >= (repeated ? 0.85 : 0.60))
            {
                LogLossSupport(site, "hold-roll");
                return;
            }
        }

        var request = _lossSupport[site];
        if (request == null || now >= request.Until) _lossSupport[site] = request = new LossSupport();
        request.Human |= human;
        request.Until = now + (request.Human ? 11f : 9f);
        request.LastDecision = "";
        int local = GetMatchPlayers().Where(p => p.Slot != victimSlot && p.TeamNum == (byte)CsTeam.CounterTerrorist)
            .Select(p => p.PlayerPawn.Value).Where(p => p is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                && SiteOf(pos, SiteHoldBoundary) == site).Select(p => p!.EntityHandle.Raw).Distinct().Count();
        request.Wanted = Math.Max(request.Wanted, Math.Min(2, local + 1));
        LogLossSupport(site, $"queued human={human} local={local} wanted={request.Wanted}");
        TryLossSupport(site, request, now);
    }

    private void UpdateLossSupport(float now)
    {
        if (!IsDefusalCombatEnabled() || _defusalBombPlanted || _botController == null || !_moveToReady) return;
        for (int site = 0; site < 2; site++)
        {
            if (_lossSupport[site] is not { } request || now < request.Next) continue;
            if (now >= request.Until)
            {
                _lossSupport[site] = null;
                _lossSupportExpired++;
                LogLossSupport(site, $"expired last={request.LastDecision}");
                continue;
            }
            TryLossSupport(site, request, now);
        }
    }

    private void TryLossSupport(int site, LossSupport request, float now)
    {
        request.Next = now + 0.4f;
        int other = 1 - site;
        if (_rotationSite == other && _rotationConfirmed[other] && now - _rotationConfirmedAt < 5f
            && now - _c4SeenAt[site] >= 8f)
        {
            LossDecision(site, request, "confirmed-opposite");
            return;
        }
        // Preserve deliberate stacks for bot losses while they still have no strong contact.
        if (!request.Human && _ctGambleSite == other && now - _c4SeenAt[site] >= 8f)
        {
            LossDecision(site, request, "stack-holds");
            return;
        }

        int local = 0, donor = 0, enroute = 0;
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, int At, float Cost)>();
        var seen = new HashSet<uint>();
        var center = SiteCenter(site);
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !seen.Add(pawn.EntityHandle.Raw)) continue;
            int slot = player.Slot, at = SiteOf(pos, SiteHoldBoundary);
            bool botAvailable = IsAliveBot(player, out _, out var bot);
            bool inTransit = botAvailable && HasCtTravel(slot, now, out _);
            int destination = inTransit ? _rolePlan[slot].Site : -1;
            if (at == site && (!inTransit || destination == site)) { local++; continue; }
            if (at == other && (!inTransit || destination == other)) donor++;
            if (inTransit && destination == site) { enroute++; continue; }
            if (!botAvailable || (inTransit && destination != other) || RetakeBusy(player, pawn, bot, now)
                || TradeSupportActive(slot, pawn, now) || BotControllerBridge.IsViewControlled(_botController!, slot)) continue;
            if (_rolePlan.TryGetValue(slot, out var existing) && existing.Role is "ct-bombcamp" or "ct-screen") continue;
            if (request.Sent.Contains(slot)) continue;
            float cost = RetakeDistance(pos, center) + (at == other ? 450f : 0f);
            candidates.Add((player, pawn, bot, at, cost));
        }
        if (local >= request.Wanted)
        {
            _lossSupport[site] = null;
            _lossSupportFilled++;
            LogLossSupport(site, $"filled local={local}");
            return;
        }
        if (enroute > 0) { LossDecision(site, request, "in-transit"); return; }
        var helper = candidates.Where(c => c.At != other || donor > 1 || (request.Human && local == 0))
            .OrderBy(c => c.Cost).FirstOrDefault();
        if (helper.Player == null)
        {
            LossDecision(site, request, candidates.Count > 0 ? "preserve-anchor" : "no-eligible-helper");
            return;
        }
        QueueDefusalRotation(helper.Player, helper.Pawn, site, 0, now, confirmed: false);
        var plan = _rolePlan[helper.Player.Slot];
        plan.StartAt = now;
        if (BotMoveTo(helper.Bot, plan.Legs[0].goal, plan.Legs[0].route))
        {
            plan.LegIssued = plan.BestAt = now;
            SetHurry(helper.Bot, 5f);
        }
        request.Sent.Add(helper.Player.Slot);
        if (request.Human) _humanLossSupportOrders++;
        _casualtyOrders++;
        _rotationOrders++;
        LogLossSupport(site, $"sent bot={helper.Player.PlayerName} local={local} donor={donor}");
    }

    private bool HasCtTravel(int slot, float now, out RolePlan? plan)
    {
        if (!_rolePlan.TryGetValue(slot, out plan) || !IsCtSiteDelivery(plan) || plan.Leg >= plan.Legs.Count) return false;
        if (now - plan.StartAt < 2.8f) return true;
        return plan.LegIssued > 0f && plan.PausedAt < 0f && now - plan.TravelAt < 2.8f
            && now - Math.Max(plan.StartAt, plan.TowardAt) < 5f;
    }

    private void LossDecision(int site, LossSupport request, string decision)
    {
        if (request.LastDecision == decision) return;
        request.LastDecision = decision;
        LogLossSupport(site, decision);
    }

    private void LogLossSupport(int site, string message)
    {
        if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Loss] {RetakeSiteLabel(site)} {message}");
    }

    private void TriggerDefusalPreplantRotate(int site, bool heavy)
    {
        float now = Server.CurrentTime;
        int contacts = _siteContacts.Count(kv => kv.Key / 128 == site && now - kv.Value <= 5f);
        bool bombSeen = now - _c4SeenAt[site] < 8f;
        bool confirmed = bombSeen || contacts >= 3 || (heavy && contacts >= 2);
        bool escalated = confirmed && (!_rotationConfirmed[site] || _rotationSite != site);
        if (!escalated && now < _siteTriggerCooldown[site]) return;
        if (!bombSeen && _rotationSite == 1 - site && now - _rotationConfirmedAt < 5f) return;

        var center = SiteCenter(site);
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, float Distance, int At)>();
        int other = 1 - site, alive = 0, otherAnchors = 0, local = 0, enroute = 0;
        var countedPawns = new HashSet<uint>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !countedPawns.Add(pawn.EntityHandle.Raw)) continue;
            alive++;
            int at = SiteOf(pos);
            if (at == site)
            {
                local++;
                if (confirmed && IsAliveBot(player, out _, out var nearbyBot)
                    && _rolePlan.TryGetValue(player.Slot, out var nearbyPlan) && nearbyPlan.Site >= 0
                    && nearbyPlan.Site != site && !RetakeBusy(player, pawn, nearbyBot, now))
                    QueueDefusalRotation(player, pawn, site, 0, now, true);
                continue;
            }
            bool rotating = HasCtTravel(player.Slot, now, out var travel);
            int destination = rotating ? travel!.Site : -1;
            if (rotating && destination == site) { enroute++; continue; }
            if (at == other) otherAnchors++;
            if (!IsAliveBot(player, out _, out var bot)) continue;
            if (confirmed && !bot.IsEnemyVisible && _smokeHold.TryGetValue(player.Slot, out var smokeHold))
                EndSmokeHold(player.Slot, smokeHold);
            if (RetakeBusy(player, pawn, bot, now)) continue;
            if (rotating && !confirmed) continue;
            if (!confirmed && _rolePlan.TryGetValue(player.Slot, out var old) && old.Role == "ct-bombcamp") continue;
            candidates.Add((player, pawn, Dist2D(pos, center), at));
        }

        if (confirmed)
        {
            _ctGambleSite = -1;
            _ctFormationApplied = true;
            _rotationConfirmed[site] = true;
            _rotationConfirmed[other] = false;
            _rotationConfirmedAt = now;
            _rotationSite = site;
        }
        // A fresh carrier report is strong, but a lone carrier can still change sites.
        // Keep one anchor only while enough CTs remain and the attack is not yet concentrated.
        int attackersAlive = AliveCount(CsTeam.Terrorist);
        bool recentLoss = now - _ctLastLossAt[site] <= 10f;
        bool decisive = bombSeen && (contacts >= 2 || recentLoss || attackersAlive <= 2
            || alive <= 3 || TimeLeft() < 25f);
        if (confirmed && (_ctResponseSite != site || now >= _ctResponseUntil))
        {
            _ctResponseSite = site;
            _ctResponseUntil = now + 12f;
            _ctScreenSlot = -1;
            double response = _random.NextDouble();
            _ctResponseMode = alive <= 2 || TimeLeft() < 25f ? 0 : response < 0.50 ? 0 : response < 0.78 ? 1 : 2;
        }
        if (alive <= 2 || TimeLeft() < 25f) _ctResponseMode = 0;
        int anchors = !confirmed ? 1 : !decisive || _ctResponseMode == 1 ? 1 : 0;
        int screen = 0;
        if (confirmed && decisive && _ctResponseMode == 2 && alive >= 4)
        {
            var existing = Utilities.GetPlayerFromSlot(_ctScreenSlot);
            if (_ctScreenSlot >= 0 && IsAliveBot(existing, out _, out _) && _rolePlan.TryGetValue(_ctScreenSlot, out var oldScreen)
                && oldScreen.Role == "ct-screen")
            {
                screen = 1;
                candidates.RemoveAll(c => c.Player.Slot == _ctScreenSlot);
            }
            else
            {
                var midpoint = Lerp(SiteCenter(0), SiteCenter(1), 0.5f);
                var pick = candidates.OrderBy(c => RetakeDistance(c.Pawn.AbsOrigin!, midpoint)).FirstOrDefault();
                if (pick.Player != null)
                {
                    var plan = new RolePlan { Role = "ct-screen", Arrive = 250f, StartAt = now, Hurry = 4f };
                    plan.Legs.Add((Jitter(midpoint, 80f), 1, 0f));
                    _rolePlan[pick.Player.Slot] = plan;
                    _ctScreenSlot = pick.Player.Slot;
                    _rotateUntil[pick.Player.Slot] = now + 12f;
                    _rotateSite[pick.Player.Slot] = -1;
                    if (pick.At == other) otherAnchors--;
                    candidates.RemoveAll(c => c.Player.Slot == pick.Player.Slot);
                    screen = 1;
                }
            }
        }
        int needed = confirmed ? Math.Max(0, alive - local - enroute - screen - Math.Min(anchors, otherAnchors)) : (enroute == 0 ? 1 : 0);
        int sent = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Distance + (c.At == other ? 300f : 0f)))
        {
            if (sent >= needed) break;
            if (candidate.At == other && otherAnchors <= anchors) continue;
            QueueDefusalRotation(candidate.Player, candidate.Pawn, site, sent, now, confirmed);
            if (candidate.At == other) otherAnchors--;
            sent++;
        }
        _siteTriggerCooldown[site] = now + (confirmed ? 2f : 3f);
        _rotationOrders += sent;
        if (_debugTactics && (sent > 0 || escalated))
            BroadcastDebug($"[Smarter-Bot/Rotate] {(site == 0 ? 'A' : 'B')} {(confirmed ? "confirmed" : "support")} x{sent}");
    }

    private void QueueDefusalRotation(CCSPlayerController player, CCSPlayerPawn pawn, int site, int lane, float now, bool confirmed)
    {
        int slot = player.Slot;
        if (confirmed) _tradeSupport.Remove(slot);
        var center = SiteCenter(site);
        var target = RotationApproach(pawn, center, lane, 240f);
        var plan = new RolePlan { Role = "ct-rotate", Site = site, Hurry = 5f, Arrive = 210f,
            StartAt = now + (confirmed ? 0f : 0.15f) + Math.Min(lane, 3) * 0.12f };
        plan.Legs.Add((target ?? center, 1, 0f));
        DropSiteHold(slot);
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        _rolePlan[slot] = plan;
        _rotateUntil[slot] = now + (confirmed ? 22f : 13f);
        _rotateSite[slot] = site;
        _rotateIssued[slot] = now;
        // The role owns retries; prevent the independent old repath after it expires.
        _rotateReissues[slot] = 2;
    }

    private bool RetakeBusy(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (DefusalObjectiveBusy(pawn) || DuelFlashActive(pawn, (int)player.Index, now)
            || _cachedNearLadder.GetValueOrDefault((int)player.Index) || !DuelGrounded(pawn)
            || _doorEventCooldown.GetValueOrDefault((int)player.Index) > now
            || _smokeHold.ContainsKey(player.Slot) || ActiveMoveOwner(player.Slot, now) > MoveOwner.Peek) return true;
        var enemy = bot.Enemy.Value;
        return (bot.IsEnemyVisible || (bot.IsAttacking && now - bot.LastSawEnemyTimestamp < 0.65f))
            && enemy is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE }
            && enemy.TeamNum != player.TeamNum;
    }

    private Vector? RotationApproach(CCSPlayerPawn pawn, Vector target, int lane, float distance)
    {
        var pos = pawn.AbsOrigin!;
        float dx = pos.X - target.X, dy = pos.Y - target.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < distance + 120f || !float.IsFinite(length)) return null;
        dx /= length; dy /= length;
        float side = lane % 2 == 0 ? 1f : -1f;
        for (int attempt = 0; attempt < 2; attempt++, side = -side)
        {
            float fwd = distance * (lane % 3 == 0 ? 1f : 1.2f);
            float x = target.X + fwd * dx - side * 150f * dy;
            float y = target.Y + fwd * dy + side * 150f * dx;
            try
            {
                var a = new Vector(x, y, target.Z + 48f);
                var b = new Vector(x, y, target.Z - 96f);
                var down = Trace.TraceEndShape(a, b, pawn, RetakeGeometry);
                if (down.IsAllSolid || down.Fraction <= 0f || down.Fraction >= 1f || down.Normal.Z < 0.60f) continue;
                float z = a.Z + (b.Z - a.Z) * down.Fraction;
                a.Z = z + 6f; b.Z = z + 42f;
                var clear = Trace.TraceHullShape(a, b, new Vector(-15f, -15f, 0f), new Vector(15f, 15f, 24f), pawn, RetakeGeometry);
                if (!clear.IsAllSolid && clear.Fraction > 0.99f) return new Vector(x, y, z);
            }
            catch { return null; }
        }
        return null;
    }

    private bool HasDefusalRetakeRoute(int slot)
        => _defusalBombPlanted && _retakeRoutes.TryGetValue(slot, out var r) && !r.Done;

    private void UpdateDefusalRetake(float now)
    {
        if (!_moveToReady || !IsDefusalCombatEnabled() || !_defusalBombPlanted)
        {
            if (_retakeBomb != 0) ClearRetakeRoutes();
            return;
        }
        if (now < _nextRetakeScan) return;
        _nextRetakeScan = now + 0.32f;

        var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(b => b is { IsValid: true, BombTicking: true, BombDefused: false, CannotBeDefused: false, AbsOrigin: not null });
        var target = bomb?.AbsOrigin;
        if (bomb == null || target == null) { ClearRetakeRoutes(); return; }
        float left = bomb.C4Blow - now;
        if (!float.IsFinite(left) || left <= 0f) { ClearRetakeRoutes(); return; }

        bool first = _retakeBomb != bomb.EntityHandle.Raw;
        if (first)
        {
            ClearRetakeRoutes();
            _retakeBomb = bomb.EntityHandle.Raw;
            ResolveSites();
            _retakeSite = SiteOf(target, float.MaxValue);
            if (_debugTactics) BroadcastDebug($"[Smarter-Bot/Retake] actual C4 {RetakeSiteLabel(_retakeSite)} {left:F0}s");
        }

        var ct = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, float Dist, bool Kit)>();
        foreach (var p in GetMatchPlayers())
        {
            if (p.TeamNum != (byte)CsTeam.CounterTerrorist || !IsAliveBot(p, out var pawn, out var bot)) continue;
            int slot = p.Slot;
            if (first)
            {
                _rolePlan.Remove(slot);
                _rotateUntil.Remove(slot);
                ClearDefusalGuard(slot);
                CancelIdlePeek(slot);
            }
            if (_retakeRoutes.TryGetValue(slot, out var route) && route.Pawn != pawn.EntityHandle.Raw)
                _retakeRoutes.Remove(slot);
            bool kit = pawn.ItemServices is { Handle: not 0 } items && new CCSPlayer_ItemServices(items.Handle).HasDefuser;
            ct.Add((p, pawn, bot, RetakeDistance(pawn.AbsOrigin!, target), kit));
        }
        foreach (int slot in _retakeRoutes.Keys.Where(s => !ct.Any(c => c.Player.Slot == s)).ToArray()) _retakeRoutes.Remove(slot);

        bool defusing = bomb.BeingDefused || ct.Any(c => c.Pawn.IsDefusing);
        if (!defusing && !ct.Any(c => c.Player.Slot == _retakeLead
            && left >= (c.Kit ? 5f : 10f) + c.Dist / 290f + 0.35f))
        {
            var lead = ct.Where(c => left >= (c.Kit ? 5f : 10f) + c.Dist / 290f)
                .OrderBy(c => c.Dist + (c.Kit ? 0f : 400f) + (RetakeBusy(c.Player, c.Pawn, c.Bot, now) ? 450f : 0f))
                .FirstOrDefault();
            _retakeLead = lead.Player?.Slot ?? -1;
            if (_retakeLead >= 0) _retakeRoutes.Remove(_retakeLead);
        }
        // Nobody can make a fresh defuse; leave the engine in charge of saves or an active defuse.
        if (left < 5f || (_retakeLead < 0 && !defusing)) return;

        int support = 0;
        foreach (var c in ct.OrderBy(c => c.Dist))
        {
            int slot = c.Player.Slot;
            bool lead = !defusing && slot == _retakeLead;
            bool busy = RetakeBusy(c.Player, c.Pawn, c.Bot, now);
            if (!lead && left > 12f && TradeSupportActive(slot, c.Pawn, now)) continue;
            // A replacement defuser must approach the use point, not keep its old support goal.
            if (!defusing && _retakeRoutes.TryGetValue(slot, out var previousRole) && previousRole.Support == lead)
                _retakeRoutes.Remove(slot);
            if (lead && !busy && c.Dist < 210f && _defuseBombFunction != null
                && now >= _defuseRequestAt && left > (c.Kit ? 5f : 10f)
                && BotCanSee(c.Pawn, new Vector(target.X, target.Y, target.Z + 8f)))
            {
                // Native MoveTo can finish outside use range. Hand a clear, close approach back to DefuseBomb.
                _defuseRequestAt = now + 2.5f;
                ClearDefusalGuard(slot);
                CancelIdlePeek(slot);
                ForceDefuseBombState(slot);
            }
            if (c.Pawn.IsDefusing || c.Dist < (lead ? 110f : 445f))
            {
                if (_retakeRoutes.TryGetValue(slot, out var arrived)) arrived.Done = arrived.Arrived = true;
                continue;
            }
            if (!lead) support++;
            if (!_retakeRoutes.TryGetValue(slot, out var route))
            {
                if (busy) continue;
                // The lead goes to the bomb. Other CTs approach from adjacent positions, never the use point.
                var goal = lead ? target : RotationApproach(c.Pawn, target, support, 320f);
                if (goal == null) continue;
                _retakeRoutes[slot] = route = new RetakeRoute { Pawn = c.Pawn.EntityHandle.Raw, Goal = goal,
                    Support = !lead, At = now + (lead ? 0f : Math.Min(support - 1, 4) * 0.20f), ProgressAt = now };
            }
            if (route.Done)
            {
                // Contact or a cancelled defuse can pull an arrived CT away again.
                // Reopen only completed approaches; exhausted failed routes keep their retry limit.
                if (!route.Arrived || busy || c.Dist <= (lead ? 230f : 580f)) continue;
                route.Done = route.Arrived = route.Paused = false;
                route.Attempts = 0;
                route.BestDist = float.MaxValue;
                route.LastOrder = 0f;
                route.ProgressAt = route.At = now;
            }
            if (now < route.At) continue;
            if (busy) { route.Paused = true; continue; }
            SetHurry(c.Bot, Math.Min(left, 3f));
            float distance = RetakeDistance(c.Pawn.AbsOrigin!, route.Goal);
            if (distance < (route.Support ? 160f : 125f)) { route.Done = route.Arrived = true; continue; }
            if (distance < route.BestDist - 72f) { route.BestDist = distance; route.ProgressAt = now; }

            bool stalled = now - route.ProgressAt >= 3.5f;
            bool misdirected = route.Attempts > 0 && now - route.LastOrder > 2f && now - route.ProgressAt > 2.2f
                && RetakeDistance(c.Bot.GoalPosition, route.Goal) > 440f;
            if (route.Attempts > 0 && !route.Paused && !stalled && !misdirected) continue;
            bool resumed = route.Paused;
            if (route.Attempts >= 3 && !resumed) { route.Done = true; continue; }
            route.Paused = false;
            if (route.Support && stalled && !route.Fallback && route.Attempts > 0)
            {
                route.Fallback = true;
                // Try an approach on the other side before returning control to native pathing.
                var alternative = RotationApproach(c.Pawn, target, support + 1, 240f);
                if (alternative != null) { route.Goal = alternative; route.BestDist = float.MaxValue; }
            }
            ClearDefusalGuard(slot);
            CancelIdlePeek(slot);
            route.LastOrder = route.ProgressAt = now;
            bool newAttempt = route.Attempts == 0 || !resumed;
            if (newAttempt) route.Attempts++;
            if (!BotMoveTo(c.Bot, route.Goal, 1)) continue;
            SetHurry(c.Bot, Math.Min(left, 6f));
            if (newAttempt)
            {
                if (route.Attempts == 1) _retakeOrders++; else _retakeRetries++;
            }
        }
    }

    private static float RetakeDistance(Vector a, Vector b)
        => Dist2D(a, b) + MathF.Abs(a.Z - b.Z);

    private static string RetakeSiteLabel(int site) => site == 0 ? "A" : site == 1 ? "B" : "?";

    private void ClearRetakeRoutes()
    {
        _retakeRoutes.Clear();
        _retakeBomb = 0;
        _retakeLead = _retakeSite = -1;
        _defuseRequestAt = 0f;
    }

    private void ResetDefusalRetakeRoundState()
    {
        ClearRetakeRoutes();
        Array.Clear(_rotationConfirmed);
        _ctLossVictims.Clear();
        Array.Clear(_lossSupport);
        Array.Fill(_ctLastLossAt, -99f);
        _ctResponseSite = _ctScreenSlot = -1;
        _ctResponseUntil = 0f;
        _rotationSite = -1;
        _rotationConfirmedAt = -99f;
        _nextRetakeScan = 0f;
    }

    private void ResetDefusalRetakeMap()
    {
        ResetDefusalRetakeRoundState();
        _rotationOrders = _rotationArrivals = _retakeOrders = _retakeRetries = 0;
        _casualtyOrders = _casualtyRolls = _lossSupportFilled = _lossSupportExpired = 0;
    }

    private string DefusalRotationStatus()
        => $"[Smarter-Bot] ctRotate={_rotationOrders} arrived={_rotationArrivals} lossRotate={_casualtyOrders}/{_casualtyRolls} lossFilled={_lossSupportFilled} lossExpired={_lossSupportExpired} retake={_retakeOrders} retry={_retakeRetries} trade={_tradeSupportOrders} humanSupport={_humanPressureOrders} humanLoss={_humanLossSupportOrders} site={RetakeSiteLabel(_retakeSite)}";
}
