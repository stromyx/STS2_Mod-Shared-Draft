using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SharedDraft.UI;

namespace SharedDraft;

// ═══════════════════════════════════════════════════════════════════════
//  DATA TYPES
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents a single card in the shared draft pool with its provenance.
/// 
/// For local player's cards, Card is always non-null (real CardModel from CardReward).
/// For remote player's cards, Card may be null — only CardEntry is available.
/// The UI will display whatever info is available (full card details or just entry name).
/// 
/// DraftId is assigned deterministically across all clients by sorting cards
/// by OwnerPlayerSlot first, then by position within that player's card list.
/// This ensures all clients have the same DraftId → card mapping.
/// </summary>
public class DraftCard
{
    /// <summary>The game's card model. Null for remote player's cards where lookup failed.</summary>
    public CardModel? Card { get; init; }

    /// <summary>Card entry string (e.g., "POMMEL_STRIKE"). Always available, even for remote cards.</summary>
    public required string CardEntry { get; init; }

    /// <summary>The player slot index that originally generated this card reward.</summary>
    public required int OwnerPlayerSlot { get; init; }

    /// <summary>Index within the original CardReward's card list.</summary>
    public required int OriginalIndex { get; init; }

    /// <summary>Globally unique identifier for this draft card (used in sync messages).</summary>
    public required int DraftId { get; init; }

    /// <summary>Whether this card has a real CardModel (local cards always do, remote cards may not).</summary>
    public bool HasCardModel => Card != null;
}

/// <summary>
/// Tracks one player's state within the current draft round.
/// </summary>
public class PlayerDraftState
{
    /// <summary>Player slot index (0-based, matches RunState.Players order).</summary>
    public int PlayerSlot { get; init; }

    /// <summary>Reference to the actual Player object (null for debug virtual players).</summary>
    public Player? Player { get; init; }

    /// <summary>Display name for logging/UI.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>The DraftId this player has selected, or -1 if not yet selected.</summary>
    public int SelectedDraftId { get; set; } = -1;

    /// <summary>The final card this player was awarded after conflict resolution.</summary>
    public CardModel? AwardedCard { get; set; }

    /// <summary>Whether this player has been awarded their card already.</summary>
    public bool IsAwarded => AwardedCard != null;

    /// <summary>Whether this player has made a selection.</summary>
    public bool HasSelected => SelectedDraftId >= 0;

    /// <summary>Whether this player has entered the shared draft screen (is ready).</summary>
    public bool IsReady { get; set; } = false;

    /// <summary>Whether this player was opted-out due to timeout (didn't enter in time).</summary>
    public bool IsOptedOut { get; set; } = false;

    /// <summary>Whether this player has completed the draft (awarded card and exited, or opted out).</summary>
    public bool IsCompleted { get; set; } = false;

    // ── Derived state helpers for the new 3-state model ──

    /// <summary>未进入: not ready and not completed.</summary>
    public bool IsNotEntered => !IsReady && !IsCompleted;

    /// <summary>选择中: ready, not selected, not completed.</summary>
    public bool IsSelecting => IsReady && !HasSelected && !IsCompleted;

    /// <summary>已选择: ready, has selected, not completed.</summary>
    public bool IsSelectedState => IsReady && HasSelected && !IsCompleted;

    /// <summary>Reset this player back to Selecting state (for losers after conflict).</summary>
    public void ResetToSelecting()
    {
        SelectedDraftId = -1;
        AwardedCard = null;
        // IsReady stays true, IsCompleted stays false
    }
}

/// <summary>
/// State machine phases for the shared draft.
/// </summary>
public enum DraftPhase
{
    /// <summary>Not active — normal reward flow.</summary>
    Inactive,

    /// <summary>Collecting CardRewards from each player's RewardsSet.Offer calls.</summary>
    Collecting,

    /// <summary>Waiting for all players to enter the shared draft screen (click CardReward button).</summary>
    WaitingForReady,

    /// <summary>Players are choosing cards from the merged pool. No time limit.</summary>
    Selecting,

    /// <summary>Conflicts detected — running rock/paper/scissors resolution.</summary>
    Resolving,

    /// <summary>Awarding cards to each player's deck.</summary>
    Awarding,

    /// <summary>Draft round complete, cleaning up.</summary>
    Complete
}

/// <summary>
/// Describes the outcome of a single conflict (multiple players picking the same card).
/// </summary>
public class ConflictResult
{
    /// <summary>The draft card that was contested.</summary>
    public required DraftCard ContestedCard { get; init; }

    /// <summary>Players who competed for this card.</summary>
    public required List<PlayerDraftState> Competitors { get; init; }

    /// <summary>The winner of the rock/paper/scissors fight.</summary>
    public required PlayerDraftState Winner { get; init; }

    /// <summary>Players who lost and must re-pick.</summary>
    public required List<PlayerDraftState> Losers { get; init; }

