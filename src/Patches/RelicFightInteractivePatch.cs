using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Fight;
using STS2BetterRockPaperScissors.Net;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Intercepts the relic-award animation so that any FoughtOver result has its random rock-paper-
/// scissors fight replaced by player-chosen gestures (synced across peers) before the stock
/// animation replays it.
///
/// Stable and beta use different award signatures. Resolve the matching pair before taking over
/// so the original animation can finish the room on either branch.
/// </summary>
[HarmonyPatch]
public static class RelicFightInteractivePatch
{
    private static readonly FieldInfo? RelicIndexField = AccessTools.Field(typeof(RelicPickingResult), "relicIndex");
    private static readonly (MethodInfo OnAwarded, MethodInfo Animate, bool UsesRelicIndex)? AwardMethods =
        ResolveAwardMethods();

    private static (MethodInfo, MethodInfo, bool)? ResolveAwardMethods()
    {
        var collectionType = typeof(NTreasureRoomRelicCollection);
        var stableSignature = new[] { typeof(List<RelicPickingResult>) };
        var betaSignature = new[] { typeof(List<RelicPickingResult>), typeof(bool) };

        if (AccessTools.DeclaredMethod(collectionType, "OnRelicsAwarded", betaSignature) is { } beta &&
            AccessTools.DeclaredMethod(collectionType, "AnimateRelicAwards", betaSignature) is { } betaAnimation &&
            beta.ReturnType == typeof(void) && betaAnimation.ReturnType == typeof(Task) &&
            RelicIndexField?.FieldType == typeof(int))
            return (beta, betaAnimation, true);

        if (AccessTools.DeclaredMethod(collectionType, "OnRelicsAwarded", stableSignature) is { } stable &&
            AccessTools.DeclaredMethod(collectionType, "AnimateRelicAwards", stableSignature) is { } stableAnimation &&
            stable.ReturnType == typeof(void) && stableAnimation.ReturnType == typeof(Task))
            return (stable, stableAnimation, false);

        return null;
    }

    [HarmonyPrepare]
    private static bool Prepare() => AwardMethods.HasValue;

    private static MethodBase TargetMethod() => AwardMethods!.Value.OnAwarded;

    private static bool Prefix(NTreasureRoomRelicCollection __instance, object[] __args)
    {
        if (AwardMethods is not { } methods || __args[0] is not List<RelicPickingResult> results ||
            (methods.UsesRelicIndex && __args[1] is not true))
            return true;

        var fights = new List<(RelicPickingResult Result, byte Slot)>();
        var slots = new HashSet<byte>();
        IReadOnlyList<RelicModel>? currentRelics = methods.UsesRelicIndex
            ? null
            : RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;

        foreach (RelicPickingResult result in results)
        {
            if (result.type != RelicPickingResultType.FoughtOver || result.fight == null)
                continue;

            int index = methods.UsesRelicIndex
                ? (int)RelicIndexField!.GetValue(result)!
                : IndexOfRelic(currentRelics, result.relic);
            if (index < 0 || index > byte.MaxValue || !slots.Add((byte)index))
            {
                Entry.Logger.Warn("[RPS] Could not identify distinct relic slots. Using the original awards.");
                return true;
            }
            fights.Add((result, (byte)index));
        }

        if (fights.Count == 0)
            return true;

        fights.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        TaskHelperRunSafely(OrchestrateAsync(__instance, results, fights, methods.Animate, (object[])__args.Clone()));
        return false;
    }

    private static async Task OrchestrateAsync(
        NTreasureRoomRelicCollection collection,
        List<RelicPickingResult> results,
        List<(RelicPickingResult Result, byte Slot)> fights,
        MethodInfo animateAwards,
        object[] awardArgs)
    {
        using var cts = new CancellationTokenSource();
        void OnTreeExiting() => cts.Cancel();
        collection.TreeExiting += OnTreeExiting;
        var granted = new HashSet<RelicPickingResult>();
        FightAnimator? animator = null;

