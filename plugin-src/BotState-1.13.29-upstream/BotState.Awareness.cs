using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private enum CueKind { Step, Landing, QuietShot, Shot, Visual }

    private sealed record SoundCue(byte Team, CueKind Kind, Vector Position, float Time);
    private sealed record AwarenessCue(uint Pawn, Vector Position, float Time, float Until, float Strength, bool Radio);

    private readonly Dictionary<(int Slot, CueKind Kind), SoundCue> _pendingSounds = new();
    private readonly Dictionary<int, AwarenessCue> _awareness = new();
    private readonly Dictionary<int, float> _nextSoundReport = new();
    private readonly Dictionary<int, float> _awarenessLooks = new();
    private readonly Dictionary<int, (uint Pawn, float X, float Y, float At)> _runningSamples = new();
    private readonly Dictionary<int, float> _lastFootstep = new();
    private float _nextSoundScan;
    private int _heardSteps, _heardShots, _heardLandings, _awarenessReports;
    private static readonly TraceOptions HearingTrace = new() { InteractsWith = Masks.SolidBrushOnly };

    private void RegisterHearingEvents()
    {
        try { RegisterEventHandler<EventPlayerFootstep>((e, _) => { QueueBotSound(e.Userid, CueKind.Step); return HookResult.Continue; }); }
        catch (Exception e) { Console.WriteLine("[Smarter-Bot] footstep event unavailable: " + e.Message); }
        try { RegisterEventHandler<EventPlayerFalldamage>((e, _) => { QueueBotSound(e.Userid, CueKind.Landing); return HookResult.Continue; }); }
        catch (Exception e) { Console.WriteLine("[Smarter-Bot] landing event unavailable: " + e.Message); }
    }

    private void QueueBotSound(CCSPlayerController? source, CueKind kind)
    {
        if (!IsDefusalCombatEnabled() || source is not { IsValid: true, TeamNum: 2 or 3 }
            || source.PlayerPawn.Value is not { IsValid: true, AbsOrigin: { } pos } pawn) return;
        if (kind == CueKind.Step && (DuelSpeedSquared(pawn) < 132f * 132f || DuelDuckAmount(pawn) >= 0.52f)) return;
        if (kind == CueKind.Step) _lastFootstep[source.Slot] = Server.CurrentTime;
        _pendingSounds[(source.Slot, kind)] = new SoundCue(source.TeamNum, kind,
            new Vector(pos.X, pos.Y, pos.Z), Server.CurrentTime);
    }

    private void QueueBotShot(CCSPlayerController shooter, string? name)
    {
        string weapon = (name ?? "").Replace("weapon_", "", StringComparison.Ordinal);
        if (weapon.Length == 0 || weapon.StartsWith("knife", StringComparison.Ordinal)
            || weapon is "bayonet" or "c4" or "hegrenade" or "flashbang" or "smokegrenade" or "molotov" or "incgrenade" or "decoy") return;
        var active = shooter.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        bool quiet = weapon is "mp5sd" or "taser" || active is { IsValid: true }
            && weapon is ("usp_silencer" or "m4a1_silencer") && new CCSWeaponBase(active.Handle).SilencerOn;
        QueueBotSound(shooter, quiet ? CueKind.QuietShot : CueKind.Shot);
    }

    private bool RememberCue(CCSPlayerController player, CCSPlayerPawn pawn, Vector pos, CueKind kind,
        float heardAt, float uncertainty, float strength, bool radio, float now)
    {
        float lifetime = kind switch { CueKind.Step => 2.7f, CueKind.Landing => 3.0f, CueKind.QuietShot => 3.1f, _ => 4.0f };
        if (radio && kind != CueKind.Visual) lifetime -= 0.45f;
        uint id = pawn.EntityHandle.Raw;
        float until = heardAt + lifetime;
        if (until <= now) return false;
        if (_awareness.TryGetValue(player.Slot, out var old) && old.Pawn == id && old.Until > now)
        {
            if (heardAt < old.Time || old.Strength > strength + 0.30f && now - old.Time < 1.2f) return false;
            if (now - old.Time < 0.40f && RetakeDistance(pos, old.Position) < uncertainty + 70f) return false;
        }
        // Keep one fixed, imprecise snapshot. No native enemy or visibility fields are written.
        float angle = RandF(0f, 2f * MathF.PI), error = RandF(uncertainty * 0.42f, uncertainty);
        var estimate = new Vector(pos.X + MathF.Cos(angle) * error, pos.Y + MathF.Sin(angle) * error, pos.Z);
        _awareness[player.Slot] = new AwarenessCue(id, estimate, heardAt, until, strength, radio);
        return true;
    }

    private void UpdateHearing(float now)
    {
        if (!IsDefusalCombatEnabled()) return;
        if (now < _nextSoundScan) return;
        _nextSoundScan = now + 0.16f;
        foreach (int slot in _awareness.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _awareness.Remove(slot);
        var players = GetMatchPlayers().ToArray();
        foreach (var player in players)
        {
            int slot = player.Slot;
            if (player.TeamNum is not (2 or 3) || player.PlayerPawn.Value is not
                { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } pos } pawn
                || !DuelGrounded(pawn) || DuelSpeedSquared(pawn) < 178f * 178f || DuelDuckAmount(pawn) >= 0.40f)
            {
                _runningSamples.Remove(slot);
                continue;
            }
            uint id = pawn.EntityHandle.Raw;
            if (_runningSamples.TryGetValue(slot, out var sample) && sample.Pawn == id && now - sample.At < 0.28f) continue;
            _runningSamples[slot] = (id, pos.X, pos.Y, now);
            if (sample.Pawn != id || now - sample.At > 0.62f || now - _lastFootstep.GetValueOrDefault(slot, -10f) < 0.45f) continue;
            float dx = pos.X - sample.X, dy = pos.Y - sample.Y;
            float distance2 = dx * dx + dy * dy;
            if (distance2 is >= 45f * 45f and <= 155f * 155f) QueueBotSound(player, CueKind.Step);
        }
        if (_pendingSounds.Count == 0) return;
        var sounds = _pendingSounds.Values.ToArray();
        _pendingSounds.Clear();
        foreach (var sound in sounds)
        {
            if (now - sound.Time > 0.6f) continue;
            float range = sound.Kind switch { CueKind.Step => 690f, CueKind.Landing => 540f, CueKind.QuietShot => 830f, _ => 1470f };
            CCSPlayerController? reporter = null;
            float reporterDistance = float.MaxValue;
            foreach (var player in players)
            {
                if (player.TeamNum is not (2 or 3) || player.TeamNum == sound.Team || !IsAliveBot(player, out var pawn, out var bot)) continue;
                if (bot.IsEnemyVisible || bot.IsAttacking) continue;
                var pos = pawn.AbsOrigin!;
                float floor = MathF.Abs(pos.Z - sound.Position.Z);
                float distance = Dist2D(pos, sound.Position) + floor * 1.6f;
                if (distance > range) continue;
                var ear = new Vector(pos.X, pos.Y, pos.Z + 56f);
                var origin = new Vector(sound.Position.X, sound.Position.Y, sound.Position.Z + 32f);
                bool muffled;
                try
                {
                    var trace = Trace.TraceEndShape(ear, origin, pawn, HearingTrace);
                    muffled = trace.IsAllSolid || trace.Fraction < 0.97f;
                }
                catch { continue; }
                if (distance > range * (muffled ? 0.64f : 1f)
                    || (muffled && floor > 150f && sound.Kind is (CueKind.Step or CueKind.Landing))) continue;
                float radius = Math.Clamp(55f + distance * 0.12f + (muffled ? 105f : 0f), 55f, 320f);
                if (!RememberCue(player, pawn, sound.Position, sound.Kind, sound.Time, radius, muffled ? 0.48f : 0.78f, false, now)) continue;
                if (sound.Kind == CueKind.Step) _heardSteps++;
                else if (sound.Kind == CueKind.Landing) _heardLandings++;
                else _heardShots++;
                if ((sound.Kind != CueKind.Step || !muffled && distance < 365f)
                    && distance < reporterDistance && _nextSoundReport.GetValueOrDefault(player.Slot) <= now)
                {
                    reporter = player;
                    reporterDistance = distance;
                }
            }
            if (reporter != null && _awareness.TryGetValue(reporter.Slot, out var memory))
            {
                _nextSoundReport[reporter.Slot] = now + RandF(2.2f, 3.5f);
                if (_random.NextDouble() < (sound.Kind == CueKind.Step ? 0.30 : 0.55))
                    ShareAwareness(reporter, players, memory.Position, sound.Kind, sound.Time, now);
            }
        }
    }

    private void ShareAwareness(CCSPlayerController source, CCSPlayerController[] players, Vector pos, CueKind kind, float at, float now)
    {
        int cap = kind == CueKind.Visual ? 2 : 1;
        var candidates = players.Where(p => p.Slot != source.Slot && p.TeamNum == source.TeamNum)
            .OrderBy(p => p.PlayerPawn.Value?.AbsOrigin is { } origin ? RetakeDistance(origin, pos) : float.MaxValue);
        foreach (var player in candidates)
        {
            if (_shareRecvCd.GetValueOrDefault(player.Slot) > now || !IsAliveBot(player, out var pawn, out var bot)
                || bot.IsEnemyVisible || bot.IsAttacking) continue;
            float distance = RetakeDistance(pawn.AbsOrigin!, pos);
            if (distance > (kind == CueKind.Visual ? 2200f : 1350f)) continue;
            if (!RememberCue(player, pawn, pos, kind, at, Math.Clamp(100f + distance * 0.08f, 100f, 310f),
                kind == CueKind.Visual ? 0.83f : 0.42f, true, now)) continue;
            _shareRecvCd[player.Slot] = now + RandF(1.3f, 2.6f);
            _awarenessReports++;
            if (--cap == 0) break;
        }
    }

    private bool TryAwarenessCue(int slot, CCSPlayerPawn pawn, float now, out AwarenessCue cue)
        => _awareness.TryGetValue(slot, out cue!) && cue.Pawn == pawn.EntityHandle.Raw && cue.Until > now;

    private float EntranceYaw(CCSPlayerPawn pawn, float preferred, float fallback)
    {
        if (pawn.AbsOrigin is not { } pos) return fallback;
        var eye = new Vector(pos.X, pos.Y, pos.Z + 60f);
        float best = fallback, scoreBest = float.NegativeInfinity;
        ReadOnlySpan<float> offsets = [0f, -21f, 21f, -43f, 43f, -68f, 68f];
        foreach (float offset in offsets)
        {
            float yaw = preferred + offset, rad = yaw * MathF.PI / 180f;
            float fx = MathF.Cos(rad), fy = MathF.Sin(rad);
            float sight = SightDistance(pawn, fx, fy, 0f, 0f, 0f, 820f);
            if (sight < 115f) continue;
            float check = Math.Min(sight - 10f, 400f);
            if (_smokes.Count != 0 && SegmentThroughSmoke(eye, new Vector(pos.X + fx * check, pos.Y + fy * check, eye.Z), SmokeEdge)) continue;
            float score = MathF.Min(sight, 550f) - MathF.Abs(offset) * 4.3f;
            if (score > scoreBest) { scoreBest = score; best = yaw; }
        }
        return best;
    }

    private void LookTowardCueWhileIdle(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        int slot = player.Slot;
        if (!TryAwarenessCue(slot, pawn, now, out var cue) || now < _awarenessLooks.GetValueOrDefault(slot)
            || bot.IsAttacking || bot.IsEnemyVisible || DefusalObjectiveBusy(pawn) || DuelFlashActive(pawn, idx, now)
            || _fastPlant.ContainsKey(slot) || _smokeHold.ContainsKey(slot) || HasDefusalRetakeRoute(slot)
            || SiteHoldTravelActive(slot, _defusalBombPlanted) || GuardOwnsLocalMovement(slot, pawn, now)
            || ActiveMoveOwner(slot, now) >= MoveOwner.Combat || _botController == null
            || BotControllerBridge.IsViewControlled(_botController, slot) || pawn.AbsOrigin is not { } pos
            || DuelSpeedSquared(pawn) > 125f * 125f || RetakeDistance(pos, cue.Position) > 1250f
            || MathF.Abs(pos.Z - cue.Position.Z) > 155f) return;
        _awarenessLooks[slot] = now + 0.7f;
        float yaw = MathF.Atan2(cue.Position.Y - pos.Y, cue.Position.X - pos.X) * 180f / MathF.PI;
        if (MathF.Abs((float)NormalizeAngleDeg(yaw - pawn.EyeAngles.Y)) > (cue.Radio ? 62f : 93f)) return;
        yaw = EntranceYaw(pawn, yaw, pawn.EyeAngles.Y) * MathF.PI / 180f;
        var look = bot.LookAtSpot;
        look.X = pos.X + MathF.Cos(yaw) * 430f;
        look.Y = pos.Y + MathF.Sin(yaw) * 430f;
        look.Z = pos.Z + 60f;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.LookAtSpotTimestamp = now;
        bot.LookAtSpotDuration = Math.Min(0.45f, cue.Until - now);
        bot.LookAtSpotAttack = false;
    }

    private void ResetAwareness()
    {
        _pendingSounds.Clear();
        _awareness.Clear();
        _nextSoundReport.Clear();
        _awarenessLooks.Clear();
        _runningSamples.Clear();
        _lastFootstep.Clear();
        _nextSoundScan = 0f;
    }

    private string AwarenessStatus() => $"[Smarter-Bot] hearing steps={_heardSteps} shots={_heardShots} landings={_heardLandings} callouts={_awarenessReports} cues={_awareness.Count}";
}
