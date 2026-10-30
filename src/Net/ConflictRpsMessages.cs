using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>
/// Host -> all clients: a conflict RPS session has started.
///
/// Participants are encoded as *player slot indices*, not net ids, because slot order is identical on every
/// peer and a slot fits in one byte. The voted sides are not sent at all: every peer can read them back from
/// the map/event synchronizer, so shipping them would only add a second source of truth.
///
/// Auto-discovered as an <see cref="INetMessage"/> subtype via reflection; requires identical mod builds on
/// all peers.
/// </summary>
public struct ConflictRpsBeganMessage : INetMessage, IPacketSerializable
{
    /// <summary>Identifier of this conflict; monotonically increasing per host.</summary>
    public byte sessionId;

    /// <summary><c>ConflictKind</c> (MapPath = 0 / EventOption = 1).</summary>
    public byte kind;

    /// <summary>Host-authoritative countdown for each round, in whole seconds.</summary>
    public byte countdownSeconds;

    /// <summary>Non-zero when re-choosing is allowed (host-authoritative).</summary>
    public byte allowReset;

    /// <summary>Player slot index of every participant, in the order rounds are aligned to.</summary>
    public List<byte> participants;

    public bool ShouldBroadcast => false; // sent by host directly to every peer
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.sessionId);
        writer.WriteByte(this.kind);
        writer.WriteByte(this.countdownSeconds);
        writer.WriteByte(this.allowReset);
        int count = this.participants?.Count ?? 0;
        writer.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
            writer.WriteByte(this.participants![i]);
    }

    public void Deserialize(PacketReader reader)
    {
        this.sessionId = reader.ReadByte();
        this.kind = reader.ReadByte();
        this.countdownSeconds = reader.ReadByte();
        this.allowReset = reader.ReadByte();
        int count = reader.ReadByte();
        this.participants = new List<byte>(count);
        for (int i = 0; i < count; i++)
            this.participants.Add(reader.ReadByte());
    }

    public override string ToString() =>
        $"{nameof(ConflictRpsBeganMessage)} session:{this.sessionId} kind:{this.kind} countdown:{this.countdownSeconds}s participants:{this.participants?.Count ?? 0}";
}

/// <summary>Host -> all clients: round <see cref="round"/> of <see cref="sessionId"/> has started.</summary>
public struct ConflictRpsRoundBeganMessage : INetMessage, IPacketSerializable
{
    public byte sessionId;
    public byte round;
    public byte countdownSeconds;
    public byte allowReset;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.sessionId);
        writer.WriteByte(this.round);
        writer.WriteByte(this.countdownSeconds);
        writer.WriteByte(this.allowReset);
    }

    public void Deserialize(PacketReader reader)
    {
        this.sessionId = reader.ReadByte();
        this.round = reader.ReadByte();
        this.countdownSeconds = reader.ReadByte();
        this.allowReset = reader.ReadByte();
    }

    public override string ToString() =>
        $"{nameof(ConflictRpsRoundBeganMessage)} session:{this.sessionId} round:{this.round} countdown:{this.countdownSeconds}s";
}

/// <summary>
/// Client -> host: the local player's gesture for a round. Re-sent whenever the player re-chooses, so the
/// host always keeps the latest value for (session, round, slot).
/// </summary>
public struct ConflictRpsMoveMessage : INetMessage, IPacketSerializable
{
    public byte sessionId;
    public byte round;
    public byte move;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.sessionId);
        writer.WriteByte(this.round);
        writer.WriteByte(this.move, 2);
    }

    public void Deserialize(PacketReader reader)
    {
        this.sessionId = reader.ReadByte();
        this.round = reader.ReadByte();
        this.move = reader.ReadByte(2);
    }

    public override string ToString() =>
        $"{nameof(ConflictRpsMoveMessage)} session:{this.sessionId} round:{this.round} move:{this.move}";
}

/// <summary>
/// Host -> all clients: the authoritative result of one round. <see cref="moves"/> is aligned to the
/// participant order from <see cref="ConflictRpsBeganMessage"/> and holds <c>RpsMove.None</c> (3) for
/// participants already eliminated.
/// </summary>
public struct ConflictRpsRoundResolvedMessage : INetMessage, IPacketSerializable
{
    public byte sessionId;
    public byte round;
    public List<byte> moves;

    /// <summary>Survivors *after* this round (bit i = participant i is still in).</summary>
    public uint aliveMask;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.sessionId);
        writer.WriteByte(this.round);
        int count = this.moves?.Count ?? 0;
        writer.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
            writer.WriteByte(this.moves![i], 2);
        writer.WriteUInt(this.aliveMask);
    }

    public void Deserialize(PacketReader reader)
    {
        this.sessionId = reader.ReadByte();
        this.round = reader.ReadByte();
        int count = reader.ReadByte();
        this.moves = new List<byte>(count);
        for (int i = 0; i < count; i++)
            this.moves.Add(reader.ReadByte(2));
        this.aliveMask = reader.ReadUInt();
    }

    public override string ToString() =>
        $"{nameof(ConflictRpsRoundResolvedMessage)} session:{this.sessionId} round:{this.round} moves:{this.moves?.Count ?? 0} alive:0x{this.aliveMask:X}";
}

/// <summary>
/// Host -> all clients: the session is over. <see cref="status"/> is a <c>ConflictSessionStatus</c>; on
/// <c>Decided</c> <see cref="winnerSide"/> is the winning side (packed map coordinate or event option index) and
/// <see cref="winnerSlot"/> is one representative player on it. There is no single winner by design: the fight
/// ends as soon as every remaining player is on the same side, so several players can share the win.
/// </summary>
public struct ConflictRpsResolvedMessage : INetMessage, IPacketSerializable
{
    public byte sessionId;
    public byte status;
    public byte winnerSlot;

    /// <summary>The side that won; <c>int.MinValue</c> when the session was aborted.</summary>
    public int winnerSide;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(this.sessionId);
        writer.WriteByte(this.status);
        writer.WriteByte(this.winnerSlot);
        writer.WriteInt(this.winnerSide);
    }

    public void Deserialize(PacketReader reader)
    {
        this.sessionId = reader.ReadByte();
        this.status = reader.ReadByte();
        this.winnerSlot = reader.ReadByte();
        this.winnerSide = reader.ReadInt();
    }

    public override string ToString() =>
        $"{nameof(ConflictRpsResolvedMessage)} session:{this.sessionId} status:{this.status} winnerSlot:{this.winnerSlot} winnerSide:{this.winnerSide}";
}