    /// <summary>The fight details (rounds, moves), if available.</summary>
    public RelicPickingFight? Fight { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════
//  SHARED DRAFT MANAGER — singleton state machine
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Core manager for the SharedDraft mod. Orchestrates:
///   1. Collecting each player's CardReward cards into a merged pool
///   2. Managing player selections (local + remote/debug)
///   3. Detecting conflicts (multiple players picking the same card)
///   4. Resolving conflicts via rock/paper/scissors (reusing game's RelicPickingResult)
///   5. Awarding final cards to each player's deck
///
/// Thread-safety note: All operations run on Godot's main thread (single-threaded).
/// </summary>
public class SharedDraftManager
{
    // ── Singleton ──
    private static SharedDraftManager? _instance;
    public static SharedDraftManager Instance => _instance ??= new SharedDraftManager();

    // ── State machine ──
    public DraftPhase Phase { get; private set; } = DraftPhase.Inactive;

    // ── Draft pool ──
    private readonly List<DraftCard> _draftPool = new();
    private int _nextDraftId;

    /// <summary>Read-only view of the current merged draft pool.</summary>
    public IReadOnlyList<DraftCard> DraftPool => _draftPool;

    // ── Player states ──
    private readonly List<PlayerDraftState> _playerStates = new();
    public IReadOnlyList<PlayerDraftState> PlayerStates => _playerStates;

    // ── Collected CardRewards (before merge) ──
    private readonly Dictionary<int, List<CardReward>> _collectedRewards = new();

    // ── Number of players expected (real or debug) ──
    private int _expectedPlayerCount;

    // ── Task completion source for async coordination ──
    private TaskCompletionSource<bool>? _draftCompletionSource;

    // ── CancellationTokenSource for aborting async flow on Reset ──
    private CancellationTokenSource? _draftCancellation;

    // ── Conflict results from the last resolution round ──
    private readonly List<ConflictResult> _lastConflictResults = new();
    public IReadOnlyList<ConflictResult> LastConflictResults => _lastConflictResults;

    // ── Ready tracking for WaitingForReady phase ──
    private readonly HashSet<int> _readyPlayers = new();

    // ── Awarded card tracking (cards that have been given out and removed from pool) ──
    private readonly HashSet<int> _awardedDraftIds = new();

    /// <summary>Read-only view of awarded draft IDs (for UI grey-out).</summary>
    public IReadOnlyCollection<int> AwardedDraftIds => _awardedDraftIds;

    // ── "End Draft" signal flag ──
    private volatile bool _endDraftRequested = false;

    /// <summary>
    /// Called when any player requests to end the draft early (force settlement).
    /// Sets the flag that WaitForSettlement checks.
    /// </summary>
    public void OnEndDraftRequested()
    {
        if (!_endDraftRequested)
        {
            _endDraftRequested = true;
            ModEntry.Logger.Info("End draft requested — will trigger settlement on next check.");
        }
    }

    // ── Remote card data tracking (for merged pool building) ──
    private readonly Dictionary<int, string[]> _remoteCardEntries = new();
    private int _expectedRemoteCardDataCount;

    /// <summary>
    /// Buffer for remote card data that arrives BEFORE RegisterPlayerCardRewards
    /// is called (before BuildDraftPool). This can happen because the ActionQueue
    /// may deliver the remote DraftCardExchangeGameAction before the local 
    /// RewardsSet.Offer() postfix fires.
    /// </summary>
    private readonly Dictionary<int, string[]> _earlyRemoteCardBuffer = new();

    /// <summary>Whether all remote players' card data has been received.</summary>
    public bool AllRemoteCardDataReceived =>
        _remoteCardEntries.Count >= _expectedRemoteCardDataCount;

    // ═══════════════════════════════════════════════
    //  ACTIVATION CHECK
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Determines whether SharedDraft should activate for the current game state.
    /// Returns true if we're in multiplayer OR if DebugMode is enabled.
    /// </summary>
    public static bool ShouldActivate()
    {
        if (SharedDraftConfig.DebugMode)
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

    // ═══════════════════════════════════════════════
    //  1. COLLECTION PHASE
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Called from RewardsPatch.OfferPatch.Prefix — registers one player's CardRewards
    /// for the shared draft pool. Once all expected players have registered, the
    /// draft pool is built and phase transitions to Selecting.
    /// </summary>
    public void RegisterPlayerCardRewards(Player player, List<CardReward> cardRewards)
    {
        if (Phase != DraftPhase.Inactive && Phase != DraftPhase.Collecting)
        {
            ModEntry.Logger.Info(
                $"RegisterPlayerCardRewards called in unexpected phase {Phase}, " +
                "resetting first.");
            Reset();
        }

        if (Phase == DraftPhase.Inactive)
        {
            BeginCollecting();
        }

        int playerSlot = GetPlayerSlot(player);
        if (_collectedRewards.ContainsKey(playerSlot))
        {
            ModEntry.Logger.Info(
                $"Player slot {playerSlot} already registered, skipping duplicate.");
            return;
        }

        _collectedRewards[playerSlot] = cardRewards;

        ModEntry.Logger.Info(
            $"Registered {cardRewards.Count} CardReward(s) for slot {playerSlot} " +
            $"({_collectedRewards.Count}/{_expectedPlayerCount} players registered).");

        // Check if all players have registered
        if (_collectedRewards.Count >= _expectedPlayerCount)
        {
            BuildDraftPool();
            TransitionTo(DraftPhase.WaitingForReady);
        }
    }

    /// <summary>
    /// Initialize the collecting phase — determine expected player count.
    /// 
    /// IMPORTANT: _expectedPlayerCount is ALWAYS set to 1 (the local player only),
    /// because in both debug mode and real multiplayer, only 1 local RewardsSet.Offer 
    /// call will happen on this client. All clients share the same seed (deterministic 
    /// consensus), so BuildDraftPool() can reconstruct the full card pool for all players.
    /// </summary>
    private void BeginCollecting()
    {
        TransitionTo(DraftPhase.Collecting);

        // Only the local player will register real CardRewards via RewardsSet.Offer.
        // BuildDraftPool() will duplicate cards for all other players using the 
        // deterministic consensus (same seed = same cards).
        _expectedPlayerCount = 1;

        InitializePlayerStates();
    }

    /// <summary>
    /// Set up PlayerDraftState entries for each player.
    /// Note: _playerStates should contain ALL real players (or debug simulated players),
    /// independent of _expectedPlayerCount (which only controls the Offer collection threshold).
    /// </summary>
    private void InitializePlayerStates()
    {
        _playerStates.Clear();

        if (SharedDraftConfig.DebugMode)
        {
            int debugCount = SharedDraftConfig.DebugPlayerCount;
            string[] debugNames = ["You (Local)", "Alice", "Bob", "Charlie"];
            for (int i = 0; i < debugCount; i++)
            {
                Player? realPlayer = null;
                try
                {
                    var players = RunManager.Instance?.State?.Players;
                    if (players != null && i < players.Count)
                        realPlayer = players[i];
                }
                catch { /* ignore in debug mode */ }

                _playerStates.Add(new PlayerDraftState
                {
                    PlayerSlot = i,
                    Player = realPlayer,
                    DisplayName = i < debugNames.Length ? debugNames[i] : $"Player {i + 1}"
                });
            }
        }
        else
        {
            var players = RunManager.Instance?.State?.Players;
            if (players == null) return;

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                string name = $"Player {player.NetId}";

                // Priority 1: Steam nickname via PlatformUtil
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

                // Priority 2: Character title (fallback if Steam name unavailable)
                if (name == $"Player {player.NetId}")
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

                _playerStates.Add(new PlayerDraftState
                {
                    PlayerSlot = i,
                    Player = player,
                    DisplayName = name
                });

                ModEntry.Logger.Info(
                    $"Player slot {i}: NetId={player.NetId}, DisplayName='{name}'");
            }
        }
    }

    /// <summary>
    /// Build the draft pool from local player's cards and any already-buffered remote data.
    /// Remote players' cards that haven't arrived yet will be added later via AddRemotePlayerCards.
    /// 
    /// CRITICAL: DraftId assignment is DETERMINISTIC — cards are ordered by playerSlot
    /// (ascending), then by position within that player's card list. This ensures all
    /// clients assign the same DraftId to the same logical card, even though each client
    /// independently builds its own pool.
    /// 
    /// In debug mode, duplicates local cards for virtual players (old behavior).
    /// In real multiplayer, broadcasts local card IDs and integrates buffered remote data.
    /// </summary>
    private void BuildDraftPool()
    {
        _draftPool.Clear();
        _nextDraftId = 0;
        _remoteCardEntries.Clear();

        if (_collectedRewards.Count == 0)
        {
            ModEntry.Logger.Error("BuildDraftPool called with no collected rewards!");
            return;
        }

        var localEntry = _collectedRewards.First();
        int localSlot = localEntry.Key;
        int totalPlayers = _playerStates.Count;

        if (SharedDraftConfig.DebugMode)
        {
            // In debug mode, duplicate local cards for virtual players
            // DraftId order: slot 0 cards first, slot 1 cards next, etc.
            for (int slot = 0; slot < totalPlayers; slot++)
            {
                foreach (var cardReward in localEntry.Value)
                {
                    foreach (var card in cardReward.Cards)
                    {
                        _draftPool.Add(new DraftCard
                        {
                            Card = card,
                            CardEntry = card.Id.Entry,
                            OwnerPlayerSlot = slot,
                            OriginalIndex = _draftPool.Count,
                            DraftId = _nextDraftId++
                        });
                    }
                }
            }

            _expectedRemoteCardDataCount = 0;
            ModEntry.Logger.Info(
                $"Draft pool built (debug): {_draftPool.Count} cards for " +
                $"{totalPlayers} player(s).");
        }
        else
        {
            // ── Real multiplayer: deterministic DraftId assignment ──
            // Cards MUST be ordered by slot ascending to ensure consistency.
            // Each client independently builds the pool in the same order.

            // Collect local card entry strings for broadcasting
            var localCardEntries = new List<string>();
            foreach (var cardReward in localEntry.Value)
            {
                foreach (var card in cardReward.Cards)
                {
                    localCardEntries.Add(card.Id.Entry);
                }
            }

            // Count cards per player from local rewards
            int cardsPerPlayer = localCardEntries.Count;

            // Check if we have early-buffered remote data
            var bufferedSlots = new Dictionary<int, string[]>(_earlyRemoteCardBuffer);
            _earlyRemoteCardBuffer.Clear();

            ModEntry.Logger.Info(
                $"BuildDraftPool: localSlot={localSlot}, totalPlayers={totalPlayers}, " +
                $"cardsPerPlayer={cardsPerPlayer}, earlyBuffered={bufferedSlots.Count} remote slot(s)");

            // Build the pool in slot order (0, 1, 2, ...) for deterministic DraftId
            // DraftId formula: slot * cardsPerPlayer + cardIndex
            for (int slot = 0; slot < totalPlayers; slot++)
            {
                if (slot == localSlot)
                {
                    // Local player's cards — we have full CardModel objects
                    int cardIdx = 0;
                    foreach (var cardReward in localEntry.Value)
                    {
                        foreach (var card in cardReward.Cards)
                        {
                            int draftId = slot * cardsPerPlayer + cardIdx;
                            _draftPool.Add(new DraftCard
                            {
                                Card = card,
                                CardEntry = card.Id.Entry,
                                OwnerPlayerSlot = slot,
                                OriginalIndex = cardIdx,
                                DraftId = draftId
                            });
                            cardIdx++;
                        }
                    }
                }
                else if (bufferedSlots.TryGetValue(slot, out var bufferedEntries))
                {
                    // Remote player's cards arrived early — add them now with full CardModel
                    _remoteCardEntries[slot] = bufferedEntries;
                    for (int i = 0; i < bufferedEntries.Length; i++)
                    {
                        int draftId = slot * cardsPerPlayer + i;
                        // Create a full CardModel for display and awarding
                        var cardModel = CreateCardFromEntry(
                            bufferedEntries[i], GetLocalPlayer());
                        _draftPool.Add(new DraftCard
                        {
                            Card = cardModel, // Full CardModel via RunState.CreateCard
                            CardEntry = bufferedEntries[i],
                            OwnerPlayerSlot = slot,
                            OriginalIndex = i,
                            DraftId = draftId
                        });
                    }
                    ModEntry.Logger.Info(
                        $"Merged {bufferedEntries.Length} early-buffered cards for slot {slot}");
                }
                else
                {
                    // Remote player's cards haven't arrived yet — leave placeholder slots
                    // They'll be filled by AddRemotePlayerCards later
                    ModEntry.Logger.Info(
                        $"Slot {slot} cards not yet available, will be added when received.");
                }
            }

            // Update _nextDraftId to be after all potential slots
            _nextDraftId = totalPlayers * cardsPerPlayer;

            // Expect card data from all non-local, non-buffered players
            int remotePlayers = _playerStates.Count(ps => ps.PlayerSlot != localSlot);
            _expectedRemoteCardDataCount = remotePlayers;

            ModEntry.Logger.Info(
                $"Draft pool built: {_draftPool.Count} cards so far " +
                $"({_remoteCardEntries.Count}/{remotePlayers} remote slots already received). " +
                $"Waiting for remaining remote data.");

            // Broadcast our card IDs to all remote clients
            SharedDraftSynchronizer.Instance.BroadcastLocalCardData(
                localSlot, localCardEntries.ToArray());
        }
    }

    /// <summary>
    /// Add remote player's cards to the draft pool.
    /// Called when card data arrives from a remote client via network.
    /// 
    /// IMPORTANT: The ActionQueue system broadcasts actions to ALL clients, including
    /// the sender. We must ignore card data from ourselves (local player), because
    /// our own cards are already in the pool from BuildDraftPool().
    /// 
    /// TIMING: Remote data may arrive BEFORE or AFTER BuildDraftPool() is called.
    /// - If BEFORE: buffer the data in _earlyRemoteCardBuffer, merge during BuildDraftPool
    /// - If AFTER: insert cards into the pool using deterministic DraftId assignment
    /// </summary>
    public void AddRemotePlayerCards(int playerSlot, string[] cardEntries)
    {
        // ── Guard: ignore our own card data echoed back via network ──
        int localSlot = GetLocalPlayerSlot();
        ModEntry.Logger.Info(
            $"AddRemotePlayerCards: playerSlot={playerSlot}, localSlot={localSlot}, " +
            $"phase={Phase}, cards=[{string.Join(", ", cardEntries)}]");

        if (playerSlot == localSlot)
        {
            ModEntry.Logger.Info(
                $"Ignoring echoed card data for local slot {playerSlot} " +
                $"(already in pool from BuildDraftPool).");
            return;
        }

        // ── If we haven't built the pool yet, buffer the data ──
        if (Phase == DraftPhase.Inactive || _collectedRewards.Count == 0)
        {
            if (!_earlyRemoteCardBuffer.ContainsKey(playerSlot))
            {
                _earlyRemoteCardBuffer[playerSlot] = cardEntries;
                ModEntry.Logger.Info(
                    $"Buffered early remote card data for slot {playerSlot} " +
                    $"({cardEntries.Length} cards). Will merge during BuildDraftPool.");
            }
            else
            {
                ModEntry.Logger.Info(
                    $"Early buffer for slot {playerSlot} already exists, ignoring duplicate.");
            }
            return;
        }

        // ── Normal case: pool already built, insert remote cards ──
        if (_remoteCardEntries.ContainsKey(playerSlot))
        {
            ModEntry.Logger.Info(
                $"Remote card data for slot {playerSlot} already received, ignoring duplicate.");
            return;
        }

        _remoteCardEntries[playerSlot] = cardEntries;

        // Calculate deterministic DraftId for this slot's cards.
        // DraftId = (playerSlot * cardsPerPlayer) + cardIndex
        // This must match the assignment in BuildDraftPool.
        int cardsPerPlayer = GetCardsPerPlayer();
        int baseDraftId = playerSlot * cardsPerPlayer;

        for (int i = 0; i < cardEntries.Length; i++)
        {
            string entry = cardEntries[i];
            int draftId = baseDraftId + i;

            // Check if a placeholder already exists at this DraftId (shouldn't happen in normal flow)
            var existing = _draftPool.FirstOrDefault(dc => dc.DraftId == draftId);
            if (existing != null)
            {
                ModEntry.Logger.Info(
                    $"DraftId={draftId} already in pool (entry={existing.CardEntry}), skipping.");
                continue;
            }

            _draftPool.Add(new DraftCard
            {
                Card = CreateCardFromEntry(entry, GetLocalPlayer()), // Full CardModel via RunState.CreateCard
                CardEntry = entry,
                OwnerPlayerSlot = playerSlot,
                OriginalIndex = i,
                DraftId = draftId
            });

            ModEntry.Logger.Info(
                $"Added remote card '{entry}' for slot {playerSlot} (DraftId={draftId}).");
        }

        // Sort pool by DraftId to maintain deterministic order
        _draftPool.Sort((a, b) => a.DraftId.CompareTo(b.DraftId));

        // Update _nextDraftId
        if (_draftPool.Count > 0)
            _nextDraftId = _draftPool.Max(dc => dc.DraftId) + 1;

        ModEntry.Logger.Info(
            $"Remote card data processed for slot {playerSlot}: " +
            $"{cardEntries.Length} cards. Pool now has {_draftPool.Count} total. " +
            $"({_remoteCardEntries.Count}/{_expectedRemoteCardDataCount} remote slots received).");

        // Refresh UI if the draft screen is already showing
        if (Phase == DraftPhase.WaitingForReady || Phase == DraftPhase.Selecting)
        {
            SharedDraftScreen.RefreshCards();
        }
    }

    /// <summary>
    /// Get the number of cards each player contributes to the draft pool.
    /// Used for deterministic DraftId calculation.
    /// </summary>
    private int GetCardsPerPlayer()
    {
        // Count from the first (local) player's collected rewards
        if (_collectedRewards.Count > 0)
        {
            var localRewards = _collectedRewards.First().Value;
            return localRewards.Sum(cr => cr.Cards.Count());
        }
        return 3; // Default assumption
    }

    /// <summary>
    /// Wait for all remote players' card data to arrive.
    /// Called during the draft flow before entering the Selecting phase.
    /// </summary>
    private async Task WaitForRemoteCardData(CancellationToken ct)
    {
        if (SharedDraftConfig.DebugMode || _expectedRemoteCardDataCount == 0)
            return;

        int timeoutMs = 30000; // 30 second timeout for card data exchange
        int elapsed = 0;

        ModEntry.Logger.Info(
            $"Waiting for card data from {_expectedRemoteCardDataCount} remote player(s)...");

        while (!AllRemoteCardDataReceived && elapsed < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100);
            elapsed += 100;
        }

        if (AllRemoteCardDataReceived)
        {
            ModEntry.Logger.Info("All remote card data received!");
        }
        else
        {
            ModEntry.Logger.Info(
                $"Card data timeout after {elapsed}ms. " +
                $"Received {_remoteCardEntries.Count}/{_expectedRemoteCardDataCount}. " +
                $"Proceeding with available cards.");
        }
    }

    // ═══════════════════════════════════════════════
    //  2. SELECTION PHASE
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Check if we have any registered card rewards for the current draft.
    /// Called from CardReward.OnSelect patch to decide whether to intercept.
    /// Returns true when draft pool is built and we're in any active phase.
    /// </summary>
    public bool HasRegisteredRewards()
    {
        bool result = Phase != DraftPhase.Inactive && Phase != DraftPhase.Collecting && _draftPool.Count > 0;
        ModEntry.Logger.Info(
            $"[DIAG] HasRegisteredRewards: Phase={Phase}, poolCount={_draftPool.Count}, result={result}");
        return result;
    }

    /// <summary>
    /// Called from CardReward.OnSelect Prefix — replaces the original card selection
    /// flow with our shared draft flow. Returns a Task&lt;bool&gt; that completes
    /// when the entire draft round is finished (true = remove reward from screen).
    /// 
    /// IMPORTANT: When a player opts out then re-enters, SubmitLocalOptOut has already
    /// completed the old TCS and created a new one. The draft async loop is still running.
    /// We detect this re-entry by checking Phase (still active) and the new TCS.
    /// </summary>
    public Task<bool> HandleCardRewardSelect(CardReward cardReward)
    {
        // Check if draft flow is still running (Phase is active, not Inactive)
        bool draftStillRunning = Phase != DraftPhase.Inactive && Phase != DraftPhase.Collecting;

        ModEntry.Logger.Info(
            $"[DIAG] HandleCardRewardSelect called: Phase={Phase}, " +
            $"draftStillRunning={draftStillRunning}, " +
            $"TCS_null={_draftCompletionSource == null}, " +
            $"TCS_completed={_draftCompletionSource?.Task.IsCompleted ?? true}, " +
            $"poolCount={_draftPool.Count}");

        if (draftStillRunning && _draftCompletionSource != null)
        {
            // Draft already in progress — handle re-entry from opt-out (skip)
            // The player previously clicked "Skip" (opted out) and is now clicking
            // CardReward again to re-enter. We need to:
            //   1. Reset their IsOptedOut flag
            //   2. Re-show the SharedDraft UI
            //   3. Broadcast re-entry to other clients
            ModEntry.Logger.Info("[DIAG] → Taking RE-ENTRY path (draft still running)");
            HandleReEntryFromOptOut();
            return _draftCompletionSource.Task;
        }

        ModEntry.Logger.Info("[DIAG] → Taking NEW DRAFT path (creating fresh TCS)");
        _draftCompletionSource = new TaskCompletionSource<bool>();

        // Fire-and-forget the async draft flow, which will complete the TCS
        _ = RunDraftFlowAsync(cardReward);

        return _draftCompletionSource.Task;
    }

    /// <summary>
    /// Handle a player re-entering the draft after previously opting out (clicking "Skip").
    /// Resets opt-out state, re-shows the UI, and broadcasts ready signal for re-entry.
    /// Public so the floating re-entry button in SharedDraftScreen can call it directly.
    /// </summary>
    public void HandleReEntryFromOptOut()
    {
        int localSlot = GetLocalPlayerSlot();
        var localState = _playerStates.FirstOrDefault(p => p.PlayerSlot == localSlot);

        if (localState == null)
        {
            ModEntry.Logger.Info("HandleReEntryFromOptOut: local player state not found.");
            return;
        }

        if (localState.IsCompleted)
        {
            ModEntry.Logger.Info(
                "HandleReEntryFromOptOut: local player already completed, " +
                "re-showing UI in read-only mode.");
            // Player already got their card — just show the UI for spectating
            SharedDraftScreen.Show();
            if (Phase == DraftPhase.Resolving)
                SharedDraftScreen.ShowWaitingForResolve();
            else if (Phase == DraftPhase.Selecting)
                SharedDraftScreen.ShowSelectingState();
            return;
        }

        bool wasOptedOut = localState.IsOptedOut;

        if (wasOptedOut)
        {
            // Reset opt-out status — MarkPlayerReady handles this via re-entry logic
            ModEntry.Logger.Info(
                $"Local player re-entering draft from opt-out " +
                $"(Phase={Phase}, IsOptedOut={localState.IsOptedOut}).");

            // MarkPlayerReady resets IsOptedOut and SelectedDraftId when re-entering
            MarkPlayerReady(localSlot);

            // Broadcast ready signal so remote clients know we're back
            SharedDraftSynchronizer.Instance.BroadcastReady();
        }

        // Re-show the draft screen
        SharedDraftScreen.Show();

        // Set appropriate UI state based on current phase
        if (Phase == DraftPhase.Resolving)
        {
            SharedDraftScreen.ShowWaitingForResolve();
        }
        else if (Phase == DraftPhase.Selecting)
        {
            SharedDraftScreen.ShowSelectingState();
        }

        // Refresh to show correct card states (greyed out awarded cards, etc.)
        SharedDraftScreen.RefreshCards();

        ModEntry.Logger.Info(
            $"HandleReEntryFromOptOut: UI re-shown, " +
            $"wasOptedOut={wasOptedOut}, Phase={Phase}.");
    }

    /// <summary>
    /// Main async flow for a shared draft round — NEW LOOP STRUCTURE:
    ///   1. BeginSync + enter Selecting directly (skip WaitForAllReady)
    ///   2. LOOP:
    ///      a. WaitForSettlement — wait until all active players selected OR endDraft signal
    ///      b. TransitionTo(Resolving) — lock UI
    ///      c. ResolveAndAwardOneRound — handle conflicts, award winners, reset losers
    ///      d. Remove awarded cards from pool
    ///      e. Check: if no active Selecting players → break
    ///      f. TransitionTo(Selecting) — unlock UI for remaining players
    ///   3. Complete + Reset
    /// </summary>
    private async Task RunDraftFlowAsync(CardReward triggeringReward)
    {
        // Create a new cancellation token for this draft round
        _draftCancellation?.Cancel();
        _draftCancellation = new CancellationTokenSource();
        var ct = _draftCancellation.Token;

        try
        {
            ModEntry.Logger.Info("Starting shared draft flow (v0.22 loop structure)...");

            // Begin network synchronization session
            SharedDraftSynchronizer.Instance.BeginSync();

            // Mark local player as ready immediately (entering the draft)
            int localSlot = GetLocalPlayerSlot();
            MarkPlayerReady(localSlot);

            // Broadcast ready signal to other clients
            SharedDraftSynchronizer.Instance.BroadcastReady();

            // Show the shared draft screen UI — directly in Selecting mode (no waiting)
            SharedDraftScreen.Show();

            // Wait for remote card data to arrive (real multiplayer only)
            await WaitForRemoteCardData(ct);
            ct.ThrowIfCancellationRequested();

            // Refresh UI to show all cards (including remote player's cards)
            SharedDraftScreen.RefreshCards();

            // Enter Selecting phase immediately — no waiting for other players
            TransitionTo(DraftPhase.Selecting);
            SharedDraftScreen.ShowSelectingState();

            if (SharedDraftConfig.DebugMode)
            {
                // In debug mode, auto-ready all virtual players after a short delay
                _ = AutoReadyDebugPlayers();
            }

            // ═══════════════════════════════════════════════
            //  MAIN SETTLEMENT LOOP
            // ═══════════════════════════════════════════════
            int maxRounds = 50; // Safety limit (higher to accommodate opt-out re-entries)
            int round = 0;

            while (round < maxRounds)
            {
                ct.ThrowIfCancellationRequested();
                round++;

                ModEntry.Logger.Info($"=== Settlement loop: round {round} ===");

                // a. Wait for settlement trigger
                if (SharedDraftConfig.DebugMode)
                {
                    // In debug mode, start virtual player AI selections
                    _ = SharedDraftSynchronizer.Instance.SimulateVirtualPlayerSelections();
                }

                await WaitForSettlement(ct);
                ct.ThrowIfCancellationRequested();

                // b. Lock UI — enter Resolving phase
                TransitionTo(DraftPhase.Resolving);
                SharedDraftScreen.ShowWaitingForResolve();

                // c. Resolve conflicts and award cards for this round
                await ResolveAndAwardOneRound(triggeringReward, ct);
                ct.ThrowIfCancellationRequested();

                // d. Check: should the draft continue?
                // The draft stays alive as long as ANY player hasn't gotten a card yet.
                // This includes opted-out players who can re-enter at any time.
                //
                // Draft ends ONLY when ALL players are either:
                //   - IsCompleted (got a card) 
                //   - or there are no more available cards in the pool
                bool allPlayersCompleted = _playerStates.All(ps => ps.IsCompleted);
                bool noCardsLeft = GetAvailableCardsForSelection().Count == 0;

                if (allPlayersCompleted)
                {
                    ModEntry.Logger.Info("All players have completed (got cards) — draft complete.");
                    break;
                }

                if (noCardsLeft)
                {
                    ModEntry.Logger.Info("No more cards available in pool — draft complete.");
                    break;
                }

                // Check if there are active players currently selecting (not opted-out, not completed)
                bool hasActiveSelectingPlayers = _playerStates.Any(
                    ps => ps.IsReady && !ps.IsCompleted && !ps.IsOptedOut);

                if (!hasActiveSelectingPlayers)
                {
                    // No one is actively selecting right now, but there are opted-out players
                    // who haven't gotten a card. The draft should stay alive and wait for them
                    // to re-enter via CardReward button.
                    bool hasOptedOutWithoutCard = _playerStates.Any(
                        ps => ps.IsOptedOut && !ps.IsCompleted);

                    if (hasOptedOutWithoutCard)
                    {
                        ModEntry.Logger.Info(
                            "No active selectors, but opted-out player(s) without cards remain. " +
                            "Draft stays alive — waiting for re-entry or end-draft signal.");
                        // Continue the loop — WaitForSettlement will wait for:
                        //   1. An opted-out player to re-enter and select
                        //   2. An end-draft signal from any player
                    }
                    else
                    {
                        ModEntry.Logger.Info("No active or opted-out players remaining — draft complete.");
                        break;
                    }
                }

                // e. Reset end-draft flag for the next round
                _endDraftRequested = false;

                // f. Go back to Selecting for remaining players (losers + new entrants)
                TransitionTo(DraftPhase.Selecting);
                SharedDraftScreen.ShowSelectingState();
                SharedDraftScreen.RefreshCards(); // Refresh to grey out awarded cards

                ModEntry.Logger.Info(
                    $"Back to Selecting: {_playerStates.Count(ps => ps.IsSelecting)} player(s) still selecting.");
            }

            if (round >= maxRounds)
            {
                ModEntry.Logger.Error($"Settlement loop exceeded {maxRounds} rounds, forcing completion.");
            }

            // Done — hide UI and award remaining
            TransitionTo(DraftPhase.Complete);
            SharedDraftScreen.Hide();

            // Determine whether CardReward should be removed from the rewards screen.
            // true  = local player got a card → remove CardReward button (normal completion)
            // false = local player did NOT get a card (opted out / skipped / never entered)
            //         → keep CardReward button so they can still pick via the original flow
            bool localPlayerGotCard = false;
            {
                int completionCheckSlot = GetLocalPlayerSlot();
                var localPs = _playerStates.FirstOrDefault(p => p.PlayerSlot == completionCheckSlot);
                localPlayerGotCard = localPs != null && localPs.IsCompleted && !localPs.IsOptedOut && localPs.IsAwarded;
            }

            ModEntry.Logger.Info(
                $"[DIAG] RunDraftFlowAsync completing: localPlayerGotCard={localPlayerGotCard}, " +
                $"TCS_null={_draftCompletionSource == null}, " +
                $"TCS_completed={_draftCompletionSource?.Task.IsCompleted ?? true}");

            _draftCompletionSource?.TrySetResult(localPlayerGotCard);

            ModEntry.Logger.Info(
                $"Shared draft flow completed successfully. " +
                $"localPlayerGotCard={localPlayerGotCard} (CardReward {(localPlayerGotCard ? "removed" : "kept")}).");
        }
        catch (OperationCanceledException)
        {
            ModEntry.Logger.Info("Shared draft flow was cancelled (Reset was called).");
            SharedDraftScreen.Hide();
            _draftCompletionSource?.TrySetResult(false);
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error in shared draft flow: {ex.Message}\n{ex.StackTrace}");
            SharedDraftScreen.Hide();
            _draftCompletionSource?.TrySetResult(false); // Don't remove reward on error
        }
        finally
        {
            // End synchronization session
            SharedDraftSynchronizer.Instance.EndSync();
            // Reset for next potential draft round (e.g., next combat)
            Reset();
        }
    }

    // ═══════════════════════════════════════════════
    //  READY & SETTLEMENT PHASE
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Mark a player as ready (they have entered the shared draft screen).
    /// Called when the local player clicks CardReward, or when a remote ready signal arrives.
    /// 
    /// NEW BEHAVIOR: If we're in Resolving phase when they enter, they are marked
    /// as ready but the UI stays locked (ShowWaitingForResolve). They'll be unlocked
    /// when the current round finishes and we transition back to Selecting.
    /// </summary>
    public void MarkPlayerReady(int playerSlot)
    {
        var playerState = _playerStates.FirstOrDefault(p => p.PlayerSlot == playerSlot);

        // Handle re-entry: if player previously opted out, reset their opt-out status
        if (playerState != null && playerState.IsOptedOut && !playerState.IsCompleted)
        {
            playerState.IsOptedOut = false;
            playerState.SelectedDraftId = -1;
            ModEntry.Logger.Info(
                $"Player {playerState.DisplayName} (slot {playerSlot}) re-entering draft after opt-out.");
            // Allow re-registration as ready (remove from set so the check below passes)
            _readyPlayers.Remove(playerSlot);
        }

        if (_readyPlayers.Contains(playerSlot))
        {
            ModEntry.Logger.Info($"Player slot {playerSlot} already ready, ignoring duplicate.");
            return;
        }

        _readyPlayers.Add(playerSlot);

        if (playerState != null)
        {
            playerState.IsReady = true;
            ModEntry.Logger.Info(
                $"Player {playerState.DisplayName} (slot {playerSlot}) is now READY " +
                $"({_readyPlayers.Count}/{_playerStates.Count} ready). Phase={Phase}");
        }
        else
        {
            ModEntry.Logger.Info(
                $"Player slot {playerSlot} marked ready but not found in player states.");
        }

        // Refresh UI to show updated ready status
        SharedDraftScreen.RefreshPlayerStatus();
    }

    /// <summary>
    /// Wait for a settlement trigger. Returns when EITHER:
    ///   1. All active (ready, non-completed, non-opted-out) players have selected
    ///   2. _endDraftRequested flag is set (any player clicked "End Draft")
    /// Polls every 100ms.
    /// </summary>
    private async Task WaitForSettlement(CancellationToken ct)
    {
        int elapsed = 0;

        ModEntry.Logger.Info("Waiting for settlement trigger...");

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Check trigger 1: all active players have selected
            if (AllActivePlayersSelected())
            {
                ModEntry.Logger.Info("Settlement triggered: all active players have selected.");
                break;
            }

            // Check trigger 2: end draft signal
            if (_endDraftRequested)
            {
                ModEntry.Logger.Info("Settlement triggered: end draft signal received.");
                break;
            }

            await Task.Delay(100);
            elapsed += 100;

            // Refresh player status in the UI every 500ms
            if (elapsed % 500 == 0)
            {
                SharedDraftScreen.RefreshPlayerStatus();
            }
        }
    }

