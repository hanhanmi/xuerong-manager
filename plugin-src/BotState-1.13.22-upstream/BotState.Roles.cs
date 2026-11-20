using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    private enum TStrategy { Default = 0, Split = 1, Commit = 2, SlowDefault = 3, Execute = 4, Fake = 5, Contact = 6, Probe = 7, Wave = 8 }

    private sealed class RolePlan
    {
        public string Role = "";
        public List<(Vector goal, int route, float holdUntil)> Legs = new();
        public int Leg;
        public float LegIssued;
        public float BestDist = float.MaxValue;
        public float BestAt;
        public int Reissues;
        public float StartAt, NextWalkInput;
        public bool Walk;
        public float PausedAt = -1f;
        public float Hurry;            // >0: SetHurry on every issue
        public bool Sticky;            // never give up a leg on no progress
        public float Arrive = 280f;    // leg reached within this 2D distance
        public string Group = "";      // gather group tag (released together)
        public int GatherLeg = -1;     // leg index held until the group is released
        public int Site = -1;          // site this plan ends on (for counting)
    }

    private readonly Dictionary<int, RolePlan> _rolePlan = new();
    private readonly Dictionary<int, float> _clumpCd = new();
    private Vector? _tSpawn, _ctSpawn;
    private float _roundLiveAt = -1f;
    private bool _rolesAssigned;
    private TStrategy _tStrategy;
    private int _tTargetSite = -1;
    private string _lastStrategyText = "-";
    private float _nextRoleScan;
    private bool _ctScoutRolled, _ctSupportRolled;
    private readonly float[] _ctFightAt = { -99f, -99f };
    private readonly Dictionary<int, int> _ctHomeSite = new();
    private readonly Dictionary<int, float> _ctHomeRedirect = new();

    private void OnRoundLiveForRoles()
    {
        _roundLiveAt = Server.CurrentTime;
        _rolesAssigned = false;
        _tSpawn = Centroid(CsTeam.Terrorist);
        _ctSpawn = Centroid(CsTeam.CounterTerrorist);
    }

    private static Vector? Centroid(CsTeam team)
    {
        float x = 0, y = 0, z = 0; int n = 0;
        var seen = new HashSet<uint>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)team) continue;
            var pw = p.PlayerPawn.Value;
            if (pw?.AbsOrigin == null || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || !seen.Add(pw.EntityHandle.Raw)) continue;
            x += pw.AbsOrigin.X; y += pw.AbsOrigin.Y; z += pw.AbsOrigin.Z; n++;
        }
        return n == 0 ? null : new Vector(x / n, y / n, z / n);
    }

    private static Vector Lerp(Vector a, Vector b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    private Vector Jitter(Vector v, float r) => new(v.X + RandF(-r, r), v.Y + RandF(-r, r), v.Z);

    private void UpdateRoles(float now)
    {
        if (now < _nextRoleScan) return;
        _nextRoleScan = now + 0.5f;
        if (!TacticsEnabled())
        {
            if (IsDefusalCombatEnabled() && _defusalBombPlanted) StepRolePlans(now);
            return;
        }
        bool startReady = _roundLiveAt >= 0f && _tSpawn != null && _ctSpawn != null;

        if (startReady && !_rolesAssigned && now - _roundLiveAt > 0.6f)
        {
            _rolesAssigned = true;
            AssignTRoles(now);
            AssignCtRound(now);
        }
        if (startReady) UpdateCtGamble(now);
        if (startReady && _ctGambleSite < 0 && IsDefusalCombatEnabled() && !_defusalBombPlanted && now - _roundLiveAt > 2f)
            UpdateCtForwardPlans(now);
        if (startReady) ApplyCtFormation(now);
        if (startReady && now - _roundLiveAt > 12f) UpdateCtSupportPosition(now);
        StepRolePlans(now);
        if (startReady && now - _roundLiveAt > 6f) UpdateCtAntiClump(now);
    }

    // Rolls the round's T plan and gives every T bot its role and route.
    private void AssignTRoles(float now)
    {
        var bots = new List<(CCSPlayerController p, CCSPlayerPawn pawn, CCSBot bot)>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != (byte)CsTeam.Terrorist || !IsAliveBot(p, out var pawn, out var bot)) continue;
            bots.Add((p, pawn, bot));
        }
        if (bots.Count == 0) { _lastStrategyText = "none (no T bots)"; return; }
        bots = bots.OrderBy(_ => _random.Next()).ToList();

        double r = _random.NextDouble();
        var rules = Rules();
        int totalRounds = rules?.TotalRoundsPlayed ?? -1;
        int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
        bool pistolRound = totalRounds == 0 || (maxRounds > 1 && totalRounds == maxRounds / 2);
        bool eco = IsEcoRound(CsTeam.Terrorist) || pistolRound;
        int deagles = bots.Count(b => b.pawn.WeaponServices?.MyWeapons.Any(h => h.Value?.DesignerName == "weapon_deagle") == true);
        bool slowEco = eco && !pistolRound && deagles >= Math.Max(2, (bots.Count + 1) / 2);
        if (eco)
            // Pistols vs rifles: stack one site fast or group up, no slow defaults.
            _tStrategy = r < 0.45 ? TStrategy.Commit : r < 0.85 ? TStrategy.Execute : TStrategy.Contact;
        else
            _tStrategy = r < 0.19 ? TStrategy.Default : r < 0.31 ? TStrategy.SlowDefault
                       : r < 0.41 ? TStrategy.Split : r < 0.51 ? TStrategy.Commit
                       : r < 0.64 ? TStrategy.Execute : r < 0.72 ? TStrategy.Fake
                       : r < 0.80 ? TStrategy.Contact : r < 0.90 ? TStrategy.Probe : TStrategy.Wave;
        if (slowEco) _tStrategy = TStrategy.SlowDefault;
        if (bots.Count < 3 && _tStrategy is TStrategy.Fake or TStrategy.Split or TStrategy.Probe or TStrategy.Wave)
            _tStrategy = TStrategy.Execute;
        _tTargetSite = _random.Next(2);
        int other = 1 - _tTargetSite;
        var mid = Lerp(_tSpawn!, _ctSpawn!, 0.5f);
        // When a default turns into a hit if nothing happened (seconds left on the clock).
        _tExecAtLeft = _tStrategy switch
        {
            TStrategy.Default => RandF(42f, 58f),
            TStrategy.SlowDefault => RandF(34f, 44f),
            TStrategy.Contact => RandF(38f, 50f),
            _ => RandF(30f, 40f),
        };

        if (eco && !slowEco && bots.Count >= 3 && r < 0.48)
        {
            _tStrategy = TStrategy.Commit;
            StartTExecute(_tTargetSite, "eco rush", gather: false, hurry: 12f);
            _lastStrategyText = $"Eco rush {(_tTargetSite == 0 ? 'A' : 'B')} x{bots.Count}";
            return;
        }
        if (eco && !slowEco && bots.Count >= 4 && r < 0.68 && StartEcoSplitAttack(_tTargetSite, bots.Select(b => b.p).ToList(), now))
            return;
        if (eco && !slowEco && bots.Count >= 4 && r < 0.86 && StartEcoSteal(_tTargetSite, bots.Select(b => b.p).ToList(), now))
            return;
        if (_tStrategy == TStrategy.Probe)
        {
            for (int i = 0; i < bots.Count; i++)
            {
                var (player, _, _) = bots[i];
                if (KeepOwnPlan(_rolePlan.GetValueOrDefault(player.Slot))) continue;
                int site = i % 2 == 0 ? _tTargetSite : other;
                var plan = new RolePlan { Role = "t-probe", Site = site, StartAt = now + RandF(0.1f, 0.7f), Arrive = 190f };
                plan.Legs.Add((Jitter(Lerp(_tSpawn!, SiteCenter(site), RandF(0.48f, 0.55f)), 90f), 2, 0f));
                plan.Legs.Add((Jitter(Lerp(_tSpawn!, SiteCenter(site), RandF(0.60f, 0.67f)), 100f), 2, 0f));
                _rolePlan[player.Slot] = plan;
            }
            _tProbeDecisionAt = now + RandF(13f, 20f);
            _lastStrategyText = "Two-site probe, then regroup";
            return;
        }
        if (_tStrategy == TStrategy.Wave)
        {
            StartTExecute(_tTargetSite, "two-wave", gather: false, hurry: 5f);
            var support = bots.OrderByDescending(b => HasC4(b.pawn)).Take(Math.Max(1, bots.Count / 2));
            foreach (var (player, _, _) in support)
            {
                if (!_rolePlan.TryGetValue(player.Slot, out var plan) || KeepOwnPlan(plan)
                    || !plan.Role.StartsWith("exec", StringComparison.Ordinal)) continue;
                plan.Role = "t-wave-support";
                plan.StartAt = now + RandF(0.7f, 1.4f);
                plan.Legs.Insert(0, (StagingFor(_tTargetSite), 2, 0f));
            }
            _lastStrategyText = $"Two-wave attack {(_tTargetSite == 0 ? 'A' : 'B')}";
            return;
        }
        if (_tStrategy == TStrategy.Execute)
        {
            // Group at a staging point, then hit together; one bot may lurk.
            if (bots.Count >= 4 && _random.NextDouble() < 0.35)
            {
                var lk = bots.FirstOrDefault(b => !HasC4(b.pawn));
                if (lk.p != null)
                    _rolePlan[lk.p.Slot] = new RolePlan { Role = "lurker", Walk = true, StartAt = now + RandF(0.5f, 2f) };
            }
            StartTExecute(_tTargetSite, eco ? "eco stack" : "opening", gather: true, hurry: 0f);
            if (_groups.TryGetValue(TExecGroup, out var eg))
            { eg.NotBefore = now + RandF(eco ? 8f : 12f, eco ? 14f : 24f); eg.Deadline = eg.NotBefore + 14f; }
            return;
        }
        if (_tStrategy == TStrategy.Fake)
        {
            // Two bots show themselves on the fake site; the rest wait at the real site's staging.
            var fakers = bots.Where(b => !HasC4(b.pawn)).Take(2).ToList();
            _fakers.Clear();
            foreach (var f in fakers) _fakers.Add(f.p.Slot);
            StartTExecute(_tTargetSite, "fake", gather: true, hurry: 0f, skip: _fakers);
            if (_groups.TryGetValue(TExecGroup, out var fg))
            { fg.NotBefore = now + 600f; fg.Deadline = now + 60f; }
            _fakeReleaseAt = now + RandF(18f, 30f);
            foreach (var f in fakers)
            {
                var fp = new RolePlan { Role = $"fake{(other == 0 ? 'A' : 'B')}", StartAt = now + RandF(0.2f, 1f), Hurry = 4f, Site = other };
                fp.Legs.Add((Jitter(Lerp(_tSpawn!, SiteCenter(other), RandF(0.7f, 0.82f)), 150f), 1, now + RandF(22f, 32f)));
                fp.Legs.Add((Jitter(SiteCenter(_tTargetSite), 200f), 1, 0f)); // then rotate to the real hit
                _rolePlan[f.p.Slot] = fp;
            }
            _lastStrategyText = $"Fake {(other == 0 ? 'A' : 'B')} -> hit {(_tTargetSite == 0 ? 'A' : 'B')}";
            if (_debugTactics) BroadcastDebug("[Smarter-Bot/Roles] T plan: " + _lastStrategyText);
            return;
        }
        if (_tStrategy == TStrategy.Contact)
        {
            // Everyone together, slow and on the safe route, close to each other.
            StartTExecute(_tTargetSite, "contact", gather: true, hurry: 0f);
            foreach (var pl in _rolePlan.Values) { pl.Walk = true; pl.Legs = pl.Legs.Select(l => (l.goal, 2, l.holdUntil)).ToList(); }
            if (_groups.TryGetValue(TExecGroup, out var cg))
            { cg.NotBefore = now + RandF(16f, 28f); cg.Deadline = cg.NotBefore + 12f; }
            return;
        }

        // Lurker: never the bomb carrier, needs at least 3 T alive.
        int lurker = -1;
        double lurkChance = _tStrategy == TStrategy.Commit ? 0.3 : 0.6;
        if (bots.Count >= 3 && _random.NextDouble() < lurkChance)
        {
            for (int i = 0; i < bots.Count; i++)
                if (!HasC4(bots[i].pawn)) { lurker = i; break; }
        }

        int splitHalf = 0;
        for (int i = 0; i < bots.Count; i++)
        {
            var (p, pawn, bot) = bots[i];
            var plan = new RolePlan { StartAt = now + RandF(0.3f, 2.5f) };
            if (i == lurker)
            {
                plan.Role = "lurker";
                plan.Walk = true;
                var flank = Lerp(mid, SiteCenter(other), RandF(0.55f, 0.75f));
                plan.Legs.Add((Jitter(flank, 150f), 2, now + RandF(20f, 35f)));
                plan.Legs.Add((Jitter(SiteCenter(other), 200f), 2, 0f));
                _rolePlan[p.Slot] = plan;
                continue;
            }

            int site = _tTargetSite;
            switch (_tStrategy)
            {
                case TStrategy.Default:
                    // Default spread: about half the team gets an off-meta route, the rest stay native.
                    if (_random.NextDouble() < 0.5 && !HasC4(pawn)) continue;
                    site = _random.Next(2);
                    break;
                case TStrategy.Split:
                    site = (splitHalf++ % 2 == 0) ? _tTargetSite : other;
                    break;
                case TStrategy.SlowDefault:
                    plan.Legs.Add((Jitter(Lerp(_tSpawn!, mid, RandF(0.45f, 0.7f)), 250f), 2,
                                   _roundLiveAt + RandF(15f, 25f)));
                    break;
            }

            // Route variety: fastest, safest, or a detour via mid.
            double rr = _random.NextDouble();
            var target = Jitter(SiteCenter(site), 180f);
            if (rr < 0.35) { plan.Role = $"site{(site == 0 ? 'A' : 'B')}-fast"; plan.Legs.Add((target, 1, 0f)); }
            else if (rr < 0.70) { plan.Role = $"site{(site == 0 ? 'A' : 'B')}-safe"; plan.Legs.Add((target, 2, 0f)); }
            else
            {
                plan.Role = $"site{(site == 0 ? 'A' : 'B')}-viaMid";
                if (_tStrategy != TStrategy.SlowDefault) plan.Legs.Add((Jitter(mid, 200f), 2, 0f));
                plan.Legs.Add((target, 2, 0f));
            }
            if (_tStrategy == TStrategy.SlowDefault) plan.Role = "slow-" + plan.Role;
            _rolePlan[p.Slot] = plan;
        }

        _lastStrategyText = $"{_tStrategy} target={(_tTargetSite == 0 ? 'A' : 'B')} lurker={(lurker >= 0 ? bots[lurker].p.PlayerName : "none")}";
        if (_debugTactics) BroadcastDebug("[Smarter-Bot/Roles] T plan: " + _lastStrategyText);
    }

    private void StepRolePlans(float now)
    {
        if (_rolePlan.Count == 0) return;
        foreach (var slot in _rolePlan.Keys.ToList())
        {
            var plan = _rolePlan[slot];
            if (_defusalBombPlanted && plan.Role != "eco-postplant") continue;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (!IsAliveBot(p, out var pawn, out var bot) || plan.Leg >= plan.Legs.Count)
            { _rolePlan.Remove(slot); continue; }
            if (IsDefusalCombatEnabled()) DropSiteHold(slot);
            if (now < plan.StartAt || TradeSupportActive(slot, pawn, now)) continue;

            bool formationMove = IsDefusalCombatEnabled() && p!.TeamNum == (byte)CsTeam.CounterTerrorist
                && (plan.Role == "ct-formation" || plan.Role.StartsWith("ct-fill") || plan.Role == "ct-reposition");
            if (formationMove && !RetakeBusy(p!, pawn, bot, now)) RebalanceFormationRoute(slot, plan, now);
            bool rotating = IsDefusalCombatEnabled() && plan.Role == "ct-rotate";
            // Rotators resume as soon as the fight is over; other roles still observe their usual contact window.
            bool immediateResume = plan.Role is "t-mid-follow" or "t-mid-flank" or "t-mid-support" or "ct-mid-angle" or "bomb-fetch";
            bool pause = rotating || formationMove || immediateResume ? RetakeBusy(p!, pawn, bot, now)
                                 : bot.IsAttacking || bot.IsEnemyVisible || now - bot.LastSawEnemyTimestamp < 3f;
            if (pause)
            {
                if (plan.PausedAt < 0f) plan.PausedAt = now;
                continue;
            }
            if (_smokeHold.ContainsKey(slot)) continue;

            // Group already went: a late member skips the meeting point and follows.
            if (plan.Leg == plan.GatherLeg && plan.GatherLeg >= 0 && GroupReleased(plan.Group)
                && plan.Leg + 1 < plan.Legs.Count)
            {
                plan.Leg++; plan.LegIssued = 0f; plan.BestDist = float.MaxValue; plan.Reissues = 0;
            }

            var leg = plan.Legs[plan.Leg];
            bool siteDelivery = IsDefusalCombatEnabled() && p!.TeamNum == (byte)CsTeam.CounterTerrorist && IsCtSiteDelivery(plan);
            float d = rotating || siteDelivery ? RetakeDistance(pawn.AbsOrigin!, leg.goal) : Dist2D(pawn.AbsOrigin!, leg.goal);
            if (rotating && d >= plan.Arrive) SetHurry(bot, 3f);
            bool issue = plan.LegIssued == 0f || plan.PausedAt >= 0f;
            plan.PausedAt = -1f;
            bool gathering = plan.Leg == plan.GatherLeg && !GroupReleased(plan.Group);

            bool finalSiteLeg = siteDelivery && plan.Leg == plan.Legs.Count - 1;
            if (d < plan.Arrive && (!finalSiteLeg || (DuelGrounded(pawn)
                && SiteOf(pawn.AbsOrigin!, SiteHoldBoundary) == plan.Site)))
            {
                if (leg.holdUntil > now || gathering) continue;   // wait at this leg
                plan.Leg++; plan.LegIssued = 0f; plan.BestDist = float.MaxValue; plan.Reissues = 0;
                if (plan.Leg >= plan.Legs.Count)
                {
                    if (rotating) _rotationArrivals++;
                    _rolePlan.Remove(slot);
                    if (finalSiteLeg) BeginSiteHold(slot, pawn, plan.Site, now);
                    continue;
                }
                leg = plan.Legs[plan.Leg];
                d = rotating || siteDelivery ? RetakeDistance(pawn.AbsOrigin!, leg.goal) : Dist2D(pawn.AbsOrigin!, leg.goal);
                issue = true;
            }

            if (d < plan.BestDist - 60f) { plan.BestDist = d; plan.BestAt = now; }
            else if (!issue && now - plan.BestAt > (rotating ? 3.4f : 6f))
            {
                // No progress: retry twice, then skip the leg (fall back to the next / native).
                if (plan.Reissues >= 2 && !plan.Sticky)
                {
                    plan.Leg++; plan.LegIssued = 0f; plan.BestDist = float.MaxValue; plan.Reissues = 0;
                    continue;
                }
                plan.Reissues++;
                issue = true;
            }
            // Native idle logic can wander off; re-steer if the bot is standing around.
            float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
            if (!siteDelivery && !issue && vx * vx + vy * vy < 25f && now - plan.LegIssued > 3f && leg.holdUntil <= now) issue = true;

            if (issue)
            {
                if (rotating)
                {
                    ClearDefusalGuard(slot);
                    CancelIdlePeek(slot);
                }
                if (BotMoveTo(bot, leg.goal, leg.route))
                {
                    plan.LegIssued = now;
                    plan.BestAt = now;
                    if (plan.Hurry > 0f) SetHurry(bot, plan.Hurry);
                    else if (!plan.Walk && _tStrategy == TStrategy.Commit && p!.TeamNum == (byte)CsTeam.Terrorist) SetHurry(bot, 6f);
                }
            }
        }
    }

    private void TryEvadeUnseenDamage(int slot, bool throughSmoke, float now)
    {
        if (_botController == null || now < _formationHurtCooldown.GetValueOrDefault(slot)) return;
        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsAliveBot(player, out var pawn, out var bot) || pawn.AbsOrigin == null || !DuelGrounded(pawn)
            || bot.IsEnemyVisible || DefusalObjectiveBusy(pawn) || DuelFlashActive(pawn, (int)player!.Index, now)
            || _cachedNearLadder.GetValueOrDefault((int)player.Index)
            || _doorEventCooldown.GetValueOrDefault((int)player.Index) > now
            || ActiveMoveOwner(slot, now) >= MoveOwner.Flash
            || IsNonGunWeapon(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName)
            || BotControllerBridge.IsViewControlled(_botController, slot)) return;
        if (_duels.TryGetValue(slot, out var duel) && duel.Phase != DuelPhase.None) return;
        if (duel == null) _duels[slot] = duel = new DuelState();
        var origin = pawn.AbsOrigin;
        var velocity = pawn.AbsVelocity;
        var motion = new System.Numerics.Vector2(velocity.X, velocity.Y);
        var forward = motion.LengthSquared() > 40f * 40f ? System.Numerics.Vector2.Normalize(motion) : DuelFacing(pawn);
        var smoke = throughSmoke ? _smokes.OrderBy(s => (s.X - origin.X) * (s.X - origin.X) + (s.Y - origin.Y) * (s.Y - origin.Y)).FirstOrDefault() : null;
        if (smoke != null)
        {
            var toSmoke = new System.Numerics.Vector2(smoke.X - origin.X, smoke.Y - origin.Y);
            if (toSmoke.LengthSquared() > 1f) forward = System.Numerics.Vector2.Normalize(toSmoke);
        }
        float sideSign = _random.Next(2) == 0 ? 1f : -1f;
        var side = DuelLeft(forward) * sideSign;
        double roll = _random.NextDouble();
        float along = smoke == null ? -0.35f : roll < 0.50 ? -0.75f : roll < 0.80 ? -0.15f : 0.30f;
        var direction = System.Numerics.Vector2.Normalize(side + forward * along);
        if (!DuelRayClear(pawn, direction, 40f)) direction = System.Numerics.Vector2.Normalize(-side + forward * along);
        if (!DuelRayClear(pawn, direction, 40f))
        {
            direction = -forward;
            if (!DuelRayClear(pawn, direction, 30f)) return;
        }
        if (_smokeHold.TryGetValue(slot, out var hold)) EndSmokeHold(slot, hold);
        ClearDefusalGuard(slot);
        CancelIdlePeek(slot);
        KeepDuelStanding(slot, duel);
        var point = new System.Numerics.Vector2(origin.X, origin.Y);
        if (!BeginDuelLeg(slot, pawn, duel, DuelPhase.Recheck, point, direction, 0.9f, RandF(0.4f, 0.65f), now))
        {
            ReleaseDuel(slot, duel);
            return;
        }
        if (_rolePlan.TryGetValue(slot, out var plan)) plan.PausedAt = now;
        if (_retakeRoutes.TryGetValue(slot, out var route)) route.Paused = true;
        _formationHurtCooldown[slot] = now + 0.8f;
    }
    private void RebalanceFormationRoute(int slot, RolePlan plan, float now)
    {
        if (_defusalBombPlanted || plan.Site is < 0 or > 1 || _ctGambleSite >= 0
            || (_rotationSite >= 0 && now - _rotationConfirmedAt < 8f)) return;
        int[] at = new int[2];
        int humansAtDestination = 0, alive = 0;
        var seen = new HashSet<uint>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !seen.Add(pawn.EntityHandle.Raw)) continue;
            alive++;
            if (player.Slot == slot) continue;
            bool ai = IsAliveBot(player, out _, out _);
            int site = CtCoverageSite(player, pawn, 1100f);
            int reserved = ai ? CtInboundSite(player.Slot) : -1;
            if (reserved >= 0) at[reserved]++;
            else if (site >= 0) at[site]++;
            if (!ai && site == plan.Site) humansAtDestination++;
        }
        if (humansAtDestination == 0) return;
        int[] desired = alive >= 4 ? new[] { 2, 2 } : new[] { 1, 1 };
        if (alive >= 4 && _ctFormation == 2) desired = new[] { 3, 1 };
        if (alive >= 4 && _ctFormation == 3) desired = new[] { 1, 3 };
        int otherSite = 1 - plan.Site;
        if (at[plan.Site] < desired[plan.Site] || at[otherSite] >= desired[otherSite]) return;
        plan.Site = otherSite;
        plan.Role = "ct-formation";
        plan.Leg = 0;
        plan.Legs.Clear();
        plan.Legs.Add((Jitter(SiteCenter(otherSite), 180f), 1, 0f));
        plan.LegIssued = 0f;
        plan.BestDist = float.MaxValue;
        plan.Reissues = 0;
        plan.StartAt = now;
    }
    private void UpdateCtSupportPosition(float now)
    {
        if (_ctSupportRolled || !IsDefusalCombatEnabled() || _defusalBombPlanted || _ctGambleSite >= 0
            || _tSpawn == null || _ctSpawn == null) return;
        _ctSupportRolled = true;
        if ((_rotationSite >= 0 && now - _rotationConfirmedAt < 10f)
            || _siteContacts.Any(contact => now - contact.Value < 8f)) return;
        double choice = _random.NextDouble();
        if (choice >= 0.52) return;

        var defenders = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, int Site, bool Bot)>();
        var seen = new HashSet<uint>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos }
                || !seen.Add(pawn.EntityHandle.Raw)) continue;
            defenders.Add((player, pawn, SiteOf(pos, 1000f), IsAliveBot(player, out _, out _)));
        }
        var available = defenders.Where(d => d.Bot && !_rolePlan.ContainsKey(d.Player.Slot)
            && !SiteHoldActive(d.Player.Slot, d.Pawn)
            && !TradeSupportActive(d.Player.Slot, d.Pawn, now)
            && IsAliveBot(d.Player, out _, out var bot) && !RetakeBusy(d.Player, d.Pawn, bot, now)
            && now - bot.LastSawEnemyTimestamp > 4f).ToList();
        if (choice < 0.32)
        {
            foreach (int site in new[] { 0, 1 }.OrderBy(_ => _random.Next()))
            {
                if (defenders.Count(d => d.Site == site) < 2) continue;
                var center = SiteCenter(site);
                float dx = center.X - _tSpawn.X, dy = center.Y - _tSpawn.Y;
                float length = MathF.Sqrt(dx * dx + dy * dy);
                if (!float.IsFinite(length) || length < 1f) continue;
                dx /= length; dy /= length;
                foreach (var candidate in available.Where(d => d.Site == site).OrderBy(_ => _random.Next()))
                {
                    var teammate = defenders.Where(d => d.Site == site && d.Player.Slot != candidate.Player.Slot)
                        .OrderBy(d => Dist2D(d.Pawn.AbsOrigin!, candidate.Pawn.AbsOrigin!)).First();
                    float side = (teammate.Pawn.AbsOrigin!.X - center.X) * -dy
                        + (teammate.Pawn.AbsOrigin.Y - center.Y) * dx >= 0f ? -1f : 1f;
                    var goal = new Vector(center.X + dx * 100f - dy * side * 165f,
                        center.Y + dy * 100f + dx * side * 165f, center.Z);
                    float distance = RetakeDistance(candidate.Pawn.AbsOrigin!, goal);
                    if (distance < 110f || distance > 550f) continue;
                    var plan = new RolePlan { Role = "ct-crossfire", Site = site, StartAt = now, Arrive = 100f };
                    plan.Legs.Add((goal, 2, now + RandF(12f, 20f)));
                    _rolePlan[candidate.Player.Slot] = plan;
                    return;
                }
            }
            return;
        }
        if (defenders.Count < 5 || defenders.Count(d => d.Site == 0) < 2 || defenders.Count(d => d.Site == 1) < 2)
            return;
        var midpoint = Lerp(SiteCenter(0), SiteCenter(1), 0.5f);
        var reservePoint = Lerp(midpoint, _ctSpawn, 0.55f);
        var reserve = available.Where(d => d.Site < 0 && RetakeDistance(d.Pawn.AbsOrigin!, reservePoint) < 850f)
            .OrderBy(d => RetakeDistance(d.Pawn.AbsOrigin!, reservePoint)).FirstOrDefault();
        if (reserve.Player == null) return;
        var reservePlan = new RolePlan { Role = "ct-reserve", StartAt = now, Arrive = 140f };
        reservePlan.Legs.Add((reservePoint, 2, now + RandF(14f, 22f)));
        _rolePlan[reserve.Player.Slot] = reservePlan;
    }

    private void UpdateCtForwardPlans(float now)
    {
        var bots = new List<(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int site)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player == null || player.TeamNum != (byte)CsTeam.CounterTerrorist
                || !IsAliveBot(player, out var pawn, out var bot) || pawn.AbsOrigin == null) continue;
            int site = CtCoverageSite(player, pawn, SiteHoldBoundary);
            if (site >= 0 && !_ctHomeSite.ContainsKey(player.Slot)) _ctHomeSite[player.Slot] = site;
            if (site < 0 && !_ctHomeSite.ContainsKey(player.Slot) && now - _roundLiveAt > 5f
                && _tSpawn != null && _ctSpawn != null
                && Dist2D(pawn.AbsOrigin, _tSpawn) + 250f < Dist2D(pawn.AbsOrigin, _ctSpawn))
            {
                int nearest = SiteOf(pawn.AbsOrigin, float.MaxValue);
                if (nearest >= 0) _ctHomeSite[player.Slot] = nearest;
            }
            bots.Add((player, pawn, bot, site));
        }
        if (!_ctScoutRolled && (_rotationSite < 0 || now - _rotationConfirmedAt >= 8f)
            && now - _roundLiveAt > 14f && bots.Count >= 4)
        {
            _ctScoutRolled = true;
            var candidates = bots.Where(b => b.site >= 0 && bots.Count(other => other.site == b.site) >= 2
                && !b.bot.IsEnemyVisible && !b.bot.IsAttacking && !_rolePlan.ContainsKey(b.player.Slot) && !SiteHoldActive(b.player.Slot, b.pawn))
                .OrderBy(_ => _random.Next()).ToList();
            if (candidates.Count > 0 && _random.NextDouble() < 0.005)
            {
                var scout = candidates[0];
                var plan = new RolePlan { Role = "ct-scout", Walk = true, Site = scout.site, Arrive = 190f };
                plan.Legs.Add((Jitter(Lerp(SiteCenter(scout.site), _tSpawn!, 0.38f), 90f), 2, 0f));
                _rolePlan[scout.player.Slot] = plan;
            }
        }
        foreach (var entry in bots)
        {
            int slot = entry.player.Slot;
            if (_rolePlan.TryGetValue(slot, out var plan) && plan.Role == "ct-scout")
            {
                int otherSite = 1 - plan.Site;
                if (now - _ctFightAt[otherSite] < 3f && !plan.Sticky)
                {
                    plan.Sticky = true;
                    if (_random.NextDouble() < 0.32)
                    {
                        plan.Role = "ct-flank";
                        plan.Walk = false;
                        plan.Hurry = 6f;
                        plan.Site = otherSite;
                        plan.Leg = 0;
                        plan.LegIssued = 0f;
                        plan.Legs.Clear();
                        plan.Legs.Add((Jitter(Lerp(SiteCenter(otherSite), _tSpawn!, 0.43f), 80f), 1, 0f));
                        plan.Legs.Add((Jitter(SiteCenter(otherSite), 100f), 1, 0f));
                    }
                }
                continue;
            }
            if (!_ctHomeSite.TryGetValue(slot, out int home)
                || _rolePlan.ContainsKey(slot) || SiteHoldActive(slot, entry.pawn) || HasDefusalRetakeRoute(slot) || TradeSupportActive(slot, entry.pawn, now)
                || _rotateUntil.GetValueOrDefault(slot) > now
                || DefusalObjectiveBusy(entry.pawn) || RetakeBusy(entry.player, entry.pawn, entry.bot, now)
                || entry.bot.IsEnemyVisible || entry.bot.IsAttacking || now - entry.bot.LastSawEnemyTimestamp < 1.5f
                || (_reaction.TryGetValue(slot, out var reaction) && now - reaction.LastHurtAt < 1f)
                || _ctHomeRedirect.GetValueOrDefault(slot) > now) continue;
            float fromSite = RetakeDistance(entry.pawn.AbsOrigin!, SiteCenter(home));
            bool enemySide = _tSpawn != null
                && Dist2D(entry.pawn.AbsOrigin!, _tSpawn) + 250f < Dist2D(SiteCenter(home), _tSpawn);
            if (fromSite < (enemySide ? 340f : 800f)) continue;
            _ctHomeRedirect[slot] = now + RandF(2f, 3f);
            var returnPlan = new RolePlan { Role = "ct-reposition", Site = home, Arrive = 230f, Hurry = 3f };
            returnPlan.Legs.Add((Jitter(SiteCenter(home), 150f), 1, 0f));
            _rolePlan[slot] = returnPlan;
            ClearDefusalGuard(slot);
            CancelIdlePeek(slot);
            if (BotMoveTo(entry.bot, returnPlan.Legs[0].goal, 1))
            {
                returnPlan.LegIssued = returnPlan.BestAt = now;
                SetHurry(entry.bot, 3f);
            }
        }
    }

    private void UpdateCtScoutWalk(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        if (_botController == null || !_rolePlan.TryGetValue(player.Slot, out var plan) || plan.Role != "ct-scout"
            || now < plan.StartAt || bot.IsEnemyVisible || bot.IsAttacking || DuelFlashActive(pawn, idx, now)
            || _cachedNearLadder.GetValueOrDefault(idx) || now < plan.NextWalkInput) return;
        try { BotControllerBridge.InjectUsercmd(_botController, player.Slot, (ulong)PlayerButtons.Speed, 200); }
        catch { }
        plan.NextWalkInput = now + 0.12f;
    }
    // Stops CTs from pushing out together: a pack of 3+ away from sites and spawn is split up.
    private void UpdateCtAntiClump(float now)
    {
        var cts = new List<(CCSPlayerController p, CCSPawnPos x)>();
        int[] atSite = new int[2];
        var seen = new HashSet<uint>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || !p.IsValid || p.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            var pw = p.PlayerPawn.Value;
            if (pw?.AbsOrigin == null || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || !seen.Add(pw.EntityHandle.Raw)) continue;
            int s = SiteOf(pw.AbsOrigin);
            if (s >= 0) atSite[s]++;
            if (s >= 0 || !IsAliveBot(p, out var pawn, out var bot)) continue;
            if (bot.IsAttacking || bot.IsEnemyVisible) continue;
            if (_rotateUntil.GetValueOrDefault(p.Slot) > now || _clumpCd.GetValueOrDefault(p.Slot) > now || SiteHoldActive(p.Slot, pawn)) continue;
            if (_rolePlan.ContainsKey(p.Slot)) continue;
            if (Dist2D(pawn.AbsOrigin!, _ctSpawn!) < 900f) continue;
            cts.Add((p, new CCSPawnPos(pawn.AbsOrigin!, bot)));
        }
        if (cts.Count < 3) return;

        foreach (var c in cts)
        {
            var group = cts.Where(o => Dist2D(o.x.Pos, c.x.Pos) < 500f).ToList();
            if (group.Count < 3) continue;
            // Keep two (random), send the rest to the thinner site.
            group = group.OrderBy(_ => _random.Next()).ToList();
            for (int i = 2; i < group.Count; i++)
            {
                int site = atSite[0] <= atSite[1] ? 0 : 1;
                if (BotMoveTo(group[i].x.Bot, Jitter(SiteCenter(site), 180f), 1))
                {
                    atSite[site]++;
                    _clumpCd[group[i].p.Slot] = now + 20f;
                }
            }
            foreach (var g in group) _clumpCd[g.p.Slot] = now + 20f;
            break; // one pack per scan
        }
    }

    private readonly record struct CCSPawnPos(Vector Pos, CCSBot Bot);

    private void ResetRolesRoundState()
    {
        _ctScoutRolled = false;
        _ctSupportRolled = false;
        _ctFightAt[0] = _ctFightAt[1] = -99f;
        _ctHomeSite.Clear();
        _ctHomeRedirect.Clear();
        _rolePlan.Clear();
        _clumpCd.Clear();
        _roundLiveAt = -1f;
        _rolesAssigned = false;
        _lastStrategyText = "-";
        _tSpawn = _ctSpawn = null;
        _nextRoleScan = 0f;
    }
}
