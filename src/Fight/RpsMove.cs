using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;

namespace STS2BetterRockPaperScissors.Fight;

/// <summary>
/// Wire/encoding helpers for rock-paper-scissors moves.
/// We reuse the game's <see cref="RelicPickingFightMove"/> (Rock=0, Paper=1, Scissors=2)
/// and add a sentinel for "no move" (eliminated this round, or not yet chosen).
/// </summary>
public static class RpsMove
{
    /// <summary>Sentinel byte meaning "no move" (null in the game's nullable move lists).</summary>
    public const byte None = 3;

    public static byte ToByte(RelicPickingFightMove? move)
    {
        return move.HasValue ? (byte)move.Value : None;
    }

    public static RelicPickingFightMove? FromByte(byte value)
    {
        return value == None ? null : (RelicPickingFightMove)value;
    }

    public static bool IsValidMove(byte value) => value <= 2;
}
