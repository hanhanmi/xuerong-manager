using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Vector2 = System.Numerics.Vector2;

namespace BotState;

public partial class BotState
{
    private enum DuelPhase { None, ContactStep, ReloadEscape, Swing, FireStep, CrouchLead, CrouchPeek, Recheck, RunGun, RifleWide }

    private sealed class DuelState
    {
        public DuelPhase Phase;
        public float Until, RetryAt, LastSeenAt = -10f, LastKillAt = -10f;
        public float Side, Strength, StartedAt, NextPathCheck, NextInputAt, LastProgressAt;
        public Vector2 Direction, Start, LastProgressPosition;
        public uint Enemy, Weapon;
        public int BurstShots, BurstLimit, CrouchLeg, PeekChanges;
        public float NextSpecialAt;
        public bool WideCrouch;
        public float NextCrouchAt, FullyCrouchedAt, NextReloadJumpAt;
        public Vector2 FullyCrouchedPosition;
        public bool ReturnAfterShot, ReloadJumpPlanned, ReloadJumped, ReloadDuckPlanned, RunGun, EntryRunGun;
        public float NextRunGunAt;
        public long ReloadJump;
        public long BlockFire, Duck, Stand;
    }

    private const ulong DuelDuckButton = (ulong)PlayerButtons.Duck;

    private readonly Dictionary<int, DuelState> _duels = new();
    private bool _defusalCombatLive, _defusalRoundEnded, _defusalBombPlanted, _defusalMapSeen;
    private float _nextDefusalCheck;
    private int _duelMoves, _duelWide, _duelCrouch, _duelSwitch, _duelBursts, _duelFireGates;
    private int _duelRifleStill, _duelRifleMoving, _duelKillChecks, _duelKillResume;
    private int _duelDuckTurns, _duelDuckAborts, _duelRedirects, _duelContactSteps;

    private bool IsDefusalCombatEnabled() => _defusalCombatLive && !_isFreezeTime && !_defusalRoundEnded;

    private void RefreshDefusalCombat(float now)
    {
        if (now < _nextDefusalCheck) return;
        _nextDefusalCheck = now + 0.25f;
        _defusalCombatLive = false;
        if (_isFreezeTime || _defusalRoundEnded || ConVar.Find("game_type")?.GetPrimitiveValue<int>() != 0) return;
        if (ConVar.Find("game_mode")?.GetPrimitiveValue<int>() is not (0 or 1 or 2)) return;
        var rules = Rules();
        _defusalCombatLive = rules is { MapHasBombTarget: true, WarmupPeriod: false, FreezePeriod: false };
        _defusalBombPlanted = _defusalCombatLive && rules!.BombPlanted;
        _defusalMapSeen |= _defusalCombatLive;
    }

    private static bool DefusalObjectiveBusy(CCSPlayerPawn pawn)
        => pawn.IsDefusing || pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName == "weapon_c4";

    private bool DuelFlashActive(CCSPlayerPawn pawn, int idx, float now)
        => pawn.BlindUntilTime > now || InCommittedBlind(idx, now)
            || (_flashTurn.TryGetValue(idx, out var flash) && now >= flash.Start && now < flash.Until);

    private static bool DuelWeaponIsRifle(string w)
        => w is "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_galilar" or
                "weapon_famas" or "weapon_aug" or "weapon_sg556" or "weapon_m249" or "weapon_negev";

    private static bool DuelGrounded(CCSPlayerPawn pawn)
        => pawn.OnGroundLastTick || pawn.GroundEntity is { IsValid: true };

    private static float DuelSpeedSquared(CCSPlayerPawn pawn)
    {
        var velocity = pawn.AbsVelocity;
        return velocity.X * velocity.X + velocity.Y * velocity.Y;
    }

    private void StopDuelPose(int slot, DuelState d)
    {
        if (d.Duck > 0 && _botController != null)
        {
            try { BotControllerBridge.CancelUsercmdInjection(_botController, slot, d.Duck); }
            catch { }
        }
        d.Duck = 0;
    }