    /// <summary>
    /// In debug mode, auto-ready virtual players after a short delay.
    /// Updated: uses Selecting phase check instead of WaitingForReady.
    /// </summary>
    private async Task AutoReadyDebugPlayers()
    {
        var rng = new System.Random();
        for (int i = 1; i < _playerStates.Count; i++)
        {
            int delayMs = rng.Next(500, 2000);
            await Task.Delay(delayMs);

            if (Phase != DraftPhase.Selecting && Phase != DraftPhase.Resolving)
                break;

            MarkPlayerReady(i);
        }
    }

    /// <summary>
    /// Submit the local player's card selection by DraftId.
    /// Called from the SharedDraftScreen UI when the player clicks a card,
    /// or from SharedDraftSynchronizer when the local selection is confirmed.
    /// </summary>
    public void SubmitLocalSelection(int draftId)
    {
        int localSlot = GetLocalPlayerSlot();
        var localState = _playerStates.FirstOrDefault(p => p.PlayerSlot == localSlot);

        if (localState == null)
        {
            ModEntry.Logger.Error($"Local player slot {localSlot} not found in player states.");
            return;
        }

        if (localState.HasSelected)
        {
            ModEntry.Logger.Info(
                $"Local player already selected DraftId={localState.SelectedDraftId}, " +
                $"changing to {draftId}.");
        }

        localState.SelectedDraftId = draftId;

        var draftCard = _draftPool.FirstOrDefault(dc => dc.DraftId == draftId);
        ModEntry.Logger.Info(
            $"Local player selected: DraftId={draftId}, " +
            $"Card={draftCard?.CardEntry ?? "?"}");
    }

