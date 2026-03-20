using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SharedDraft;

/// <summary>
/// Custom INetAction for SharedDraft network serialization.
/// 
/// The game's ActionTypes auto-discovers INetAction subtypes in mods via
/// ReflectionHelper.GetSubtypesInMods&lt;INetAction&gt;(), so this struct
/// is automatically registered.
/// 
/// Uses full 32-bit serialization for the encoded value, unlike
/// NetPickRelicAction which only uses 8 bits (truncating values > 255).
/// 
/// Encoding:
///   - Ready signal: encodedValue = -1
///   - Draft pick:   encodedValue = draftId (0+)
/// </summary>
public struct NetDraftAction : INetAction, IPacketSerializable
{
    /// <summary>
    /// Encoded draft action value.
    /// -1 = ready signal, 0+ = draft card selection (DraftId).
    /// </summary>
    public int encodedValue;

    public GameAction ToGameAction(Player player)
    {
        return new DraftGameAction(player, encodedValue);
    }

    public void Serialize(PacketWriter writer)
    {
        // Use full 32 bits to avoid truncation
        writer.WriteInt(encodedValue, 32);
    }

    public void Deserialize(PacketReader reader)
    {
        encodedValue = reader.ReadInt(32);
    }

    public override string ToString()
    {
        return $"NetDraftAction encoded: {encodedValue}";
    }
}

/// <summary>
/// GameAction that executes draft actions (ready signal or card selection)
/// when received via network.
/// </summary>
public class DraftGameAction : GameAction
{
    private readonly Player _player;
    private readonly int _encodedValue;

    /// <summary>Value used to signal "player is ready".</summary>
    public const int ReadySignalValue = -1;

    /// <summary>Value used to signal "player opts out (skip without picking a card)".</summary>
    public const int OptOutSignalValue = -2;

    /// <summary>Value used to signal "end draft early (force settlement)".</summary>
    public const int EndDraftSignalValue = -3;

    public override ulong OwnerId => _player.NetId;
    public override GameActionType ActionType => GameActionType.NonCombat;

    public DraftGameAction(Player player, int encodedValue)
    {
        _player = player;
        _encodedValue = encodedValue;
    }

    public override Task ExecuteAction()
    {
        var synchronizer = SharedDraftSynchronizer.Instance;

        if (_encodedValue == ReadySignalValue)
        {
            // Ready signal
            ModEntry.Logger.Info(
                $"Executing DraftGameAction: READY signal from player {_player.NetId}");
            synchronizer.HandleNetworkReady(_player);
        }
        else if (_encodedValue == OptOutSignalValue)
        {
            // Opt-out signal (skip without picking a card)
            ModEntry.Logger.Info(
                $"Executing DraftGameAction: OPT-OUT signal from player {_player.NetId}");
            synchronizer.HandleNetworkOptOut(_player);
        }
        else if (_encodedValue == EndDraftSignalValue)
        {
            // End draft signal (force settlement)
            ModEntry.Logger.Info(
                $"Executing DraftGameAction: END-DRAFT signal from player {_player.NetId}");
            synchronizer.HandleNetworkEndDraft(_player);
        }
        else
        {
            // Card selection (draftId = _encodedValue)
            ModEntry.Logger.Info(
                $"Executing DraftGameAction: selection draftId={_encodedValue} " +
                $"from player {_player.NetId}");
            synchronizer.HandleNetworkSelection(_player, _encodedValue);
        }

        return Task.CompletedTask;
    }

    public override INetAction ToNetAction()
    {
        return new NetDraftAction
        {
            encodedValue = _encodedValue
        };
    }

    public override string ToString()
    {
        string type = _encodedValue == ReadySignalValue ? "READY" 
            : _encodedValue == OptOutSignalValue ? "OPT_OUT"
            : _encodedValue == EndDraftSignalValue ? "END_DRAFT"
            : $"SELECT({_encodedValue})";
        return $"DraftGameAction [{type}] for player {_player.NetId}";
    }
}
