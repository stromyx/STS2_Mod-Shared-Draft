namespace GoldGift;

/// <summary>
/// Network message for gold gift transactions between players.
/// Simple data structure for serialization.
/// </summary>
public class GoldGiftMessage
{
    /// <summary>
    /// The slot ID of the player sending the gold.
    /// </summary>
    public int SenderSlotId { get; set; }

    /// <summary>
    /// The slot ID of the player receiving the gold.
    /// </summary>
    public int ReceiverSlotId { get; set; }

    /// <summary>
    /// The amount of gold being gifted.
    /// </summary>
    public int Amount { get; set; }

    public GoldGiftMessage() { }

    public GoldGiftMessage(int senderSlotId, int receiverSlotId, int amount)
    {
        SenderSlotId = senderSlotId;
        ReceiverSlotId = receiverSlotId;
        Amount = amount;
    }

    /// <summary>
    /// Serialize to byte array for network transmission.
    /// </summary>
    public byte[] ToBytes()
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);
        writer.Write(SenderSlotId);
        writer.Write(ReceiverSlotId);
        writer.Write(Amount);
        return ms.ToArray();
    }

    /// <summary>
    /// Deserialize from byte array.
    /// </summary>
    public static GoldGiftMessage FromBytes(byte[] data)
    {
        using var ms = new System.IO.MemoryStream(data);
        using var reader = new System.IO.BinaryReader(ms);
        return new GoldGiftMessage
        {
            SenderSlotId = reader.ReadInt32(),
            ReceiverSlotId = reader.ReadInt32(),
            Amount = reader.ReadInt32()
        };
    }
}
