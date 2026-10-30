using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using STS2BetterRockPaperScissors.Conflict;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Replaces the host's random shared-event-option pick with a manual rock-paper-scissors fight.
///
/// Seam: <c>EventSynchronizer.ChooseSharedEventOption()</c>. The stock method runs on the host once every player
/// has voted on a shared event, picks a random vote, broadcasts the chosen index, then runs it, all in one
/// synchronous body. We take it over only for a real split vote on a shared event, then reproduce that body with
/// the RPS winner's index (see <see cref="EventConflictAdapter.Apply"/>).
///
/// Non-shared events never reach this method, and any failed precondition simply lets the original run.
/// </summary>
[HarmonyPatch]
public static class EventConflictDecisionPatch
{
    [HarmonyPrepare]
    private static bool Prepare() =>
        ConflictRpsHandles.ChooseSharedEventOptionMethod != null
        && ConflictRpsHandles.ChooseOptionForSharedEventMethod != null;

    private static MethodBase TargetMethod() => ConflictRpsHandles.ChooseSharedEventOptionMethod!;

    private static bool Prefix(EventSynchronizer __instance)
    {
        if (!ConflictRpsGate.CanHostDecide(ConflictKind.EventOption))
            return true;

        if (EventConflictAdapter.FallbackInProgress)
            return true;

        if (ConflictRpsCoordinator.HostBusy)
        {
            Entry.Logger.Info(
                "[conflict] ignoring a repeated shared-event decision request while a conflict RPS is in flight");
            return false;
        }

        if (!EventConflictAdapter.TrySnapshot(__instance, out ConflictRpsSession? session) || session == null)
            return true;

        if (!ConflictRpsCoordinator.TryBeginHostSession(session))
            return true;

        TaskHelper.RunSafely(RunAsync(__instance, session));
        return false;
    }

    private static async Task RunAsync(EventSynchronizer synchronizer, ConflictRpsSession session)
    {
        (CancellationToken token, Action dispose) = ConflictCancellation.Create(session.Kind);
        try
        {
            ConflictRpsOutcome outcome;
            try
            {
                outcome = await ConflictRpsHost.RunAsync(session, token);
            }
            catch (Exception e)
            {
                Entry.Logger.Error($"[conflict] shared-event conflict session {session.Id} threw: {e}");
                outcome = ConflictRpsOutcome.Aborted;
            }

            switch (outcome)
            {
                case ConflictRpsOutcome.Decided:
                    try
                    {
                        EventConflictAdapter.Apply(synchronizer, session);
                    }
                    catch (Exception e)
                    {
                        // The winner was already broadcast; only the local option execution can be missing here.
                        Entry.Logger.Error($"[conflict] failed to apply the event decision of session {session.Id}: {e}");
                    }

                    break;

                case ConflictRpsOutcome.Aborted:
                    EventConflictAdapter.Fallback(synchronizer);
                    break;

                case ConflictRpsOutcome.Cancelled:
                    // The event screen went away mid-fight: leave the game alone.
                    break;
            }
        }
        finally
        {
            dispose();
            ConflictRpsCoordinator.EndHostSession();
        }
    }
}
