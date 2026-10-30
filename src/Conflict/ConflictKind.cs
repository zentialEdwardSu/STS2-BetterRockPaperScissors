namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Which of the two stock "split vote" conflicts a session belongs to.
///
/// The value is part of the mod's wire protocol (<see cref="STS2BetterRockPaperScissors.Net.ConflictRpsBeganMessage"/>),
/// so the numbers are fixed: changing them would make older peers misinterpret newer messages.
/// </summary>
public enum ConflictKind : byte
{
    /// <summary>Players voted for different map coordinates; the winner's vote picks the next room.</summary>
    MapPath = 0,

    /// <summary>Players voted for different options in a shared event; the winner's vote picks the option.</summary>
    EventOption = 1,
}
