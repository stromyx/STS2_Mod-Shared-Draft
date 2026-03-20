using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SharedDraft;

/// <summary>
/// Custom INetAction for exchanging card data between clients.
/// 
/// The game's ActionTypes auto-discovers INetAction subtypes in mods via
/// ReflectionHelper.GetSubtypesInMods&lt;INetAction&gt;(), so this struct
/// is automatically registered.
/// 
/// This action transmits a player's card IDs (Entry strings) to other clients
/// so they can build a merged draft pool with cards from all players.
/// 
/// Encoding in Serialize/Deserialize:
///   - playerSlot (8 bits)
///   - cardCount (8 bits)
///   - For each card:
///     - entryLength (8 bits)
///     - entry characters (8 bits each, ASCII)
/// </summary>
public struct NetDraftCardExchangeAction : INetAction, IPacketSerializable
{
    /// <summary>
    /// The player slot that owns these cards.
    /// </summary>
    public int playerSlot;

    /// <summary>
    /// Card entry IDs (e.g., "POMMEL_STRIKE", "GUIDING_STAR").
    /// </summary>
    public string[] cardEntries;

    public GameAction ToGameAction(Player player)
    {
        return new DraftCardExchangeGameAction(player, playerSlot, cardEntries);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(playerSlot, 8);
        int count = cardEntries?.Length ?? 0;
        writer.WriteInt(count, 8);

        for (int i = 0; i < count; i++)
        {
            string entry = cardEntries[i] ?? "";
            // Encode string length (max 255 chars)
            int len = Math.Min(entry.Length, 255);
            writer.WriteInt(len, 8);
            // Encode each character as 8-bit ASCII
            for (int j = 0; j < len; j++)
            {
                writer.WriteInt((int)entry[j] & 0xFF, 8);
            }
        }
    }

    public void Deserialize(PacketReader reader)
    {
        playerSlot = reader.ReadInt(8);
        int count = reader.ReadInt(8);
        cardEntries = new string[count];

        for (int i = 0; i < count; i++)
        {
            int len = reader.ReadInt(8);
            var sb = new StringBuilder(len);
            for (int j = 0; j < len; j++)
            {
                sb.Append((char)reader.ReadInt(8));
            }
            cardEntries[i] = sb.ToString();
        }
    }

    public override string ToString()
    {
        string cards = cardEntries != null ? string.Join(", ", cardEntries) : "null";
        return $"NetDraftCardExchangeAction slot={playerSlot} cards=[{cards}]";
    }
}

/// <summary>
/// GameAction that processes received card data from a remote player.
/// When executed, it tells the SharedDraftManager to add these cards to
/// the draft pool for the specified player slot.
/// </summary>
public class DraftCardExchangeGameAction : GameAction
{
    private readonly Player _player;
    private readonly int _playerSlot;
    private readonly string[] _cardEntries;

    public override ulong OwnerId => _player.NetId;
    public override GameActionType ActionType => GameActionType.NonCombat;

    public DraftCardExchangeGameAction(Player player, int playerSlot, string[] cardEntries)
    {
        _player = player;
        _playerSlot = playerSlot;
        _cardEntries = cardEntries;
    }

    public override Task ExecuteAction()
    {
        ModEntry.Logger.Info(
            $"Executing DraftCardExchangeGameAction: " +
            $"{_cardEntries?.Length ?? 0} cards for slot {_playerSlot} " +
            $"from player {_player.NetId}");

        SharedDraftSynchronizer.Instance.HandleRemoteCardData(_playerSlot, _cardEntries);

        return Task.CompletedTask;
    }

    public override INetAction ToNetAction()
    {
        return new NetDraftCardExchangeAction
        {
            playerSlot = _playerSlot,
            cardEntries = _cardEntries
        };
    }

    public override string ToString()
    {
        string cards = _cardEntries != null ? string.Join(", ", _cardEntries) : "null";
        return $"DraftCardExchangeGameAction [slot={_playerSlot}, cards=[{cards}]]";
    }
}
