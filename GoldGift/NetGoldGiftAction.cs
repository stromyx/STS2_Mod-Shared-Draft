using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace GoldGift;

/// <summary>
/// Custom INetAction for gold gift network serialization.
/// 
/// The game's ActionTypes static constructor auto-discovers INetAction subtypes
/// in mods via ReflectionHelper.GetSubtypesInMods&lt;INetAction&gt;(), so this struct
/// is automatically registered without any manual setup.
/// 
/// Uses full 32-bit serialization for the encoded value, unlike NetPickRelicAction
/// which only uses 8 bits (causing truncation for values > 255).
/// </summary>
public struct NetGoldGiftAction : INetAction, IPacketSerializable
{
    /// <summary>
    /// Encoded gold gift value: senderSlot * 10000 + targetSlot * 1000 + amount
    /// Supports: senderSlot 0-8, targetSlot 0-8, amount 1-999
    /// </summary>
    public int encodedGift;

    public GameAction ToGameAction(Player player)
    {
        // Create a GoldGiftGameAction that will execute ApplyGoldGiftLocally
        return new GoldGiftGameAction(player, encodedGift);
    }

    public void Serialize(PacketWriter writer)
    {
        // Use full 32 bits — NOT the 8-bit truncation that NetPickRelicAction uses
        writer.WriteInt(encodedGift, 32);
    }

    public void Deserialize(PacketReader reader)
    {
        encodedGift = reader.ReadInt(32);
    }

    public override string ToString()
    {
        return $"NetGoldGiftAction encoded: {encodedGift}";
    }
}

/// <summary>
/// GameAction that executes the gold gift locally when the NetGoldGiftAction
/// is received and deserialized by ActionQueueSynchronizer.
/// </summary>
public class GoldGiftGameAction : GameAction
{
    private readonly Player _player;
    private readonly int _encodedGift;

    public override ulong OwnerId => _player.NetId;
    public override GameActionType ActionType => GameActionType.NonCombat;

    public GoldGiftGameAction(Player player, int encodedGift)
    {
        _player = player;
        _encodedGift = encodedGift;
    }

    public override Task ExecuteAction()
    {
        var (senderSlot, targetSlot, amount) = GoldGiftNetworkHandler.DecodeGoldGiftDirect(_encodedGift);

        ModEntry.Logger.Info(
            $"Executing GoldGiftGameAction: sender={senderSlot}, " +
            $"target={targetSlot}, amount={amount}");

        GoldGiftNetworkHandler.ApplyGoldGiftLocally(senderSlot, targetSlot, amount);

        return Task.CompletedTask;
    }

    public override INetAction ToNetAction()
    {
        return new NetGoldGiftAction
        {
            encodedGift = _encodedGift
        };
    }

    public override string ToString()
    {
        return $"GoldGiftGameAction for player {_player.NetId} encoded {_encodedGift}";
    }
}
