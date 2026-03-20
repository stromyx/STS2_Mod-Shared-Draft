using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace GoldGift.Patches;

/// <summary>
/// Harmony patches for gold synchronization in multiplayer.
/// Uses RunManager hooks to initialize the mod.
/// </summary>
public static class GoldSyncPatch
{
    private static bool _initialized;

    /// <summary>
    /// Patch RunManager.EnterRoom to detect when a run is active,
    /// and initialize our config and state for multiplayer.
    /// EnterRoom is an async method, so we patch the MoveNext of the state machine.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), "EnterRoom")]
    private static class RunEnterRoomPatch
    {
        static void Prefix()
        {
            try
            {
                if (_initialized) return;

                // In debug mode, always initialize (even in single-player)
                if (GoldGiftConfig.DebugMode || GoldGiftNetworkHandler.IsMultiplayer())
                {
                    GoldGiftConfig.LoadConfig();
                    _initialized = true;

                    if (GoldGiftConfig.DebugMode)
                    {
                        ModEntry.Logger.Info(
                            "[DEBUG] Debug mode active — simulating multiplayer. " +
                            "Gold Gift mod initialized.");
                    }
                    else
                    {
                        ModEntry.Logger.Info(
                            "Multiplayer run room entered. Gold Gift mod initialized.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Error in run enter room patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reset initialization state when returning to main menu.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), "AbandonInternal")]
    private static class RunAbandonPatch
    {
        static void Prefix()
        {
            _initialized = false;
            GoldGiftNetworkHandler.ResetDebug();
            TopBarPatch.Reset();
        }
    }
}
