using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Net;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>How a host-side conflict session ended.</summary>
internal enum ConflictRpsOutcome
{
    /// <summary>A single survivor was found; the caller must inject their vote as the decision.</summary>
    Decided,

    /// <summary>Gave up; the caller must re-run the stock decision.</summary>
    Aborted,

    /// <summary>The screen/run went away mid-session; nothing should be injected or re-run.</summary>
    Cancelled,
}

/// <summary>
/// Host-side driver for one conflict: announce the session, play rounds until exactly one survivor remains,
/// and broadcast every authoritative result.
///
/// The host is the only peer that decides anything. Clients never compute the winner themselves — they just
/// mirror the round results and, at the end, read the winner slot out of the terminal message. That is what
/// keeps both peers' view of "whose vote won" identical no matter how the messages interleave.
/// </summary>
internal static class ConflictRpsHost
{
    internal static async Task<ConflictRpsOutcome> RunAsync(ConflictRpsSession session, CancellationToken token)
    {
        // Belt and braces: the registration patch normally runs this at run start, but the host has to be
        // able to receive client gestures even if that patch was skipped for this session.
        ConflictRpsNetHub.EnsureRegistered(RunManager.Instance?.NetService);

        byte countdownSeconds = (byte)Math.Clamp(ModConfig.CountdownSeconds, 1, 30);
        bool allowReset = ModConfig.AllowReset;

        try
        {
            ConflictRpsNetHub.BroadcastBegan(session, countdownSeconds, allowReset);

            for (int round = 0; round < ModConfig.MaxConflictRounds; round++)
            {
                if (token.IsCancellationRequested)
                    return Abort(session, ConflictRpsOutcome.Cancelled);

                session.Round = round;
                if (round > 0)
                    ConflictRpsNetHub.BroadcastRoundBegan(session.Id, round, countdownSeconds, allowReset);

                byte[] moves = await ConflictPickerHost.RunHostRoundAsync(session, round, countdownSeconds, allowReset, token);
                if (token.IsCancellationRequested)
                    return Abort(session, ConflictRpsOutcome.Cancelled);

                var survivorsBefore = new HashSet<int>(session.Survivors);
                ConflictElimination.Apply(moves, session.Survivors);
                ConflictRpsNetHub.BroadcastRoundResolved(session.Id, round, moves, session.AliveMask());
                Entry.Logger.Info(
                    $"[conflict] {session.Kind} session {session.Id} round {round}: moves [{string.Join(",", moves)}] -> survivors [{string.Join(",", session.Survivors)}]");

                // Throw animation: the host waits out the beat before announcing the next round, and clients play
                // the same beat when the round result arrives — so neither side opens the next picker mid-throw.
                HashSet<int> eliminated = survivorsBefore.Where(slot => !session.Survivors.Contains(slot)).ToHashSet();
                await ConflictGestureStage.ThrowAsync(session, moves, survivorsBefore, eliminated, token);
                if (token.IsCancellationRequested)
                    return Abort(session, ConflictRpsOutcome.Cancelled);

                // The fight is over as soon as only one side is left: the decision is about which side wins, so the
                // survivors never have to play each other just to crown a "last one standing".
                int winningSide = session.SharedSurvivorSide();
                if (winningSide == ConflictRpsSession.NoSide)
                    continue;

                int representative = session.Survivors.First();
                ConflictRpsNetHub.BroadcastResolved(session.Id, ConflictSessionStatus.Decided, representative, winningSide);
                ConflictRpsCoordinator.MarkDecided(session, winningSide, startReveal: true);
                Entry.Logger.Info(
                    $"[conflict] {session.Kind} session {session.Id} won by side {winningSide} — {session.WinnerSlots.Count} player(s) on it: slots [{string.Join(",", session.Slots.Where((_, i) => session.WinnerSlots.Contains(i)))}]");
                return ConflictRpsOutcome.Decided;
            }

            Entry.Logger.Warn(
                $"[conflict] {session.Kind} session {session.Id} hit the {ModConfig.MaxConflictRounds}-round cap without settling on one side");
            return Abort(session, ConflictRpsOutcome.Aborted);
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"[conflict] {session.Kind} session {session.Id} failed: {e}");
            return Abort(session, ConflictRpsOutcome.Aborted);
        }
    }

    private static ConflictRpsOutcome Abort(ConflictRpsSession session, ConflictRpsOutcome outcome)
    {
        ConflictGestureStage.Close();
        ConflictRpsCoordinator.MarkAborted(session);
        ConflictRpsNetHub.BroadcastResolved(session.Id, ConflictSessionStatus.Aborted, 0, ConflictRpsSession.NoSide);
        return outcome;
    }
}
