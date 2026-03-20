using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using SharedDraft.UI;

namespace SharedDraft.Patches;

/// <summary>
/// Harmony patches for mod lifecycle management.
/// 
/// Resets SharedDraft state when the run ends (CleanUp) or is abandoned (AbandonInternal).
/// All patch targets are pure C# methods — safe for Harmony patching.
/// </summary>
public static class LifecyclePatch
{
    /// <summary>
    /// Reset mod state when abandoning a run.
    /// AbandonInternal is a private async C# method — safe for Harmony patching.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), "AbandonInternal")]
    private static class RunAbandonPatch
    {
        static void Prefix()
        {
            try
            {
                SharedDraftManager.Instance.Reset();
                SharedDraftScreen.Cleanup();
                DraftResultOverlay.Cleanup();
                ModEntry.Logger.Info("SharedDraft state reset (run abandoned).");
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Error in AbandonInternal patch: {ex.Message}");
            }
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
            try
            {
                SharedDraftManager.Instance.Reset();
                SharedDraftScreen.Cleanup();
                DraftResultOverlay.Cleanup();
                ModEntry.Logger.Info("SharedDraft state reset (run cleaned up).");
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Error in CleanUp patch: {ex.Message}");
            }
        }
    }
}
