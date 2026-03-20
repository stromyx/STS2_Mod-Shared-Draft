using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace GoldGift;

/// <summary>
/// Handles the network communication for gold gifting.
/// 
/// NETWORK SYNC DESIGN:
///   In multiplayer, directly modifying Player.Gold only changes the local memory copy.
///   The game does NOT auto-sync Gold changes to other clients. Therefore, we use
///   the same "piggybacking on PickRelicAction" strategy as SharedDraft:
///   
///   - Sender broadcasts a PickRelicAction with encoded gift info in the relicIndex field
///   - All clients receive the action via ActionQueueSynchronizer
///   - Our Harmony patch on PickRelicAction.ExecuteAction intercepts encoded values
///   - Each client applies the gold change locally (sender deducts, receiver adds)
///   
///   Encoding: relicIndex = GoldGiftEncodingBase + senderSlot * 1000 + targetSlot * 100 + amount
///   Range: 5000+ (distinct from SharedDraft's 1000+ range and real relic indices 0-3)
///   Max amount per transaction: 99. For larger amounts, we split into multiple actions.
///   
/// In debug mode (config.json: "DebugMode": true), simulates multiplayer
/// with fake players so you can test the full UI/logic in single-player.
/// </summary>
public static class GoldGiftNetworkHandler
{
    // ═══════════════════════════════════════════════
    //  ENCODING CONSTANTS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Base offset for gold gift encoding. Chosen to not collide with:
    ///   - Real relic indices (0-3)
    ///   - SharedDraft ready signal (999)  
    ///   - SharedDraft draft picks (1000+)
    /// </summary>
    private const int GoldGiftEncodingBase = 5000;

    /// <summary>
    /// Maximum amount encodable in a single action.
    /// With our custom NetGoldGiftAction using 32-bit serialization,
    /// encoding: sender*10000 + target*1000 + amount (amount 1-999).
    /// </summary>
    private const int MaxAmountPerAction = 999;

    // ═══════════════════════════════════════════════
    //  GIFT RECEIVED EVENT (for UI notification)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Raised when the local player receives a gold gift from another player.
    /// Args: (senderName, amount)
    /// </summary>
    public static event Action<string, int>? GoldGiftReceived;

    // ═══════════════════════════════════════════════
    //  DEBUG MODE — simulated player state
    // ═══════════════════════════════════════════════

    private static readonly string[] DebugPlayerNames =
        ["You (Debug)", "Alice", "Bob", "Charlie", "Diana", "Eve", "Frank", "Grace"];

    private static readonly string[] DebugCharClasses =
        ["Warrior", "Mage", "Rogue", "Cleric", "Ranger", "Bard", "Monk", "Paladin"];

    /// <summary>Gold amounts for each simulated player, keyed by player index.</summary>
    private static Dictionary<int, int>? _debugGold;

    private static bool _debugInitialized;

    /// <summary>
    /// Initialize debug player data if not already done.
    /// </summary>
    private static void EnsureDebugInit()
    {
        if (_debugInitialized) return;
        _debugInitialized = true;

        int count = Math.Clamp(GoldGiftConfig.DebugPlayerCount, 2, 8);
        int startGold = GoldGiftConfig.DebugStartingGold;

        _debugGold = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            _debugGold[i] = startGold;
        }

