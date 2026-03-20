using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes;
using GoldGift.UI;

namespace GoldGift.Patches;

/// <summary>
/// Harmony patch to inject the Gold Gift button into the game's top bar UI.
/// Only active in multiplayer mode (or debug mode).
/// </summary>
public static class TopBarPatch
{
    private static bool _buttonAdded;

    /// <summary>
    /// Patch the top bar / HUD ready method to add our gift button.
    /// NTopBar lives at MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar
    /// </summary>
    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
    private static class NTopBarReadyPatch
    {
        static void Postfix(NTopBar __instance)
        {
            try
            {
                // Only add in multiplayer mode (or debug mode)
                if (!GoldGiftNetworkHandler.IsMultiplayer())
                    return;

                // Prevent duplicate buttons
                if (_buttonAdded)
                    return;

                if (__instance.GetNodeOrNull("GoldGiftButton") != null)
                    return;

                // Create the gift button via factory method
                var button = GoldGiftButton.Create(__instance);

                // Find the right container to add to.
                Node? container = __instance.GetNodeOrNull("%ButtonContainer")
                    ?? __instance.GetNodeOrNull("%TopBarButtons")
                    ?? __instance.GetNodeOrNull("ButtonContainer");

                if (container != null)
                {
                    container.AddChild(button);
                    container.MoveChild(button, 0);
                }
                else
                {
                    button.Position = new Vector2(
                        __instance.Size.X - 200, 4);
                    __instance.AddChild(button);
                }

                _buttonAdded = true;
                ModEntry.Logger.Info("Gold Gift button added to top bar.");
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Failed to add gift button: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Alternative: Patch the NRun node which is the main run screen.
    /// </summary>
    [HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
    private static class NRunReadyPatch
    {
        static void Postfix(NRun __instance)
        {
            try
            {
                if (!GoldGiftNetworkHandler.IsMultiplayer())
                    return;

                var existingButton = GoldGiftButton.GetButton();
                if (existingButton != null && GodotObject.IsInstanceValid(existingButton) 
                    && existingButton.IsInsideTree())
                    return;

                if (__instance.GetNodeOrNull("GoldGiftButtonOverlay") != null)
                    return;

                var canvasLayer = new CanvasLayer();
                canvasLayer.Name = "GoldGiftButtonOverlay";
                canvasLayer.Layer = 90;

                var marginContainer = new MarginContainer();
                marginContainer.AnchorsPreset = (int)Control.LayoutPreset.TopWide;
                marginContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
                marginContainer.AddThemeConstantOverride("margin_top", 8);
                marginContainer.AddThemeConstantOverride("margin_right", 16);
                marginContainer.MouseFilter = Control.MouseFilterEnum.Ignore;

                var hbox = new HBoxContainer();
                hbox.Alignment = BoxContainer.AlignmentMode.End;
                hbox.MouseFilter = Control.MouseFilterEnum.Ignore;

                // Create button via factory, panel will be added to __instance
                var button = GoldGiftButton.Create(__instance);
                hbox.AddChild(button);

                marginContainer.AddChild(hbox);
                canvasLayer.AddChild(marginContainer);
                __instance.AddChild(canvasLayer);

                _buttonAdded = true;
                ModEntry.Logger.Info("Gold Gift button added to run screen overlay.");
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Failed to add gift button overlay: {ex.Message}");
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
