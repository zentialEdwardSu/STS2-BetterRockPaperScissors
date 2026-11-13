using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Net;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Installs the conflict message handlers once per run, on every peer.
///
/// Timing matters: a client can only act on the host's "session began" message if its handlers are already
/// registered, and the very first conflict can happen in the first event room, before the first map screen. The
/// map-selection synchronizer is created with the run state on host and client alike, before any room, so its
/// constructor is the earliest safe hook. Registration is idempotent per net service instance, so a second run (or
/// a reconnect handing us a new service) re-registers cleanly.
/// </summary>
[HarmonyPatch]
public static class RunStartNetRegistrationPatch
{
    private static readonly Type SynchronizerType = typeof(MapSelectionSynchronizer);

    private static readonly ConstructorInfo? Target =
        AccessTools.Constructor(
            SynchronizerType,
            new[] { typeof(INetGameService), typeof(ActionQueueSynchronizer), typeof(RunState) })
        ?? AccessTools.Constructor(
            SynchronizerType,
            new[] { typeof(INetGameService), typeof(ActionQueueSynchronizer), typeof(IRunState) });

    [HarmonyPrepare]
    private static bool Prepare() => Target != null;

    private static MethodBase TargetMethod() => Target!;

    private static void Postfix(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is not INetGameService net)
            return;

        ConflictRpsNetHub.EnsureRegistered(net);
    }
}