        ModEntry.Logger.Info(
            $"[DEBUG] Simulated {count} players with {startGold}g each.");
    }

    /// <summary>Reset debug state (e.g., when returning to main menu).</summary>
    public static void ResetDebug()
    {
        _debugInitialized = false;
        _debugGold = null;
    }

    // ═══════════════════════════════════════════════
    //  PUBLIC API — all methods check debug first
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Send a gold gift from the local player to a target player.
    /// In real multiplayer, broadcasts via PickRelicAction so all clients sync.
    /// </summary>
    public static bool SendGoldGift(int targetIndex, int amount)
    {
        // ── Debug path ──
        if (GoldGiftConfig.DebugMode)
        {
            EnsureDebugInit();
            return DebugSendGold(targetIndex, amount);
        }

        // ── Real multiplayer path ──
        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null || !runManager.IsInProgress)
            {
                ModEntry.Logger.Error("RunManager not available or no run in progress.");
                return false;
            }

            if (runManager.IsSinglePlayerOrFakeMultiplayer)
            {
                ModEntry.Logger.Error("Not in multiplayer mode.");
                return false;
            }

            var players = runManager.State?.Players;
            if (players == null || players.Count == 0)
            {
                ModEntry.Logger.Error("No players found.");
                return false;
            }

            int localIndex = GetLocalPlayerIndex();
            if (localIndex < 0 || localIndex >= players.Count)
            {
                ModEntry.Logger.Error("Could not determine local player index.");
                return false;
            }

            if (localIndex == targetIndex)
            {
                ModEntry.Logger.Info("Cannot gift gold to yourself.");
                return false;
            }

            if (targetIndex < 0 || targetIndex >= players.Count)
            {
                ModEntry.Logger.Error($"Invalid target player index: {targetIndex}");
                return false;
            }

            if (amount <= 0)
            {
                ModEntry.Logger.Info("Invalid amount.");
                return false;
            }

            var localPlayer = players[localIndex];
            if (localPlayer.Gold < amount)
            {
                ModEntry.Logger.Info($"Not enough gold. Have {localPlayer.Gold}, need {amount}.");
                return false;
            }

            // Broadcast via custom GoldGiftGameAction so all clients sync.
            // Uses our own NetGoldGiftAction with full 32-bit serialization,
            // unlike NetPickRelicAction which truncates to 8 bits.
            int remaining = amount;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, MaxAmountPerAction);
                int encoded = EncodeGoldGiftDirect(localIndex, targetIndex, chunk);

                var giftAction = new GoldGiftGameAction(localPlayer, encoded);
                var actionQueueSync = GetActionQueueSynchronizer();
                if (actionQueueSync != null)
                {
                    actionQueueSync.RequestEnqueue(giftAction);
                    ModEntry.Logger.Info(
                        $"Broadcast gold gift: {chunk}g to slot {targetIndex}, encoded={encoded}");
                }
                else
                {
                    // Fallback: apply locally only (other client won't get it)
                    ModEntry.Logger.Error("ActionQueueSynchronizer not available, applying locally only.");
                    ApplyGoldGiftLocally(localIndex, targetIndex, chunk);
                }

                remaining -= chunk;
            }

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Failed to send gold gift: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if we're in a multiplayer game (or debug mode).
    /// </summary>
    public static bool IsMultiplayer()
    {
        if (GoldGiftConfig.DebugMode)
            return true;

        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null || !runManager.IsInProgress)
                return false;

            return !runManager.IsSinglePlayerOrFakeMultiplayer;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the local player's index.
    /// </summary>
    public static int GetLocalPlayerIndex()
    {
        if (GoldGiftConfig.DebugMode)
            return 0; // Local player is always index 0 in debug

        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null || !runManager.IsInProgress)
                return 0;

            ulong localNetId = runManager.NetService.NetId;
            var players = runManager.State?.Players;
            if (players == null) return 0;

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].NetId == localNetId)
                    return i;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Get a player's gold amount by their index.
    /// </summary>
    public static int GetPlayerGold(int playerIndex)
    {
        if (GoldGiftConfig.DebugMode)
        {
            EnsureDebugInit();
            return _debugGold?.GetValueOrDefault(playerIndex, 0) ?? 0;
        }

        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players == null || playerIndex < 0 || playerIndex >= players.Count)
                return 0;

            return players[playerIndex].Gold;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Get the local player's gold for UI display.
    /// </summary>
    public static int GetLocalGold()
    {
        return GetPlayerGold(GetLocalPlayerIndex());
    }

    /// <summary>
    /// Get all other player info for the gift UI.
    /// </summary>
    public static List<PlayerInfo> GetOtherPlayers()
    {
        if (GoldGiftConfig.DebugMode)
        {
            EnsureDebugInit();
            return DebugGetOtherPlayers();
        }

        return GetOtherPlayersReal();
    }

    // ═══════════════════════════════════════════════
    //  ENCODING / DECODING (for custom NetGoldGiftAction)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Encode a gold gift into a single int for NetGoldGiftAction.encodedGift.
    /// Format: senderSlot * 10000 + targetSlot * 1000 + amount
    /// Supports: senderSlot 0-8, targetSlot 0-8, amount 1-999
    /// </summary>
    internal static int EncodeGoldGiftDirect(int senderSlot, int targetSlot, int amount)
    {
        return senderSlot * 10000 + targetSlot * 1000 + amount;
    }

    /// <summary>
    /// Decode a gold gift from the encoded value (used by GoldGiftGameAction).
    /// Returns (senderSlot, targetSlot, amount).
    /// </summary>
    public static (int senderSlot, int targetSlot, int amount) DecodeGoldGiftDirect(int encodedValue)
    {
        int senderSlot = encodedValue / 10000;
        int remainder = encodedValue % 10000;
        int targetSlot = remainder / 1000;
        int amount = remainder % 1000;
        return (senderSlot, targetSlot, amount);
    }

    // ═══════════════════════════════════════════════
    //  LEGACY ENCODING (kept for PickRelicAction Harmony patches)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Encode a gold gift into a single int for PickRelicAction.relicIndex (LEGACY).
    /// NOTE: This encoding is only used by the Harmony patch interceptor to detect
    /// and skip gold gift signals that may have been sent from older versions.
    /// New code uses EncodeGoldGiftDirect + custom NetGoldGiftAction instead.
    /// </summary>
    private static int EncodeGoldGift(int senderSlot, int targetSlot, int amount)
    {
        return GoldGiftEncodingBase + senderSlot * 1000 + targetSlot * 100 + amount;
    }

    /// <summary>
    /// Decode a gold gift from the encoded relicIndex (LEGACY).
    /// </summary>
    private static (int senderSlot, int targetSlot, int amount) DecodeGoldGift(int encodedValue)
    {
        int val = encodedValue - GoldGiftEncodingBase;
        int senderSlot = val / 1000;
        val %= 1000;
        int targetSlot = val / 100;
        int amount = val % 100;
        return (senderSlot, targetSlot, amount);
    }

    /// <summary>
    /// Check if an encoded relicIndex is a gold gift signal (LEGACY).
    /// </summary>
    public static bool IsEncodedGoldGift(int relicIndex)
    {
        if (relicIndex < GoldGiftEncodingBase) return false;
        int val = relicIndex - GoldGiftEncodingBase;
        int senderSlot = val / 1000;
        if (senderSlot > 8) return false;
        val %= 1000;
        int targetSlot = val / 100;
        if (targetSlot > 8) return false;
        int amount = val % 100;
        return amount > 0 && amount <= 99;
    }

    // ═══════════════════════════════════════════════
    //  APPLYING GOLD CHANGES
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Apply a gold gift locally (called from Harmony patch on all clients).
    /// Each client runs this independently to keep their local state in sync.
    /// </summary>
    internal static void ApplyGoldGiftLocally(int senderSlot, int targetSlot, int amount)
    {
        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players == null) return;

            if (senderSlot < 0 || senderSlot >= players.Count ||
                targetSlot < 0 || targetSlot >= players.Count)
            {
                ModEntry.Logger.Error(
                    $"Invalid slots for gold gift: sender={senderSlot}, target={targetSlot}, " +
                    $"playerCount={players.Count}");
                return;
            }

            var sender = players[senderSlot];
            var target = players[targetSlot];

            sender.Gold -= amount;
            target.Gold += amount;

            // Check if the local player is the receiver — fire event for UI notification
            int localIndex = GetLocalPlayerIndex();
            if (targetSlot == localIndex)
            {
                string senderName = GetSafePlayerName(sender, senderSlot);
                ModEntry.Logger.Info(
                    $"Received {amount}g from {senderName}! New balance: {target.Gold}g");
                GoldGiftReceived?.Invoke(senderName, amount);
            }
            else if (senderSlot == localIndex)
            {
                string targetName = GetSafePlayerName(target, targetSlot);
                ModEntry.Logger.Info(
                    $"Sent {amount}g to {targetName}. New balance: {sender.Gold}g");
            }
            else
            {
                ModEntry.Logger.Info(
                    $"Observed gold gift: slot {senderSlot} → slot {targetSlot}, {amount}g");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error applying gold gift: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    //  HELPER — safe player name
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Get a safe display name for a player.
    /// Priority: Steam nickname → Character title → Steam NetId fallback.
    /// </summary>
    private static string GetSafePlayerName(Player player, int slotIndex)
    {
        // Priority 1: Steam nickname via PlatformUtil
        try
        {
            string steamName = PlatformUtil.GetPlayerName(PlatformType.Steam, player.NetId);
            if (!string.IsNullOrEmpty(steamName) &&
                steamName != player.NetId.ToString()) // PlatformUtil returns NetId string as fallback
            {
                return steamName;
            }
        }
        catch { /* Steam API may not be available */ }

        // Priority 2: Character title
        try
        {
            var titleLoc = player.Character?.Title;
            if (titleLoc != null)
            {
                string charTitle = titleLoc.ToString() ?? "";
                if (!string.IsNullOrEmpty(charTitle) && 
                    !charTitle.Contains("LocString") && 
                    !charTitle.Contains("MegaCrit"))
                {
                    return charTitle;
                }
            }
        }
        catch { /* ignore */ }

        // Fallback: use Steam NetId
        return $"Player {player.NetId}";
    }

    // ═══════════════════════════════════════════════
    //  HELPER — ActionQueueSynchronizer access
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Get the ActionQueueSynchronizer from TreasureRoomRelicSynchronizer.
    /// Same approach as SharedDraft.
    /// </summary>
    private static ActionQueueSynchronizer? GetActionQueueSynchronizer()
    {
        try
        {
            var treasureSync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
            if (treasureSync == null)
                return null;

            var field = AccessTools.Field(
                typeof(TreasureRoomRelicSynchronizer),
                "_actionQueueSynchronizer");

            return field?.GetValue(treasureSync) as ActionQueueSynchronizer;
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Failed to get ActionQueueSynchronizer: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════
    //  DEBUG — simulated operations
    // ═══════════════════════════════════════════════

    private static bool DebugSendGold(int targetIndex, int amount)
    {
        if (_debugGold == null) return false;

        const int localIndex = 0;

        if (localIndex == targetIndex)
        {
            ModEntry.Logger.Info("[DEBUG] Cannot gift gold to yourself.");
            return false;
        }

        if (amount <= 0)
        {
            ModEntry.Logger.Info("[DEBUG] Invalid amount.");
            return false;
        }

        int currentGold = _debugGold.GetValueOrDefault(localIndex, 0);
        if (currentGold < amount)
        {
            ModEntry.Logger.Info(
                $"[DEBUG] Not enough gold. Have {currentGold}, need {amount}.");
            return false;
        }

        if (!_debugGold.ContainsKey(targetIndex))
        {
            ModEntry.Logger.Error($"[DEBUG] Target player {targetIndex} does not exist.");
            return false;
        }

        _debugGold[localIndex] = currentGold - amount;
        _debugGold[targetIndex] = _debugGold.GetValueOrDefault(targetIndex, 0) + amount;

        string targetName = targetIndex < DebugPlayerNames.Length
            ? DebugPlayerNames[targetIndex]
            : $"Player {targetIndex + 1}";

        ModEntry.Logger.Info(
            $"[DEBUG] Sent {amount}g to {targetName}. " +
            $"You: {_debugGold[localIndex]}g, {targetName}: {_debugGold[targetIndex]}g");

        return true;
    }

    private static List<PlayerInfo> DebugGetOtherPlayers()
    {
        var result = new List<PlayerInfo>();
        if (_debugGold == null) return result;

        int count = Math.Clamp(GoldGiftConfig.DebugPlayerCount, 2, 8);
        for (int i = 1; i < count; i++) // skip index 0 (local player)
        {
            result.Add(new PlayerInfo
            {
                SlotId = i,
                Name = i < DebugPlayerNames.Length ? DebugPlayerNames[i] : $"Player {i + 1}",
                CharacterClass = i < DebugCharClasses.Length ? DebugCharClasses[i] : "Unknown",
                Gold = _debugGold.GetValueOrDefault(i, 0)
            });
        }

        return result;
    }

    // ═══════════════════════════════════════════════
    //  REAL MULTIPLAYER — player info
    // ═══════════════════════════════════════════════

    private static List<PlayerInfo> GetOtherPlayersReal()
    {
        var result = new List<PlayerInfo>();

        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players == null) return result;

            int localIndex = GetLocalPlayerIndex();

            for (int i = 0; i < players.Count; i++)
            {
                if (i != localIndex)
                {
                    var player = players[i];
                    
                    // Get player name — priority: Steam nickname > Character title > NetId
                    string name = "";
                    
                    // Priority 1: Steam nickname
                    try
                    {
                        string steamName = PlatformUtil.GetPlayerName(PlatformType.Steam, player.NetId);
                        if (!string.IsNullOrEmpty(steamName) &&
                            steamName != player.NetId.ToString())
                        {
                            name = steamName;
                        }
                    }
                    catch { /* Steam API may not be available */ }
                    
                    // Priority 2: Character title
                    if (string.IsNullOrEmpty(name))
                    {
                        try
                        {
                            var titleLoc = player.Character?.Title;
                            if (titleLoc != null)
                            {
                                string charTitle = titleLoc.ToString() ?? "";
                                if (!string.IsNullOrEmpty(charTitle) && 
                                    !charTitle.Contains("LocString") && 
                                    !charTitle.Contains("MegaCrit"))
                                {
                                    name = charTitle;
                                }
                            }
                        }
                        catch { /* ignore */ }
                    }
                    
                    // Fallback: use Steam NetId as display name
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Player {player.NetId}";
                    }
                    
                    string charClass = "";
                    try
                    {
                        charClass = player.Character?.GetType().Name ?? "";
                    }
                    catch { /* ignore */ }

                    result.Add(new PlayerInfo
                    {
                        SlotId = i,
                        Name = name,
                        CharacterClass = charClass,
                        Gold = player.Gold
                    });
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error getting player list: {ex.Message}");
        }

        return result;
    }

    // ═══════════════════════════════════════════════
    //  HARMONY PATCHES — intercept PickRelicAction for gold gifts
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Harmony patch on PickRelicAction.ExecuteAction() to intercept
    /// gold gift signals piggybacking on the relic pick mechanism.
    /// 
    /// When the relicIndex is an encoded gold gift (5000+):
    ///   - Decode sender, target, amount
    ///   - Apply gold changes locally
    ///   - Skip the original relic-picking logic
    /// </summary>
    [HarmonyPatch(typeof(PickRelicAction), "ExecuteAction")]
    internal static class GoldGiftPickRelicPatch
    {
        private static readonly System.Reflection.FieldInfo? RelicIndexField =
            AccessTools.Field(typeof(PickRelicAction), "_relicIndex");

        private static readonly System.Reflection.FieldInfo? PlayerField =
            AccessTools.Field(typeof(PickRelicAction), "_player");

        static bool Prefix(PickRelicAction __instance, ref Task __result)
        {
            try
            {
                if (RelicIndexField == null || PlayerField == null)
                    return true;

                int? relicIndexNullable = (int?)RelicIndexField.GetValue(__instance);

                // If index is null (skip/pass), let original handle it
                if (!relicIndexNullable.HasValue)
                    return true;

                int relicIndex = relicIndexNullable.Value;

                // Only intercept our gold gift range
                if (!IsEncodedGoldGift(relicIndex))
                    return true; // Not ours — let SharedDraft or original handle it

                var (senderSlot, targetSlot, amount) = DecodeGoldGift(relicIndex);

                ModEntry.Logger.Info(
                    $"Intercepted gold gift action: sender={senderSlot}, " +
                    $"target={targetSlot}, amount={amount}");

                ApplyGoldGiftLocally(senderSlot, targetSlot, amount);

                // Skip original PickRelicAction logic
                __result = Task.CompletedTask;
                return false;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Error(
                    $"Error in GoldGift PickRelicAction Prefix: {ex.Message}");
                return true;
            }
        }
    }

    /// <summary>
    /// Harmony patch on TreasureRoomRelicSynchronizer.OnPicked()
    /// to prevent gold gift encoded values from causing IndexOutOfRangeException.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer),
        nameof(TreasureRoomRelicSynchronizer.OnPicked))]
    internal static class GoldGiftTreasureRelicOnPickedPatch
    {
        static bool Prefix(Player player, int? index)
        {
            try
            {
                if (!index.HasValue)
                    return true; // null index (skip) — let original handle it

                if (IsEncodedGoldGift(index.Value))
                {
                    ModEntry.Logger.Info(
                        $"Blocked gold gift encoded index ({index.Value}) " +
                        $"from TreasureRoomRelicSynchronizer.OnPicked");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Error(
                    $"Error in GoldGift OnPicked Prefix: {ex.Message}");
                return true;
            }
        }
    }
}

/// <summary>
/// Simple data class for player display info.
/// </summary>
public class PlayerInfo
{
    public int SlotId { get; set; }
    public string Name { get; set; } = "";
    public string CharacterClass { get; set; } = "";
    public int Gold { get; set; } = 0;
}