    /// <summary>
    /// Called from UI: the local player picks a card.
    /// This broadcasts the selection via the synchronizer (which handles
    /// both debug and real multiplayer modes).
    /// </summary>
    public void OnLocalPlayerPickCard(int draftId)
    {
        // In debug mode, submit directly. In multiplayer, broadcast via sync.
        if (SharedDraftConfig.DebugMode)
        {
            SubmitLocalSelection(draftId);
        }
        else
        {
            // Broadcast via synchronizer — it will call back SubmitLocalSelection
            // when the action is confirmed by the ActionQueueSynchronizer
            SharedDraftSynchronizer.Instance.BroadcastLocalSelection(draftId);
        }
    }

    /// <summary>
    /// Called from UI: the local player opts out (skips without picking any card).
    /// This broadcasts the opt-out via the synchronizer.
    /// </summary>
    public void OnLocalPlayerOptOut()
    {
        if (SharedDraftConfig.DebugMode)
        {
            SubmitLocalOptOut();
        }
        else
        {
            // Broadcast via synchronizer — it will call back SubmitLocalOptOut
            // when the action is confirmed by the ActionQueueSynchronizer
            SharedDraftSynchronizer.Instance.BroadcastOptOut();
        }
    }

    /// <summary>
    /// Submit the local player's opt-out decision.
    /// Called from SharedDraftSynchronizer when the opt-out action is confirmed.
    /// 
    /// CRITICAL: We must complete the current _draftCompletionSource with false
    /// (meaning "don't remove CardReward button") so the original RewardsScreen
    /// stops awaiting and the CardReward button becomes clickable again.
    /// We then create a NEW _draftCompletionSource for potential re-entry.
    /// Without this, the RewardsScreen is stuck awaiting the uncompleted Task,
    /// and the player can never click CardReward again.
    /// </summary>
    public void SubmitLocalOptOut()
    {
        int localSlot = GetLocalPlayerSlot();
        var localState = _playerStates.FirstOrDefault(p => p.PlayerSlot == localSlot);

        if (localState == null)
        {
            ModEntry.Logger.Error($"Local player slot {localSlot} not found in player states.");
            return;
        }

        localState.IsOptedOut = true;
        // Note: Do NOT set IsCompleted here — player can re-enter via CardReward button
        ModEntry.Logger.Info(
            $"[DIAG] SubmitLocalOptOut: slot={localSlot}, Phase={Phase}, " +
            $"oldTCS_null={_draftCompletionSource == null}, " +
            $"oldTCS_completed={_draftCompletionSource?.Task.IsCompleted ?? true}");

        // CRITICAL: Complete the current TCS with false so the RewardsScreen unblocks
        // and the CardReward button stays visible and clickable.
        // Then create a new TCS for re-entry.
        var oldTcs = _draftCompletionSource;
        _draftCompletionSource = new TaskCompletionSource<bool>();
        bool setResult = oldTcs?.TrySetResult(false) ?? false; // false = keep CardReward button

        ModEntry.Logger.Info(
            $"[DIAG] SubmitLocalOptOut: oldTCS.TrySetResult(false) returned {setResult}, " +
            $"new TCS created. Phase still={Phase}");

        // Hide main UI — player can re-enter via the CardReward button
        SharedDraftScreen.Hide();
        SharedDraftScreen.RefreshPlayerStatus();
    }

