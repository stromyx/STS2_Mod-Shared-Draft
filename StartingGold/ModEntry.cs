using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Unlocks;

namespace StartingGold;

/// <summary>
/// Simple mod: sets starting gold to a configurable amount for all characters.
/// Uses Harmony postfix patch on Player.CreateForNewRun.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        // Load config
        StartingGoldConfig.LoadConfig();

        _harmony = new Harmony("com.guyinan.startinggold");
        _harmony.PatchAll(typeof(ModEntry).Assembly);

        // Register with ModConfig (deferred — wait 2 frames for ModConfig to load first)
        var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
        tree.CreateTimer(0).Timeout += () =>
        {
            tree.CreateTimer(0).Timeout += () => ModConfigBridge.Register();
        };

        Godot.GD.Print($"[StartingGold] Mod loaded! Starting gold will be set to {StartingGoldConfig.GoldAmount}.");
    }
}

/// <summary>
/// Patches Player.CreateForNewRun to override starting gold.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.CreateForNewRun), new[] { typeof(CharacterModel), typeof(UnlockState), typeof(ulong) })]
public static class StartingGoldPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __result)
    {
        __result.Gold = StartingGoldConfig.GoldAmount;
        Godot.GD.Print($"[StartingGold] Player gold set to {__result.Gold}");
    }
}