    private void GateDuelFire(int slot, DuelState d, bool block)
    {
        if (_botController == null) return;
        if (block && d.BlockFire <= 0)
        {
            try { d.BlockFire = BotControllerBridge.StartUsercmdSuppression(_botController, slot, AttackButtonMask); }
            catch { return; }
            if (d.BlockFire > 0) _duelFireGates++;
        }
        else if (!block && d.BlockFire > 0)
        {
            try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, d.BlockFire); }
            catch { }
            d.BlockFire = 0;
        }
    }

    private void ReleaseDuel(int slot, DuelState d)
    {
        if (d.ReloadJump > 0 && _botController != null)
        {
            try { BotControllerBridge.CancelUsercmdInjection(_botController, slot, d.ReloadJump); } catch { }
            d.ReloadJump = 0;
        }
        ReleaseMove(slot, MoveOwner.Combat);
        StopDuelPose(slot, d);
        ReleaseDuelStand(slot, d);
        GateDuelFire(slot, d, false);
        d.Phase = DuelPhase.None;
    }

    private void ClearDefusalDuel(int slot)
    {
        if (_duels.Remove(slot, out var d)) ReleaseDuel(slot, d);
    }

    private void ResetDefusalDuels()
    {
        foreach (var (slot, d) in _duels) ReleaseDuel(slot, d);
        _duels.Clear();
    }

    private void ResetDefusalMap()
    {
        ResetInfoShareRoundState();
        _heardShots = _heardSteps = _heardLandings = _awarenessReports = 0;
        ResetDefusalDuels();
        ResetDefusalGuards();
        ResetDefusalRetakeMap();
        _defusalRoundEnded = false;
        _defusalCombatLive = false;
        _defusalBombPlanted = false;
        _defusalMapSeen = false;
        _nextDefusalCheck = 0f;
        _duelMoves = _duelWide = _duelCrouch = _duelSwitch = _duelBursts = _duelFireGates = 0;
        _duelRifleStill = _duelRifleMoving = _duelKillChecks = _duelKillResume = 0;
        _duelDuckTurns = _duelDuckAborts = _duelRedirects = 0;
        _duelContactSteps = 0;
        _firstShotCount = 0;
        _guardPeeks = _guardReturns = _guardShifts = 0;
        Array.Clear(_ctPeekMoved);
        Array.Clear(_ctPeekBack);
        Array.Clear(_tPeekMoved);
        Array.Clear(_tPeekBack);
        _guardCrouchHolds = _guardCrouchPushes = _guardCrouchAborts = _guardIdleRescues = 0;
        ClearSiteHoldAssignments();
        ResetPostplantGuard();
        _postplantPeekMoves = _postplantPeekReturns = _postplantArrivals = _postplantPatrols = 0;
        _siteHoldArrivals = _siteHoldReturns = _siteHoldShifts = _siteHoldPatrols = 0;
        _statRecoveryBypasses = _statRecoveryRepaths = _statRecoveryClimbs = _statRecoveryRecovered = 0;
    }

    private void CancelIdlePeek(int slot)
    {
        ReleaseMove(slot, MoveOwner.Peek);
        ReleaseMove(slot, MoveOwner.GuardRecovery);
        if (!_strafe.TryGetValue(slot, out var s)) return;
        s.PeekPhase = 0;
        s.HoldStart = -1f;
    }

    private void NoteDefusalKill(CCSPlayerController? attacker, CCSPlayerController? victim, float now)
    {
        if (!IsDefusalCombatEnabled() || _botController == null || victim == null || !victim.IsValid
            || attacker == null || !attacker.IsValid || attacker.TeamNum == victim.TeamNum
            || !IsAliveBot(attacker, out var pawn, out _) || DefusalObjectiveBusy(pawn)) return;
        int slot = attacker.Slot;
        if (!_duels.TryGetValue(slot, out var d)) _duels[slot] = d = new DuelState();
        ReleaseDuel(slot, d);
        d.LastKillAt = now;
        d.RetryAt = now;
        d.ReturnAfterShot = false;
        d.Enemy = 0;
        d.PeekChanges = 0;
        d.WideCrouch = false;
        d.RunGun = d.EntryRunGun = false;
        d.NextRunGunAt = 0f;
        // A kill ends any stale stop or hold; the next tick can fight or check a new angle.
        CancelIdlePeek(slot);
    }

    private void NoteDefusalWeaponFire(CCSPlayerController shooter, CCSPlayerPawn pawn, float now)
    {
        int slot = shooter.Slot;
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        if (IsNonGunWeapon(weapon)) return;
        NoteDefusalReactionShot(slot, now);
        if (DuelWeaponIsRifle(weapon!))
        {
            if (!DuelGrounded(pawn) || DuelSpeedSquared(pawn) > 88f * 88f) _duelRifleMoving++;
            else _duelRifleStill++;
        }
        if (!_duels.TryGetValue(slot, out var d)) return;
        if (IsSniperWeapon(weapon)) d.ReturnAfterShot = true;
        if (d.Phase is (DuelPhase.FireStep or DuelPhase.CrouchPeek) && ++d.BurstShots == 1) _duelBursts++;
    }

    // Movement uses a world-space direction so adjusting aim does not reverse a strafe.
    private bool UpdateDefusalDuel(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        int slot = player.Slot;
        var activeHandle = pawn.WeaponServices?.ActiveWeapon;
        var active = activeHandle?.Value;
        var weapon = active?.DesignerName;
        var origin = pawn.AbsOrigin;
        if (_botController == null || origin == null || DefusalObjectiveBusy(pawn) || IsNonGunWeapon(weapon)
            || ActiveMoveOwner(slot, now) >= MoveOwner.Flash || DuelFlashActive(pawn, idx, now)
            || _cachedNearLadder.GetValueOrDefault(idx) || BotControllerBridge.IsViewControlled(_botController, slot))
        {
            ClearDefusalDuel(slot);
            return true;
        }

        var enemy = bot.IsEnemyVisible ? bot.Enemy.Value : null;
        var target = enemy is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE }
            && enemy.TeamNum != player.TeamNum ? enemy.AbsOrigin : null;
        if (!_duels.TryGetValue(slot, out var d))
        {
            if (target == null && !IsReloading(player)) return false;
            _duels[slot] = d = new DuelState();
        }

        var point = new Vector2(origin.X, origin.Y);
        var velocity = pawn.AbsVelocity;
        var motion = new Vector2(velocity.X, velocity.Y);
        bool grounded = DuelGrounded(pawn);
        if (target == null)
        {
            if (IsReloading(player))
            {
                Vector? lastEnemy = now - bot.LastSawEnemyTimestamp < 4f ? bot.LastEnemyPosition : null;
                Vector2 reloadForward = DuelFacing(pawn);
                if (lastEnemy != null)
                {
                    var towardEnemy = new Vector2(lastEnemy.X, lastEnemy.Y) - point;
                    if (towardEnemy.LengthSquared() > 1f) reloadForward = Vector2.Normalize(towardEnemy);
                }
                CancelIdlePeek(slot);
                ClearDefusalGuard(slot);
                UpdateReloadEscape(slot, idx, pawn, bot, d, point, motion, reloadForward, lastEnemy, now);
                return true;
            }
            bool checking = d.Phase == DuelPhase.Recheck || (d.Phase == DuelPhase.ReloadEscape && IsReloading(player));
            bool recentContact = d.Phase != DuelPhase.None && d.Phase != DuelPhase.CrouchLead
                && now - d.LastSeenAt < 0.27f;
            if ((checking || recentContact) && (grounded || d.Phase == DuelPhase.ReloadEscape) && now < d.Until
                && DuelLegCanContinue(pawn, d, point, motion, now) && MaintainDuelMove(slot, pawn, d, now)) return true;
            ReleaseDuel(slot, d);
            if (now - d.LastKillAt < 0.8f)
            {
                d.LastKillAt = -10f;
                d.Enemy = 0;
                _duelKillResume++;
                if (grounded && !bot.IsWaitingBehindFriend && !NearSiteWithC4(pawn)
                    && TryDuelRecheck(slot, pawn, d, active!, point, now)) return true;
            }
            if (now - d.LastSeenAt > 1f) _duels.Remove(slot);
            return false;
        }

        CancelIdlePeek(slot);
        ClearDefusalGuard(slot);
        d.LastSeenAt = now;
        if (d.Weapon != activeHandle!.Raw || d.Phase == DuelPhase.Recheck)
        {
            ReleaseDuel(slot, d);
            d.Weapon = activeHandle.Raw;
            d.RetryAt = now;
            d.ReturnAfterShot = false;
            d.RunGun = d.EntryRunGun = false;
            d.NextRunGunAt = 0f;
        }
        if (d.Enemy != 0 && d.Enemy != bot.Enemy.Raw)
        {
            ReleaseDuel(slot, d);
            d.PeekChanges = 0;
            d.WideCrouch = false;
            d.RunGun = d.EntryRunGun = false;
            d.NextRunGunAt = 0f;
            d.RetryAt = now;
        }
        d.Enemy = bot.Enemy.Raw;
        if (now - d.LastKillAt < 0.8f)
        {
            _duelKillResume++;
            d.LastKillAt = -10f;
        }

        var toEnemy = new Vector2(target.X, target.Y) - point;
        float distance = toEnemy.Length();
        Vector2 forward = distance > 1f ? toEnemy / distance : DuelFacing(pawn);
        bool allowAdvance = DuelAdvanceFitsTactics(player, pawn, forward, now);
        bool defendingSite = player.TeamNum == (byte)CsTeam.CounterTerrorist && !_defusalBombPlanted
            && !_rolePlan.ContainsKey(slot) && !TradeSupportActive(slot, pawn, now);
        bool reloading = IsReloading(player);
        if (reloading)
        {
            UpdateReloadEscape(slot, idx, pawn, bot, d, point, motion, forward, target, now);
            return true;
        }
        if (d.Phase == DuelPhase.ReloadEscape) ReleaseDuel(slot, d);
        bool sniper = IsSniperWeapon(weapon);
        bool smg = weapon is "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon";
        bool pistol = weapon is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000" or "weapon_p250"
            or "weapon_tec9" or "weapon_fiveseven" or "weapon_cz75a" or "weapon_elite" or "weapon_deagle" or "weapon_revolver";
        bool precisionPistol = weapon is "weapon_deagle" or "weapon_revolver";
        bool mobilePistol = weapon is "weapon_glock" or "weapon_tec9" or "weapon_elite" or "weapon_cz75a";
        if ((smg || pistol) && now >= d.NextRunGunAt)
        {
            bool entry = smg && allowAdvance && !defendingSite && IsDuelEntry(player, pawn, forward, now);
            double chance;
            if (smg)
                chance = distance < 360f ? (entry ? 0.90 : 0.68)
                    : distance < 700f ? (entry ? 0.84 : 0.48)
                    : distance < 950f ? (entry ? 0.62 : 0.18) : 0.0;
            else if (precisionPistol)
                chance = distance < 300f ? 0.08 : distance < 430f ? 0.025 : 0.0;
            else
                chance = distance < 300f ? (mobilePistol ? 0.28 : 0.20)
                    : distance < 560f ? (mobilePistol ? 0.13 : 0.08)
                    : distance < 720f ? (mobilePistol ? 0.04 : 0.02) : 0.0;
            d.RunGun = _random.NextDouble() < chance;
            d.EntryRunGun = entry && d.RunGun;
            d.NextRunGunAt = now + (d.RunGun && entry ? RandF(4.0f, 6.0f)
                : d.RunGun ? RandF(1.8f, 3.0f) : RandF(3.0f, 4.5f));
        }
        float runRange = smg ? 950f : precisionPistol ? 430f : 720f;
        bool runGun = (smg || pistol) && d.RunGun && distance < runRange;
        bool mobile = runGun || (!smg && IsCloseRangeWeapon(weapon) && distance < 590f);
        float fireSpeed = sniper ? 41f : mobile && !runGun ? 235f : distance < 430f ? 88f : 74f;
        if (!grounded)
        {
            ReleaseDuel(slot, d);
            GateDuelFire(slot, d, !mobile);
            return true;
        }

        // Re-route immediately when the tactical destination changes or the target
        // leaves the selected run-and-gun range. A new leg can begin in the same tick.
        if (d.Phase == DuelPhase.RunGun && (!runGun
            || ((!allowAdvance || defendingSite) && Vector2.Dot(d.Direction, forward) > 0.12f)))
            d.Until = now;
        bool retreat = sniper && d.ReturnAfterShot;
        d.ReturnAfterShot = false;
        bool blocked = d.Phase != DuelPhase.None && !DuelLegCanContinue(pawn, d, point, motion, now);
        bool crouching = d.Phase == DuelPhase.CrouchPeek;
        if (crouching && blocked)
        {
            StopDuelPose(slot, d);
            KeepDuelStanding(slot, d);
            _duelDuckAborts++;
        }
        if (blocked) _duelRedirects++;

        // A crouch swing is armed only after the pawn has actually travelled standing in this direction.
        if (!blocked && d.Phase == DuelPhase.CrouchLead && now < d.Until && now - d.StartedAt >= 0.12f
            && Vector2.Dot(point - d.Start, d.Direction) >= 12f && Vector2.Dot(motion, d.Direction) >= 78f
            && DuelDuckAmount(pawn) < 0.12f && IsDirectionClear(pawn, d.Direction.X, d.Direction.Y, 58f))
        {
            if (!StartDuelCrouch(slot, pawn, d, point, now)) d.Until = now;
        }

        // Time the visible crouch, including the movement after the pose has settled.
        if (d.Phase == DuelPhase.CrouchPeek && d.FullyCrouchedAt < 0f && DuelDuckAmount(pawn) >= 0.65f)
        {
            d.FullyCrouchedAt = now;
            d.FullyCrouchedPosition = point;
        }
        bool duckTurn = d.Phase == DuelPhase.CrouchPeek && d.FullyCrouchedAt >= 0f
            && now - d.FullyCrouchedAt >= 0.55f
            && Vector2.Dot(point - d.FullyCrouchedPosition, d.Direction) >= 24f;
        if (!blocked && duckTurn && !d.WideCrouch && d.CrouchLeg == 1 && TryTurnDuelCrouch(slot, pawn, d, point, now))
            duckTurn = false;

        bool first = d.Phase == DuelPhase.None;
        bool fireStepDone = d.Phase == DuelPhase.FireStep && d.BurstShots >= d.BurstLimit;
        bool prepareNext = retreat || blocked || now >= d.Until || fireStepDone || duckTurn;
        if (first ? now >= d.RetryAt : prepareNext)
        {
            var prior = d.Phase;
            bool preferFire = !runGun && (prior is DuelPhase.ContactStep or DuelPhase.Swing or DuelPhase.RifleWide);
            if (retreat || blocked) preferFire = false;
            bool outOfAmmo = active!.Clip1 == 0;
            if (outOfAmmo) preferFire = false;
            bool sensitive = sniper || pawn.IsScoped;
            float side = ChooseDuelSide(d, motion, forward, (first || preferFire || prior == DuelPhase.CrouchPeek) && !blocked);
            // During a reload or on a sniper shot, step away from the angle as well as sideways.
            bool takeCover = retreat || outOfAmmo || (distance < 260f && !mobile);
            bool started = false;
            if (runGun && !takeCover && !outOfAmmo && !sensitive)
            {
                // Keep the same shoulder for most consecutive legs so speed is not
                // lost to constant reversals; blocked routes can use the other side.
                if (prior == DuelPhase.RunGun && !blocked && d.Side != 0f && _random.NextDouble() < 0.72)
                    side = d.Side;
                started = TryStartRunGunLeg(slot, pawn, d, point, forward, side, distance,
                    allowAdvance && !defendingSite, now);
            }
            if (!started && !first && prior is not (DuelPhase.CrouchLead or DuelPhase.CrouchPeek or DuelPhase.RifleWide)
                && !blocked && !takeCover && !outOfAmmo && !sensitive && d.PeekChanges >= 1 && now >= d.NextSpecialAt
                && DuelWeaponIsRifle(weapon!) && distance is > 280f and < 1450f && _random.NextDouble() < 0.40)
            {
                bool crouchSwing = _random.NextDouble() < 0.42;
                d.WideCrouch = crouchSwing;
                started = crouchSwing
                    ? TryStartCrouchLead(slot, pawn, d, point, forward, side, now, wide: true)
                    : TryStartRifleWide(slot, pawn, d, point, forward, side, now);
                if (!started) d.WideCrouch = false;
                if (started)
                {
                    d.PeekChanges = 0;
                    d.NextSpecialAt = now + RandF(3.0f, 5.0f);
                }
            }
            if (!started && first) started = TryStartContactStep(slot, pawn, d, point, motion, forward, side, takeCover, allowAdvance, now);
            if (prior is DuelPhase.CrouchLead or DuelPhase.CrouchPeek) d.WideCrouch = false;
            if (!started && prior is not (DuelPhase.CrouchPeek or DuelPhase.RifleWide) && !sensitive && !blocked && !takeCover && !mobile && distance is > 255f and < 1480f
                && now >= d.NextCrouchAt && _random.NextDouble() < (prior == DuelPhase.Swing ? 0.46 : 0.34))
            {
                d.NextCrouchAt = now + RandF(1.55f, 2.2f);
                float crouchSide = ChooseDuelSide(d, motion, forward, true);
                started = TryStartCrouchLead(slot, pawn, d, point, forward, crouchSide, now);
            }
            if (!started)
                started = TryStartStandingDuelLeg(slot, pawn, d, point, forward, side, now, preferFire, sensitive,
                              mobile, takeCover, allowAdvance && !defendingSite, distance)
                    || TryStartStandingDuelLeg(slot, pawn, d, point, forward, -side, now, prior != DuelPhase.CrouchPeek && !preferFire, sensitive,
                              mobile, takeCover, allowAdvance && !defendingSite, distance, compact: true);
            if (!started)
            {
                // Even a narrow retreat can work. If every direction is shut, let the navmesh take over.
                ReleaseDuel(slot, d);
                KeepDuelStanding(slot, d);
                d.RetryAt = now + 0.095f;
            }
        }

        if (d.Phase != DuelPhase.None && !MaintainDuelMove(slot, pawn, d, now))
        {
            ReleaseDuel(slot, d);
            d.RetryAt = now + 0.08f;
        }
        bool runningShot = runGun && d.Phase == DuelPhase.RunGun;
        if (d.Phase == DuelPhase.CrouchPeek && DuelDuckAmount(pawn) >= 0.65f)
            fireSpeed = MathF.Max(fireSpeed, 100f);
        bool highSpeed = motion.LengthSquared() > fireSpeed * fireSpeed;
        GateDuelFire(slot, d, !runningShot && highSpeed);
        return true;
    }

    // Deliberate advances must fit the current assignment and the defender's position.
    private bool DuelAdvanceFitsTactics(CCSPlayerController player, CCSPlayerPawn pawn, Vector2 towardEnemy, float now)
    {
        int slot = player.Slot;
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        Vector? goal = null;
        if (TradeSupportActive(slot, pawn, now)) goal = pawn.Bot?.GoalPosition;
        else if (HasDefusalRetakeRoute(slot) && _retakeRoutes.TryGetValue(slot, out var retake)) goal = retake.Goal;
        else if (_rolePlan.TryGetValue(slot, out var plan))
        {
            if (IsPostplantRoute(plan) || plan.Role is "ct-bombcamp" or "ct-screen") return false;
            if (plan.GatherLeg == plan.Leg && plan.GatherLeg >= 0 && !GroupReleased(plan.Group)) return false;
            if (plan.Leg < plan.Legs.Count) goal = plan.Legs[plan.Leg].goal;
        }
        if (goal != null)
        {
            var toGoal = new Vector2(goal.X - origin.X, goal.Y - origin.Y);
            if (toGoal.LengthSquared() > 250f * 250f && Vector2.Dot(Vector2.Normalize(toGoal), towardEnemy) < -0.15f)
                return false;
        }
        if (player.TeamNum == (byte)CsTeam.CounterTerrorist && !_defusalBombPlanted && goal == null
            && _ctHomeSite.TryGetValue(slot, out int home) && home is >= 0 and <= 1)
        {
            var site = SiteCenter(home);
            var fromSite = new Vector2(origin.X - site.X, origin.Y - site.Y);
            if ((fromSite + towardEnemy * 180f).LengthSquared() > 550f * 550f) return false;
        }
        if (_defusalBombPlanted && player.TeamNum == (byte)CsTeam.Terrorist && goal == null) return false;
        return true;
    }

    // Identify a real front player from nearby alive teammates, including humans.
    // Only attacking or retaking orders qualify for the elevated SMG probability.
    private bool IsDuelEntry(CCSPlayerController player, CCSPlayerPawn pawn, Vector2 towardEnemy, float now)
    {
        if (pawn.AbsOrigin == null || TradeSupportActive(player.Slot, pawn, now)) return false;
        if (player.TeamNum == (byte)CsTeam.Terrorist && !_defusalBombPlanted)
        {
            _rolePlan.TryGetValue(player.Slot, out var plan);
            if (plan != null && (KeepOwnPlan(plan) || plan.Role.StartsWith("fake")
                || plan.Role is "eco-steal" or "lurker" or "lurk-cut" or "t-wave-support")) return false;
            Vector? goal = plan != null && plan.Leg < plan.Legs.Count ? plan.Legs[plan.Leg].goal
                : _tExecuted && _tExecSite >= 0 ? SiteCenter(_tExecSite) : null;
            if (goal == null) return false;
            var toGoal = new Vector2(goal.X - pawn.AbsOrigin.X, goal.Y - pawn.AbsOrigin.Y);
            if (toGoal.LengthSquared() < 160f * 160f || Vector2.Dot(Vector2.Normalize(toGoal), towardEnemy) < 0.20f)
                return false;
        }
        else if (player.TeamNum != (byte)CsTeam.CounterTerrorist || !_defusalBombPlanted
                 || !HasDefusalRetakeRoute(player.Slot)) return false;

        bool hasNearbyTeammate = false;
        var counted = new HashSet<uint> { pawn.EntityHandle.Raw };
        var lateral = DuelLeft(towardEnemy);
        foreach (var teammate in GetMatchPlayers())
        {
            if (teammate.Slot == player.Slot || teammate.TeamNum != player.TeamNum) continue;
            var other = BombPlayerPawn(teammate);
            if (other?.AbsOrigin == null || !counted.Add(other.EntityHandle.Raw)
                || MathF.Abs(other.AbsOrigin.Z - pawn.AbsOrigin.Z) > 130f) continue;
            var delta = new Vector2(other.AbsOrigin.X - pawn.AbsOrigin.X, other.AbsOrigin.Y - pawn.AbsOrigin.Y);
            if (delta.LengthSquared() > 1000f * 1000f || MathF.Abs(Vector2.Dot(delta, lateral)) > 550f) continue;
            hasNearbyTeammate = true;
            float ahead = Vector2.Dot(delta, towardEnemy);
            if (ahead > 70f || (ahead >= -70f && teammate.Slot < player.Slot)) return false;
        }
        return hasNearbyTeammate;
    }

    private bool TryStartRunGunLeg(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point,
        Vector2 forward, float side, float distance, bool allowAdvance, float now)
    {
        float along = allowAdvance && distance > 170f ? (d.EntryRunGun ? 0.78f : 0.58f) : -0.35f;
        var lateral = DuelLeft(forward) * side;
        var direction = lateral + forward * along;
        float clearance = d.EntryRunGun ? 80f : 60f;
        if (!DuelRayClear(pawn, direction, clearance))
        {
            side = -side;
            direction = -lateral + forward * along;
            if (!DuelRayClear(pawn, direction, clearance))
            {
                // Corners or teammates may block both advancing shoulders.
                // Fall back to a diagonal retreat rather than running into them.
                direction = -lateral - forward * 0.50f;
                if (!DuelRayClear(pawn, direction, 42f))
                {
                    side = -side;
                    direction = lateral - forward * 0.50f;
                    if (!DuelRayClear(pawn, direction, 42f)) return false;
                }
            }
        }
        StopDuelPose(slot, d);
        KeepDuelStanding(slot, d);
        float duration = d.EntryRunGun ? RandF(0.95f, 1.30f) : RandF(0.68f, 1.00f);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.RunGun, point, Vector2.Normalize(direction), 1f, duration, now))
            return false;
        if (d.Side != 0f && side != d.Side) _duelSwitch++;
        d.Side = side;
        d.BurstShots = 0;
        return true;
    }

    private bool TryStartRifleWide(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point,
        Vector2 forward, float side, float now)
    {
        var direction = DuelLeft(forward) * side;
        if (!DuelRayClear(pawn, direction, 115f))
        {
            side = -side;
            direction = -direction;
            if (!DuelRayClear(pawn, direction, 115f))
            {
                if (!DuelRayClear(pawn, direction, 72f))
                {
                    side = -side;
                    direction = -direction;
                    if (!DuelRayClear(pawn, direction, 72f)) return false;
                }
            }
        }
        StopDuelPose(slot, d);
        KeepDuelStanding(slot, d);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.RifleWide, point, direction, 1f, RandF(0.60f, 0.80f), now)) return false;
        d.Side = side;
        d.BurstShots = 0;
        _duelWide++;
        return true;
    }

    private void UpdateReloadEscape(int slot, int idx, CCSPlayerPawn pawn, CCSBot bot, DuelState d, Vector2 point,
        Vector2 motion, Vector2 forward, Vector? target, float now)
    {
        bool grounded = DuelGrounded(pawn);
        if (d.Phase == DuelPhase.ReloadEscape && now < d.Until
            && (!grounded || DuelLegCanContinue(pawn, d, point, motion, now)))
        {
            MaintainDuelMove(slot, pawn, d, now);
            if (grounded && d.ReloadJumpPlanned && !d.ReloadJumped
                && now - d.StartedAt >= 0.12f && Vector2.Dot(motion, d.Direction) >= 70f)
            {
                _jumpAllowUntil[idx] = now + 0.5f;
                ReleaseJumpGate(slot);
                try { d.ReloadJump = BotControllerBridge.InjectUsercmd(_botController!, slot, JumpButtonMask, 110); }
                catch { d.ReloadJump = 0; }
                d.ReloadJumped = true;
                d.NextReloadJumpAt = now + RandF(1.2f, 1.8f);
                if (d.ReloadJump > 0 && _random.NextDouble() < 0.35)
                {
                    float yaw = (float)NormalizeAngleDeg(pawn.EyeAngles.Y + d.Side * RandF(35f, 60f));
                    try
                    {
                        BriefNativeLook(pawn, bot, yaw, RandF(0.25f, 0.4f), now);
                    }
                    catch { }
                }
                if (d.ReloadDuckPlanned && d.ReloadJump > 0)
                {
                    ReleaseDuelStand(slot, d);
                    try { d.Duck = BotControllerBridge.InjectUsercmd(_botController!, slot, DuelDuckButton, 430); }
                    catch { d.Duck = 0; }
                }
            }
            return;
        }
        if (!grounded) return;

        float side = ChooseDuelSide(d, motion, forward, true);
        var left = DuelLeft(forward);
        var directions = new[]
        {
            Vector2.Normalize(left * side - forward * 0.45f),
            Vector2.Normalize(-left * side - forward * 0.45f),
            -forward,
            left * side,
            -left * side
        };
        Vector2 chosen = Vector2.Zero;
        bool cover = false;
        foreach (var direction in directions)
        {
            if (!DuelRayClear(pawn, direction, 105f)) continue;
            if (chosen == Vector2.Zero) chosen = direction;
            if (target == null || !ReloadRouteHasCover(pawn, target, direction, 100f)) continue;
            chosen = direction;
            cover = true;
            break;
        }
        if (chosen == Vector2.Zero)
        {
            ReleaseDuel(slot, d);
            return;
        }
        ReleaseDuel(slot, d);
        KeepDuelStanding(slot, d);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.ReloadEscape, point, chosen, 0.95f,
                cover ? 0.65f : RandF(0.55f, 0.8f), now)) return;
        d.Side = Vector2.Dot(chosen, left) >= 0 ? 1f : -1f;
        d.ReloadJumpPlanned = target != null && !cover && now >= d.NextReloadJumpAt && _random.NextDouble() < 0.65;
        d.ReloadDuckPlanned = d.ReloadJumpPlanned && _random.NextDouble() < 0.50;
        d.ReloadJumped = false;
    }

    private static bool ReloadRouteHasCover(CCSPlayerPawn pawn, Vector enemy, Vector2 direction, float distance)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        try
        {
            var start = new Vector(origin.X + direction.X * distance, origin.Y + direction.Y * distance,
                origin.Z + pawn.ViewOffset.Z);
            var end = new Vector(enemy.X, enemy.Y, enemy.Z + 55f);
            var hit = Trace.TraceEndShape(start, end, pawn, new TraceOptions { InteractsWith = Masks.SolidBrushOnly });
            return hit.Fraction < 0.97f;
        }
        catch { return false; }
    }
    // Carry the incoming stride into the duel before a low-speed firing step can brake it.
    private bool TryStartContactStep(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point, Vector2 motion,
        Vector2 forward, float side, bool takeCover, bool allowAdvance, float now)
    {
        float speed = motion.Length();
        if (!float.IsFinite(speed) || speed < 36f) return false;

        var incoming = motion / speed;
        float approach = Vector2.Dot(incoming, forward);
        if ((takeCover || !allowAdvance) && approach > 0.20f) return false;
        var lateral = DuelLeft(forward) * side;
        var direction = Vector2.Normalize(incoming * 0.65f + lateral * 0.35f);
        float clearance = Math.Clamp(13f + speed * 0.12f, 22f, 44f);
        if (!DuelRayClear(pawn, direction, clearance))
        {
            // At a narrow corner, keep the existing line if there is still room on it.
            if (!DuelRayClear(pawn, incoming, clearance)) return false;
            direction = incoming;
        }

        StopDuelPose(slot, d);
        KeepDuelStanding(slot, d);
        float strength = Math.Clamp(speed / 225f, 0.42f, 0.96f);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.ContactStep, point, direction, strength, 0.13f, now)) return false;
        d.Side = side;
        d.BurstShots = 0;
        _duelContactSteps++;
        return true;
    }

    private float ChooseDuelSide(DuelState d, Vector2 motion, Vector2 forward, bool keepMomentum)
    {
        float lateral = Vector2.Dot(motion, DuelLeft(forward));
        if (keepMomentum && MathF.Abs(lateral) > 16f) return MathF.Sign(lateral);
        if (d.Side != 0f) return -d.Side;
        return _random.Next(2) == 0 ? -1f : 1f;
    }

    private bool TryStartStandingDuelLeg(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point, Vector2 forward,
        float side, float now, bool fireStep, bool sensitive, bool mobile, bool takeCover, bool allowAdvance, float distance, bool compact = false)
    {
        float along = takeCover ? -0.56f : allowAdvance && mobile && distance > 330f ? 0.36f : 0f;
        bool wide = !compact && !fireStep && !sensitive && _random.NextDouble() < 0.42;
        float clearance = fireStep ? 34f : wide ? 98f : compact ? 23f : 51f;
        float strength = fireStep ? sensitive ? 0.15f : mobile ? 0.50f : 0.26f
            : wide ? 0.95f : sensitive ? 0.66f : 0.78f;
        float duration = fireStep ? RandF(0.20f, 0.29f) : wide ? RandF(0.44f, 0.59f) : RandF(0.28f, 0.43f);
        var lateral = DuelLeft(forward) * side;
        var offset = lateral + forward * along;
        if (!DuelRayClear(pawn, offset, clearance))
        {
            wide = false;
            clearance = fireStep || compact ? 27f : 46f;
            offset = lateral - forward * 0.48f;
            if (!DuelRayClear(pawn, offset, clearance))
            {
                side = -side;
                offset = -lateral + forward * along;
                if (!DuelRayClear(pawn, offset, clearance))
                {
                    offset = -forward;
                    if (!DuelRayClear(pawn, offset, 23f))
                    {
                        if (!allowAdvance) return false;
                        offset = forward;
                        if (!DuelRayClear(pawn, offset, 23f)) return false;
                    }
                    side = 0f;
                }
            }
        }
        StopDuelPose(slot, d);
        KeepDuelStanding(slot, d);
        var phase = fireStep ? DuelPhase.FireStep : DuelPhase.Swing;
        if (!BeginDuelLeg(slot, pawn, d, phase, point, Vector2.Normalize(offset), strength, duration, now)) return false;
        if (side != 0f)
        {
            if (d.Side != 0f && side != d.Side) { _duelSwitch++; d.PeekChanges++; }
            d.Side = side;
        }
        d.BurstShots = 0;
        d.BurstLimit = sensitive ? 1 : distance > 1450f ? _random.Next(2, 4) : _random.Next(3, 6);
        if (wide) _duelWide++;
        return true;
    }

    private bool TryStartCrouchLead(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point, Vector2 forward, float side, float now, bool wide = false)
    {
        var dir = DuelLeft(forward) * side;
        if (!DuelRayClear(pawn, dir, 92f))
        {
            dir = -dir; side = -side;
            if (!DuelRayClear(pawn, dir, 92f) && !DuelRayClear(pawn, dir, 65f))
            {
                dir = -dir; side = -side;
                if (!DuelRayClear(pawn, dir, 65f)) return false;
            }
        }
        StopDuelPose(slot, d);
        KeepDuelStanding(slot, d);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.CrouchLead, point, dir, wide ? 1f : 0.88f, wide ? 0.72f : 0.52f, now)) return false;
        if (d.Side != 0f && side != d.Side) { _duelSwitch++; d.PeekChanges++; }
        d.Side = side;
        return true;
    }

    private bool StartDuelCrouch(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point, float now)
    {
        if (_botController == null) return false;
        ReleaseDuelStand(slot, d);
        try { d.Duck = BotControllerBridge.InjectUsercmd(_botController, slot, DuelDuckButton, 3100); }
        catch { d.Duck = 0; }
        if (d.Duck <= 0) { KeepDuelStanding(slot, d); return false; }
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.CrouchPeek, point, d.Direction, 0.96f, d.WideCrouch ? 0.95f : 1.35f, now))
        {
            StopDuelPose(slot, d);
            KeepDuelStanding(slot, d);
            return false;
        }
        d.CrouchLeg = 1;
        d.FullyCrouchedAt = -1f;
        d.BurstShots = 0;
        d.NextCrouchAt = now + RandF(2.25f, 3.0f);
        _duelCrouch++;
        return true;
    }

    private bool TryTurnDuelCrouch(int slot, CCSPlayerPawn pawn, DuelState d, Vector2 point, float now)
    {
        var opposite = -d.Direction;
        if (!DuelRayClear(pawn, opposite, 54f)) return false;
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.CrouchPeek, point, opposite, 0.96f, 1.32f, now)) return false;
        d.CrouchLeg = 2;
        d.FullyCrouchedAt = -1f;
        d.Side = -d.Side;
        d.BurstShots = 0;
        _duelSwitch++;
        _duelDuckTurns++;
        return true;
    }

    private bool DuelLegCanContinue(CCSPlayerPawn pawn, DuelState d, Vector2 point, Vector2 motion, float now)
    {
        bool crouched = d.Phase == DuelPhase.CrouchPeek;
        if (Vector2.Dot(motion, d.Direction) > (crouched ? 13f : 10f)
            && Vector2.Dot(point - d.LastProgressPosition, d.Direction) >= (crouched ? 3.5f : 3f))
        {
            d.LastProgressPosition = point;
            d.LastProgressAt = now;
        }
        // A reversal takes a few physics ticks. After that, require real progress in the requested direction.
        float grace = crouched ? (d.CrouchLeg == 2 && now - d.StartedAt < 0.40f ? 0.42f : 0.32f)
            : d.Phase == DuelPhase.CrouchLead ? 0.34f
            : d.Phase == DuelPhase.RifleWide ? 0.30f
            : d.Phase == DuelPhase.RunGun ? 0.28f : 0.19f;
        if (now - d.LastProgressAt >= grace) return false;
        if (now < d.NextPathCheck) return true;
        d.NextPathCheck = now + 0.085f;
        float speed = MathF.Max(0f, Vector2.Dot(motion, d.Direction));
        float lookAhead = Math.Clamp(15f + speed * 0.10f, 17f, 36f);
        return DuelRayClear(pawn, d.Direction, lookAhead);
    }

    private bool TryDuelRecheck(int slot, CCSPlayerPawn pawn, DuelState d, CBasePlayerWeapon weapon, Vector2 point, float now)
    {
        float side = d.Side == 0f ? (_random.Next(2) == 0 ? -1f : 1f) : -d.Side;
        bool lowAmmo = weapon.Clip1 is >= 0 and <= 5 && !IsSniperWeapon(weapon.DesignerName);
        var forward = DuelFacing(pawn);
        var lateral = DuelLeft(forward) * side;
        Vector2 direction = lateral - forward * (lowAmmo ? 0.62f : 0.18f);
        if (!DuelRayClear(pawn, direction, 52f))
        {
            direction -= 2f * lateral;
            if (!DuelRayClear(pawn, direction, 45f))
            {
                direction = -forward;
                if (!DuelRayClear(pawn, direction, 29f)) return false;
            }
        }
        KeepDuelStanding(slot, d);
        if (!BeginDuelLeg(slot, pawn, d, DuelPhase.Recheck, point, Vector2.Normalize(direction), 0.73f, RandF(0.30f, 0.42f), now))
        {
            ReleaseDuelStand(slot, d);
            return false;
        }
        _duelKillChecks++;
        return true;
    }

    private bool BeginDuelLeg(int slot, CCSPlayerPawn pawn, DuelState d, DuelPhase phase, Vector2 point,
        Vector2 direction, float strength, float duration, float now)
    {
        d.Direction = direction;
        d.Strength = strength;
        d.Until = now + duration;
        d.NextInputAt = now;
        if (!MaintainDuelMove(slot, pawn, d, now)) return false;
        d.Phase = phase;
        d.Start = d.LastProgressPosition = point;
        d.StartedAt = d.LastProgressAt = now;
        d.NextPathCheck = now + 0.075f;
        _duelMoves++;
        return true;
    }

    private bool MaintainDuelMove(int slot, CCSPlayerPawn pawn, DuelState d, float now)
    {
        if (now < d.NextInputAt) return true;
        d.NextInputAt = now + 0.040f;
        var forward = DuelFacing(pawn);
        float forwardInput = Vector2.Dot(d.Direction, forward) * d.Strength;
        float leftInput = Vector2.Dot(d.Direction, DuelLeft(forward)) * d.Strength;
        return SetMove(slot, MoveOwner.Combat, forwardInput, leftInput, d.Until + 0.035f);
    }

    private void KeepDuelStanding(int slot, DuelState d)
    {
        if (_botController == null || d.Stand > 0) return;
        try { d.Stand = BotControllerBridge.StartUsercmdSuppression(_botController, slot, DuelDuckButton); }
        catch { d.Stand = 0; }
    }

    private void ReleaseDuelStand(int slot, DuelState d)
    {
        if (d.Stand <= 0) return;
        try { if (_botController != null) BotControllerBridge.CancelUsercmdSuppression(_botController, slot, d.Stand); }
        catch { }
        d.Stand = 0;
    }

    private static float DuelDuckAmount(CCSPlayerPawn pawn)
    {
        var services = pawn.MovementServices;
        return services is { Handle: not 0 } ? services.As<CCSPlayer_MovementServices>().DuckAmount : 1f;
    }

    private static Vector2 DuelFacing(CCSPlayerPawn pawn)
    {
        float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        return new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
    }

    private static Vector2 DuelLeft(Vector2 forward) => new(-forward.Y, forward.X);

    private bool DuelRayClear(CCSPlayerPawn pawn, Vector2 direction, float distance)
    {
        var unit = Vector2.Normalize(direction);
        return IsDirectionClear(pawn, unit.X, unit.Y, distance);
    }

    private string[] DefusalDuelStatus() => new[]
    {
        $"[Smarter-Bot] duelMove={_duelMoves} wide={_duelWide} crouch={_duelCrouch} switchSides={_duelSwitch} bursts={_duelBursts}",
        $"[Smarter-Bot] contactSteps={_duelContactSteps} duckTurns={_duelDuckTurns} duckAborts={_duelDuckAborts} redirects={_duelRedirects}",
        $"[Smarter-Bot] killResume={_duelKillResume} killChecks={_duelKillChecks} rifleMoving={_duelRifleMoving} rifleStill={_duelRifleStill} fireGates={_duelFireGates}",
    };
}
