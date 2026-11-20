using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System;

namespace BotState;

public partial class BotState
{
    private bool _isFreezeTime = false;
    //---------------------------------------------------------------------------------------
    // Registers game events and the per-tick bot behavior listener
    public override void Load(bool hotReload)
    {
        InstallDefuseBombHook();
        InstallBotBlindHook();
        InitializeFovPatches();
        LoadCustomFov();
        RegisterListener<Listeners.OnMapEnd>(ClearFovObservers);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            _defusalRoundEnded = true;
            ResetOpeningActions();
            ResetFlashTurns();
            ResetFlashFireBlocks();
            ResetFastPlant();
            ResetDefusalDuels();
            ResetDefusalGuards();
            ClearSiteHoldAssignments();
            ResetDefusalRecoveryRoundState();
            ResetDefusalRetakeRoundState();
            ResetInfoShareRoundState();
            if (_defusalCombatLive) ResetReactionRoundState();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventFlashbangDetonate>(OnFlashDetonate);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventDoorOpen>(OnDoorOpen);
        RegisterEventHandler<EventDoorClose>(OnDoorClose);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterHearingEvents();
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventSmokegrenadeExpired>(OnSmokeExpired);
        RegisterListener<Listeners.OnMapStart>(_ => { _announcedThisMap = false; _smokes.Clear(); });
        RegisterListener<Listeners.OnMapStart>(_ => ResetDefusalMap());
        RegisterListener<Listeners.OnMapEnd>(() => { ResetOpeningActions(); ResetMovementRoundState(); ResetStrafeRoundState(); ResetDefusalMap(); ResetDefusalRecoveryRoundState(); ResetReactionRoundState(); });
        RegisterListener<Listeners.OnEntityCreated>(InvalidateFlashEntities);
        RegisterListener<Listeners.OnEntitySpawned>(InvalidateFlashEntities);
        RegisterListener<Listeners.OnEntityDeleted>(InvalidateFlashEntities);
        RegisterListener<Listeners.OnMapStart>(_ => { ResetFlashbangRoundState(); ClearFlashEntities(); });
        RegisterListener<Listeners.OnMapEnd>(() => { ResetFlashbangRoundState(); ClearFlashEntities(); });
        RegisterListener<Listeners.OnTick>(OnTick);
        // Prevent bots from holding their knives when there're enemies alive
        _gunReequipTimer = AddTimer(
            1.0f,
            ReequipGunForActiveBots,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        StartRushAntennaPublisher();
        RefreshDefusalDifficulty();
    }

    // Resolves capabilities supplied by plugins after every plugin has loaded
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _scratchEye = new Vector();
        try { _botController = BotControllerBridge.TryGet(); } catch { _botController = null; }
        InitBotMoveTo();
        Logger.LogInformation($"[Smarter-Bot] tactics MoveTo {(_moveToReady ? "ready" : "unavailable")}, BotController {(_botController != null ? "ok" : "NOT available")}");
    }

    // Restores plugin-owned state before the plugin unloads
    public override void Unload(bool hotReload)
    {
        ResetOpeningActions();
        ResetFlashTurns();
        ResetKnifeRunRoundState();
        ResetFastPlant();
        ResetDefusalDuels();
        ResetDefusalGuards();
        ClearSiteHoldAssignments();
        ResetDefusalRecoveryRoundState();
        ResetMovementRoundState();
        ResetStrafeRoundState();
        ResetReactionRoundState();
        CancelAllMoveTokens();
        _customFov?.Dispose(); _customFov = null;
        ClearFovObservers();
        ResetFlashbangRoundState();
        ClearFlashEntities();
        CancelAllFakeDefuseSuppressions();
        UninstallBotBlindHook();
        UninstallDefuseBombHook();
        RestoreAllFovPatches();
        ReleaseKnifeLocks();
        ClearReveals();
        _defuseRevealTimer?.Kill();
        _gunReequipTimer?.Kill();
        StopRushAntennaPublisher();
    }
    //---------------------------------------------------------------------------------------
    // Applies initial Bot state on the frame following spawn.
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    // Reapplies initial Bot state when the freeze period ends.
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _isFreezeTime = false;
        FinishOpeningFreeze(Server.CurrentTime);
        _nextDefusalCheck = 0f;
        try { OnRoundLiveForRoles(); AnnounceOnce(); } catch (Exception e) { Logger.LogWarning("[Smarter-Bot] roles init: " + e.Message); }
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    // Clears per-round state and releases elimination knife locks
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ClearFovObservers();
        ReleaseKnifeLocks();
        StopDefuseReveal();
        ClearReveals();
        _eliminationHandled = false;
        _isFreezeTime = true;
        StartOpeningActions(Server.CurrentTime);
        _defusalRoundEnded = false;
        _nextDefusalCheck = 0f;