    /// <summary>
    /// Submit a remote player's opt-out decision.
    /// Called from SharedDraftSynchronizer when a remote opt-out signal arrives.
    /// </summary>
    public void SubmitRemoteOptOut(int playerSlot)
    {
        var playerState = _playerStates.FirstOrDefault(p => p.PlayerSlot == playerSlot);
        if (playerState == null)
        {
            ModEntry.Logger.Error($"Remote player slot {playerSlot} not found.");
            return;
        }

        playerState.IsOptedOut = true;
        // Note: Do NOT set IsCompleted here — player can re-enter via CardReward button
        ModEntry.Logger.Info(
            $"Player {playerState.DisplayName} (slot {playerSlot}) opted out of card selection (can re-enter later).");

        // Refresh UI
        SharedDraftScreen.RefreshPlayerStatus();
    }

    /// <summary>
    /// Submit a remote (or debug) player's card selection.
    /// Called from SharedDraftSynchronizer when a selection message arrives,
    /// or from debug AI logic.
    /// </summary>
    public void SubmitRemoteSelection(int playerSlot, int draftId)
    {
        var playerState = _playerStates.FirstOrDefault(p => p.PlayerSlot == playerSlot);
        if (playerState == null)
        {
            ModEntry.Logger.Error($"Remote player slot {playerSlot} not found.");
            return;
        }

        playerState.SelectedDraftId = draftId;

        var draftCard = _draftPool.FirstOrDefault(dc => dc.DraftId == draftId);
        ModEntry.Logger.Info(
            $"Player {playerState.DisplayName} (slot {playerSlot}) selected: " +
            $"DraftId={draftId}, Card={draftCard?.CardEntry ?? "?"}");
    }

