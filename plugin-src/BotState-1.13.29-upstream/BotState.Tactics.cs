using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Runtime.InteropServices;

namespace BotState;

public partial class BotState
{
    // CCSBot::SetState(CCSBot*, BotState*) entry (Windows, unique).
    private const string SetStateSig =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 48 8B 02 48 8B D9 48 8B CA 48 8B FA FF 50 18 48 8B 8B";
    // Inlined CCSBot::MoveTo in the bot_goto_* console commands:
    // lea rdx,[bot+moveToState]; store goal xy/z; store route type 1; call SetState.
    private const string MoveToCallSig =
        "F2 0F 10 47 0C 48 8D 91 ? ? ? ? 8B 47 14 F2 0F 11 81 ? ? ? ? 89 81 ? ? ? ? C7 81 ? ? ? ? 01 00 00 00 E8";

    private MemoryFunctionVoid<nint, nint>? _setState;
    private int _moveToStateOff, _moveToGoalOff, _moveToRouteOff;
    private bool _moveToReady;

    private const float SiteRadius = 1100f;
    private Vector? _siteA, _siteB;
    private bool _sitesResolved;

    private readonly Dictionary<int, float> _rotateUntil = new();
    private readonly Dictionary<int, int> _rotateSite = new();
    private readonly Dictionary<int, float> _rotateIssued = new();
    private readonly Dictionary<int, int> _rotateReissues = new();
    private readonly float[] _siteTriggerCooldown = new float[2];
    private readonly float[] _lastBombRotationCheck = { -99f, -99f };
    private readonly Dictionary<int, float> _siteContacts = new(); // site*128+enemySlot -> time
    private readonly Dictionary<int, float> _tradeCooldown = new(); // team -> time
    private float _nextTacticsScan;
    private readonly Dictionary<int, (uint Pawn, float Until)> _tradeSupport = new();
    private readonly float[] _humanPressureCooldown = new float[2];
    private int _tradeSupportOrders, _humanPressureOrders;
    private readonly Dictionary<int, float> _tradeFireCooldown = new();
    private readonly Dictionary<int, float> _formationHurtCooldown = new();

    private bool TradeSupportActive(int slot, CCSPlayerPawn pawn, float now)
    {
        if (!_tradeSupport.TryGetValue(slot, out var support)) return false;
        if (support.Pawn == pawn.EntityHandle.Raw && now < support.Until) return true;
        _tradeSupport.Remove(slot);
        return false;
    }

