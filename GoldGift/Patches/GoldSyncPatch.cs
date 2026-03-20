using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace GoldGift.Patches;

/// <summary>
/// Harmony patches for gold synchronization and mod lifecycle in multiplayer.
/// 
/// All patch targets are pure C# methods (RunManager methods), NOT Godot
/// lifecycle methods (_Ready, _Process, etc.), to avoid native bridge crashes.
/// </summary>
public static class GoldSyncPatch
{
    private static bool _initialized;

    /// <summary>
    /// Patch RunManager.EnterRoom to:
    /// 1. Initialize mod config and state when entering a room
    /// 2. Trigger UI injection (delegated to TopBarPatch)
    /// 
    /// EnterRoom is a pure C# async method — safe for Harmony patching.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterRoom))]
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
    /// Reset initialization state when abandoning run.
    /// AbandonInternal is a private async C# method — safe for Harmony patching.
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

    /// <summary>
    /// Also reset on CleanUp (called when run ends normally).
    /// CleanUp is a public void C# method — safe for Harmony patching.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static class RunCleanUpPatch
    {
        static void Prefix()
        {
            _initialized = false;
            GoldGiftNetworkHandler.ResetDebug();
            TopBarPatch.Reset();
        }
    }
}
