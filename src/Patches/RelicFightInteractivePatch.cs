using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Fight;
using STS2BetterRockPaperScissors.Net;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Intercepts the relic-award animation so that any FoughtOver result has its random rock-paper-
/// scissors fight replaced by player-chosen gestures (synced across peers) before the stock
/// animation replays it.
///
/// Seam: NTreasureRoomRelicCollection.OnRelicsAwarded(List&lt;RelicPickingResult&gt;) — a plain
/// synchronous method that runs on every peer at the same logical point. We prefix it, run our
/// async resolver, then call the original private AnimateRelicAwards so all existing animation and
/// relic-granting code runs unchanged. On any failure we fall back to animating the original RNG
/// fights so the room can never hang.
/// </summary>
[HarmonyPatch]
public static class RelicFightInteractivePatch
{
    private static readonly Type CollectionType = AccessTools.TypeByName(
        "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection");

    private static readonly MethodInfo AnimateRelicAwardsMethod =
        CollectionType != null ? AccessTools.Method(CollectionType, "AnimateRelicAwards") : null;

    private static MethodBase TargetMethod()
    {
        if (CollectionType == null)
            throw new Exception("Cannot find NTreasureRoomRelicCollection type");
        MethodInfo method = AccessTools.Method(CollectionType, "OnRelicsAwarded");
        if (method == null)
            throw new Exception("Cannot find OnRelicsAwarded method");
        return method;
    }

    private static bool Prefix(object __instance, List<RelicPickingResult> results)
    {
        // If anything we depend on is missing, let the original run untouched.
        if (AnimateRelicAwardsMethod == null || __instance is not Control overlayParent)
            return true;

        TaskHelperRunSafely(OrchestrateAsync(__instance, overlayParent, results));
        return false; // we take over; original is suppressed.
    }

    private static async Task OrchestrateAsync(object instance, Control overlayParent, List<RelicPickingResult> results)
    {
        // Cancel in-flight prompts/awaits if the relic screen leaves the tree mid-fight.
        var cts = new System.Threading.CancellationTokenSource();
        void OnTreeExiting() => cts.Cancel();
        overlayParent.TreeExiting += OnTreeExiting;

        try
        {
            RpsNetHub.Register();
            try
            {
                IReadOnlyList<RelicModel> currentRelics =
                    RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;

                // Animates each RPS round as it resolves (per-round), instead of the stock end-of-fight
                // replay. When unavailable (missing reflection handle) we leave fights on the stock
                // path so the room still works.
                var animator = new FightAnimator(instance, cts.Token);
                if (animator.Available)
                    animator.PreFreezeHands();

                // Process fought-over relics in a deterministic order (relic index) on every peer.
                var fights = new List<(RelicPickingResult result, byte key)>();
                foreach (RelicPickingResult result in results)
                {
                    if (result.type != RelicPickingResultType.FoughtOver || result.fight == null)
                        continue;
                    byte key = (byte)IndexOfRelic(currentRelics, result.relic);
                    fights.Add((result, key));
                }
                fights.Sort((a, b) => a.key.CompareTo(b.key));

                Entry.Logger.Info(
                    $"[RPS] OnRelicsAwarded intercepted: {results.Count} result(s), {fights.Count} fought-over fight(s) to resolve interactively (animator {(animator.Available ? "on" : "unavailable")})");

                foreach ((RelicPickingResult result, byte key) in fights)
                {
                    await InteractiveFightResolver.ResolveFight(
                        result, key, overlayParent, animator, cts.Token);
                }

                // Fight winners may have changed vs. the RNG outcome the game used to assign
                // consolation/skip relics. Recompute those so a flipped winner doesn't also keep the
                // consolation relic that was originally handed to the (then-)loser.
                ReassignConsolationPrizes(results);

                // The per-round animator already animated and GRANTED each fought-over relic. Strip
                // those results so the stock AnimateRelicAwards doesn't re-flash the backstop or
                // re-grant them; it still fires the begin/finished signals and animates+grants the
                // single-voter / consolation / skipped relics.
                if (animator.Available)
                    results.RemoveAll(r => r.type == RelicPickingResultType.FoughtOver);
            }
            finally
            {
                RpsNetHub.Unregister();
            }
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"Interactive relic fight failed, falling back to default animation: {e}");
        }
        finally
        {
            overlayParent.TreeExiting -= OnTreeExiting;
            cts.Dispose();
        }

        // Hand off to the stock animation + relic granting with the (possibly rewritten) results.
        try
        {
            var task = (Task)AnimateRelicAwardsMethod.Invoke(instance, new object[] { results });
            if (task != null)
                await task;
        }
        catch (Exception e)
        {
            Entry.Logger.Error($"Failed to invoke AnimateRelicAwards: {e}");
        }
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
        var pool = new List<RelicModel>();
        for (var i = results.Count - 1; i >= 0; i--)
        {
            var type = results[i].type;
            if (type != RelicPickingResultType.ConsolationPrize && type != RelicPickingResultType.Skipped) continue;
            pool.Insert(0, results[i].relic);
            results.RemoveAt(i);
        }
        if (pool.Count == 0)
            return;

        // Winners of decided relics (single-voter + fought-over).
        var winners = new HashSet<Player>();
        IRunState runState = null;
        foreach (RelicPickingResult r in results)
        {
            if (r.player != null)
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
            if (i < recipients.Count)
            {
                results.Add(new RelicPickingResult
                {
                    type = RelicPickingResultType.ConsolationPrize,
                    player = recipients[i],
                    relic = pool[i],
                });
                Entry.Logger.Info($"[RPS] consolation relic {pool[i]} -> {recipients[i].NetId}");
            }
            else
            {
                results.Add(new RelicPickingResult
                {
                    type = RelicPickingResultType.Skipped,
                    player = null,
                    relic = pool[i],
                });
                Entry.Logger.Info($"[RPS] relic {pool[i]} left behind (no recipient)");
            }
        }
    }

    private static int IndexOfRelic(IReadOnlyList<RelicModel> relics, RelicModel relic)
    {
        if (relics == null)
            return 0;
        for (var i = 0; i < relics.Count; i++)
        {
            if (ReferenceEquals(relics[i], relic))
                return i;
        }
        return 0;
    }

    private static void TaskHelperRunSafely(Task task)
    {
        // Mirror the game's fire-and-forget pattern but log faults via our logger.
        _ = task.ContinueWith(
            t => Entry.Logger.Error($"Unhandled relic fight orchestration error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
