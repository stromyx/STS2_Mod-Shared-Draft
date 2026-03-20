using System.Reflection;
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
        harmony.PatchAll(Assembly.GetExecutingAssembly());

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
