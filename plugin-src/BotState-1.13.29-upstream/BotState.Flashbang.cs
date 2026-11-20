using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Commands;
using BotControllerApi;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

namespace BotState;

public partial class BotState
{
    private const string BotBlindWindowsSignature =
        "40 53 48 81 EC ? ? ? ? 0F 29 B4 24 ? ? ? ? 48 8D 15";
    private const string BotBlindLinuxSignature =
        "55 48 8D 35 ? ? ? ? B8 ? ? ? ? F3 0F 5A D2";

    // Flashbang avoidance
    private Vector? _scratchEye;
    private readonly List<CHandle<CBaseEntity>> _flashProjectiles = new();
    private readonly List<CHandle<CCSPlayerController>> _flashControllers = new();
    private bool _flashEntitiesDirty = true;
    private MemoryFunctionVoid<nint, float, float, float>? _botBlindFunction;

    private const float FlashFuseSeconds = 1.5f;        // CS2 flashbang fuse
    private const float FlashFovHorizDeg = 110f;        // bot horizontal cone (full angle)
    private const float FlashFovVertDeg = 90f;         // bot vertical cone (full angle)
    private readonly Dictionary<uint, float> _flashThrownAt = new();   // flash entindex -> server time first seen
    private readonly Dictionary<int, HashSet<uint>> _flashRolledByBot = new(); // bot idx  -> evaluated flashes

    // Awareness diagnostics are keyed by the projectile's entity index, like the game events.
    private struct FlashDecision
    {
        public float FirstSeen;
        public float LastSeen;
        public float DetonateAt;
        public bool Avoided;
    }
    private readonly Dictionary<(int bot, uint flash), FlashDecision> _flashDecisions = new();
    private readonly HashSet<(int bot, uint flash)> _flashRejectLogged = new();
    // While this window is open, later flashes are not rolled and cannot clear the effect for this bot.
    private readonly Dictionary<int, float> _flashBlindUntil = new();
    private readonly Dictionary<int, (uint Pawn, long Token, float Until)> _flashFireBlocks = new();

    // Debug logging (toggle with `css_botstate_flashdebug`)
    private bool _debugFlash = false;
    private int _statFlashUpgraded, _statFlashTurns, _statFlashBlinds, _statFlashFireInterrupted;

    // Toggles or explicitly sets flashbang decision diagnostics.
    [ConsoleCommand("css_botstate_flashdebug", "Toggle Smarter-Bot flashbang debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnFlashDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugFlash = arg == "1"
                       || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugFlash = !_debugFlash;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] flash debug = {_debugFlash}");
        Console.WriteLine($"[Smarter-Bot] flash debug = {_debugFlash}");
    }

