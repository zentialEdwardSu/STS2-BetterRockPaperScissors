using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Net;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Client-side driver for one conflict: it never decides anything, it only mirrors the host.
///
/// Lifecycle: the began message creates the session and starts this loop; each round the loop waits for the
/// host's round announcement, shows the picker (if this player is still alive), then waits for the host's
/// authoritative result and trims the survivor set from the bitmask the host sent. When a single survivor
/// remains — or the host aborts — the loop reads the terminal message and, for map conflicts, starts the
/// same 1s winner reveal the host is playing.
/// </summary>
internal static class ConflictRpsClient
{
    /// <summary>Give up if the host goes silent (disconnect mid-fight) instead of hanging the picker forever.</summary>
    private static readonly TimeSpan RoundAnnounceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Give up waiting for one round's result: the host may have died between announcing and resolving.</summary>
    private static readonly TimeSpan RoundResolveTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long to wait for the terminal message after the last round resolved.</summary>
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Entry point from the network layer: turns a host announcement into a session. Local toggles are
    /// deliberately ignored — the host owns the rule (see <see cref="ConflictRpsGate.CanClientParticipate"/>).
    /// </summary>
    internal static void OnBegan(ConflictRpsBeganMessage message)
    {
        var kind = (ConflictKind)message.kind;
        if (!ConflictRpsGate.CanClientParticipate(kind))
            return;

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null || message.participants == null)
            return;

        var participants = new List<Player>();
        var slots = new List<int>();
        var sides = new List<int>();
        foreach (byte slot in message.participants)
        {
            if (slot >= runState.Players.Count)
            {
                Entry.Logger.Warn(
                    $"[conflict] began message names slot {slot} but the run only has {runState.Players.Count} players");
                continue;
            }

            Player player = runState.Players[slot];
            participants.Add(player);
            slots.Add(slot);
            sides.Add(ReadSide(kind, player));
        }

        if (participants.Count < 2)
        {
            Entry.Logger.Warn($"[conflict] ignoring began message with {participants.Count} usable participant(s)");
            return;
        }

        var session = new ConflictRpsSession(message.sessionId, kind, participants, slots, sides)
        {
            Status = ConflictSessionStatus.Deciding,
        };
        if (!ConflictRpsCoordinator.TryAcceptClientSession(session))
            return;

