using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using GoldGift.UI;

namespace GoldGift.Patches;

/// <summary>
/// Injects the Gold Gift button into the game UI.
/// 
/// IMPORTANT: We do NOT patch Godot lifecycle methods like _Ready().
/// Those methods are called via Godot's native C++ → GDExtension → C# bridge,
/// and Harmony's JIT trampoline injection breaks that call chain (causes SIGSEGV).
/// 
/// Instead, we patch pure C# methods (RunManager.EnterRoom) and use CallDeferred
/// to safely add UI nodes after the scene tree is fully ready.
/// </summary>
public static class TopBarPatch
{
    private static bool _buttonAdded;

    /// <summary>
    /// Patch RunManager.EnterRoom (a pure C# async method) to inject the UI.
    /// This is safe because EnterRoom is a regular managed method, not a Godot
    /// native bridge method.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterRoom))]
    private static class RunEnterRoomUiPatch
    {
        static void Postfix()
        {
            try
            {
                // Only add in multiplayer mode (or debug mode)
                if (!GoldGiftNetworkHandler.IsMultiplayer())
                    return;

                // Prevent duplicate buttons
                if (_buttonAdded)
                {
                    // Check if button is still valid (might have been freed)
                    var existingButton = GoldGiftButton.GetButton();
                    if (existingButton != null && GodotObject.IsInstanceValid(existingButton)
                        && existingButton.IsInsideTree())
                        return;

                    // Button was freed, allow re-creation
                    _buttonAdded = false;
                }

                // Use CallDeferred to safely add UI after the scene tree is ready.
                var sceneTree = Engine.GetMainLoop() as SceneTree;
                if (sceneTree == null)
                {
                    ModEntry.Logger.Error("Could not get SceneTree.");
                    return;
                }

                // GoldGiftButton.CreateOverlay returns a complete CanvasLayer
                // with the draggable button and panel already set up.
                var overlay = GoldGiftButton.CreateOverlay(sceneTree.Root);
                sceneTree.Root.CallDeferred("add_child", overlay);
                _buttonAdded = true;
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Failed to schedule gift button injection: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reset button state when abandoning run.
    /// </summary>
    public static void Reset()
    {
        _buttonAdded = false;
        GoldGiftButton.Cleanup();
    }
}
