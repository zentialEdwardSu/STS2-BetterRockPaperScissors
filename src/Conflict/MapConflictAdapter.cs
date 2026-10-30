using System.Reflection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Glue between the conflict RPS and the stock map decision.
///
/// The stock <c>MoveToMapCoord</c> picks a random vote and then enqueues a travel action, so the only way to
/// make the RPS winner's vote the destination is to (a) read the votes before the pick, and (b) reproduce the
/// two side effects the stock method has — updating "which location we accept votes for" and enqueueing
/// <see cref="MoveToMapCoordAction"/>. Everything after that (travel animation, room entry, fades) stays stock.
/// </summary>
internal static class MapConflictAdapter
{
    [ThreadStatic] private static bool _fallbackInProgress;

    /// <summary>True while <see cref="Fallback"/> re-enters the patched method, so the prefix lets it through.</summary>
    internal static bool FallbackInProgress => _fallbackInProgress;

    /// <summary>Packs a map coordinate into one int (the side id used inside a session).</summary>
    internal static int PackCoord(MapCoord coord) => (coord.col << 16) | (coord.row & 0xFFFF);

    /// <summary>Inverse of <see cref="PackCoord"/>.</summary>
    internal static MapCoord UnpackCoord(int packed) => new((packed >> 16) & 0xFFFF, packed & 0xFFFF);

    /// <summary>
    /// Reads every player's vote and builds a session, or returns false when this is not a conflict: a vote is
    /// missing (should not happen — the caller only runs once all votes are in) or everybody agrees.
    /// Votes are snapshotted here, at decision time, so a later vote change cannot alter this fight.
    /// </summary>
    internal static bool TrySnapshot(MapSelectionSynchronizer synchronizer, out ConflictRpsSession? session)
    {
        session = null;
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
                MapVote? vote = synchronizer.GetVote(player);
                if (vote?.coord is not MapCoord coord)
                    return false;

                participants.Add(player);
                slots.Add(runState.GetPlayerSlotIndex(player));
                sides.Add(PackCoord(coord));
            }
        }
        catch (Exception e)
        {
            // Reading the synchronizer's vote array can only fail if it is mid-reset; never let that escape
            // into the game's map code — just let the stock decision run.
            Entry.Logger.Warn($"[conflict] could not snapshot map votes: {e.Message}");
            return false;
        }

        if (participants.Count < 2 || sides.Distinct().Count() < 2)
            return false;

        session = new ConflictRpsSession(
            ConflictRpsCoordinator.AllocateSessionId(),
            ConflictKind.MapPath,
            participants,
            slots,
            sides);
        return true;
    }

    /// <summary>
    /// Injects the winning coordinate using the stock method's own side effects, in the stock order: mark the
    /// old location as no longer accepting votes (so a late vote change is rejected instead of starting a
    /// second decision), then queue the travel action that every peer executes.
    /// </summary>
    internal static void Apply(MapSelectionSynchronizer synchronizer, ConflictRpsSession session)
    {
        if (session.WinnerSide == ConflictRpsSession.NoSide)
        {
            Entry.Logger.Warn($"[conflict] cannot apply session {session.Id}: no winning side");
            return;
        }

        MapCoord destination = UnpackCoord(session.WinnerSide);
        FieldInfo? field = ConflictRpsHandles.AcceptingVotesFromSourceField;
        if (field == null)
        {
            Entry.Logger.Warn("[conflict] cannot apply the map decision: MapSelectionSynchronizer._acceptingVotesFromSource is missing");
            return;
        }

        if (field.GetValue(synchronizer) is MapLocation location)
        {
            location.coord = destination;
            field.SetValue(synchronizer, location);
        }

        Player? localPlayer = LocalContext.GetMe(session.Participants);
        if (localPlayer == null)
        {
            Entry.Logger.Warn("[conflict] cannot queue the travel action: the local player is not a participant");
            return;
        }

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new MoveToMapCoordAction(localPlayer, destination));
        Entry.Logger.Info(
            $"[conflict] map decision from session {session.Id}: travelling to {destination} (winning side {session.WinnerSide}, {session.WinnerSlots.Count} player(s) on it)");
    }

    /// <summary>
    /// Re-runs the stock random decision. The thread-static flag keeps the prefix from taking the method over
    /// again, so the result is produced by exactly the same code (and the same RNG) as without this mod.
    /// </summary>
    internal static void Fallback(MapSelectionSynchronizer synchronizer)
    {
        MethodInfo? method = ConflictRpsHandles.MoveToMapCoordMethod;
        if (method == null)
            return;

        _fallbackInProgress = true;
        try
        {
            Entry.Logger.Info("[conflict] falling back to the stock map decision");
            method.Invoke(synchronizer, null);
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"[conflict] stock map decision fallback failed: {e}");
        }
        finally
        {
            _fallbackInProgress = false;
        }
    }
}
