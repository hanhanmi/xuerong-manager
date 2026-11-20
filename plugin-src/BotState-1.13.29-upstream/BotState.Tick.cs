using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotState;

public partial class BotState
{
    //---------------------------------------------------------------------------------------
    // Runs bot modules in their original order using one controller traversal.
    private void OnTick()
    {
        float now = Server.CurrentTime;
        ExpireReveals(now);
        RefreshDefusalCombat(now);
        RefreshPostplantGuard(now);
        UpdateHearing(now);
        try { UpdateOpeningActions(now); } catch (Exception e) { Console.WriteLine("[Smarter-Bot] opening actions: " + e.Message); }
        PruneFlashTurns();
        ProcessFlashbangAvoidance();
        bool defusal = IsDefusalCombatEnabled();
        if (!defusal && _fastPlant.Count > 0) ResetFastPlant();
        if (!defusal && _duels.Count > 0) ResetDefusalDuels();
        if (!defusal && (_defusalGuards.Count > 0 || _staticStandTokens.Count > 0 || _stationaryGuardWatch.Count > 0)) ResetDefusalGuards();
        if (!defusal && _siteHolds.Count > 0) ClearSiteHoldAssignments();

        // Server.MaxPlayers can cache the main menu's one-player limit.
        int maxPlayers = NativeAPI.GetMaxClients();
        for (int slot = 0; slot < maxPlayers; slot++)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot)
            {
                StopFastPlant(slot);
                ClearDefusalDuel(slot);
                ClearDefusalReaction(slot);
                ClearDefusalGuard(slot);
                DropSiteHold(slot);
                ClearStationaryGuardWatch(slot);
                ResetDefusalRecoveryForSlot(slot);
                ResetStrafeForSlot(slot);
                ResetKnifeRunForSlot(slot);
                ReleaseJumpGate(slot);
                continue;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
            {
                StopFastPlant(slot);
                ClearDefusalDuel(slot);
                ClearDefusalReaction(slot);
                ClearDefusalGuard(slot);
                DropSiteHold(slot);
                ClearStationaryGuardWatch(slot);
                ResetDefusalRecoveryForSlot(slot);
                ResetStrafeForSlot(slot);
                ResetKnifeRunForSlot(slot);
                ReleaseJumpGate(slot);
                continue;
            }

            var bot = pawn.Bot;
            if (bot == null)
            {
                StopFastPlant(slot);
                ClearDefusalDuel(slot);
                ClearDefusalReaction(slot);
                ClearDefusalGuard(slot);
                DropSiteHold(slot);
                ClearStationaryGuardWatch(slot);
                ResetDefusalRecoveryForSlot(slot);
                ResetStrafeForSlot(slot);
                ResetKnifeRunForSlot(slot);
                ReleaseJumpGate(slot);
                continue;
            }
            // In case the bot has been taken over
            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                StopFastPlant(slot);
                ClearDefusalDuel(slot);
                ClearDefusalReaction(slot);
                ClearDefusalGuard(slot);
                DropSiteHold(slot);
                ClearStationaryGuardWatch(slot);
                ResetDefusalRecoveryForSlot(slot);
                if (_moveTokens.ContainsKey(slot)) ResetStrafeForSlot(slot);
                if (_knifeRun.ContainsKey(slot)) ResetKnifeRunForSlot(slot);
                if (_jumpSuppressId.ContainsKey(slot)) ReleaseJumpGate(slot);
                continue;
            }

            int idx = (int)player.Index;
            InterruptReload(player, pawn, bot, now);
            // Door Stuck Fix
            bool inDoorCooldown = _doorEventCooldown.TryGetValue(idx, out float doorCooldownEnd) && now < doorCooldownEnd;

            UpdateBotBehavior(bot, now);
            bool curIsAttacking = UpdateBotCombat(player, pawn, bot, idx);
            UpdateReaction(player, pawn, bot, slot, now);
            UpdateFlashTurn(player, pawn, bot, now);
            if (defusal && UpdateFastPlant(player, pawn, bot, idx, now))
            {
                ClearStationaryGuardWatch(slot);
                continue;
            }
            if (!UpdateBotMovement(pawn, bot, slot, idx, curIsAttacking, inDoorCooldown))
            {
                // Doors: leave movement fully to the engine.
                StopFastPlant(slot);
                ClearDefusalDuel(slot);
                ClearDefusalGuard(slot);
                ClearStationaryGuardWatch(slot);
                ReleaseMove(slot);
                continue;
            }

            RecoverStuckBot(player, pawn, bot, idx, now, curIsAttacking);
            ApplyMapMovementFix(pawn, bot);

            if (_botController != null && !_isFreezeTime)
            {
                UpdateSmokeApproach(player, pawn, bot, slot, now, curIsAttacking);
                UpdateSmokeSpam(pawn, bot, slot, now);
                UpdateKnifeRun(player, pawn, bot, slot, now, curIsAttacking);
                if (defusal) UpdateCtScoutWalk(player, pawn, bot, idx, now);
                if (defusal)
                {
                    if (UpdateDefusalDuel(player, pawn, bot, idx, now)) ClearDefusalGuard(slot);
                    else if (!UpdateDefusalGuard(player, pawn, bot, idx, now))
                        UpdateHoldPeek(player, pawn, bot, slot, idx, now, curIsAttacking);
                    LookTowardCueWhileIdle(player, pawn, bot, idx, now);
                }
                else
                {
                    UpdateCombatStrafe(pawn, bot, slot, idx, now, curIsAttacking);
                    UpdateHoldPeek(player, pawn, bot, slot, idx, now, curIsAttacking);
                }
                UpdateStationaryGuard(player, pawn, bot, idx, now);
            }
        }

        ExpireStationaryGuard(Server.CurrentTime);
        ExpireMoveTokens(Server.CurrentTime);
        ExpireSmokes(Server.CurrentTime);
        UpdateTacticsScan(Server.CurrentTime);
        UpdateRoles(Server.CurrentTime);
        UpdateInfoShare(Server.CurrentTime);
        try
        {
            float t = Server.CurrentTime;
            UpdateLossSupport(t);
            UpdateDroppedBomb(t);
            UpdateTDirector(t);
            UpdateMidRound(t);
            UpdateSiteFill(t);
            UpdateGroups(t);
            UpdateDefusalRetake(t);
        }
        catch (Exception e) { Console.WriteLine("[Smarter-Bot] tactics tick: " + e.Message); }
    }
}
