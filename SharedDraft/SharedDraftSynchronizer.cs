using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SharedDraft;

// ═══════════════════════════════════════════════════════════════════════
//  SHARED DRAFT SYNCHRONIZER
//
//  Handles multiplayer synchronization for shared card drafting.
//
//  DESIGN CONSTRAINT: The game's network serialization system uses
//  source-generated INetMessage/INetAction subtypes. Mods cannot register
//  new message or action types. Therefore, we use the following strategies:
//
//  Strategy A — "Piggybacking on PickRelicAction":
//    In real multiplayer, we reuse the existing PickRelicAction (normally
//    used for treasure room relic picking) as a transport mechanism.
//    The relicIndex field encodes the DraftId of the selected card.
//    We patch PickRelicAction.ExecuteAction() and
//    TreasureRoomRelicSynchronizer.OnPicked() to intercept these
//    "draft pick" actions when SharedDraft is active.
//
//  Strategy B — "Deterministic Consensus":
//    Since all clients see the same game state (same seed, same rewards),
//    the draft pool is built identically on all clients. Combined with
//    Strategy A for selection sync, all clients converge to the same state.
//
//  Strategy C — "Debug Mode":
//    Virtual players are simulated entirely locally with configurable
//    AI delay. No network traffic is needed.
//
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Synchronizes shared draft selections across multiplayer clients.
/// 
/// In real multiplayer:
///   - Local selection → PickRelicAction enqueued via ActionQueueSynchronizer
///   - Remote selection → Intercepted from PickRelicAction.ExecuteAction
///   - Draft pool is deterministically identical on all clients (same seed)
///
/// In debug mode:
///   - Virtual players simulated locally with random AI
///   - Configurable delay to simulate network latency / thinking time
/// </summary>
public class SharedDraftSynchronizer
{
    // ── Singleton ──
    private static SharedDraftSynchronizer? _instance;
    public static SharedDraftSynchronizer Instance => _instance ??= new SharedDraftSynchronizer();

    // ── State ──

    /// <summary>
    /// Whether we're currently in a shared draft sync session.
    /// When true, PickRelicAction interceptions are active.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Encoding offset applied to DraftId when piggybacking on PickRelicAction.
    /// We add this offset so the interceptor can distinguish draft picks from
    /// real relic picks (real relic indices are typically 0–3).
    /// </summary>
    private const int DraftIdEncodingOffset = 1000;

    /// <summary>
    /// Special encoded value used to signal "player is ready" (entered draft screen).
    /// DraftId = -1 → encoded = 1000 + (-1) = 999.
    /// This value doesn't collide with real DraftIds (which start at 0 and go up).
    /// </summary>
    private const int ReadySignalEncodedValue = 999;

    /// <summary>
    /// Maps player NetId → their submitted DraftId selection.
    /// Used to track who has selected what in multiplayer.
    /// </summary>
    private readonly Dictionary<ulong, int> _remoteSelections = new();

    /// <summary>
    /// Buffered READY signals received before BeginSync() was called.
    /// These are replayed immediately when BeginSync() activates.
    /// </summary>
    private readonly List<Player> _bufferedReadySignals = new();

    /// <summary>
    /// Buffered card selections received before BeginSync() was called.
    /// These are replayed immediately when BeginSync() activates.
    /// </summary>
    private readonly List<(Player player, int draftId)> _bufferedSelections = new();

    /// <summary>
    /// Event raised when a remote player's selection is received.
    /// The UI and manager can subscribe to this for updates.
    /// </summary>
    public event Action<int, int>? RemoteSelectionReceived; // (playerSlot, draftId)

    /// <summary>
    /// Event raised when a remote player's ready signal is received.
    /// </summary>
    public event Action<int>? ReadySignalReceived; // (playerSlot)

