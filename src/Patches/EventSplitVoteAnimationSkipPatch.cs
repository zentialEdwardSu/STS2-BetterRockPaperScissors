using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events;
using STS2BetterRockPaperScissors.Conflict;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Suppresses the stock split-vote roulette for a shared-event conflict the mod already decided.
///
/// Seam: <c>EventSplitVoteAnimation.TryPlay(NEventOptionButton)</c>, awaited by
/// <c>NEventLayout.BeforeSharedOptionChosen</c>. Only the wheel is skipped: the caller then continues with the
/// vanilla post-choice presentation (the chosen option flashes, the others grey out), which is the feedback the
/// feature is supposed to keep.
/// </summary>
[HarmonyPatch]
public static class EventSplitVoteAnimationSkipPatch
{
    private static readonly MethodInfo? TryPlayMethod =
        AccessTools.Method(typeof(EventSplitVoteAnimation), "TryPlay", new[] { typeof(NEventOptionButton) });

    [HarmonyPrepare]
    private static bool Prepare() => TryPlayMethod != null;

    private static MethodBase TargetMethod() => TryPlayMethod!;

    private static bool Prefix(ref Task __result)
    {
        if (!ConflictRpsCoordinator.TryClaimEventSkip())
        {
            ConflictRpsCoordinator.LogEventSkipMiss();
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}