    // Resolves the native MoveTo path once; disabled on any mismatch.
    private void InitBotMoveTo()
    {
        _moveToReady = false;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            nint call = NativeAPI.FindSignature(Addresses.ServerPath, MoveToCallSig);
            nint setState = NativeAPI.FindSignature(Addresses.ServerPath, SetStateSig);
            if (call == nint.Zero || setState == nint.Zero) return;

            int stateOff = Marshal.ReadInt32(call + 8);
            int goalOff = Marshal.ReadInt32(call + 19);
            int goalZOff = Marshal.ReadInt32(call + 25);
            int routeOff = Marshal.ReadInt32(call + 31);
            nint target = call + 44 + Marshal.ReadInt32(call + 40);

            bool sane = target == setState
                && goalZOff == goalOff + 8 && routeOff == goalOff + 12
                && goalOff > stateOff && goalOff - stateOff <= 64
                && stateOff > 0 && stateOff < 0x4000;
            if (!sane)
            {
                Console.WriteLine("[Smarter-Bot] MoveTo layout mismatch; tactics rotations disabled");
                return;
            }
            _moveToStateOff = stateOff;
            _moveToGoalOff = goalOff;
            _moveToRouteOff = routeOff;
            _setState = new MemoryFunctionVoid<nint, nint>(SetStateSig);
            _moveToReady = true;
        }
        catch (Exception e)
        {
            Console.WriteLine("[Smarter-Bot] MoveTo init failed: " + e.Message);
        }
    }

    // Native equivalent of CCSBot::MoveTo(goal, route). route 1 = fastest.
    private bool BotMoveTo(CCSBot bot, Vector goal, int route = 1)
    {
        if (!_moveToReady || _setState == null) return false;
        try
        {
            nint b = bot.Handle;
            if (b == nint.Zero) return false;
            float[] g = { goal.X, goal.Y, goal.Z };
            Marshal.Copy(g, 0, b + _moveToGoalOff, 3);
            Marshal.WriteInt32(b + _moveToRouteOff, route);
            _setState.Invoke(b, b + _moveToStateOff);
            return true;
        }
        catch { return false; }
    }

    private static void SetHurry(CCSBot bot, float seconds)
    {
        CountdownTimer t = bot.HurryTimer;
        t.Duration = seconds;
        t.Timestamp = Server.CurrentTime + seconds;
        t.Timescale = 1f;
        ref bool isRunning = ref bot.IsRunning;
        isRunning = true;
    }
    // Classic bomb-defusal rounds only (not deathmatch / rush / arms race).
    private bool TacticsEnabled()
    {
        if (!_moveToReady || _isFreezeTime || IsDeathmatch()) return false;
        if (ConVar.Find("game_type")?.GetPrimitiveValue<int>() != 0) return false;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        if (rules == null || rules.WarmupPeriod || rules.FreezePeriod || rules.BombPlanted) return false;
        ResolveSites();
        return _siteA != null && _siteB != null;
    }

    private void ResolveSites()
    {
        if (_sitesResolved) return;
        _sitesResolved = true;
        _siteA = _siteB = null;
        try
        {
            var res = Utilities.FindAllEntitiesByDesignerName<CCSPlayerResource>("cs_player_manager").FirstOrDefault();
            if (res != null)
            {
                var a = res.BombsiteCenterA; var b = res.BombsiteCenterB;
                if (a != null && b != null && (a.X != 0f || a.Y != 0f) && (b.X != 0f || b.Y != 0f))
                {
                    _siteA = new Vector(a.X, a.Y, a.Z);
                    _siteB = new Vector(b.X, b.Y, b.Z);
                    return;
                }
            }
            // Fallback: centre of the func_bomb_target trigger bounds.
            foreach (var t in Utilities.FindAllEntitiesByDesignerName<CBombTarget>("func_bomb_target"))
            {
                var c = t.Collision; var o = t.AbsOrigin;
                if (c == null || o == null) continue;
                var v = new Vector(o.X + (c.Mins.X + c.Maxs.X) * 0.5f,
                                   o.Y + (c.Mins.Y + c.Maxs.Y) * 0.5f,
                                   o.Z + c.Mins.Z + 16f);
                if (t.IsBombSiteB) _siteB = v; else _siteA = v;
            }
        }
        catch { _siteA = _siteB = null; }
    }

    private Vector SiteCenter(int site) => site == 0 ? _siteA! : _siteB!;

    private static float Dist2D(Vector a, Vector b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // 0 = A, 1 = B, -1 = neither (mid / spawn / rotation path).
    private int SiteOf(Vector pos, float radius = SiteRadius)
    {
        if (_siteA == null || _siteB == null) return -1;
        float da = Dist2D(pos, _siteA), db = Dist2D(pos, _siteB);
        if (IsDefusalCombatEnabled())
        {
            // A and B can overlap in plan view (Nuke); account for floor height.
            da += MathF.Abs(pos.Z - _siteA.Z);
            db += MathF.Abs(pos.Z - _siteB.Z);
        }
        if (da <= db && da < radius) return 0;
        if (db < da && db < radius) return 1;
        return -1;
    }

    private static bool IsAliveBot(CCSPlayerController? p, out CCSPlayerPawn pawn, out CCSBot bot)
    {
        pawn = null!; bot = null!;
        if (p == null || !p.IsValid || !p.IsBot || p.HasBeenControlledByPlayerThisRound) return false;
        var pw = p.PlayerPawn.Value;
        if (pw == null || !pw.IsValid || pw.LifeState != (byte)LifeState_t.LIFE_ALIVE || pw.AbsOrigin == null) return false;
        var b = pw.Bot;
        if (b == null) return false;
        pawn = pw; bot = b;
        return true;
    }

    // Death-driven tactics: CT losses on a site pull rotators; T losses pull a trade.
    private void HandleTacticsOnDeath(CCSPlayerController? victim, CCSPlayerController? attacker)
    {
        if (victim == null || !victim.IsValid) return;
        ResetStrafeForSlot(victim.Slot);
        ResetKnifeRunForSlot(victim.Slot);
        ResetSmokeForSlot(victim.Slot);
        _rolePlan.Remove(victim.Slot);
        _rotateUntil.Remove(victim.Slot);
        _tradeSupport.Remove(victim.Slot);
        if (!IsDefusalCombatEnabled() && !TacticsEnabled()) return;
        var origin = victim.PlayerPawn.Value?.AbsOrigin;
        if (origin == null) return;
        var deathPos = new Vector(origin.X, origin.Y, origin.Z);
        byte team = victim.TeamNum;
        bool enemyKill = attacker is { IsValid: true } && attacker.TeamNum is 2 or 3 && attacker.TeamNum != team;
        int victimSlot = victim.Slot;
        bool humanVictim = !victim.IsBot || victim.ControllingBot || victim.HasBeenControlledByPlayerThisRound;
        Server.NextFrame(() =>
        {
            try
            {
                if (IsDefusalCombatEnabled())
                {
                    if (!enemyKill) return;
                    TriggerDefusalTrade(deathPos, team, victimSlot, true);
                    if (!_defusalBombPlanted && team == (byte)CsTeam.CounterTerrorist && TacticsEnabled())
                    {
                        int site = SiteOf(deathPos, 1300f);
                        if (site < 0 && humanVictim) site = SiteOf(deathPos, 1800f);
                        if (site >= 0)
                        {
                            RequestLossSupport(site, victimSlot, Server.CurrentTime, humanVictim);
                        }
                    }
                    return;
                }
                if (!TacticsEnabled()) return;
                if (team == (byte)CsTeam.CounterTerrorist)
                {
                    int site = SiteOf(deathPos, 1300f);
                    if (site >= 0) TriggerRotate(site, heavy: true);
                }
                else if (team == (byte)CsTeam.Terrorist) TriggerTrade(deathPos, team);
            }
            catch (Exception e) { Console.WriteLine("[Smarter-Bot] tactics error: " + e.Message); }
        });
    }
    private void NoteDefusalTeamHurt(CCSPlayerController victim, CCSPlayerController attacker)
    {
        if (!IsDefusalCombatEnabled() || !_moveToReady || victim.TeamNum is not (2 or 3)
            || attacker.TeamNum is not (2 or 3) || victim.TeamNum == attacker.TeamNum) return;
        var origin = victim.PlayerPawn?.Value?.AbsOrigin;
        if (origin == null) return;
        var position = new Vector(origin.X, origin.Y, origin.Z);
        int slot = victim.Slot;
        byte team = victim.TeamNum;
        bool human = !victim.IsBot || victim.ControllingBot || victim.HasBeenControlledByPlayerThisRound;
        var attackerOrigin = attacker.PlayerPawn.Value?.AbsOrigin;
        bool throughSmoke = attackerOrigin != null && SegmentThroughSmoke(position, attackerOrigin, SmokeEdge);
        Server.NextFrame(() =>
        {
            if (!IsDefusalCombatEnabled()) return;
            float now = Server.CurrentTime;
            TryEvadeUnseenDamage(slot, throughSmoke, now);
            TriggerDefusalTrade(position, team, slot, false);
            if (team != (byte)CsTeam.CounterTerrorist || _defusalBombPlanted || !TacticsEnabled()) return;
            int site = SiteOf(position, 1300f);
            if (site < 0 || now < _humanPressureCooldown[site]) return;
            _humanPressureCooldown[site] = now + 5f;
            TriggerDefusalPreplantRotate(site, false);
            if (human) _humanPressureOrders++;
        });
    }

    // Fire can prompt support only when a teammate has an actual visible contact nearby.
    private void NoteDefusalTeamFire(CCSPlayerController shooter)
    {
        if (!IsDefusalCombatEnabled() || !_moveToReady || shooter.TeamNum is not (2 or 3)) return;
        float now = Server.CurrentTime;
        if (now < _tradeCooldown.GetValueOrDefault(shooter.TeamNum)
            || now < _tradeFireCooldown.GetValueOrDefault(shooter.Slot)) return;
        _tradeFireCooldown[shooter.Slot] = now + 1.4f;
        var origin = shooter.PlayerPawn?.Value?.AbsOrigin;
        if (origin == null) return;
        bool contact = false;
        foreach (var player in GetMatchPlayers())
        {
            if (player.TeamNum != shooter.TeamNum || !IsAliveBot(player, out var pawn, out var bot)
                || pawn.AbsOrigin == null || Dist2D(pawn.AbsOrigin, origin) > 900f || !bot.IsEnemyVisible) continue;
            var enemy = bot.Enemy.Value;
            if (enemy is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE } && enemy.TeamNum != shooter.TeamNum)
            {
                contact = true;
                break;
            }
        }
        if (!contact) return;
        var position = new Vector(origin.X, origin.Y, origin.Z);
        byte team = shooter.TeamNum;
        int slot = shooter.Slot;
        Server.NextFrame(() => { if (IsDefusalCombatEnabled()) TriggerDefusalTrade(position, team, slot, false); });
    }

    // Teammate position is the only shared target; each helper finds enemies for itself.
    private void TriggerDefusalTrade(Vector position, byte team, int victimSlot, bool casualty)
    {
        float now = Server.CurrentTime;
        if (!_moveToReady || (!casualty && now < _tradeCooldown.GetValueOrDefault(team))) return;
        var candidates = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn, CCSBot Bot, float Distance, bool Active)>();
        foreach (var player in GetMatchPlayers())
        {
            if (player.Slot == victimSlot || player.TeamNum != team || !IsAliveBot(player, out var pawn, out var bot)
                || pawn.AbsOrigin == null || RetakeBusy(player, pawn, bot, now)) continue;
            bool active = TradeSupportActive(player.Slot, pawn, now);
            if (active && !casualty) continue;
            if (_defusalBombPlanted && team == (byte)CsTeam.CounterTerrorist
                && (player.Slot == _retakeLead || _retakeBomb == 0)) continue;
            if (!GetReloadInterruptWeapon(pawn, out _)) continue;
            float distance = RetakeDistance(pawn.AbsOrigin, position);
            if (distance < 85f || distance > (casualty ? 1150f : 950f)) continue;
            candidates.Add((player, pawn, bot, distance, active));
        }
        foreach (var candidate in candidates.OrderBy(c => c.Distance - (c.Active ? 200f : 0f)))
        {
            if (!casualty && _random.NextDouble() >= 0.88) continue;
            var origin = candidate.Pawn.AbsOrigin!;
            float dx = origin.X - position.X, dy = origin.Y - position.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1f) continue;
            float offset = casualty ? 70f : 115f;
            var goal = new Vector(position.X + dx / length * offset, position.Y + dy / length * offset, position.Z);
            ClearDefusalGuard(candidate.Player.Slot);
            CancelIdlePeek(candidate.Player.Slot);
            if (!BotMoveTo(candidate.Bot, goal, 1)) continue;
            float duration = Math.Clamp(candidate.Distance / 210f + 1.5f, 3f, 6f);
            SetHurry(candidate.Bot, duration);
            if (_rolePlan.TryGetValue(candidate.Player.Slot, out var oldPlan)) oldPlan.PausedAt = now;
            if (_retakeRoutes.TryGetValue(candidate.Player.Slot, out var oldRoute)) oldRoute.Paused = true;
            _tradeSupport[candidate.Player.Slot] = (candidate.Pawn.EntityHandle.Raw, now + duration);
            _tradeCooldown[team] = now + (casualty ? 1.5f : 2.5f);
            _tradeSupportOrders++;
            break;
        }
    }    private static bool HasC4(CCSPlayerPawn pawn)
    {
        var ws = pawn.WeaponServices;
        if (ws == null) return false;
        foreach (var h in ws.MyWeapons)
            if (h.Value?.DesignerName == "weapon_c4") return true;
        return false;
    }

    // Up to two nearby idle teammates push the spot where a teammate just died.
    private void TriggerTrade(Vector deathPos, byte team)
    {
        float now = Server.CurrentTime;
        if (now < _tradeCooldown.GetValueOrDefault(team)) return;
        var near = new List<(CCSBot bot, float d)>();
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != team || !IsAliveBot(p, out var pawn, out var bot)) continue;
            if (bot.IsAttacking || bot.IsEnemyVisible) continue;
            if (HasC4(pawn)) continue;
            float d = Dist2D(pawn.AbsOrigin!, deathPos);
            if (d > 120f && d < 900f) near.Add((bot, d));
        }
        if (near.Count == 0) return;
        near.Sort((a, b) => a.d.CompareTo(b.d));
        int sent = 0;
        foreach (var n in near)
        {
            if (sent >= 2 || _random.NextDouble() > 0.6) continue;
            if (BotMoveTo(n.bot, deathPos)) { SetHurry(n.bot, 4f); sent++; }
        }
        if (sent > 0) _tradeCooldown[team] = now + 5f;
    }

    // Twice a second: CT sightings of 2+ enemies on a site trigger an early rotate,
    // and rotators that drifted away are sent back (at most twice).
    private void UpdateTacticsScan(float now)
    {
        if (now < _nextTacticsScan) return;
        _nextTacticsScan = now + 0.5f;
        if (!TacticsEnabled()) return;

        int[] distinct = new int[2];
        foreach (var p in GetMatchPlayers())
        {
            if (p == null || p.TeamNum != (byte)CsTeam.CounterTerrorist || !IsAliveBot(p, out var pawn, out var bot)) continue;

            if (bot.IsEnemyVisible)
            {
                var e = bot.Enemy.Value;
                if (e != null && e.IsValid && e.AbsOrigin != null)
                {
                    int site = SiteOf(e.AbsOrigin, 1300f);
                    if (site >= 0)
                    {
                        int key = site * 128 + (int)(e.OriginalController.Value?.Slot ?? (int)e.Index % 128);
                        _siteContacts[key] = now;
                    }
                    // Bomb carrier spotted near a site = the hit is confirmed.
                    int c4Site = SiteOf(e.AbsOrigin, 1800f);
                    if (c4Site >= 0 && HasC4(e))
                    {
                        _c4SeenAt[c4Site] = now;
                        if (now - _lastBombRotationCheck[c4Site] >= 2f)
                        {
                            _lastBombRotationCheck[c4Site] = now;
                            TriggerRotate(c4Site, heavy: true);
                        }
                    }
                }
            }

            if (_rotateUntil.GetValueOrDefault(p.Slot) > now && !bot.IsAttacking && !bot.IsEnemyVisible
                && !_rolePlan.ContainsKey(p.Slot))
            {
                int rs = _rotateSite.GetValueOrDefault(p.Slot, -1);
                if (rs >= 0 && now - _rotateIssued.GetValueOrDefault(p.Slot) > 5f
                    && _rotateReissues.GetValueOrDefault(p.Slot) < 2)
                {
                    var c = SiteCenter(rs);
                    var vx = pawn.AbsVelocity.X; var vy = pawn.AbsVelocity.Y;
                    bool idle = vx * vx + vy * vy < 25f;
                    if (Dist2D(pawn.AbsOrigin!, c) > 1500f && (idle || _random.NextDouble() < 0.3))
                    {
                        if (BotMoveTo(bot, c)) SetHurry(bot, 6f);
                        _rotateIssued[p.Slot] = now;
                        _rotateReissues[p.Slot] = _rotateReissues.GetValueOrDefault(p.Slot) + 1;
                    }
                }
            }
        }

        if (_siteContacts.Count == 0) return;
        foreach (var k in _siteContacts.Keys.ToList())
        {
            if (now - _siteContacts[k] > 5f) { _siteContacts.Remove(k); continue; }
            distinct[k / 128]++;
        }
        for (int s = 0; s < 2; s++)
            if (distinct[s] >= 2) TriggerRotate(s, heavy: distinct[s] >= 3);
    }

    private void ResetTacticsRoundState()
    {
        _rotateUntil.Clear();
        _rotateSite.Clear();
        _rotateIssued.Clear();
        _rotateReissues.Clear();
        _siteContacts.Clear();
        Array.Fill(_lastBombRotationCheck, -99f);
        _tradeCooldown.Clear();
        _tradeSupport.Clear();
        _tradeFireCooldown.Clear();
        _formationHurtCooldown.Clear();
        Array.Clear(_humanPressureCooldown);
        _siteTriggerCooldown[0] = _siteTriggerCooldown[1] = 0f;
        _nextTacticsScan = 0f;
        _sitesResolved = false; // map may have changed
    }
}