    // ═══════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Begin a synchronization session. Call when entering the Selecting phase.
    /// </summary>
    public void BeginSync()
    {
        if (IsActive)
        {
            ModEntry.Logger.Info("SharedDraftSynchronizer already active, resetting first.");
            EndSync();
        }

        IsActive = true;
        _remoteSelections.Clear();

        ModEntry.Logger.Info("SharedDraftSynchronizer: sync session started.");

        // Replay any READY signals that arrived before sync was active
        if (_bufferedReadySignals.Count > 0)
        {
            ModEntry.Logger.Info(
                $"Replaying {_bufferedReadySignals.Count} buffered READY signal(s).");
            foreach (var player in _bufferedReadySignals)
            {
                HandleNetworkReady(player);
            }
            _bufferedReadySignals.Clear();
        }

        // Replay any card selections that arrived before sync was active
        if (_bufferedSelections.Count > 0)
        {
            ModEntry.Logger.Info(
                $"Replaying {_bufferedSelections.Count} buffered selection(s).");
            foreach (var (player, draftId) in _bufferedSelections)
            {
                HandleNetworkSelection(player, draftId);
            }
            _bufferedSelections.Clear();
        }
    }

    /// <summary>
    /// End the current synchronization session.
    /// </summary>
    public void EndSync()
    {
        IsActive = false;
        _remoteSelections.Clear();
        _bufferedReadySignals.Clear();
        _bufferedSelections.Clear();

        ModEntry.Logger.Info("SharedDraftSynchronizer: sync session ended.");
    }

    /// <summary>
    /// Full reset — called from LifecyclePatch or when the run ends.
    /// </summary>
    public void Reset()
    {
        EndSync();
    }

