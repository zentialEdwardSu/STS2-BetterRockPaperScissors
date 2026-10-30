using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>Lifecycle of one conflict RPS session, mirrored on the host and on every client.</summary>
internal enum ConflictSessionStatus : byte
{
    /// <summary>Announced; nobody has picked yet.</summary>
    Begun = 0,

    /// <summary>At least one round has started.</summary>
    Deciding = 1,

    /// <summary>A single survivor was found; the winner's side is the result.</summary>
    Decided = 2,

    /// <summary>Gave up (error / round cap / cancellation); the stock random decision runs instead.</summary>
    Aborted = 3,
}

/// <summary>
/// One conflict being resolved by hand: who takes part, which side each participant voted for, who is
/// still alive this round, and how it ended.
///
/// Participants are always ordered by player slot index, and the slot index — not the player's net id — is
/// what travels on the wire, so every peer can line the same players up in the same order.
/// </summary>
internal sealed class ConflictRpsSession
{
    internal ConflictRpsSession(
        byte id,
        ConflictKind kind,
        IReadOnlyList<Player> participants,
        IReadOnlyList<int> slots,
        IReadOnlyList<int> sides)
    {
        Id = id;
        Kind = kind;
        Participants = participants;
        Slots = slots;
        Sides = sides;
        for (int i = 0; i < participants.Count; i++)
            Survivors.Add(i);
    }

    internal byte Id { get; }

    internal ConflictKind Kind { get; }

    /// <summary>The players taking part, ordered by slot index so every peer agrees on the order.</summary>
    internal IReadOnlyList<Player> Participants { get; }

    /// <summary>Player slot index per participant; this is what the mod's messages carry.</summary>
    internal IReadOnlyList<int> Slots { get; }

    /// <summary>
    /// Each participant's voted side: a packed map coordinate for <see cref="ConflictKind.MapPath"/>, or the
    /// shared-event option index for <see cref="ConflictKind.EventOption"/>.
    /// </summary>
    internal IReadOnlyList<int> Sides { get; }

    /// <summary>Zero-based index of the round currently being played.</summary>
    internal int Round { get; set; }

    /// <summary>Participant indices still in the fight; everyone starts alive.</summary>
    internal HashSet<int> Survivors { get; } = new();

    /// <summary>Sentinel for "no side yet" (real sides are packed coordinates / option indices, never negative).</summary>
    internal const int NoSide = int.MinValue;

    /// <summary>
    /// The side that won the fight (packed coordinate for map conflicts, option index for events), or
    /// <see cref="NoSide"/> while undecided.
    ///
    /// The fight is decided by the *side*, not by a single player: the moment every remaining player is on one
    /// side there is nothing left to decide, so the survivors never have to play each other just to produce a
    /// "last one standing".
    /// </summary>
    internal int WinnerSide { get; set; } = NoSide;

    /// <summary>Participants whose vote is on <see cref="WinnerSide"/>; the reveal enlarges all of them.</summary>
    internal List<int> WinnerSlots { get; } = new();

    /// <summary>
    /// Representative winner (the lowest winner slot), kept for the network message and logs. -1 while undecided.
    /// </summary>
    internal int WinnerSlot { get; set; } = -1;

    internal ConflictSessionStatus Status { get; set; } = ConflictSessionStatus.Begun;

    /// <summary>
    /// Set once the stock split-vote animation was suppressed for this session, so the suppression can only
    /// happen once even if the animation is entered twice (e.g. the event option is chosen twice).
    /// </summary>
    internal bool SkipConsumed { get; set; }

    /// <summary>
    /// The side every survivor is on, or <see cref="NoSide"/> while they still disagree. Used as the fight's only
    /// stopping condition: one side left means the outcome is already known.
    /// </summary>
    internal int SharedSurvivorSide()
    {
        if (Survivors.Count == 0)
            return NoSide;

        int shared = NoSide;
        foreach (int slot in Survivors)
        {
            int voted = Sides[slot];
            if (shared == NoSide)
            {
                shared = voted;
                continue;
            }

            if (voted != shared)
                return NoSide;
        }

        return shared;
    }

    /// <summary>Records the winning side and the participants that voted for it.</summary>
    internal void RememberWinningSide(int side)
    {
        WinnerSide = side;
        WinnerSlots.Clear();
        for (int i = 0; i < Participants.Count; i++)
        {
            if (Sides[i] == side)
                WinnerSlots.Add(i);
        }

        WinnerSlot = WinnerSlots.Count > 0 ? WinnerSlots[0] : -1;
    }

    /// <summary>Participant index of <paramref name="player"/>, or -1 when it is not a participant.</summary>
    internal int IndexOf(Player player)
    {
        for (int i = 0; i < Participants.Count; i++)
        {
            if (ReferenceEquals(Participants[i], player))
                return i;
        }

        return -1;
    }

    /// <summary>The local player's participant index, or -1 when this peer is only watching.</summary>
    internal int LocalSlot()
    {
        for (int i = 0; i < Participants.Count; i++)
        {
            if (LocalContext.IsMe(Participants[i]))
                return i;
        }

        return -1;
    }

    /// <summary>Survivor set as a bitmask (bit i = participant i is still alive).</summary>
    internal uint AliveMask()
    {
        uint mask = 0;
        foreach (int slot in Survivors)
        {
            if (slot is >= 0 and < 32)
                mask |= 1u << slot;
        }

        return mask;
    }

    /// <summary>Inverse of <see cref="AliveMask"/>; slots above the mask width are treated as dead.</summary>
    internal static HashSet<int> FromAliveMask(uint mask, int participantCount)
    {
        var survivors = new HashSet<int>();
        for (int i = 0; i < participantCount && i < 32; i++)
        {
            if ((mask & (1u << i)) != 0)
                survivors.Add(i);
        }

        return survivors;
    }

    /// <summary>Compact description for logs.</summary>
    internal string Describe() =>
        $"{Kind} session {Id} (participants [{string.Join(",", Slots)}], sides [{string.Join(",", Sides)}])";
}
