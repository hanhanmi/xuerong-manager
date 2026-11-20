using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private sealed class OpeningFidget
    {
        public uint Pawn;
        public float NextAt, RestoreAt;
        public bool KnifeOut;
    }

    private readonly Dictionary<int, OpeningFidget> _openingFidgets = new();
    private readonly HashSet<uint> _openingHandledWeapons = new();
    private readonly HashSet<int> _openingExchanging = new();
    private float _openingStart, _openingLiveAt = -1f, _nextOpeningScan;
    private static readonly string[] OpeningRifles =
    {
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_galilar", "weapon_famas", "weapon_awp",
        "weapon_ssg08", "weapon_aug", "weapon_sg556", "weapon_mp9", "weapon_mac10", "weapon_mp7", "weapon_mp5sd",
        "weapon_ump45", "weapon_p90", "weapon_nova", "weapon_xm1014", "weapon_mag7"
    };

    private void StartOpeningActions(float now)
    {
        ResetOpeningActions();
        _openingStart = now;
        _openingLiveAt = -1f;
        _nextOpeningScan = now + 1.25f;
        _openingHandledWeapons.Clear();
        _openingExchanging.Clear();
        _openingFidgets.Clear();
    }

    private void FinishOpeningFreeze(float now)
    {
        _openingLiveAt = now;
        foreach (var (slot, state) in _openingFidgets)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (state.KnifeOut && _botController != null && IsAliveBot(player, out var pawn, out _)
                && pawn.EntityHandle.Raw == state.Pawn && GetReequipWeapon(pawn, out int def))
                BotControllerBridge.SwitchBotWeapon(_botController, slot, def);
        }
        _openingFidgets.Clear();
        _nextOpeningScan = now;
    }

    private void ResetOpeningActions() => _openingFidgets.Clear();

    private void UpdateOpeningActions(float now)
    {
        if (_botController == null || _defusalRoundEnded) return;
        var rules = Rules();
        if (rules is not { MapHasBombTarget: true, WarmupPeriod: false }) return;
        bool freeze = _isFreezeTime && rules.FreezePeriod;
        bool liveOpening = !freeze && _openingLiveAt >= 0f && now - _openingLiveAt < 1.5f;
        if (!freeze && !liveOpening) return;
        if (now >= _nextOpeningScan && (liveOpening || now - _openingStart >= 1.25f))
        {
            _nextOpeningScan = now + 0.18f;
            ScanOpeningHumanGuns(now);
        }
        if (!freeze || now - _openingStart < 0.5f) return;
        float freezeEnd = _openingStart + (ConVar.Find("mp_freezetime")?.GetPrimitiveValue<int>() ?? 5);
        foreach (var (slot, state) in _openingFidgets.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!IsAliveBot(player, out var pawn, out _) || pawn.EntityHandle.Raw != state.Pawn)
            {
                _openingFidgets.Remove(slot);
            }
        }
        foreach (var player in GetMatchPlayers())
        {
            if (!IsAliveBot(player, out var pawn, out _) || pawn.IsDefusing) continue;
            if (!_openingFidgets.TryGetValue(player.Slot, out var state))
                _openingFidgets[player.Slot] = state = new OpeningFidget { Pawn = pawn.EntityHandle.Raw, NextAt = now + RandF(2.8f, 4.2f) };
            if (_openingExchanging.Contains(player.Slot) || !GetReequipWeapon(pawn, out int gun)) continue;
            if (state.KnifeOut && (now >= state.RestoreAt || freezeEnd - now < 0.4f))
            {
                BotControllerBridge.SwitchBotWeapon(_botController, player.Slot, gun);
                state.KnifeOut = false;
                state.NextAt = now + RandF(0.6f, 1.3f);
            }
            if (now < state.NextAt || freezeEnd - now < 0.8f) continue;
            state.NextAt = now + RandF(0.7f, 1.5f);
            if (!DefusalObjectiveBusy(pawn) && _random.NextDouble() < 0.40 && BotControllerBridge.SwitchBotWeapon(_botController, player.Slot, KnifeDefinitionIndex))
            {
                state.KnifeOut = true;
                state.RestoreAt = now + RandF(0.25f, 0.6f);
            }

        }
    }

    private void ScanOpeningHumanGuns(float now)
    {
        foreach (string name in OpeningRifles)
        foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>(name))
        {
            if (!weapon.IsValid || weapon.AbsOrigin == null || weapon.OwnerEntity.IsValid
                || _openingHandledWeapons.Contains(weapon.EntityHandle.Raw)
                || weapon.DroppedAtTime < _openingStart || now - weapon.DroppedAtTime > 5f) continue;
            var ownerPawn = weapon.PrevOwner.Value;
            var human = ownerPawn?.OriginalController.Value;
            if (human == null || !human.IsValid || (human.IsBot && !human.HasBeenControlledByPlayerThisRound)
                || !human.PawnIsAlive || human.TeamNum is not (2 or 3)) continue;
            var origin = weapon.AbsOrigin;
            var helper = GetMatchPlayers().Where(p => p.TeamNum == human.TeamNum && IsAliveBot(p, out _, out _)
                    && !_openingExchanging.Contains(p.Slot) && p.PlayerPawn.Value?.AbsOrigin != null
                    && Dist2D(p.PlayerPawn.Value.AbsOrigin, origin) < 155f)
                .OrderBy(p => Dist2D(p.PlayerPawn.Value!.AbsOrigin!, origin)).FirstOrDefault();
            if (helper == null || !IsAliveBot(helper, out var pawn, out _)
                || pawn.ItemServices == null || pawn.AbsOrigin == null) continue;
            if (_openingFidgets.TryGetValue(helper.Slot, out var fidget) && fidget.KnifeOut)
            {
                if (GetReequipWeapon(pawn, out int reequip)) BotControllerBridge.SwitchBotWeapon(_botController!, helper.Slot, reequip);
                fidget.KnifeOut = false;
            }
            var own = pawn.WeaponServices?.MyWeapons.Select(h => h.Value).FirstOrDefault(w => w != null && w.IsValid
                && w.As<CCSWeaponBase>().VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE);
            if (own != null)
            {
                int ownDef = own.As<CCSWeaponBase>().AttributeManager.Item.ItemDefinitionIndex;
                if (ownDef <= 0 || !BotControllerBridge.SwitchBotWeapon(_botController!, helper.Slot, ownDef)) continue;
            }
            _openingHandledWeapons.Add(weapon.EntityHandle.Raw);
            _openingExchanging.Add(helper.Slot);
            uint pawnId = pawn.EntityHandle.Raw;
            uint offeredId = weapon.EntityHandle.Raw;
            AddTimer(0.18f, () =>
            {
                bool checkingPickup = false;
                try
                {
                    if (!IsAliveBot(helper, out var current, out _) || current.EntityHandle.Raw != pawnId
                        || !weapon.IsValid || weapon.OwnerEntity.IsValid || current.AbsOrigin == null) return;
                    var active = current.WeaponServices?.ActiveWeapon.Value;
                    if (own != null)
                    {
                        if (active == null || !active.IsValid || active.EntityHandle.Raw != own.EntityHandle.Raw) return;
                        helper.DropActiveWeapon();
                    }
                    var pos = current.AbsOrigin;
                    weapon.Teleport(new Vector(pos.X, pos.Y, pos.Z + 14f), null, new Vector(0f, 0f, 0f));
                    weapon.AcceptInput("Use", current, current, "", 0);
                    AddTimer(0.35f, () =>
                    {
                        try
                        {
                            if (!IsAliveBot(helper, out var nextPawn, out _) || nextPawn.EntityHandle.Raw != pawnId) return;
                            bool received = nextPawn.WeaponServices?.MyWeapons.Any(h => h.Value != null && h.Value.IsValid
                                && h.Value.EntityHandle.Raw == offeredId) == true;
                            if (!received) _openingHandledWeapons.Remove(offeredId);
                            if (!received && own is { IsValid: true } && !own.OwnerEntity.IsValid && nextPawn.AbsOrigin != null)
                            {
                                var restorePos = nextPawn.AbsOrigin;
                                own.Teleport(new Vector(restorePos.X, restorePos.Y, restorePos.Z + 14f), null, new Vector(0f, 0f, 0f));
                                own.AcceptInput("Use", nextPawn, nextPawn, "", 0);
                            }
                        }
                        catch (Exception e) { Console.WriteLine("[Smarter-Bot] opening exchange restore: " + e.Message); }
                        finally { _openingExchanging.Remove(helper.Slot); }
                    });
                    checkingPickup = true;
                }
                catch (Exception e) { Console.WriteLine("[Smarter-Bot] opening exchange: " + e.Message); }
                finally
                {
                    if (!checkingPickup)
                    {
                        _openingHandledWeapons.Remove(offeredId);
                        _openingExchanging.Remove(helper.Slot);
                    }
                }
            });
        }
    }
}