    // ═══════════════════════════════════════════════
    //  SENDING READY SIGNAL (LOCAL → NETWORK)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Broadcast a "ready" signal to all other clients, indicating this player
    /// has entered the shared draft screen.
    /// 
    /// Uses custom DraftGameAction with full 32-bit serialization.
    /// In debug mode, this is a no-op (virtual players auto-ready).
    /// </summary>
    public void BroadcastReady()
    {
        if (SharedDraftConfig.DebugMode)
        {
            ModEntry.Logger.Info("[DEBUG] BroadcastReady: skipped (debug mode, virtual players auto-ready).");
            return;
        }

        try
        {
            Player? localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                ModEntry.Logger.Error("Cannot find local player for broadcasting ready signal.");
                return;
            }

            var draftAction = new DraftGameAction(localPlayer, DraftGameAction.ReadySignalValue);

            var actionQueueSync = GetActionQueueSynchronizer();
            if (actionQueueSync != null)
            {
                actionQueueSync.RequestEnqueue(draftAction);
                ModEntry.Logger.Info(
                    $"Broadcast ready signal via DraftGameAction");
            }
            else
            {
                ModEntry.Logger.Error("ActionQueueSynchronizer not available for ready broadcast.");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error broadcasting ready signal: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    //  SENDING OPT-OUT SIGNAL (LOCAL → NETWORK)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Broadcast an "opt-out" signal to all other clients, indicating this player
    /// has chosen to skip card selection (not pick any card).
    /// 
    /// Uses custom DraftGameAction with OptOutSignalValue (-2).
    /// In debug mode, this is handled locally without network.
    /// </summary>
    public void BroadcastOptOut()
    {
        if (SharedDraftConfig.DebugMode)
        {
            ModEntry.Logger.Info("[DEBUG] BroadcastOptOut: skipped (debug mode, handled locally).");
            return;
        }

        try
        {
            Player? localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                ModEntry.Logger.Error("Cannot find local player for broadcasting opt-out signal.");
                return;
            }

            var draftAction = new DraftGameAction(localPlayer, DraftGameAction.OptOutSignalValue);

            var actionQueueSync = GetActionQueueSynchronizer();
            if (actionQueueSync != null)
            {
                actionQueueSync.RequestEnqueue(draftAction);
                ModEntry.Logger.Info(
                    $"Broadcast opt-out signal via DraftGameAction");
            }
            else
            {
                ModEntry.Logger.Error("ActionQueueSynchronizer not available for opt-out broadcast.");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error broadcasting opt-out signal: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════

    /// <summary>
    /// Broadcast the local player's card IDs to all other clients.
    /// This allows remote clients to look up the actual CardModel objects
    /// and build a merged draft pool containing cards from all players.
    /// </summary>
    public void BroadcastLocalCardData(int localSlot, string[] cardEntries)
    {
        if (SharedDraftConfig.DebugMode)
        {
            ModEntry.Logger.Info("[DEBUG] BroadcastLocalCardData: skipped (debug mode).");
            return;
        }

        try
        {
            Player? localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                ModEntry.Logger.Error("Cannot find local player for broadcasting card data.");
                return;
            }

            var exchangeAction = new DraftCardExchangeGameAction(
                localPlayer, localSlot, cardEntries);

            var actionQueueSync = GetActionQueueSynchronizer();
            if (actionQueueSync != null)
            {
                actionQueueSync.RequestEnqueue(exchangeAction);
                ModEntry.Logger.Info(
                    $"Broadcast card data: slot={localSlot}, " +
                    $"cards=[{string.Join(", ", cardEntries)}]");
            }
            else
            {
                ModEntry.Logger.Error(
                    "ActionQueueSynchronizer not available for card data broadcast.");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error broadcasting card data: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle card data received from a remote player via network.
    /// Forwards to SharedDraftManager to add the remote player's cards to the draft pool.
    /// </summary>
    public void HandleRemoteCardData(int playerSlot, string[] cardEntries)
    {
        if (cardEntries == null || cardEntries.Length == 0)
        {
            ModEntry.Logger.Info(
                $"HandleRemoteCardData: empty card data for slot {playerSlot}, ignoring.");
            return;
        }

        ModEntry.Logger.Info(
            $"Received remote card data: slot={playerSlot}, " +
            $"cards=[{string.Join(", ", cardEntries)}]");

        SharedDraftManager.Instance.AddRemotePlayerCards(playerSlot, cardEntries);
    }

    // ═══════════════════════════════════════════════
    //  SENDING SELECTIONS (LOCAL → NETWORK)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Broadcast the local player's card selection to all other clients.
    /// 
    /// In real multiplayer, this enqueues a PickRelicAction with the DraftId
    /// encoded in the relicIndex field (with offset to distinguish from real picks).
    /// 
    /// In debug mode, this directly submits the selection to the manager.
    /// </summary>
    public void BroadcastLocalSelection(int draftId)
    {
        if (!IsActive)
        {
            ModEntry.Logger.Error("BroadcastLocalSelection called while sync is not active.");
            return;
        }

        if (SharedDraftConfig.DebugMode)
        {
            // In debug mode, just submit directly — no network needed
            int localSlot = SharedDraftManager.Instance.PlayerStates
                .FirstOrDefault(ps => SharedDraftManager.Instance.IsLocalPlayerState(ps))
                ?.PlayerSlot ?? 0;
            SharedDraftManager.Instance.SubmitLocalSelection(draftId);
            ModEntry.Logger.Info(
                $"[DEBUG] Local selection broadcast: slot={localSlot}, draftId={draftId}");
            return;
        }

        // Real multiplayer: use custom DraftGameAction with 32-bit serialization
        try
        {
            var runManager = RunManager.Instance;
            if (runManager == null)
            {
                ModEntry.Logger.Error("RunManager not available for broadcasting selection.");
                return;
            }

            // Get the local player
            Player? localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                ModEntry.Logger.Error("Cannot find local player for broadcasting.");
                return;
            }

            // Create and enqueue the DraftGameAction with the draftId directly
            var draftAction = new DraftGameAction(localPlayer, draftId);

            var actionQueueSync = GetActionQueueSynchronizer();
            if (actionQueueSync != null)
            {
                actionQueueSync.RequestEnqueue(draftAction);
                ModEntry.Logger.Info(
                    $"Broadcast selection via DraftGameAction: draftId={draftId}");
            }
            else
            {
                ModEntry.Logger.Error(
                    "ActionQueueSynchronizer not available. " +
                    "Falling back to local-only selection.");
                SharedDraftManager.Instance.SubmitLocalSelection(draftId);
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error broadcasting selection: {ex.Message}");
            // Fallback: submit locally only
            SharedDraftManager.Instance.SubmitLocalSelection(draftId);
        }
    }

    // ═══════════════════════════════════════════════
    //  RECEIVING SELECTIONS (NETWORK → LOCAL)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Called from our Harmony patch on PickRelicAction.ExecuteAction().
    /// When SharedDraft is active, intercepts the "relic pick" and decodes
    /// it as either a ready signal or a draft card selection.
    /// 
    /// Returns true if the action was intercepted (caller should skip original).
    /// Returns false if this is a real relic pick (not for us).
    /// </summary>
    public bool TryInterceptPickRelicAction(Player player, int relicIndex)
    {
        if (!IsActive)
            return false;

        // Check if this is a ready signal (encoded value = 999)
        if (relicIndex == ReadySignalEncodedValue)
        {
            int playerSlot = GetPlayerSlot(player);
            bool isLocal = IsLocalPlayer(player);

            ModEntry.Logger.Info(
                $"Intercepted READY signal from player {player.NetId} " +
                $"(slot {playerSlot}, isLocal={isLocal})");

            // Mark this player as ready in the manager
            SharedDraftManager.Instance.MarkPlayerReady(playerSlot);

            // Notify listeners
            ReadySignalReceived?.Invoke(playerSlot);

            return true; // Intercepted — don't run original PickRelicAction logic
        }

        // Check if this is an encoded draft pick (has our offset)
        if (!IsEncodedDraftId(relicIndex))
            return false;

        int draftId = DecodeDraftId(relicIndex);
        int slot = GetPlayerSlot(player);

        ModEntry.Logger.Info(
            $"Intercepted draft selection from player {player.NetId} " +
            $"(slot {slot}): draftId={draftId}");

        // Record the selection
        _remoteSelections[player.NetId] = draftId;

        // Check if this is the local player's own action coming back
        bool isLocalPlayer = IsLocalPlayer(player);

        if (isLocalPlayer)
        {
            SharedDraftManager.Instance.SubmitLocalSelection(draftId);
        }
        else
        {
            SharedDraftManager.Instance.SubmitRemoteSelection(slot, draftId);
        }

        // Notify listeners (UI update)
        RemoteSelectionReceived?.Invoke(slot, draftId);

        return true; // Intercepted — don't run original PickRelicAction logic
    }

    // ═══════════════════════════════════════════════
    //  NETWORK ACTION HANDLERS (called from DraftGameAction)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Handle a ready signal received via network (from DraftGameAction.ExecuteAction).
    /// </summary>
    public void HandleNetworkReady(Player player)
    {
        if (!IsActive)
        {
            // Buffer the signal for replay when BeginSync() is called
            ModEntry.Logger.Info(
                $"HandleNetworkReady: sync not active, buffering READY from player {player.NetId}.");
            _bufferedReadySignals.Add(player);
            return;
        }

        int playerSlot = GetPlayerSlot(player);
        bool isLocal = IsLocalPlayer(player);

        ModEntry.Logger.Info(
            $"Network READY from player {player.NetId} (slot {playerSlot}, isLocal={isLocal})");

        SharedDraftManager.Instance.MarkPlayerReady(playerSlot);
        ReadySignalReceived?.Invoke(playerSlot);
    }

    /// <summary>
    /// Handle a card selection received via network (from DraftGameAction.ExecuteAction).
    /// </summary>
    public void HandleNetworkSelection(Player player, int draftId)
    {
        if (!IsActive)
        {
            // Buffer the selection for replay when BeginSync() is called
            ModEntry.Logger.Info(
                $"HandleNetworkSelection: sync not active, buffering selection draftId={draftId} from player {player.NetId}.");
            _bufferedSelections.Add((player, draftId));
            return;
        }

        int slot = GetPlayerSlot(player);
        bool isLocalPlayer = IsLocalPlayer(player);

        ModEntry.Logger.Info(
            $"Network selection from player {player.NetId} (slot {slot}): draftId={draftId}");

        _remoteSelections[player.NetId] = draftId;

        if (isLocalPlayer)
        {
            SharedDraftManager.Instance.SubmitLocalSelection(draftId);
        }
        else
        {
            SharedDraftManager.Instance.SubmitRemoteSelection(slot, draftId);
        }

        RemoteSelectionReceived?.Invoke(slot, draftId);
    }

    /// <summary>
    /// Handle an opt-out signal received via network (from DraftGameAction.ExecuteAction).
    /// Marks the player as opted-out in the manager.
    /// </summary>
    public void HandleNetworkOptOut(Player player)
    {
        int playerSlot = GetPlayerSlot(player);
        bool isLocal = IsLocalPlayer(player);

        ModEntry.Logger.Info(
            $"Network OPT-OUT from player {player.NetId} (slot {playerSlot}, isLocal={isLocal})");

        if (isLocal)
        {
            SharedDraftManager.Instance.SubmitLocalOptOut();
        }
        else
        {
            SharedDraftManager.Instance.SubmitRemoteOptOut(playerSlot);
        }
    }

    // ═══════════════════════════════════════════════
    //  SENDING END-DRAFT SIGNAL (LOCAL → NETWORK)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Broadcast an "end draft" signal to all other clients, indicating this player
    /// wants to force settlement immediately (even if not all players have selected).
    /// 
    /// Uses custom DraftGameAction with EndDraftSignalValue (-3).
    /// In debug mode, this is handled locally without network.
    /// </summary>
    public void BroadcastEndDraft()
    {
        if (SharedDraftConfig.DebugMode)
        {
            ModEntry.Logger.Info("[DEBUG] BroadcastEndDraft: handled locally (debug mode).");
            SharedDraftManager.Instance.OnEndDraftRequested();
            return;
        }

        try
        {
            Player? localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                ModEntry.Logger.Error("Cannot find local player for broadcasting end-draft signal.");
                return;
            }

            var draftAction = new DraftGameAction(localPlayer, DraftGameAction.EndDraftSignalValue);

            var actionQueueSync = GetActionQueueSynchronizer();
            if (actionQueueSync != null)
            {
                actionQueueSync.RequestEnqueue(draftAction);
                ModEntry.Logger.Info(
                    $"Broadcast end-draft signal via DraftGameAction");
            }
            else
            {
                ModEntry.Logger.Error("ActionQueueSynchronizer not available for end-draft broadcast.");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error broadcasting end-draft signal: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle an end-draft signal received via network (from DraftGameAction.ExecuteAction).
    /// Sets the _endDraftRequested flag in the manager, which triggers immediate settlement.
    /// </summary>
    public void HandleNetworkEndDraft(Player player)
    {
        int playerSlot = GetPlayerSlot(player);
        bool isLocal = IsLocalPlayer(player);

        ModEntry.Logger.Info(
            $"Network END-DRAFT from player {player.NetId} (slot {playerSlot}, isLocal={isLocal})");

        SharedDraftManager.Instance.OnEndDraftRequested();
    }

    // ═══════════════════════════════════════════════
    //  DEBUG MODE — VIRTUAL PLAYER AI
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Simulate virtual player selections with configurable AI behavior.
    /// Called from SharedDraftManager during debug mode.
    /// 
    /// Each virtual player waits a random delay then picks from available cards.
    /// The selection strategy is configurable:
    ///   - Random: pick any available card
    ///   - Greedy: pick from own pool first
    ///   - Conflicting: intentionally pick popular cards (for testing RPS)
    /// </summary>
    public async Task SimulateVirtualPlayerSelections()
    {
        if (!SharedDraftConfig.DebugMode)
        {
            ModEntry.Logger.Error(
                "SimulateVirtualPlayerSelections called outside debug mode!");
            return;
        }

        var manager = SharedDraftManager.Instance;
        var playerStates = manager.PlayerStates;
        var rng = new Random();

        // Virtual players are slot 1+ (slot 0 is local)
        for (int i = 1; i < playerStates.Count; i++)
        {
            var ps = playerStates[i];
            if (ps.HasSelected)
                continue; // Already selected (e.g., from a previous round)

            // Simulate thinking delay
            int baseDelayMs = (int)(SharedDraftConfig.DebugAiDelaySeconds * 1000);
            int jitterMs = rng.Next(-500, 500);
            int delayMs = Math.Max(200, baseDelayMs + jitterMs);

            await Task.Delay(delayMs);

            // If the draft was cancelled while we were waiting, bail out
            if (manager.Phase != DraftPhase.Selecting &&
                manager.Phase != DraftPhase.Resolving)
            {
                ModEntry.Logger.Info(
                    $"[DEBUG AI] Phase changed to {manager.Phase} during delay, " +
                    $"stopping virtual player simulation.");
                break;
            }

            // Choose a card
            int draftId = SelectCardForVirtualPlayer(ps, rng);

            if (draftId >= 0)
            {
                manager.SubmitRemoteSelection(ps.PlayerSlot, draftId);
                var draftCard = manager.DraftPool
                    .FirstOrDefault(dc => dc.DraftId == draftId);

                ModEntry.Logger.Info(
                    $"[DEBUG AI] {ps.DisplayName} selected: " +
                    $"{draftCard?.CardEntry ?? "?"} (DraftId={draftId}) " +
                    $"after {delayMs}ms");
            }
            else
            {
                ModEntry.Logger.Info(
                    $"[DEBUG AI] {ps.DisplayName} found no available cards!");
            }
        }
    }

    /// <summary>
    /// Select a card for a virtual player using simple AI.
    /// Returns the DraftId of the selected card, or -1 if no card available.
    /// </summary>
    private int SelectCardForVirtualPlayer(PlayerDraftState playerState, Random rng)
    {
        var manager = SharedDraftManager.Instance;
        var available = manager.GetAvailableCardsForSelection();

        if (available.Count == 0)
            return -1;

        // Strategy: prefer cards from own pool, but allow cross-class picks
        var ownPoolCards = available
            .Where(dc => dc.OwnerPlayerSlot == playerState.PlayerSlot)
            .ToList();

        // 70% chance to pick from own pool if available, 30% pick any
        if (ownPoolCards.Count > 0 && rng.NextDouble() < 0.7)
        {
            return ownPoolCards[rng.Next(ownPoolCards.Count)].DraftId;
        }

        return available[rng.Next(available.Count)].DraftId;
    }

    /// <summary>
    /// Simulate virtual player re-selections after losing RPS.
    /// Similar to initial selection but with a shorter delay.
    /// </summary>
    public async Task SimulateVirtualPlayerReSelections(List<PlayerDraftState> losers)
    {
        if (!SharedDraftConfig.DebugMode)
            return;

        var manager = SharedDraftManager.Instance;
        var rng = new Random();

        // Track which DraftIds are already claimed (by players who won or selected)
        var claimedIds = manager.PlayerStates
            .Where(ps => ps.HasSelected)
            .Select(ps => ps.SelectedDraftId)
            .ToHashSet();

        foreach (var loser in losers)
        {
            // Only re-select for virtual players (not local player)
            if (loser.PlayerSlot == 0)
                continue;

            // Short delay for re-selection
            int delayMs = (int)(SharedDraftConfig.DebugAiDelaySeconds * 500);
            await Task.Delay(Math.Max(200, delayMs));

            // Get cards not claimed by winners
            var available = manager.DraftPool
                .Where(dc => !claimedIds.Contains(dc.DraftId))
                .ToList();

            if (available.Count > 0)
            {
                var chosen = available[rng.Next(available.Count)];
                loser.SelectedDraftId = chosen.DraftId;
                claimedIds.Add(chosen.DraftId);

                ModEntry.Logger.Info(
                    $"[DEBUG AI] {loser.DisplayName} re-selected: " +
                    $"{chosen.CardEntry} (DraftId={chosen.DraftId})");
            }
            else
            {
                ModEntry.Logger.Info(
                    $"[DEBUG AI] {loser.DisplayName} has no cards to re-select!");
            }
        }
    }

    // ═══════════════════════════════════════════════
    //  ENCODING / DECODING
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Encode a DraftId into a "relicIndex" for piggybacking on PickRelicAction.
    /// We add a large offset so values don't collide with real relic indices (0–3).
    /// </summary>
    private static int EncodeDraftId(int draftId)
    {
        return draftId + DraftIdEncodingOffset;
    }

    /// <summary>
    /// Decode a DraftId from an encoded "relicIndex".
    /// </summary>
    private static int DecodeDraftId(int encodedIndex)
    {
        return encodedIndex - DraftIdEncodingOffset;
    }

    /// <summary>
    /// Check if a relicIndex value is actually an encoded DraftId.
    /// Real relic indices are small (0–3), encoded ones are >= DraftIdEncodingOffset.
    /// </summary>
    public static bool IsEncodedDraftId(int relicIndex)
    {
        return relicIndex >= DraftIdEncodingOffset;
    }

    // ═══════════════════════════════════════════════
    //  HELPER METHODS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Get the ActionQueueSynchronizer from the TreasureRoomRelicSynchronizer.
    /// We access it via reflection since it's a private field.
    /// </summary>
    private ActionQueueSynchronizer? GetActionQueueSynchronizer()
    {
        try
        {
            var treasureSync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
            if (treasureSync == null)
                return null;

            // Access the private _actionQueueSynchronizer field
            var field = AccessTools.Field(
                typeof(TreasureRoomRelicSynchronizer),
                "_actionQueueSynchronizer");

            return field?.GetValue(treasureSync) as ActionQueueSynchronizer;
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error(
                $"Failed to get ActionQueueSynchronizer: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the local Player object.
    /// </summary>
    private Player? GetLocalPlayer()
    {
        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players != null && LocalContext.NetId.HasValue)
            {
                return players.FirstOrDefault(
                    p => p.NetId == LocalContext.NetId.Value);
            }
            return players?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if a Player is the local player.
    /// </summary>
    private bool IsLocalPlayer(Player player)
    {
        try
        {
            return LocalContext.IsMe(player);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the slot index for a Player within RunState.Players.
    /// </summary>
    private int GetPlayerSlot(Player player)
    {
        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i] == player || players[i].NetId == player.NetId)
                        return i;
                }
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    // ═══════════════════════════════════════════════
    //  HARMONY PATCHES (registered via ModEntry)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Harmony patch on PickRelicAction.ExecuteAction() to intercept
    /// draft card selections piggybacking on the relic pick mechanism.
    ///
    /// When SharedDraftSynchronizer is active:
    ///   - If the relicIndex is an encoded DraftId (>= 1000), intercept it
    ///   - Forward the selection to SharedDraftManager
    ///   - Skip the original relic-picking logic
    ///
    /// When not active, let the original method run normally.
    /// </summary>
    [HarmonyPatch(typeof(PickRelicAction), "ExecuteAction")]
    internal static class PickRelicActionPatch
    {
        /// <summary>
        /// Access the private _relicIndex field on PickRelicAction.
        /// </summary>
        private static readonly System.Reflection.FieldInfo? RelicIndexField =
            AccessTools.Field(typeof(PickRelicAction), "_relicIndex");

        /// <summary>
        /// Access the private _player field on PickRelicAction.
        /// </summary>
        private static readonly System.Reflection.FieldInfo? PlayerField =
            AccessTools.Field(typeof(PickRelicAction), "_player");

        static bool Prefix(PickRelicAction __instance, ref Task __result)
        {
            try
            {
                // Quick check: if not active, let original run
                if (!Instance.IsActive)
                    return true;

                // Read the private fields
                if (RelicIndexField == null || PlayerField == null)
                {
                    ModEntry.Logger.Error(
                        "PickRelicAction fields not found via reflection!");
                    return true;
                }

                int relicIndex = (int)RelicIndexField.GetValue(__instance)!;
                Player player = (Player)PlayerField.GetValue(__instance)!;

                // Try to intercept as a draft selection
                bool intercepted = Instance.TryInterceptPickRelicAction(
                    player, relicIndex);

                if (intercepted)
                {
                    // Skip original — return a completed task
                    __result = Task.CompletedTask;
                    return false;
                }

                // Not a draft pick — let original run
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Error(
                    $"Error in PickRelicAction.ExecuteAction Prefix: {ex.Message}");
                return true; // Fallback to original on error
            }
        }
    }

    /// <summary>
    /// Harmony patch on TreasureRoomRelicSynchronizer.OnPicked()
    /// to prevent it from processing our encoded draft picks as relic votes.
    ///
    /// Without this patch, the TreasureRoomRelicSynchronizer would try to
    /// use the encoded index (1000+) as a relic array index, causing
    /// IndexOutOfRangeException.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
    internal static class TreasureRelicOnPickedPatch
    {
        static bool Prefix(Player player, int index)
        {
            try
            {
                if (!Instance.IsActive)
                    return true;

                // If this is a ready signal or an encoded draft pick, skip the original
                if (index == ReadySignalEncodedValue || IsEncodedDraftId(index))
                {
                    ModEntry.Logger.Info(
                        $"Blocked encoded signal (index={index}) " +
                        $"from reaching TreasureRoomRelicSynchronizer.OnPicked");
                    return false;
                }

                // Real relic pick — let it through
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Error(
                    $"Error in OnPicked Prefix: {ex.Message}");
                return true;
            }
        }
    }
}
