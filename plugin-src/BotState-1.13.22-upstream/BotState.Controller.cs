using CounterStrikeSharp.API.Core.Capabilities;
using System.Runtime.CompilerServices;

namespace BotState;

public partial class BotState
{
    private object? _botController;

    // Isolates optional BotControllerApi types from the main plugin type
    private static class BotControllerBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsViewControlled(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            return controller.IsLocked(slot, BotControllerApi.LockKind.All)
                || controller.IsLocked(slot, BotControllerApi.LockKind.Aim)
                || controller.IsReplaying(slot);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasFullViewLock(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            return controller.IsLocked(slot, BotControllerApi.LockKind.All) || controller.IsReplaying(slot);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryAcquireFlashAim(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            return !IsViewControlled(api, slot) && controller.Lock(slot, BotControllerApi.LockKind.Aim);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReleaseFlashAim(object api, int slot)
        {
            ((BotControllerApi.IBotControllerApi)api).Unlock(slot, BotControllerApi.LockKind.Aim);
        }

        // Resolves the optional BotController capability at runtime
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryGet()
        {
            var capability =
                new PluginCapability<BotControllerApi.IBotControllerApi>(
                    "botcontroller:api");
            return capability.Get();
        }

        // Switches one Bot to its knife definition
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SwitchBotWeapon(slot, defIndex);
        }

        // Creates an independently cancellable usercmd injection on a Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long InjectUsercmd(
            object api, int slot, ulong buttonMask, int durationMs = 0)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .InjectUsercmd(slot, buttonMask, durationMs);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdInjection(object api, int slot, long injectionId)
        {
            return ((BotControllerApi.IBotControllerApi)api).CancelUsercmdInjection(slot, injectionId);
        }

        // Suppresses buttons for a fixed duration (expires on its own)
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SuppressUsercmd(object api, int slot, ulong buttonMask, int durationMs)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SuppressUsercmd(slot, buttonMask, durationMs);
        }

        // Starts a cancellable persistent usercmd button suppression
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long StartUsercmdSuppression(
            object api, int slot, ulong buttonMask)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .StartUsercmdSuppression(slot, buttonMask);
        }

        // Cancels one persistent usercmd suppression by its token
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdSuppression(
            object api, int slot, long suppressionId)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .CancelUsercmdSuppression(slot, suppressionId);
        }

        // Starts a usercmd movement override (forward/left in [-1,1], left>0 = move left)
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long StartUsercmdMovement(object api, int slot, float forwardMove, float leftMove)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .StartUsercmdMovement(slot, forwardMove, leftMove);
        }

        // Updates an existing usercmd movement override
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UpdateUsercmdMovement(object api, int slot, long movementId, float forwardMove, float leftMove)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .UpdateUsercmdMovement(slot, movementId, forwardMove, leftMove);
        }

        // Cancels a usercmd movement override
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdMovement(object api, int slot, long movementId)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .CancelUsercmdMovement(slot, movementId);
        }

        // Applies the knife-slot weapon lock to one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Lock(slot, BotControllerApi.LockTarget.Slot3);
        }

        // Checks whether one Bot still has the knife-slot lock
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeLocked(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .GetWeaponLock(slot) == BotControllerApi.LockTarget.Slot3;
        }

        // Releases the weapon lock from one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UnlockWeapon(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Unlock(slot, BotControllerApi.LockKind.Weapon);
        }
    }
}
