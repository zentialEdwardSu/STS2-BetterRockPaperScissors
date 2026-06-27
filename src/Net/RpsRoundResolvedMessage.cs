using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>
/// Host -> all clients: the authoritative, fully-resolved set of moves for one fight round.
/// Once a client receives this it uses these moves verbatim (ignoring its own local countdown for
/// authority purposes), guaranteeing every peer animates and resolves the identical round.
///
/// Auto-discovered as an INetMessage subtype via reflection; requires identical mod builds on all peers.
/// </summary>
public struct RpsRoundResolvedMessage : INetMessage, IPacketSerializable
{
    /// <summary>Index of the relic in TreasureRoomRelicSynchronizer.CurrentRelics this fight is for.</summary>
    public byte relicKey;

    /// <summary>Zero-based round index within the fight.</summary>
    public byte round;

    /// <summary>
    /// One entry per fighter, aligned to the fight's playersInvolved order.
    /// Each entry is Rock/Paper/Scissors (0..2) or <see cref="Fight.RpsMove.None"/> (already eliminated).
    /// </summary>
    public List<byte> moves;

    public bool ShouldBroadcast => false; // sent by host directly to every peer
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.relicKey);
        writer.WriteByte(this.round);
        int count = this.moves?.Count ?? 0;
        writer.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
            writer.WriteByte(this.moves[i], 2);
    }

    public void Deserialize(PacketReader reader)
    {
        this.relicKey = reader.ReadByte();
        this.round = reader.ReadByte();
        int count = reader.ReadByte();
        this.moves = new List<byte>(count);
        for (int i = 0; i < count; i++)
            this.moves.Add(reader.ReadByte(2));
    }

    public override string ToString() =>
        $"{nameof(RpsRoundResolvedMessage)} relic:{this.relicKey} round:{this.round} fighters:{this.moves?.Count ?? 0}";
}