        TaskHelper.RunSafely(RunAsync(session));
    }

    /// <summary>
    /// Called by the network layer the instant the host's terminal message arrives — synchronously, before any
    /// await can resume.
    ///
    /// This has to be synchronous. The map/event seams that decide whether to play the stock roulette run later in
    /// the same frame batch, and task continuations are queued asynchronously: if the session only looked
    /// "decided" after an <c>await</c>, the stock wheel would already have been started, which is exactly the
    /// misleading animation this feature replaces.
    /// </summary>
    internal static void OnResolved(byte sessionId, byte status, byte winnerSlot, int winnerSide)
    {
        ConflictRpsSession? session = ConflictRpsCoordinator.Current;
        if (session == null || session.Id != sessionId)
            return;

        Entry.Logger.Debug(
            $"[conflict] session {sessionId} resolved: status {status}, winning side {winnerSide}, representative slot {winnerSlot}");

        if (status == (byte)ConflictSessionStatus.Decided && winnerSide != ConflictRpsSession.NoSide)
            ConflictRpsCoordinator.MarkDecided(session, winnerSide, startReveal: true);
        else
            ConflictRpsCoordinator.MarkAborted(session);
    }

    private static async Task RunAsync(ConflictRpsSession session)
    {
        using var cts = new CancellationTokenSource();
        Control? parent = ConflictPickerHost.ResolveParent(session.Kind);
        Action onTreeExiting = cts.Cancel;
        if (parent != null && GodotObject.IsInstanceValid(parent))
            parent.TreeExiting += onTreeExiting;

        try
        {
            Task<ConflictSessionResult> terminal = ConflictRpsNetHub.AwaitSessionResolved(session.Id);

            for (int round = 0; round < ModConfig.MaxConflictRounds; round++)
            {
                session.Round = round;
                if (round > 0)
                {
                    // Watch the terminal message at the same time: if the host aborts, the round we are waiting
                    // for will never be announced and we must not sit here until the timeout.
                    Task<bool> began = ConflictRpsNetHub.AwaitRoundBegan(session.Id, round);
                    Task announceTimeout = Task.Delay(RoundAnnounceTimeout);
                    Task first = await Task.WhenAny(began, terminal, announceTimeout);
                    if (first == terminal)
                    {
                        await FinishAsync(session, terminal);
                        return;
                    }

                    if (first == announceTimeout || !began.IsCompletedSuccessfully || !began.Result)
                    {
                        Entry.Logger.Warn(
                            $"[conflict] session {session.Id}: host never announced round {round} — abandoning the picker");
                        return;
                    }
                }

                (byte countdown, bool allowReset) = ConflictRpsNetHub.RoundSettings(session.Id, round);
                Task<ConflictRoundResult> roundTask = ConflictRpsNetHub.AwaitRoundResolved(session.Id, round);
                Task roundFinished = Task.WhenAny(roundTask, terminal);

                int localSlot = session.LocalSlot();
                if (localSlot >= 0 && session.Survivors.Contains(localSlot))
                {
                    await ConflictPickerHost.RunClientRoundAsync(
                        session,
                        round,
                        countdown,
                        ConflictRpsNetHub.OneWayLatencySeconds(),
                        allowReset,
                        roundFinished,
                        parent,
                        cts.Token);
                }

                Task finished = await Task.WhenAny(roundTask, terminal, Task.Delay(RoundResolveTimeout));
                if (finished != roundTask)
                {
                    // Either the host ended the session (terminal) or it went silent (timeout); FinishAsync
                    // reads the verdict when there is one and aborts the local session otherwise.
                    await FinishAsync(session, terminal);
                    return;
                }

                ConflictRoundResult result = await roundTask;
                List<byte>? resolvedMoves = result.Moves;
                if (resolvedMoves == null)
                {
                    // The host ended the session before this round resolved (abort) — nothing to mirror.
                    await FinishAsync(session, terminal);
                    return;
                }

                var survivorsBefore = new HashSet<int>(session.Survivors);
                session.Survivors.Clear();
                session.Survivors.UnionWith(ConflictRpsSession.FromAliveMask(result.AliveMask, session.Participants.Count));
                Entry.Logger.Debug(
                    $"[conflict] session {session.Id} round {round}: moves [{string.Join(",", resolvedMoves)}] -> survivors [{string.Join(",", session.Survivors)}]");

                // Same beat the host is playing right now, so both sides see the throw before the next picker.
                HashSet<int> eliminated = survivorsBefore.Where(slot => !session.Survivors.Contains(slot)).ToHashSet();
                await ConflictGestureStage.ThrowAsync(session, resolvedMoves, survivorsBefore, eliminated, cts.Token);
                if (cts.Token.IsCancellationRequested)
                    return;

                // Stop as soon as one side is left — the host decides at that point, so waiting for a single
                // survivor would only make us sit through a round that will never come.
                if (session.SharedSurvivorSide() != ConflictRpsSession.NoSide)
                    break;
            }

            await FinishAsync(session, terminal);
        }
        catch (OperationCanceledException)
        {
            // The screen we were prompting on went away (run ended, host disconnected). Nothing to clean up
            // beyond the finally block: the session dies with the screen.
            Entry.Logger.Info($"[conflict] client session {session.Id} cancelled — its screen left the tree");
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"[conflict] client session {session.Id} failed: {e}");
            ConflictRpsCoordinator.MarkAborted(session);
        }
        finally
        {
            if (parent != null && GodotObject.IsInstanceValid(parent))
                parent.TreeExiting -= onTreeExiting;
            ConflictGestureStage.Close();
            ConflictRpsNetHub.ClearSession(session.Id);
        }
    }

    /// <summary>Applies the host's terminal verdict to the local session (and starts the map reveal).</summary>
    private static async Task FinishAsync(ConflictRpsSession session, Task<ConflictSessionResult> terminal)
    {
        ConflictSessionResult? result = await WithTimeout(terminal, TerminalTimeout);
        if (result == null)
        {
            Entry.Logger.Warn($"[conflict] session {session.Id}: host never sent a verdict — dropping the session");
            ConflictRpsCoordinator.MarkAborted(session);
            return;
        }

        if (result.Value.Status == (byte)ConflictSessionStatus.Decided && result.Value.WinnerSide != ConflictRpsSession.NoSide)
            ConflictRpsCoordinator.MarkDecided(session, result.Value.WinnerSide, startReveal: true);
        else
            ConflictRpsCoordinator.MarkAborted(session);
    }

    /// <summary>
    /// The participant's voted side, used by the map seam to verify that the coordinate the game is about to
    /// travel to really is the one this RPS decided. -1 means "unknown" (the votes may already be cleared by
    /// the time the message arrives), and the seam then trusts the session itself.
    /// </summary>
    private static int ReadSide(ConflictKind kind, Player player)
    {
        try
        {
            if (kind == ConflictKind.MapPath)
            {
                MapVote? vote = RunManager.Instance.MapSelectionSynchronizer.GetVote(player);
                return vote?.coord is MapCoord coord ? MapConflictAdapter.PackCoord(coord) : -1;
            }

            uint? option = RunManager.Instance.EventSynchronizer.GetPlayerVote(player);
            return option.HasValue ? (int)option.Value : -1;
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[conflict] could not read the vote of {player.NetId}: {e.Message}");
            return -1;
        }
    }

    /// <summary>Task result with a deadline; <c>null</c> means "no verdict arrived in time".</summary>
    private static async Task<T?> WithTimeout<T>(Task<T> task, TimeSpan timeout)
        where T : struct
    {
        if (task.IsCompleted)
            return task.IsCompletedSuccessfully ? task.Result : null;

        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        return completed == task && task.IsCompletedSuccessfully ? task.Result : null;
    }
}
