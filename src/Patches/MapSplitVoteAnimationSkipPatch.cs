using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using STS2BetterRockPaperScissors.Conflict;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Suppresses the stock split-vote roulette for a map conflict the mod already decided, and replaces the awaited
/// animation with our 1s winner reveal.
///
/// Seam: <c>MapSplitVoteAnimation.TryPlay(MapCoord)</c>, awaited by <c>NMapScreen.TravelToMapCoord</c> right
/// before the choose-VFX and fade-out. Returning our reveal task as the result keeps the stock caller's order
/// intact ("reveal, then enter the room") without touching <c>TravelToMapCoord</c> itself.
///
/// The stock wheel is deliberately never shown for a mod-decided conflict: it picks a random voter, so replaying
/// it after a real fight would point at the wrong player. The claim only matches a session this peer knows about
/// and, when the winning coordinate is known, the coordinate the game is travelling to.
/// </summary>
[HarmonyPatch]
public static class MapSplitVoteAnimationSkipPatch
{
    private static readonly MethodInfo? TryPlayMethod =
        AccessTools.Method(typeof(MapSplitVoteAnimation), "TryPlay", new[] { typeof(MapCoord) });

    [HarmonyPrepare]
    private static bool Prepare() => TryPlayMethod != null;

    private static MethodBase TargetMethod() => TryPlayMethod!;

    private static bool Prefix(object[] __args, ref Task __result)
    {
        // Bound through __args rather than a named parameter: the game's parameter name is not part of any
        // contract, and a rename would silently stop the suppression from ever matching.
        if (__args.Length == 0 || __args[0] is not MapCoord selectedCoord)
            return true;

        if (!ConflictRpsCoordinator.TryClaimMapSkip(selectedCoord, out Task reveal))
        {
            ConflictRpsCoordinator.LogMapSkipMiss(selectedCoord);
            return true;
        }

        __result = reveal;
        return false;
    }
}
