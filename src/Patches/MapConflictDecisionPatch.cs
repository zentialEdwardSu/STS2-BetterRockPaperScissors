using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using STS2BetterRockPaperScissors.Conflict;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Replaces the host's random map-vote pick with a manual rock-paper-scissors fight.
///
/// Seam: <c>MapSelectionSynchronizer.MoveToMapCoord()</c>. The stock method runs on the host only, the moment
/// every player has voted, and immediately picks a random vote and enqueues the travel action. Taking it over
/// means we decide which coordinate wins, which is what the feature needs: the stock wheel animation is only a
/// replay of an already-made choice.
///
/// The prefix returns false (suppressing the original) only when every precondition holds; in every other case it
/// returns true and the stock method runs untouched. A second call while our session is in flight, which the game
/// makes when a vote changes mid-fight, is swallowed instead of starting a second fight.
/// </summary>
[HarmonyPatch]
public static class MapConflictDecisionPatch
{
    [HarmonyPrepare]
    private static bool Prepare() =>
        ConflictRpsHandles.MoveToMapCoordMethod != null
        && ConflictRpsHandles.AcceptingVotesFromSourceField != null;

    private static MethodBase TargetMethod() => ConflictRpsHandles.MoveToMapCoordMethod!;

    private static bool Prefix(MapSelectionSynchronizer __instance)
    {
        if (!ConflictRpsGate.CanHostDecide(ConflictKind.MapPath))
            return true;

        // Our own fallback re-entering the patched method: let the stock decision run.
        if (MapConflictAdapter.FallbackInProgress)
            return true;

        if (ConflictRpsCoordinator.HostBusy)
        {
            Entry.Logger.Info("[conflict] ignoring a repeated map decision request while a conflict RPS is in flight");
            return false;
        }

        if (!MapConflictAdapter.TrySnapshot(__instance, out ConflictRpsSession? session) || session == null)
            return true;

        if (!ConflictRpsCoordinator.TryBeginHostSession(session))
            return true;

        TaskHelper.RunSafely(RunAsync(__instance, session));
        return false;
    }

    private static async Task RunAsync(MapSelectionSynchronizer synchronizer, ConflictRpsSession session)
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
                Entry.Logger.Error($"[conflict] map conflict session {session.Id} threw: {e}");
                outcome = ConflictRpsOutcome.Aborted;
            }

            switch (outcome)
            {
                case ConflictRpsOutcome.Decided:
                    try
                    {
                        MapConflictAdapter.Apply(synchronizer, session);
                    }
                    catch (Exception e)
                    {
                        // The decision was already broadcast, so there is nothing to undo; the stock travel
                        // action for the winner's coordinate simply will not be queued.
                        Entry.Logger.Error($"[conflict] failed to apply the map decision of session {session.Id}: {e}");
                    }

                    break;

                case ConflictRpsOutcome.Aborted:
                    MapConflictAdapter.Fallback(synchronizer);
                    break;

                case ConflictRpsOutcome.Cancelled:
                    // The map screen went away mid-fight (run ending / room transition): leave the game alone.
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