    /// <summary>
    /// Check if all active players (non-completed, non-opted-out, entered) have made their selection.
    /// Only counts players in Selecting state — they must have selected.
    /// Returns false if there are no active players (to prevent empty-set .All() returning true).
    /// </summary>
    public bool AllActivePlayersSelected()
    {
        var activePlayers = _playerStates
            .Where(ps => ps.IsReady && !ps.IsCompleted && !ps.IsOptedOut)
            .ToList();

        // If no active players, return false — nothing to settle
        if (activePlayers.Count == 0)
            return false;

        return activePlayers.All(ps => ps.HasSelected);
    }

    /// <summary>
    /// Check if there are any players still in Selecting state (ready, not selected, not completed).
    /// </summary>
    public bool HasActiveSelectingPlayers()
    {
        return _playerStates.Any(ps => ps.IsSelecting);
    }

    /// <summary>
    /// Called from UI: the local player requests to end the draft early.
    /// This broadcasts the end-draft signal via the synchronizer.
    /// </summary>
    public void OnLocalPlayerEndDraft()
    {
        if (SharedDraftConfig.DebugMode)
        {
            OnEndDraftRequested();
        }
        else
        {
            // Broadcast via synchronizer — it will call back OnEndDraftRequested
            SharedDraftSynchronizer.Instance.BroadcastEndDraft();
        }
    }

    /// <summary>
    /// Auto-select cards for any players who haven't picked yet (timeout fallback).
    /// </summary>
    private void AutoSelectForMissingPlayers()
    {
        var rng = new System.Random();
        foreach (var ps in _playerStates.Where(p => !p.HasSelected))
        {
            var available = GetAvailableCardsForSelection();
            if (available.Count > 0)
            {
                var chosen = available[rng.Next(available.Count)];
                ps.SelectedDraftId = chosen.DraftId;
                ModEntry.Logger.Info(
                    $"Auto-selected DraftId={chosen.DraftId} for " +
                    $"{ps.DisplayName} (timeout).");
            }
        }
    }

    /// <summary>
    /// Get cards that are not yet awarded (available for new selection).
    /// Uses _awardedDraftIds HashSet for O(1) lookup.
    /// </summary>
    public List<DraftCard> GetAvailableCardsForSelection()
    {
        return _draftPool
            .Where(dc => !_awardedDraftIds.Contains(dc.DraftId))
            .ToList();
    }

    // ═══════════════════════════════════════════════
    //  3. CONFLICT RESOLUTION (ROCK/PAPER/SCISSORS)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Detect conflicts, resolve via RPS, award winners, and mark losers for re-selection.
    /// This is called ONCE per settlement round (not in a loop — the outer loop handles retries).
    /// 
    /// Flow:
    ///   1. Group selected players by DraftId to find conflicts
    ///   2. Non-conflicting selections: award immediately, mark player as Completed
    ///   3. Conflicting selections: run RPS, award winner, mark winner as Completed,
    ///      reset losers to Selecting
    ///   4. Show RPS overlay if there were conflicts
    ///   5. Track awarded DraftIds in _awardedDraftIds
    /// </summary>
    private async Task ResolveAndAwardOneRound(CardReward triggeringReward, CancellationToken ct)
    {
        _lastConflictResults.Clear();

        // Get all players who have selected (ready, has selected, not completed)
        var selectedPlayers = _playerStates
            .Where(ps => ps.IsReady && ps.HasSelected && !ps.IsCompleted && !ps.IsOptedOut)
            .ToList();

        if (selectedPlayers.Count == 0)
        {
            ModEntry.Logger.Info("No selected players to resolve this round.");
            return;
        }

        // Group selections by DraftId
        var selectionGroups = selectedPlayers
            .GroupBy(ps => ps.SelectedDraftId)
            .ToList();

        // Track players to award (no conflict) and conflicts
        var playersToAward = new List<(PlayerDraftState player, DraftCard card)>();

        foreach (var group in selectionGroups)
        {
            int draftId = group.Key;
            var competitors = group.ToList();
            var draftCard = _draftPool.FirstOrDefault(dc => dc.DraftId == draftId);

            if (draftCard == null)
            {
                ModEntry.Logger.Error($"DraftId={draftId} not found in pool!");
                continue;
            }

            // Check if this card was already awarded in a previous round
            if (_awardedDraftIds.Contains(draftId))
            {
                ModEntry.Logger.Info(
                    $"DraftId={draftId} already awarded, resetting {competitors.Count} player(s).");
                foreach (var comp in competitors)
                {
                    comp.ResetToSelecting();
                }
                continue;
            }

            if (competitors.Count == 1)
            {
                // No conflict — direct award
                playersToAward.Add((competitors[0], draftCard));
            }
            else
            {
                // Conflict — run RPS
                ModEntry.Logger.Info(
                    $"Conflict on '{draftCard.CardEntry}': " +
                    $"{string.Join(", ", competitors.Select(c => c.DisplayName))}");

                var result = RunRockPaperScissors(competitors, draftCard);
                _lastConflictResults.Add(result);

                // Winner gets the card
                playersToAward.Add((result.Winner, draftCard));

                // Losers go back to Selecting
                foreach (var loser in result.Losers)
                {
                    loser.ResetToSelecting();
                    ModEntry.Logger.Info($"  {loser.DisplayName} lost RPS → back to Selecting.");
                }

                ModEntry.Logger.Info($"  Winner: {result.Winner.DisplayName}");
            }
        }

        // Show RPS results overlay if there were conflicts
        if (_lastConflictResults.Count > 0)
        {
            foreach (var cr in _lastConflictResults)
            {
                SharedDraftScreen.MarkCardContested(cr.ContestedCard.DraftId);
            }

            await DraftResultOverlay.ShowResults(_lastConflictResults);
            ct.ThrowIfCancellationRequested();
        }

        // NOTE: Do NOT mark opted-out players as Completed here!
        // Opted-out players can re-enter the draft at any time by clicking CardReward again.
        // They should remain in IsOptedOut state (not IsCompleted) until they either:
        //   1. Re-enter and pick a card (→ awarded → Completed)
        //   2. The draft truly ends with all non-opted-out players done
        // This keeps the draft alive as long as any player hasn't gotten their card.

        // Award cards to winners and no-conflict players
        // IMPORTANT: Hide SharedDraft overlay before awarding so CardPileCmd.Add animation works
        bool localPlayerAwarded = false;

        foreach (var (playerState, draftCard) in playersToAward)
        {
            bool isLocalPlayer = IsLocalPlayer(playerState);

            if (isLocalPlayer)
            {
                // Hide the overlay temporarily for the award animation
                if (!localPlayerAwarded)
                {
                    SharedDraftScreen.Hide();
                    localPlayerAwarded = true;
                }

                if (draftCard.HasCardModel)
                {
                    await AwardCardToLocalPlayer(playerState, draftCard, triggeringReward);
                }
                else
                {
                    // Create CardModel from entry for remote cards
                    var createdCard = CreateCardFromEntry(
                        draftCard.CardEntry, playerState.Player);

                    if (createdCard != null)
                    {
                        ModEntry.Logger.Info(
                            $"Local player selected remote card '{draftCard.CardEntry}' — " +
                            $"created CardModel via RunState.CreateCard(). Awarding...");

                        var resolvedCard = new DraftCard
                        {
                            Card = createdCard,
                            CardEntry = draftCard.CardEntry,
                            OwnerPlayerSlot = draftCard.OwnerPlayerSlot,
                            OriginalIndex = draftCard.OriginalIndex,
                            DraftId = draftCard.DraftId
                        };
                        await AwardCardToLocalPlayer(playerState, resolvedCard, triggeringReward);
                    }
                    else
                    {
                        ModEntry.Logger.Error(
                            $"Local player selected remote card '{draftCard.CardEntry}' " +
                            $"but ModelDb lookup+creation FAILED. Card will NOT be added to deck.");
                    }
                }
            }
            else if (SharedDraftConfig.DebugMode)
            {
                AwardCardToDebugPlayer(playerState, draftCard);
            }

            // Mark player as completed and track awarded card
            playerState.AwardedCard = draftCard.Card;
            playerState.IsCompleted = true;
            _awardedDraftIds.Add(draftCard.DraftId);

            ModEntry.Logger.Info(
                $"Player {playerState.DisplayName} awarded '{draftCard.CardEntry}' " +
                $"(DraftId={draftCard.DraftId}) → Completed.");
        }

        // If local player was awarded and we hid the screen, re-show it
        // for remaining players (if there are losers who need to re-select)
        bool hasRemainingPlayers = _playerStates.Any(
            ps => ps.IsReady && !ps.IsCompleted && !ps.IsOptedOut);

        if (localPlayerAwarded && hasRemainingPlayers)
        {
            // Don't re-show if local player is completed
            var localState = _playerStates.FirstOrDefault(
                ps => IsLocalPlayer(ps));
            if (localState != null && !localState.IsCompleted)
            {
                SharedDraftScreen.Show();
            }
        }

        // Sync skipped cards for local player if they were awarded
        var localPlayerState = _playerStates.FirstOrDefault(ps => IsLocalPlayer(ps));
        if (localPlayerState?.IsCompleted == true)
        {
            SyncSkippedCards();
        }

        ModEntry.Logger.Info(
            $"Round complete: {playersToAward.Count} awarded, " +
            $"{_lastConflictResults.Count} conflicts, " +
            $"{_playerStates.Count(ps => ps.IsSelecting)} still selecting.");
    }