    // Server stdout + every connected human's console. Use only for debug-gated lines
    // so we don't spam non-debug runs.
    private static void BroadcastDebug(string msg)
    {
        Console.WriteLine(msg);
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            p.PrintToConsole(msg);
        }
    }
    //---------------------------------------------------------------------------------------
    // The engine has already checked range, cover and the pawn's facing when it fires this event.
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;
        float duration = @event.BlindDuration;
        if (player == null || !float.IsFinite(duration) || duration <= 0f || !IsAliveBot(player, out var pawn, out _))
            return HookResult.Continue;
        int bidx = (int)player.Index;
        bool defusal = IsDefusalCombatEnabled();
        if (defusal && FlashViewControlledByOther(player.Slot, bidx)) return HookResult.Continue;

        float now = Server.CurrentTime;
        int entityId = @event.Entityid;
        FlashDecision matched = default;
        bool observed = entityId > 0 && _flashDecisions.Remove((bidx, (uint)entityId), out matched);
        if (entityId > 0) MarkFlashTurnDetonated((uint)entityId, now);

        _statFlashBlinds++;
        float until = now + duration;
        float previousUntil = _flashBlindUntil.GetValueOrDefault(bidx);
        if (until > previousUntil) _flashBlindUntil[bidx] = until;
        if (defusal) BlockFlashFire(player.Slot, pawn.EntityHandle.Raw, now, duration);
        else StopFlashTurn(bidx);

        // Keep a stronger existing response when another, weaker flash overlaps it.
        if (!defusal || until > previousUntil) StartBlindReaction(player, duration);

        if (_debugFlash)
        {
            string detail = observed ? $"noticed={(matched.LastSeen - matched.FirstSeen) * 1000f:F0}ms turnScheduled={matched.Avoided}" : "untracked";
            BroadcastDebug($"[Smarter-Bot/Flash] blind event bot={player.PlayerName} flash#{entityId} duration={duration:F2}s ({detail})");
        }
        return HookResult.Continue;
    }

    private HookResult OnFlashDetonate(EventFlashbangDetonate @event, GameEventInfo info)
    {
        if (@event.Entityid > 0) MarkFlashTurnDetonated((uint)@event.Entityid, Server.CurrentTime);
        return HookResult.Continue;
    }

    private void BlockFlashFire(int slot, uint pawn, float now, float exposure)
    {
        if (_botController == null) return;
        // Native bot handling reacts differently to a short hold. Briefly stop fire even for
        // a light exposure, while keeping the game's own flash duration and opacity intact.
        float until = now + Math.Clamp(exposure * 0.58f, 0.09f, 2.2f);
        if (_flashFireBlocks.TryGetValue(slot, out var block))
        {
            if (block.Pawn == pawn)
            {
                if (until > block.Until) _flashFireBlocks[slot] = (pawn, block.Token, until);
                _statFlashFireInterrupted++;
                return;
            }
            ReleaseFlashFire(slot);
        }
        try
        {
            long token = BotControllerBridge.StartUsercmdSuppression(_botController, slot, AttackButtonMask);
            if (token <= 0) return;
            _flashFireBlocks[slot] = (pawn, token, until);
            _statFlashFireInterrupted++;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Smarter-Bot] flash attack interruption unavailable");
        }
    }

    private void ReleaseFlashFire(int slot)
    {
        if (!_flashFireBlocks.Remove(slot, out var block) || _botController == null) return;
        try { BotControllerBridge.CancelUsercmdSuppression(_botController, slot, block.Token); }
        catch (Exception ex) { Logger.LogWarning(ex, "[Smarter-Bot] flash attack interruption cleanup failed"); }
    }

    private void ExpireFlashFireBlocks(float now)
    {
        if (_flashFireBlocks.Count == 0) return;
        List<int>? expired = null;
        foreach (var (slot, block) in _flashFireBlocks)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (now >= block.Until || !IsDefusalCombatEnabled() || player == null || !IsAliveBot(player, out var pawn, out _)
                || pawn.EntityHandle.Raw != block.Pawn || FlashViewControlledByOther(slot, (int)player.Index))
                (expired ??= new()).Add(slot);
        }
        if (expired != null)
            foreach (int slot in expired) ReleaseFlashFire(slot);
    }

    private void ResetFlashFireBlocks()
    {
        foreach (int slot in _flashFireBlocks.Keys.ToArray()) ReleaseFlashFire(slot);
    }

    // Installs the platform-specific CCSBot::Blind Pre Hook
    private void InstallBotBlindHook()
    {
        string? signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? BotBlindWindowsSignature
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? BotBlindLinuxSignature
                : null;

        if (signature == null)
        {
            Logger.LogWarning(
                "[Smarter-Bot] CCSBot::Blind hook is unavailable on this platform; using event fallback");
            return;
        }

        try
        {
            _botBlindFunction =
                new MemoryFunctionVoid<nint, float, float, float>(signature);
            _botBlindFunction.Hook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            _botBlindFunction = null;
            Logger.LogError(ex,
                "[Smarter-Bot] CCSBot::Blind hook unavailable; using event fallback");
        }
    }

    // Removes the CCSBot::Blind Pre Hook during plugin unload
    private void UninstallBotBlindHook()
    {
        if (_botBlindFunction == null) return;

        try
        {
            _botBlindFunction.Unhook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] Failed to remove CCSBot::Blind hook cleanly");
        }
        _botBlindFunction = null;
    }

    // Keep the legacy alpha adjustment outside defusal. Live defusal uses the applied blind event.
    private HookResult OnBotBlindPre(DynamicHook hook)
    {
        try
        {
            nint botAddress = hook.GetParam<nint>(0);
            if (botAddress == nint.Zero) return HookResult.Continue;

            CCSPlayerController? player = FindBotControllerByAddress(botAddress);
            if (player == null || player.HasBeenControlledByPlayerThisRound)
                return HookResult.Continue;

            float hold = hook.GetParam<float>(1);
            float fade = hook.GetParam<float>(2);
            float alpha = hook.GetParam<float>(3);
            bool upgraded = false;
            if (!IsDefusalCombatEnabled() && alpha < 255f && (alpha >= 140f || hold + fade >= 1.1f))
            {
                float k = 0.55f + 0.45f * Math.Clamp(alpha / 255f, 0f, 1f);
                hook.SetParam<float>(1, MathF.Max(0.35f, hold * k));
                hook.SetParam<float>(3, 255f);
                upgraded = true;
                _statFlashUpgraded++;
            }

            if (_debugFlash)
                BroadcastDebug(
                    $"[Smarter-Bot/Flash] native blind bot={player.PlayerName} hold={hold:F2}s fade={fade:F2}s alpha={alpha:F0}{(upgraded ? " -> upgraded to full" : "")}");

            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] CCSBot::Blind hook failed open");
            return HookResult.Continue;
        }
    }

    // Resolves a native CCSBot pointer back to its player controller
    private static CCSPlayerController? FindBotControllerByAddress(nint botAddress)
    {
        foreach (var player in GetMatchPlayers())
        {
            if (!player.IsValid || !player.IsBot) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var bot = pawn.Bot;
            if (bot != null && bot.Handle == botAddress) return player;
        }

        return null;
    }
    //---------------------------------------------------------------------------------------
    // Entity lifecycle notifications only invalidate discovery; positions and visibility
    // are still read every tick. Rebuild in native entity-list order to preserve roll order.
    private void InvalidateFlashEntities(CEntityInstance entity) => _flashEntitiesDirty = true;

    private void ClearFlashEntities()
    {
        _flashProjectiles.Clear();
        _flashControllers.Clear();
        _flashEntitiesDirty = true;
    }

    private void RefreshFlashEntities()
    {
        if (!_flashEntitiesDirty) return;

        _flashProjectiles.Clear();
        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("flashbang_projectile"))
        {
            if (entity.IsValid)
                _flashProjectiles.Add(new CHandle<CBaseEntity>(entity.EntityHandle.Raw));
        }
        _flashControllers.Clear();
        if (_flashProjectiles.Count > 0)
        {
            foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (controller.IsValid)
                    _flashControllers.Add(new CHandle<CCSPlayerController>(controller.EntityHandle.Raw));
            }
        }
        _flashEntitiesDirty = false;
    }

    // Pre-rolls avoidance from awareness of the projectile. The blind event handles exposure.
    private void ProcessFlashbangAvoidance()
    {
        float now = Server.CurrentTime;
        ExpireFlashFireBlocks(now);
        if (_scratchEye == null) return;

        RefreshFlashEntities();
        var live = new List<(uint idx, Vector pos, float detonateAt, byte team, nint thrower)>();
        foreach (var handle in _flashProjectiles)
        {
            var ent = handle.Value;
            if (ent == null || !ent.IsValid) continue;
            var pos = ent.AbsOrigin;
            if (pos == null) continue;

            uint eidx = ent.Index;
            bool isNew = !_flashThrownAt.ContainsKey(eidx);
            if (isNew)
            {
                _flashThrownAt[eidx] = now;
                if (_debugFlash)
                    BroadcastDebug($"[Smarter-Bot/Flash] new flash#{eidx} at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) fuse={FlashFuseSeconds:F2}s");
            }
            float firstSeen = _flashThrownAt[eidx];
            float detonateAt = firstSeen + FlashFuseSeconds;
            byte fteam = 0;
            nint thrower = nint.Zero;
            try
            {
                var grenade = new CBaseGrenade(ent.Handle);
                float nativeTime = grenade.DetonateTime;
                if (float.IsFinite(nativeTime) && nativeTime > 0f && nativeTime >= firstSeen - 0.20f
                    && nativeTime <= firstSeen + FlashFuseSeconds + 0.30f)
                    detonateAt = nativeTime;
                var tp = grenade.Thrower.Value;
                if (tp != null && tp.IsValid) { fteam = tp.TeamNum; thrower = tp.Handle; }
            }
            catch { }
            live.Add((eidx, pos, detonateAt, fteam, thrower));
            UpdateFlashTurnPos(eidx, pos);
        }

        // Drop tracking for flashes that no longer exist (detonated / round end). Decisions
        // linger for 2 seconds past detonation so OnPlayerBlind can still match them.
        if (_flashThrownAt.Count > live.Count)
        {
            var alive = new HashSet<uint>(live.Select(f => f.idx));
            var stale = _flashThrownAt.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (var k in stale)
            {
                if (_debugFlash)
                {
                    foreach (var key in _flashDecisions.Keys.Where(p => p.flash == k).ToList())
                    {
                        var d = _flashDecisions[key];
                        BroadcastDebug(
                            $"[Smarter-Bot/Flash] flash#{k} ended; bot#{key.bot} visible {(d.LastSeen - d.FirstSeen) * 1000f:F0}ms");
                    }
                }
                _flashThrownAt.Remove(k);
                foreach (var s in _flashRolledByBot.Values) s.Remove(k);
                _flashRejectLogged.RemoveWhere(p => p.flash == k);
            }
        }

        // Expire stale decisions (2s past their detonation) so we don't leak across rounds.
        if (_flashDecisions.Count > 0)
        {
            var expired = _flashDecisions.Where(kvp => now - kvp.Value.DetonateAt > 2f)
                                          .Select(kvp => kvp.Key)
                                          .ToList();
            foreach (var k in expired) _flashDecisions.Remove(k);
        }

        if (live.Count == 0) return;

        foreach (var handle in _flashControllers)
        {
            var bot = handle.Value;
            if (bot == null || !bot.IsValid || !bot.IsBot) continue;
            if (bot.HasBeenControlledByPlayerThisRound) continue;

            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            int bidx = (int)bot.Index;
            if (InCommittedBlind(bidx, now))
                continue;

            if (!_flashRolledByBot.TryGetValue(bidx, out var rolled))
            {
                rolled = new HashSet<uint>();
                _flashRolledByBot[bidx] = rolled;
            }

            foreach (var (fidx, fpos, detonateAt, fteam, thrower) in live)
            {
                if (now > detonateAt) continue;
                var key = (bidx, fidx);

                if (rolled.Contains(fidx))
                {
                    if (_flashDecisions.TryGetValue(key, out var d))
                    {
                        d.LastSeen = now;
                        _flashDecisions[key] = d;
                    }
                    continue;
                }

                // How does this bot know about the flash?
                //  own     - it threw it: knows the timing, usually turns away well.
                //  friendly- a teammate's flash nearby ("flashing!"): decent chance.
                //  seen    - spotted in flight (FOV + line of sight): has to react.
                bool own = thrower != nint.Zero && thrower == pawn.Handle;
                bool friendly = !own && fteam != 0 && fteam == bot.TeamNum;
                var o = pawn.AbsOrigin;
                float fd = o == null ? 9999f : MathF.Sqrt(
                    (fpos.X - o.X) * (fpos.X - o.X) + (fpos.Y - o.Y) * (fpos.Y - o.Y) + (fpos.Z - o.Z) * (fpos.Z - o.Z));
                bool seen = IsInFov(pawn, fpos, FlashFovHorizDeg, FlashFovVertDeg, out float dYaw, out float dPit)
                            && BotCanSee(pawn, fpos);

                if (!own && !(friendly && fd < 1500f) && !seen)
                {
                    if (_debugFlash && _flashRejectLogged.Add((bidx, fidx)))
                        BroadcastDebug($"[Smarter-Bot/Flash] bot={bot.PlayerName} flash#{fidx} unnoticed dYaw={dYaw:F1}");
                    continue;
                }
                _flashRejectLogged.Remove((bidx, fidx));
                rolled.Add(fidx);

                float msLeft = (detonateAt - now) * 1000f;
                var botAi = pawn.Bot;
                bool busy = botAi != null && (botAi.IsAttacking || botAi.IsEnemyVisible);

                // Chance to turn away at all. Failing = keeps looking (fully flashed).
                double prob;
                if (own) prob = 0.85;
                else if (friendly) prob = seen ? 0.65 : 0.45;
                else prob = msLeft <= 250f ? 0.05 : msLeft <= 450f ? 0.18
                          : msLeft <= 700f ? 0.32 : 0.42;
                if (busy) prob *= own ? 0.8 : 0.55;   // mid-duel: hard to give up the angle
                if (pawn.IsScoped) prob *= 0.6;

                // Keep anticipation separate from exposure. A late or unnoticed flash still applies normally.
                float react = own ? RandF(0.05f, 0.25f) : friendly ? RandF(0.15f, 0.45f) : RandF(0.18f, 0.5f);
                bool avoided = _random.NextDouble() <= prob && now + react < detonateAt - 0.08f
                    && StartFlashTurn(bot, bidx, fidx, fpos, now + react, detonateAt);
                if (avoided) _statFlashTurns++;

                _flashDecisions[key] = new FlashDecision
                {
                    FirstSeen = now,
                    LastSeen = now,
                    DetonateAt = detonateAt,
                    Avoided = avoided,
                };

                if (_debugFlash)
                {
                    BroadcastDebug(
                        $"[Smarter-Bot/Flash] bot={bot.PlayerName} sees flash#{fidx} t-{msLeft:F0}ms prob={prob * 100:F0}% roll={(avoided ? "AVOID" : "flash")}");
                }
            }
        }
    }

    // Decoupled horizontal/vertical FOV check. Source 2 QAngle convention:
    //   EyeAngles.Y = yaw   (0 deg => +X axis, 90 deg => +Y axis)
    //   EyeAngles.X = pitch (positive => looking DOWN; this is the Quake/Source convention)
    // Returns true when target is inside both cones; outDeltaYaw/outDeltaPitch are
    // signed angle deltas (target relative to bot view) for debug logging.
    private static bool IsInFov(CCSPlayerPawn pawn, Vector target,
                                float horizDeg, float vertDeg,
                                out float outDeltaYaw, out float outDeltaPitch)
    {
        outDeltaYaw = 0f;
        outDeltaPitch = 0f;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        float eyeZ = origin.Z + pawn.ViewOffset.Z;

        double dx = target.X - origin.X;
        double dy = target.Y - origin.Y;
        double dz = target.Z - eyeZ;

        double horizDist = Math.Sqrt(dx * dx + dy * dy);
        if (horizDist < 1e-3 && Math.Abs(dz) < 1e-3) return true;

        double yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double pitchToTarget = -Math.Atan2(dz, horizDist) * 180.0 / Math.PI;

        double yawDelta = NormalizeAngleDeg(yawToTarget - pawn.EyeAngles.Y);
        double pitchDelta = NormalizeAngleDeg(pitchToTarget - pawn.EyeAngles.X);

        outDeltaYaw = (float)yawDelta;
        outDeltaPitch = (float)pitchDelta;

        return Math.Abs(yawDelta) <= horizDeg * 0.5
            && Math.Abs(pitchDelta) <= vertDeg * 0.5;
    }

    // Wraps an angular difference to the signed half-turn range.
    private static double NormalizeAngleDeg(double a)
    {
        a %= 360.0;
        if (a > 180.0) a -= 360.0;
        if (a < -180.0) a += 360.0;
        return a;
    }

    // Checks brush visibility from the Bot's eye position to a flash projectile.
    private bool BotCanSee(CCSPlayerPawn pawn, Vector target)
    {
        if (_scratchEye == null) return false;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        _scratchEye.X = origin.X;
        _scratchEye.Y = origin.Y;
        _scratchEye.Z = origin.Z + pawn.ViewOffset.Z;

        var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
        var result = Trace.TraceEndShape(_scratchEye, target, pawn, opts);
        return result.Fraction >= 0.999f;
    }

    // Clears flashbang state owned by the completed round.
    private void ResetFlashbangRoundState()
    {
        ResetFlashTurns();
        ResetFlashFireBlocks();
        // Flash entity indices may be reused after a round transition.
        _flashThrownAt.Clear();
        _flashRolledByBot.Clear();
        _flashDecisions.Clear();
        _flashRejectLogged.Clear();
        _flashBlindUntil.Clear();
    }

    // Actual exposure remains committed through the longest overlapping native window.
    private bool InCommittedBlind(int botIndex, float now)
        => _flashBlindUntil.TryGetValue(botIndex, out float until) && now < until;
}