        try
        {
            collection.SetSelectionEnabled(false);
            RpsNetHub.Register();
            try
            {
                // The two-argument award path moves the holder beside the backstop.
                animator = new FightAnimator(collection, reparentHolder: awardArgs.Length == 2, cts.Token);
                animator.PreFreezeHands();
            }
            catch (Exception e)
            {
                animator = null;
                Entry.Logger.Warn($"[RPS] Could not prepare the hand animations: {e}");
            }

            foreach (var (result, slot) in fights)
            {
                if (await InteractiveFightResolver.ResolveFight(result, slot, collection, animator, cts.Token))
                    granted.Add(result);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"[RPS] Interactive fight failed. Passing remaining awards to the original animation: {e}");
        }
        finally
        {
            animator?.EndFight(false);
            if (GodotObject.IsInstanceValid(collection))
                collection.TreeExiting -= OnTreeExiting;
            try
            {
                RpsNetHub.Unregister();
            }
            catch (Exception e)
            {
                Entry.Logger.Warn($"[RPS] Could not unregister the fight message handlers: {e}");
            }
        }

        if (cts.IsCancellationRequested || !GodotObject.IsInstanceValid(collection) || !collection.IsInsideTree())
            return;

        try
        {
            ReassignConsolationPrizes(results);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[RPS] Could not recompute consolation prizes: {e}");
        }
        awardArgs[0] = results.Where(result => !granted.Contains(result)).ToList();
        await (Task)animateAwards.Invoke(collection, awardArgs)!;
    }

    /// <summary>
    /// Recomputes consolation/skip relic assignments from the (possibly flipped) fight winners,
    /// mirroring the game's own rule: leftover relics go to fight losers, ordered by slot index.
    /// Deterministic given identical resolved fights, so it stays in sync across peers.
    /// </summary>
    private static void ReassignConsolationPrizes(List<RelicPickingResult> results)
    {
        // The leftover-relic pool keeps its existing order (the host's shuffle), taken from the
        // current consolation + skipped results.
        var pool = results.Where(r => r.type is RelicPickingResultType.ConsolationPrize or RelicPickingResultType.Skipped).ToList();
        if (pool.Count == 0)
            return;

        // Winners of decided relics (single-voter + fought-over).
        var winners = new HashSet<Player>();
        IRunState? runState = null;
        foreach (RelicPickingResult r in results)
        {
            if ((r.type is RelicPickingResultType.OnlyOnePlayerVoted or RelicPickingResultType.FoughtOver) && r.player != null)
            {
                winners.Add(r.player);
                runState ??= r.player.RunState;
            }
        }

        // Eligible recipients = fight participants who didn't win (the losers), ordered by slot index.
        var recipients = new List<Player>();
        foreach (var r in results)
        {
            if (r.type != RelicPickingResultType.FoughtOver || r.fight == null)
                continue;
            foreach (Player p in r.fight.playersInvolved)
            {
                if (!winners.Contains(p) && !recipients.Contains(p))
                    recipients.Add(p);
            }
        }
        if (runState != null)
            recipients.Sort((a, b) => runState.GetPlayerSlotIndex(a).CompareTo(runState.GetPlayerSlotIndex(b)));

        for (var i = 0; i < pool.Count; i++)
        {
            var award = pool[i];
            award.player = i < recipients.Count ? recipients[i] : null;
            award.type = award.player != null ? RelicPickingResultType.ConsolationPrize : RelicPickingResultType.Skipped;
        }
    }

    private static int IndexOfRelic(IReadOnlyList<RelicModel>? relics, RelicModel relic)
    {
        if (relics == null)
            return -1;
        for (var i = 0; i < relics.Count; i++)
        {
            if (ReferenceEquals(relics[i], relic))
                return i;
        }
        return -1;
    }

    private static void TaskHelperRunSafely(Task task)
    {
        // Mirror the game's fire-and-forget pattern but log faults via our logger.
        _ = task.ContinueWith(
            t => Entry.Logger.Error($"Unhandled relic fight orchestration error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
