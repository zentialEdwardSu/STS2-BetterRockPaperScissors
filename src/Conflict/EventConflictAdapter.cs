using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Runs;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Glue between the conflict RPS and the stock shared-event decision.
///
/// Stock <c>ChooseSharedEventOption</c> rolls a random option, broadcasts it, then runs it for every player's
/// event model. We reproduce exactly those two steps with the RPS winner's option index — the broadcast goes
/// out on the game's own message type, so clients, history, checksums and the post-choice animation all keep
/// running through unchanged code.
/// </summary>
internal static class EventConflictAdapter
{
    [ThreadStatic] private static bool _fallbackInProgress;

    /// <summary>True while <see cref="Fallback"/> re-enters the patched method, so the prefix lets it through.</summary>
    internal static bool FallbackInProgress => _fallbackInProgress;

    /// <summary>
    /// Reads every player's voted option and builds a session, or returns false when this is not a conflict:
    /// not a shared event, a vote is missing, or everybody agrees. Votes are snapshotted here so a later vote
    /// change cannot alter this fight.
    /// </summary>
    internal static bool TrySnapshot(EventSynchronizer synchronizer, out ConflictRpsSession? session)
    {
        session = null;
        if (!IsShared(synchronizer))
            return false;

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return false;

        var participants = new List<Player>();
        var slots = new List<int>();
        var sides = new List<int>();
        try
        {
            foreach (Player player in runState.Players)
            {
                uint? option = synchronizer.GetPlayerVote(player);
                if (!option.HasValue)
                    return false;

                participants.Add(player);
                slots.Add(runState.GetPlayerSlotIndex(player));
                sides.Add((int)option.Value);
            }
        }
        catch (Exception e)
        {
            // Reading the synchronizer's vote array can only fail if it is mid-reset; never let that escape
            // into the game's event code — just let the stock decision run.
            Entry.Logger.Warn($"[conflict] could not snapshot shared-event votes: {e.Message}");
            return false;
        }

        if (participants.Count < 2 || sides.Distinct().Count() < 2)
            return false;

        session = new ConflictRpsSession(
            ConflictRpsCoordinator.AllocateSessionId(),
            ConflictKind.EventOption,
            participants,
            slots,
            sides);
        return true;
    }

    /// <summary>
    /// Injects the winning option by reproducing the stock tail of <c>ChooseSharedEventOption</c>: broadcast the
    /// chosen-option message, then run that option for every player's event model.
    /// </summary>
    internal static void Apply(EventSynchronizer synchronizer, ConflictRpsSession session)
    {
        if (session.WinnerSide == ConflictRpsSession.NoSide)
        {
            Entry.Logger.Warn($"[conflict] cannot apply session {session.Id}: no winning side");
            return;
        }

        uint optionIndex = (uint)session.WinnerSide;

        if (ConflictRpsHandles.EventNetServiceField?.GetValue(synchronizer) is not INetGameService net)
        {
            Entry.Logger.Warn("[conflict] cannot apply the event decision: EventSynchronizer._netService is missing");
            return;
        }

        if (ConflictRpsHandles.EventMessageBufferField?.GetValue(synchronizer) is not RunLocationTargetedMessageBuffer buffer)
        {
            Entry.Logger.Warn("[conflict] cannot apply the event decision: EventSynchronizer._messageBuffer is missing");
            return;
        }

        uint pageIndex = ConflictRpsHandles.EventPageIndexField?.GetValue(synchronizer) is uint page ? page : 0u;

        net.SendMessage(new SharedEventOptionChosenMessage
        {
            optionIndex = optionIndex,
            pageIndex = pageIndex,
            location = buffer.CurrentLocation,
        });

        MethodInfo? choose = ConflictRpsHandles.ChooseOptionForSharedEventMethod;
        if (choose == null)
        {
            Entry.Logger.Warn("[conflict] cannot apply the event decision: EventSynchronizer.ChooseOptionForSharedEvent is missing");
            return;
        }

        choose.Invoke(synchronizer, new object[] { optionIndex });
        Entry.Logger.Info(
            $"[conflict] event decision from session {session.Id}: option {optionIndex} on page {pageIndex} (winning side, {session.WinnerSlots.Count} player(s) on it)");
    }

    /// <summary>
    /// Re-runs the stock random decision. The thread-static flag keeps the prefix from taking the method over
    /// again, so the result is produced by exactly the same code (and the same RNG) as without this mod.
    /// </summary>
    internal static void Fallback(EventSynchronizer synchronizer)
    {
        MethodInfo? method = ConflictRpsHandles.ChooseSharedEventOptionMethod;
        if (method == null)
            return;

        _fallbackInProgress = true;
        try
        {
            Entry.Logger.Info("[conflict] falling back to the stock shared-event decision");
            method.Invoke(synchronizer, null);
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"[conflict] stock shared-event decision fallback failed: {e}");
        }
        finally
        {
            _fallbackInProgress = false;
        }
    }

    /// <summary>
    /// <c>IsShared</c> throws when no event is in progress; the prefix can in theory run just after an event
    /// finished, so treat that as "not a shared event" instead of letting it escape.
    /// </summary>
    private static bool IsShared(EventSynchronizer synchronizer)
    {
        try
        {
            return synchronizer.IsShared;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