    /// <summary>
    /// Execute a rock/paper/scissors fight between competitors for a card.
    /// 
    /// In real multiplayer: Uses the game's RelicPickingResult.GenerateRelicFight()
    /// with RunState.Rng.TreasureRoomRelics for deterministic sync across clients.
    /// 
    /// In debug mode: Uses random fallback because virtual players all reference
    /// the same local Player object, making GenerateRelicFight unfair (the winner
    /// mapping via Player reference equality always picks the first competitor).
    /// </summary>
    private ConflictResult RunRockPaperScissors(
        List<PlayerDraftState> competitors,
        DraftCard contestedCard)
    {
        // In debug mode, always use random fallback.
        // Virtual players share the same Player reference (or null), so
        // GenerateRelicFight's winner mapping (c.Player == winnerPlayer)
        // would always return the first competitor (local player), making
        // the RPS result deterministic instead of random.
        if (SharedDraftConfig.DebugMode)
        {
            return RunRpsFallback(competitors, contestedCard);
        }

        // Real multiplayer: build unique Player list for GenerateRelicFight
        List<Player> fighterPlayers = new();
        foreach (var comp in competitors)
        {
            if (comp.Player != null)
            {
                fighterPlayers.Add(comp.Player);
            }
        }

        // If we have enough unique real Player objects, use game API
        if (fighterPlayers.Count >= 2)
        {
            return RunRpsViaGameApi(fighterPlayers, competitors, contestedCard);
        }

        // Fallback: simple random resolution
        return RunRpsFallback(competitors, contestedCard);
    }

    /// <summary>
    /// Use the game's RelicPickingResult.GenerateRelicFight for RPS.
    /// </summary>
    private ConflictResult RunRpsViaGameApi(
        List<Player> fighterPlayers,
        List<PlayerDraftState> competitors,
        DraftCard contestedCard)
    {
        // Get an Rng for generating moves
        Rng? rng = null;
        try
        {
            rng = RunManager.Instance?.State?.Rng?.TreasureRoomRelics;
        }
        catch { /* ignore */ }

        rng ??= Rng.Chaotic; // Fallback to chaotic rng

        var possibleMoves = Enum.GetValues<RelicPickingFightMove>();

        // Generate the RPS fight
        // We need a RelicModel as parameter — use any card's Owner's first relic,
        // or just pass a dummy. Since we only care about the fight result (winner),
        // the RelicModel is just for the result's .relic property which we ignore.
        RelicModel? dummyRelic = null;
        try
        {
            dummyRelic = fighterPlayers[0].Relics.FirstOrDefault();
        }
        catch { /* ignore */ }

        // If we can't get a relic, we fall back to manual resolution
        if (dummyRelic == null)
        {
            return RunRpsFallback(competitors, contestedCard);
        }

        var pickingResult = RelicPickingResult.GenerateRelicFight(
            fighterPlayers,
            dummyRelic,
            () => rng.NextItem(possibleMoves));

        // Map the winner Player back to our PlayerDraftState
        var winnerPlayer = pickingResult.player;
        var winnerState = competitors.FirstOrDefault(
            c => c.Player == winnerPlayer) ?? competitors[0];

        var losers = competitors.Where(c => c != winnerState).ToList();

        return new ConflictResult
        {
            ContestedCard = contestedCard,
            Competitors = competitors,
            Winner = winnerState,
            Losers = losers,
            Fight = pickingResult.fight
        };
    }

    /// <summary>
    /// Fallback RPS implementation when game API isn't available
    /// (e.g., debug mode with virtual players).
    /// </summary>
    private ConflictResult RunRpsFallback(
        List<PlayerDraftState> competitors,
        DraftCard contestedCard)
    {
        // Simple random winner selection
        var rng = new System.Random();
        int winnerIndex = rng.Next(competitors.Count);
        var winner = competitors[winnerIndex];
        var losers = competitors.Where((_, i) => i != winnerIndex).ToList();

        ModEntry.Logger.Info(
            $"[Fallback RPS] Winner: {winner.DisplayName} " +
            $"(out of {competitors.Count} competitors)");

        return new ConflictResult
        {
            ContestedCard = contestedCard,
            Competitors = competitors,
            Winner = winner,
            Losers = losers,
            Fight = null
        };
    }

    /// <summary>
    /// Force-resolve any remaining unawarded players by assigning them random cards.
    /// Safety measure if the conflict resolution loop exceeds max rounds.
    /// </summary>
    private void ForceResolveRemaining()
    {
        var rng = new System.Random();
        var assignedDraftIds = _playerStates
            .Where(ps => ps.HasSelected)
            .Select(ps => ps.SelectedDraftId)
            .ToHashSet();

        foreach (var ps in _playerStates.Where(p => !p.HasSelected))
        {
            var available = _draftPool
                .Where(dc => !assignedDraftIds.Contains(dc.DraftId))
                .ToList();

            if (available.Count > 0)
            {
                var chosen = available[rng.Next(available.Count)];
                ps.SelectedDraftId = chosen.DraftId;
                assignedDraftIds.Add(chosen.DraftId);
            }
        }
    }