        ResetFastPlant();
        ResetDefusalDuels();
        ResetDefusalGuards();
        ResetSiteHoldRoundState();
        ResetMovementRoundState();
        ResetCombatRoundState();
        ResetWeaponsRoundState();
        ResetBombRoundState();
        ResetFlashbangRoundState();
        ResetKnifeRunRoundState();
        ResetStrafeRoundState();
        ResetTacticsRoundState();
        ResetSmokeRoundState();
        ResetReactionRoundState();
        ResetRolesRoundState();
        ResetInfoShareRoundState();
        ResetMatchRoundState();
        ResetTDirectorRoundState();
        ResetCtRotateRoundState();
        ResetDroppedBombRoundState();
        ResetMidRoundState();
        _hurtStreak.Clear();
        return HookResult.Continue;
    }

    // Detects elimination while explicitly excluding the current death victim
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        // Deathmatch is free-for-all even though the engine still reports
        // temporary T/CT team numbers. Do not treat the last bot on one side
        // as a round elimination and lock the other bots to their knives.
        try { DirectorOnDeath(@event.Userid, @event.Attacker, @event.Weapon ?? ""); }
        catch (Exception e) { Console.WriteLine("[Smarter-Bot] director error: " + e.Message); }
        try { HandleTacticsOnDeath(@event.Userid, @event.Attacker); }
        catch (Exception e) { Console.WriteLine("[Smarter-Bot] tactics error: " + e.Message); }
        var victim = @event.Userid;
        if (victim is { IsValid: true, IsBot: true })
        {
            int flashIndex = (int)victim.Index;
            StopFlashTurn(flashIndex);
            ReleaseFlashFire(victim.Slot);
            _flashBlindUntil.Remove(flashIndex);
            ClearDefusalDuel(victim.Slot);
            ClearDefusalReaction(victim.Slot);
            ClearDefusalGuard(victim.Slot);
            ClearStationaryGuardWatch(victim.Slot);
            ResetDefusalRecoveryForSlot(victim.Slot);
        }
        bool defusal = IsDefusalCombatEnabled();
        if (defusal) NoteDefusalKill(@event.Attacker, victim, Server.CurrentTime);

        if (IsDeathmatch())
            return HookResult.Continue;

        if (_botController == null)
            return HookResult.Continue;

        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        CsTeam victimTeam = (CsTeam)(int)victim.TeamNum;
        if (victimTeam != CsTeam.Terrorist &&
            victimTeam != CsTeam.CounterTerrorist)
            return HookResult.Continue;

        bool alreadyHandled = _eliminationHandled;
        if (!defusal || !IsBombPlanted()) HandleTeamElimination(victim.Slot, victimTeam);
        RestoreDefuseAfterElimination(victim.Slot, victimTeam);

        // 10% chance the killer Bot inspects its current weapon. Skip when this
        // exact kill just triggered the elimination switching, since that path
        // already inspects the knife.
        if (!defusal && (alreadyHandled || !_eliminationHandled))
            MaybeInspectOnKill(@event.Attacker);

        return HookResult.Continue;
    }

    // Excludes deathmatch from team-elimination behavior.
    private static bool IsDeathmatch()
    {
        var gameType = ConVar.Find("game_type");
        var gameMode = ConVar.Find("game_mode");
        return gameType?.GetPrimitiveValue<int>() == 1
            && gameMode?.GetPrimitiveValue<int>() == 2;
    }
}
