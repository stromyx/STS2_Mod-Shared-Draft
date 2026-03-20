using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SharedDraft.Patches;

/// <summary>
/// Harmony patches for intercepting the card reward flow.
///
/// Strategy:
/// 1. Postfix on RewardsSet.GenerateWithoutOffering() — collect each player's CardReward cards
///    after they've been populated, so the SharedDraftManager knows the full merged pool.
/// 2. Prefix on CardReward.OnSelect() — when the player clicks a card reward in the rewards
///    screen, intercept it and open our SharedDraftScreen instead of the original
///    NCardRewardSelectionScreen. Return false to skip the original method.
///
/// All targets are pure C# methods — safe for Harmony patching.
/// </summary>
public static class RewardsPatch
{
    /// <summary>
    /// After rewards are generated (Populate called), collect all CardRewards
    /// from each player for the shared draft pool.
    ///
    /// GenerateWithoutOffering is an async Task method that returns List&lt;Reward&gt;.
    /// We use a MoveNext patch on the async state machine to catch the completion.
    /// 
    /// Instead, we use a simpler approach: patch RewardsSet.Offer() with a Prefix
    /// that, after the base GenerateWithoutOffering has been called by the original,
    /// collects the card rewards. But since we need the data BEFORE the screen shows,
    /// we patch Offer() itself.
    /// </summary>
    [HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
    private static class OfferPatch
    {
        /// <summary>
        /// Postfix: After Offer() has run (which internally calls GenerateWithoutOffering
        /// to populate the CardReward.Cards lists), collect this player's CardRewards
        /// and register them with the SharedDraftManager.
        /// 
        /// IMPORTANT: This MUST be a Postfix, not a Prefix! In the Prefix stage,
        /// CardReward.Cards is still empty because GenerateWithoutOffering hasn't
        /// been called yet. Only after Offer() completes are the cards populated.
        /// 
        /// We do NOT skip the original Offer() — we let it run normally so the
        /// NRewardsScreen still shows (gold, potions, relics are unaffected).
        /// The interception happens at the CardReward.OnSelect level.
        /// </summary>
        static void Postfix(RewardsSet __instance)
        {
            try
            {
                if (!SharedDraftManager.ShouldActivate())
                    return;

                Player player = __instance.Player;
                List<CardReward> cardRewards = __instance.Rewards
                    .OfType<CardReward>()
                    .ToList();

                if (cardRewards.Count == 0)
                    return;

                // Register this player's card rewards for the shared draft
                SharedDraftManager.Instance.RegisterPlayerCardRewards(player, cardRewards);

                ModEntry.Logger.Info(
                    $"Collected {cardRewards.Count} CardReward(s) from player " +
                    $"NetId={player.NetId} " +
                    $"(total cards: {cardRewards.Sum(cr => cr.Cards.Count())})");
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Error in RewardsSet.Offer Prefix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Intercept CardReward.OnSelect() to redirect to our SharedDraftScreen.
    /// 
    /// When the player clicks a card reward entry in the NRewardsScreen,
    /// OnSelect() is called. We intercept this to open the shared draft UI
    /// instead of the original NCardRewardSelectionScreen.
    ///
    /// OnSelect is a protected virtual async Task&lt;bool&gt; method.
    /// We patch it via Prefix and return false to skip the original.
    /// We set __result to a completed Task&lt;bool&gt; that resolves when
    /// the shared draft process completes.
    ///
    /// This handles two scenarios:
    ///   1. Phase == WaitingForReady: Player is entering the draft for the first time.
    ///      Triggers HandleCardRewardSelect which starts the draft flow.
    ///   2. Phase == Selecting: Draft is already in progress (re-click).
    ///      Returns the existing draft task.
    /// </summary>
    [HarmonyPatch(typeof(CardReward), "OnSelect")]
    private static class CardRewardOnSelectPatch
    {
        static bool Prefix(CardReward __instance, ref Task<bool> __result)
        {
            try
            {
                if (!SharedDraftManager.ShouldActivate())
                    return true; // Let original run

                if (!SharedDraftManager.Instance.HasRegisteredRewards())
                    return true; // No shared draft data, let original run

                // Replace original OnSelect with our shared draft flow
                __result = SharedDraftManager.Instance.HandleCardRewardSelect(__instance);
                return false; // Skip original
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"Error in CardReward.OnSelect Prefix: {ex.Message}");
                return true; // Fallback to original on error
            }
        }
    }
}
