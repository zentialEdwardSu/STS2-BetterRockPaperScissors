using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>
/// Client -> host: the move the local player chose for a given relic fight round.
/// Only sent by clients (and only when the player actually picks something). The host is the
/// sole authority on round resolution and the countdown deadline.
///
/// IMPORTANT: this is auto-discovered as an INetMessage subtype via reflection
/// (MessageTypes.Initialize -> ReflectionHelper.GetSubtypesInMods). Type IDs are assigned by
/// sorted reflection order, so every peer MUST run an identical build of this mod.
/// </summary>
public struct RpsMoveChosenMessage : INetMessage, IPacketSerializable
{
    /// <summary>Index of the relic in TreasureRoomRelicSynchronizer.CurrentRelics this fight is for.</summary>
    public byte relicKey;

    /// <summary>Zero-based round index within the fight.</summary>
    public byte round;

    /// <summary>Rock/Paper/Scissors (0..2). Never <see cref="Fight.RpsMove.None"/> — we only send real picks.</summary>
    public byte move;

    // Sent client->host only; the host resolves and broadcasts the authoritative round, so this
    // should not be echoed to other clients.
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.relicKey);
        writer.WriteByte(this.round);
        writer.WriteByte(this.move, 2);
    }

    public void Deserialize(PacketReader reader)
    {
        this.relicKey = reader.ReadByte();
        this.round = reader.ReadByte();
        this.move = reader.ReadByte(2);
    }

    public override string ToString() =>
        $"{nameof(RpsMoveChosenMessage)} relic:{this.relicKey} round:{this.round} move:{this.move}";
}
