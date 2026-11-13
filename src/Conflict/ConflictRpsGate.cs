using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The single place that answers "may the mod take over this decision right now?".
///
/// Host and client answer differently on purpose: the toggles are host-authoritative, so a client whose
/// local toggle is off still has to take part in a session the host starts. Refusing would only starve the
/// host's countdown and hand that player a random gesture.
/// </summary>
internal static class ConflictRpsGate
{
    /// <summary>Host side: resolved game members, a real multiplayer host, and the matching toggle.</summary>
    internal static bool CanHostDecide(ConflictKind kind)
    {
        if (!HandlesResolved(kind))
        {
            ConflictRpsHandles.WarnOnceIfIncomplete();
            return false;
        }

        RunManager run = RunManager.Instance;
        INetGameService? net = run.NetService;
        if (net == null || net.Type != NetGameType.Host || run.IsSingleplayerOrFakeMultiplayer)
            return false;

        return kind switch
        {
            ConflictKind.MapPath => ModConfig.MapConflictRps,
            ConflictKind.EventOption => ModConfig.EventConflictRps,
            _ => false,
        };
    }

    /// <summary>Client side: participation is decided by the host, never by the local toggles.</summary>
    internal static bool CanClientParticipate(ConflictKind kind)
    {
        if (!HandlesResolved(kind))
            return false;

        RunManager run = RunManager.Instance;
        INetGameService? net = run.NetService;
        if (net == null || net.Type != NetGameType.Client || run.IsSingleplayerOrFakeMultiplayer)
            return false;

        Entry.Logger.Debug($"[conflict] client joining host-driven {kind} conflict RPS");
        return true;
    }

    /// <summary>
    /// The two replacements degrade independently: a missing map member must not disable shared-event fights,
    /// and vice versa.
    /// </summary>
    private static bool HandlesResolved(ConflictKind kind) => kind switch
    {
        ConflictKind.MapPath => ConflictRpsHandles.MapResolved,
        ConflictKind.EventOption => ConflictRpsHandles.EventResolved,
        _ => false,
    };
}
