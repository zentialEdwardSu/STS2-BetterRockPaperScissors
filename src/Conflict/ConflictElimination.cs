using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using STS2BetterRockPaperScissors.Fight;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The stock rock-paper-scissors elimination rule, factored out as pure byte arithmetic so both the host
/// driver and the DEBUG self-check can run it without any game node or network state.
///
/// Rule (identical to the treasure-relic fight and to <c>RelicPickingResult.GetLosingMove</c>): a round
/// only eliminates anyone when exactly two distinct gestures are present among the survivors; everyone
/// who threw the beaten gesture is out. Anything else — everyone the same, or all three gestures present
/// — is a draw and nobody is eliminated.
/// </summary>
internal static class ConflictElimination
{
    /// <summary>
    /// True when one of <paramref name="a"/>/<paramref name="b"/> beats the other, in which case
    /// <paramref name="losing"/> is the beaten gesture. Mirrors the game's <c>(a + 1) % 3 == b</c> test.
    /// </summary>
    internal static bool TryGetLosingMove(byte a, byte b, out byte losing)
    {
        if ((a + 1) % 3 == b)
        {
            losing = a;
            return true;
        }

        if ((b + 1) % 3 == a)
        {
            losing = b;
            return true;
        }

        losing = 0;
        return false;
    }

    /// <summary>
    /// Applies one round to <paramref name="survivors"/> in place. <paramref name="moves"/> is indexed by
    /// participant slot; entries for participants outside <paramref name="survivors"/> (or
    /// <see cref="RpsMove.None"/>) are ignored, so eliminated participants need no special casing here.
    /// </summary>
    internal static void Apply(IReadOnlyList<byte> moves, HashSet<int> survivors)
    {
        int first = -1;
        int second = -1;
        foreach (int slot in survivors)
        {
            byte move = moves[slot];
            if (!RpsMove.IsValidMove(move))
                continue;

            if (first < 0)
            {
                first = move;
                continue;
            }

            if (move == first)
                continue;

            if (second < 0)
            {
                second = move;
                continue;
            }

            if (move != second)
                return; // all three gestures present -> draw
        }

        if (first < 0 || second < 0)
            return; // fewer than two gestures -> nothing to eliminate

        if (!TryGetLosingMove((byte)first, (byte)second, out byte losing))
            return;

        survivors.RemoveWhere(slot => moves[slot] == losing);
    }

    /// <summary>
    /// DEBUG-only exhaustive check over every gesture assignment for 2..4 participants (117 cases): the
    /// result must match an independently written winner table. The mod has no test project (a test
    /// assembly referencing sts2.dll cannot boot outside Godot), so this is the cheapest way to pin the
    /// one piece of pure logic the whole feature leans on.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void SelfCheck()
    {
        int cases = 0;
        for (int players = 2; players <= 4; players++)
        {
            int combinations = (int)Math.Pow(3, players);
            for (int combination = 0; combination < combinations; combination++)
            {
                var moves = new byte[players];
                int remainder = combination;
                for (int i = 0; i < players; i++)
                {
                    moves[i] = (byte)(remainder % 3);
                    remainder /= 3;
                }

                var survivors = new HashSet<int>(Enumerable.Range(0, players));
                Apply(moves, survivors);
                cases++;

                HashSet<int> expected = ReferenceSurvivors(moves);
                if (!survivors.SetEquals(expected))
                {
                    Entry.Logger.Error(
                        $"[conflict] elimination self-check FAILED for moves [{string.Join(",", moves)}]: got [{string.Join(",", survivors)}], expected [{string.Join(",", expected)}]");
                    return;
                }
            }
        }

        Entry.Logger.Info($"[conflict] elimination self-check passed ({cases} gesture combinations, 2-4 players)");
    }

    /// <summary>
    /// Reference implementation written straight from the gesture names. Deliberately shares no code with
    /// <see cref="Apply"/>, so a bug in one cannot hide behind the same bug in the other.
    /// </summary>
    private static HashSet<int> ReferenceSurvivors(byte[] moves)
    {
        var survivors = new HashSet<int>(Enumerable.Range(0, moves.Length));
        if (moves.Distinct().Count() != 2)
            return survivors;

        byte first = moves[0];
        byte other = moves.First(move => move != first);
        byte winner = Beats(first, other) ? first : other;
        survivors.RemoveWhere(slot => moves[slot] != winner);
        return survivors;
    }

    private static bool Beats(byte a, byte b)
    {
        if (a == (byte)RelicPickingFightMove.Rock)
            return b == (byte)RelicPickingFightMove.Scissors;
        if (a == (byte)RelicPickingFightMove.Scissors)
            return b == (byte)RelicPickingFightMove.Paper;
        return a == (byte)RelicPickingFightMove.Paper && b == (byte)RelicPickingFightMove.Rock;
    }
}
