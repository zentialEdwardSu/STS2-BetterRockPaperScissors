using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>
/// Host -> all clients: announces a fight round and the AUTHORITATIVE countdown duration for it.
///
/// The countdown is host-authoritative on purpose: resolution timing is decided by the host, so
/// every peer must display and use the host's value. Otherwise a client with a longer local
/// countdown keeps its picker open past the moment the host already resolved (visible lag + the
/// player's late pick is ignored) — a desync between what the client sees and what actually happened.
///
/// Auto-discovered as an INetMessage subtype via reflection; requires identical mod builds on all peers.
/// </summary>
public struct RpsRoundBeganMessage : INetMessage, IPacketSerializable
{
    /// <summary>Index of the relic in TreasureRoomRelicSynchronizer.CurrentRelics this fight is for.</summary>
    public byte relicKey;

    /// <summary>Zero-based round index within the fight.</summary>
    public byte round;

    /// <summary>The host's countdown duration in seconds for this round.</summary>
    public byte countdownSeconds;

    public bool ShouldBroadcast => false; // sent by host directly to every peer
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.relicKey);
        writer.WriteByte(this.round);
        writer.WriteByte(this.countdownSeconds);
    }

    public void Deserialize(PacketReader reader)
    {
        this.relicKey = reader.ReadByte();
        this.round = reader.ReadByte();
        this.countdownSeconds = reader.ReadByte();
    }

    public override string ToString() =>
        $"{nameof(RpsRoundBeganMessage)} relic:{this.relicKey} round:{this.round} countdown:{this.countdownSeconds}";
}
