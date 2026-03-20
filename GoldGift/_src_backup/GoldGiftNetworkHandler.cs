using Godot;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace GoldGift;

/// <summary>
/// Handles the network communication for gold gifting.
/// Uses reflection to safely access game APIs that may be internal.
/// 
/// In debug mode (config.json: "DebugMode": true), simulates multiplayer
/// with fake players so you can test the full UI/logic in single-player.
/// </summary>
public static class GoldGiftNetworkHandler
{
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
            if (runManager == null)
            {
                ModEntry.Logger.Error("RunManager not available.");
                return false;
            }

            if (!IsMultiplayerReal())
            {
                ModEntry.Logger.Error("Not in multiplayer mode.");
                return false;
            }

            int localIndex = GetLocalPlayerIndexReal();
            if (localIndex < 0)
            {
                ModEntry.Logger.Error("Could not determine local player index.");
                return false;
            }

            if (localIndex == targetIndex)
            {
                ModEntry.Logger.Info("Cannot gift gold to yourself.");
                return false;
            }

            int currentGold = GetPlayerGoldReal(localIndex);
            if (currentGold < amount)
            {
                ModEntry.Logger.Info($"Not enough gold. Have {currentGold}, need {amount}.");
                return false;
            }

            if (amount <= 0)
            {
                ModEntry.Logger.Info("Invalid amount.");
                return false;
            }

            SetPlayerGoldReal(localIndex, currentGold - amount);
            ModEntry.Logger.Info(
                $"Sent {amount} gold to player {targetIndex}. Remaining: {currentGold - amount}");

            int receiverGold = GetPlayerGoldReal(targetIndex);
            SetPlayerGoldReal(targetIndex, receiverGold + amount);
            ModEntry.Logger.Info(
                $"Player {targetIndex} received {amount} gold. New total: {receiverGold + amount}");

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

        return IsMultiplayerReal();
    }

    /// <summary>
    /// Get the local player's index.
    /// </summary>
    public static int GetLocalPlayerIndex()
    {
        if (GoldGiftConfig.DebugMode)
            return 0; // Local player is always index 0 in debug

        return GetLocalPlayerIndexReal();
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

        return GetPlayerGoldReal(playerIndex);
    }

    /// <summary>
    /// Set a player's gold amount by their index.
    /// </summary>
    public static void SetPlayerGold(int playerIndex, int amount)
    {
        if (GoldGiftConfig.DebugMode)
        {
            EnsureDebugInit();
            if (_debugGold != null)
            {
                _debugGold[playerIndex] = amount;
                ModEntry.Logger.Info(
                    $"[DEBUG] Player {playerIndex} gold set to {amount}");
            }
            return;
        }

        SetPlayerGoldReal(playerIndex, amount);
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
    //  REAL MULTIPLAYER — reflection-based access
    // ═══════════════════════════════════════════════

    private static bool IsMultiplayerReal()
    {
        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null) return false;

            var netServiceProp = runManager.GetType().GetProperty("NetService",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (netServiceProp == null) return false;

            var netService = netServiceProp.GetValue(runManager);
            return netService != null;
        }
        catch
        {
            return false;
        }
    }

    private static int GetLocalPlayerIndexReal()
    {
        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null) return -1;

            var netServiceProp = runManager.GetType().GetProperty("NetService",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (netServiceProp == null) return 0;

            var netService = netServiceProp.GetValue(runManager);
            if (netService == null) return 0;

            var slotProp = netService.GetType().GetProperty("SlotIndex",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? netService.GetType().GetProperty("LocalSlotId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? netService.GetType().GetProperty("PeerId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (slotProp != null)
            {
                return (int)slotProp.GetValue(netService)!;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetPlayerGoldReal(int playerIndex)
    {
        try
        {
            var player = GetPlayerByIndex(playerIndex);
            if (player == null) return 0;

            var goldProp = player.GetType().GetProperty("Gold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (goldProp != null)
            {
                return (int)goldProp.GetValue(player)!;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void SetPlayerGoldReal(int playerIndex, int amount)
    {
        try
        {
            var player = GetPlayerByIndex(playerIndex);
            if (player == null) return;

            var goldProp = player.GetType().GetProperty("Gold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (goldProp != null && goldProp.CanWrite)
            {
                goldProp.SetValue(player, amount);
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Failed to set gold: {ex.Message}");
        }
    }

    private static object? GetPlayerByIndex(int playerIndex)
    {
        try
        {
            var players = GetAllPlayers();
            if (players == null) return null;

            int index = 0;
            foreach (var player in players)
            {
                if (index == playerIndex)
                    return player;
                index++;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static System.Collections.IEnumerable? GetAllPlayers()
    {
        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null) return null;

            var playersProp = runManager.GetType().GetProperty("Players",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (playersProp != null)
            {
                return playersProp.GetValue(runManager) as System.Collections.IEnumerable;
            }

            var runProp = runManager.GetType().GetProperty("Run",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (runProp != null)
            {
                var run = runProp.GetValue(runManager);
                if (run != null)
                {
                    var runPlayersProp = run.GetType().GetProperty("Players",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (runPlayersProp != null)
                    {
                        return runPlayersProp.GetValue(run) as System.Collections.IEnumerable;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<PlayerInfo> GetOtherPlayersReal()
    {
        var result = new List<PlayerInfo>();

        try
        {
            var players = GetAllPlayers();
            if (players == null) return result;

            int localIndex = GetLocalPlayerIndexReal();
            int index = 0;

            foreach (var player in players)
            {
                if (index != localIndex)
                {
                    string name = $"Player {index + 1}";
                    var nameProp = player.GetType().GetProperty("Name",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        name = nameProp.GetValue(player)?.ToString() ?? name;
                    }

                    string charClass = "";
                    var charProp = player.GetType().GetProperty("Character",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (charProp != null)
                    {
                        var character = charProp.GetValue(player);
                        charClass = character?.GetType().Name ?? "";
                    }
                    else
                    {
                        var charIdProp = player.GetType().GetProperty("CharacterId",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (charIdProp != null)
                        {
                            charClass = charIdProp.GetValue(player)?.ToString() ?? "";
                        }
                    }

                    result.Add(new PlayerInfo
                    {
                        SlotId = index,
                        Name = name,
                        CharacterClass = charClass
                    });
                }

                index++;
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error getting player list: {ex.Message}");
        }

        return result;
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
