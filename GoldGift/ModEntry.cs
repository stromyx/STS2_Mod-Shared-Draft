using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace GoldGift;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "GoldGift";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        // Load config early so DebugMode is available for Harmony patches
        GoldGiftConfig.LoadConfig();

        Harmony harmony = new(ModId);
        harmony.PatchAll(typeof(ModEntry).Assembly);

        // Register with ModConfig (deferred — wait 2 frames for ModConfig to load first)
        var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
        tree.CreateTimer(0).Timeout += () =>
        {
            tree.CreateTimer(0).Timeout += () => ModConfigBridge.Register();
        };

        if (GoldGiftConfig.DebugMode)
        {
            Logger.Info("GoldGift mod loaded - DEBUG MODE ACTIVE (simulated multiplayer)");
        }
        else
        {
            Logger.Info("GoldGift mod loaded - Multiplayer gold gifting enabled!");
        }
    }
}