    // ═══════════════════════════════════════════════
    //  4. AWARDING HELPERS (used by ResolveAndAwardOneRound)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Award a card to the local player's deck.
    /// </summary>
    private async Task AwardCardToLocalPlayer(
        PlayerDraftState playerState,
        DraftCard draftCard,
        CardReward triggeringReward)
    {
        try
        {
            var card = draftCard.Card!; // Caller guarantees HasCardModel is true

            // Add to deck (this is the same API the original CardReward.OnSelect uses)
            var addResult = await CardPileCmd.Add(card, PileType.Deck);

            if (addResult.success)
            {
                // IMPORTANT: Manually fire CardAddFinished on the Deck pile.
                //
                // NTopBarDeckButton listens to CardPile.CardAddFinished (not CardAdded)
                // to refresh the deck count display. In the original game flow,
                // CardAddFinished is triggered after the NCardFlyVfx animation completes.
                // However, when adding a brand-new card (oldPile == null) to Deck without
                // an existing NCard node on table, CardPileCmd.Add skips the fly animation
                // entirely — which means CardAddFinished never fires, and the TopBar deck
                // count never updates.
                //
                // The original CardReward.OnSelect() flow creates NCard nodes that are
                // "on table" (via NCardRewardSelectionScreen), so the fly animation plays.
                // Since SharedDraft bypasses that screen, we must trigger it manually.
                try
                {
                    var deckPile = PileType.Deck.GetPile(card.Owner);
                    deckPile?.InvokeCardAddFinished();
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Error($"Failed to invoke CardAddFinished: {ex.Message}");
                }

                ModEntry.Logger.Info(
                    $"Awarded '{card.Id.Entry}' to {playerState.DisplayName}'s deck.");

                // Sync the obtained card to other clients
                RunManager.Instance?.RewardSynchronizer?.SyncLocalObtainedCard(card);

                // Record in run history
                try
                {
                    var historyEntry = playerState.Player?.RunState?
                        .CurrentMapPointHistoryEntry;
                    if (historyEntry != null && LocalContext.NetId.HasValue)
                    {
                        historyEntry.GetEntry(LocalContext.NetId.Value)
                            .CardChoices.Add(new MegaCrit.Sts2.Core.Runs.History
                                .CardChoiceHistoryEntry(card, wasPicked: true));
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Error($"Failed to record card history: {ex.Message}");
                }
            }
            else
            {
                ModEntry.Logger.Error(
                    $"Failed to add '{card.Id.Entry}' to deck (CardPileCmd returned failure).");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error(
                $"Error awarding card to local player: {ex.Message}");
        }
    }

    /// <summary>
    /// In debug mode, simulate awarding a card to a virtual player.
    /// Since virtual players don't have real decks, we just log it.
    /// </summary>
    private void AwardCardToDebugPlayer(PlayerDraftState playerState, DraftCard draftCard)
    {
        ModEntry.Logger.Info(
            $"[DEBUG] Awarded '{draftCard.CardEntry}' to " +
            $"{playerState.DisplayName} (virtual player, no real deck).");
    }

    /// <summary>
    /// Sync "skipped" status for all cards that the local player didn't pick.
    /// This notifies other clients that these cards were not chosen.
    /// </summary>
    private void SyncSkippedCards()
    {
        try
        {
            int localSlot = GetLocalPlayerSlot();
            var localState = _playerStates.FirstOrDefault(p => p.PlayerSlot == localSlot);

            if (localState == null || !localState.HasSelected)
                return;

            // Get all cards from the local player's original card pool
            if (!_collectedRewards.TryGetValue(localSlot, out var localRewards))
                return;

            var selectedDraftCard = _draftPool.FirstOrDefault(
                dc => dc.DraftId == localState.SelectedDraftId);

            foreach (var cardReward in localRewards)
            {
                foreach (var card in cardReward.Cards)
                {
                    // Skip the card we actually picked
                    if (selectedDraftCard != null && card == selectedDraftCard.Card)
                        continue;

                    // Sync as skipped
                    try
                    {
                        RunManager.Instance?.RewardSynchronizer?.SyncLocalSkippedCard(card);

                        // Record in history
                        if (LocalContext.NetId.HasValue)
                        {
                            var historyEntry = localState.Player?.RunState?
                                .CurrentMapPointHistoryEntry;
                            historyEntry?.GetEntry(LocalContext.NetId.Value)
                                .CardChoices.Add(
                                    new MegaCrit.Sts2.Core.Runs.History
                                        .CardChoiceHistoryEntry(card, wasPicked: false));
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Logger.Error(
                            $"Error syncing skipped card '{card.Id.Entry}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Error in SyncSkippedCards: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    //  RESET / LIFECYCLE
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Reset all draft state. Called on run end/abandon and after draft completion.
    /// </summary>
    public void Reset()
    {
        var oldPhase = Phase;

        // DIAGNOSTIC: log who called Reset with a stack trace
        if (oldPhase != DraftPhase.Inactive)
        {
            var stackTrace = new System.Diagnostics.StackTrace(1, false);
            ModEntry.Logger.Info(
                $"[DIAG] Reset() called! Phase was {oldPhase}, " +
                $"TCS_null={_draftCompletionSource == null}, " +
                $"TCS_completed={_draftCompletionSource?.Task.IsCompleted ?? true}. " +
                $"Caller: {stackTrace.GetFrame(0)?.GetMethod()?.Name ?? "unknown"} " +
                $"→ {stackTrace.GetFrame(1)?.GetMethod()?.Name ?? "unknown"}");
        }

        Phase = DraftPhase.Inactive;

        // Cancel any running async draft flow FIRST
        try
        {
            _draftCancellation?.Cancel();
        }
        catch { /* ignore */ }

        _draftPool.Clear();
        _playerStates.Clear();
        _collectedRewards.Clear();
        _lastConflictResults.Clear();
        _readyPlayers.Clear();
        _awardedDraftIds.Clear();
        _remoteCardEntries.Clear();
        _earlyRemoteCardBuffer.Clear();
        _nextDraftId = 0;
        _expectedPlayerCount = 0;
        _expectedRemoteCardDataCount = 0;
        _endDraftRequested = false;

        // Complete any pending draft task
        _draftCompletionSource?.TrySetResult(false);
        _draftCompletionSource = null;

        // Clean up cancellation token
        _draftCancellation?.Dispose();
        _draftCancellation = null;

        // Also reset the synchronizer
        SharedDraftSynchronizer.Instance.Reset();

        if (oldPhase != DraftPhase.Inactive)
        {
            ModEntry.Logger.Info($"SharedDraftManager reset (was in phase: {oldPhase}).");
        }
    }

    // ═══════════════════════════════════════════════
    //  HELPER METHODS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Transition to a new phase with logging.
    /// </summary>
    private void TransitionTo(DraftPhase newPhase)
    {
        var oldPhase = Phase;
        Phase = newPhase;
        ModEntry.Logger.Info($"Draft phase: {oldPhase} → {newPhase}");
    }

    /// <summary>
    /// Get the slot index for a Player object within RunState.Players.
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

        // Fallback: return 0 (local player)
        return 0;
    }

    /// <summary>
    /// Get the local player's slot index.
    /// </summary>
    private int GetLocalPlayerSlot()
    {
        if (SharedDraftConfig.DebugMode)
            return 0;

        try
        {
            var players = RunManager.Instance?.State?.Players;
            if (players != null && LocalContext.NetId.HasValue)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].NetId == LocalContext.NetId.Value)
                        return i;
                }
            }
        }
        catch { /* ignore */ }

        return 0;
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

            // Fallback: first player
            return players?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if a PlayerDraftState represents the local player.
    /// Public wrapper used by SharedDraftSynchronizer.
    /// </summary>
    public bool IsLocalPlayerState(PlayerDraftState state)
    {
        return IsLocalPlayer(state);
    }

    /// <summary>
    /// Check if a PlayerDraftState represents the local player.
    /// </summary>
    private bool IsLocalPlayer(PlayerDraftState state)
    {
        if (SharedDraftConfig.DebugMode)
            return state.PlayerSlot == 0;

        if (state.Player != null)
            return LocalContext.IsMe(state.Player);

        return state.PlayerSlot == GetLocalPlayerSlot();
    }

    // ═══════════════════════════════════════════════
    //  CARD MODEL CREATION — create CardModel from Entry string via ModelDb
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Create a fresh, mutable CardModel from a card Entry string (e.g., "EVIL_EYE").
    /// 
    /// Uses the game's ModelDb registry to find the canonical template, then
    /// RunState.CreateCard() to properly create and register the mutable clone.
    ///
    /// RunState.CreateCard internally does:
    ///   1. canonical.ToMutable() → creates mutable clone
    ///   2. AddCard(card, owner) → registers into RunState._allCards + sets Owner
    ///   3. card.AfterCreated() → runs post-creation callbacks
    ///
    /// This is required because CardPileCmd.Add(card, PileType.Deck) checks
    /// Owner.RunState.ContainsCard(card) and throws if the card is not registered.
    ///
    /// If no owner is provided, falls back to manual ToMutable() for display-only use.
    /// </summary>
    public static CardModel? CreateCardFromEntry(string entry, Player? owner)
    {
        if (string.IsNullOrEmpty(entry))
        {
            ModEntry.Logger.Error("[CreateCard] Entry string is null or empty.");
            return null;
        }

        try
        {
            // Construct the ModelId for this card.
            // CardModel's category in ModelDb is "CARD" (SlugifyCategory("CardModel") strips "_MODEL").
            var modelId = new ModelId("CARD", entry);

            // Look up the canonical (immutable) template from the global registry.
            var canonical = ModelDb.GetByIdOrNull<CardModel>(modelId);
            if (canonical == null)
            {
                ModEntry.Logger.Error(
                    $"[CreateCard] ModelDb has no CardModel with Id='CARD.{entry}'. " +
                    $"Card may not exist in this game version.");
                return null;
            }

            CardModel mutableCard;
            if (owner?.RunState is RunState runState)
            {
                // Use RunState.CreateCard — this is the game's official API that:
                //   1. Calls ToMutable() to clone the canonical template
                //   2. Calls AddCard(card, owner) to register in RunState._allCards
                //   3. Calls card.AfterCreated() for post-creation initialization
                // Without this, CardPileCmd.Add will throw:
                //   "X must be added to a RunState before adding it to your deck."
                mutableCard = runState.CreateCard(canonical, owner);
            }
            else
            {
                // No valid RunState — fall back to manual clone for display-only use
                mutableCard = canonical.ToMutable();
                if (owner != null)
                {
                    mutableCard.Owner = owner;
                }
            }

            ModEntry.Logger.Info(
                $"[CreateCard] Created CardModel for '{entry}' via " +
                $"{(owner?.RunState is RunState ? "RunState.CreateCard" : "ToMutable")} " +
                $"(Owner={owner?.NetId.ToString() ?? "none"}).");
            return mutableCard;
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error(
                $"[CreateCard] Failed to create CardModel for '{entry}': {ex.Message}");
            return null;
        }
    }
}