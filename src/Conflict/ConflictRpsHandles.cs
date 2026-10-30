using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The private game members the conflict replacement has to reach into, resolved once at load.
///
/// None of these are public API, and the two game branches are different builds of the same version line
/// (beta's <c>EventSynchronizer</c> even grew an <c>EventCombatSynchronizer</c>), so every handle is looked
/// up by name and the feature disables itself when one is missing instead of throwing. The two types
/// referenced with <c>typeof</c> below are public and byte-for-byte identical on both branches, so using
/// them directly cannot break a build.
/// </summary>
internal static class ConflictRpsHandles
{
    /// <summary>
    /// The act/coord struct <c>MoveToMapCoord</c> rewrites so that votes arriving for the old map location
    /// get rejected. We have to reproduce that write, otherwise a vote change during our session would
    /// trigger a second decision.
    /// </summary>
    internal static readonly FieldInfo? AcceptingVotesFromSourceField =
        AccessTools.Field(typeof(MapSelectionSynchronizer), "_acceptingVotesFromSource");

    /// <summary>The host-only decision point for a split map vote.</summary>
    internal static readonly MethodInfo? MoveToMapCoordMethod =
        AccessTools.Method(typeof(MapSelectionSynchronizer), "MoveToMapCoord");

    /// <summary>The host-only decision point for a split shared-event vote.</summary>
    internal static readonly MethodInfo? ChooseSharedEventOptionMethod =
        AccessTools.Method(typeof(EventSynchronizer), "ChooseSharedEventOption");

    /// <summary>Runs one option for every player's event model and advances the event page index.</summary>
    internal static readonly MethodInfo? ChooseOptionForSharedEventMethod =
        AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForSharedEvent", new[] { typeof(uint) });

    /// <summary>Net service used by the event synchronizer to broadcast the chosen option.</summary>
    internal static readonly FieldInfo? EventNetServiceField =
        AccessTools.Field(typeof(EventSynchronizer), "_netService");

    /// <summary>Message buffer whose <c>CurrentLocation</c> tags the chosen-option message.</summary>
    internal static readonly FieldInfo? EventMessageBufferField =
        AccessTools.Field(typeof(EventSynchronizer), "_messageBuffer");

    /// <summary>Index of the option page being voted on; the stock message carries it.</summary>
    internal static readonly FieldInfo? EventPageIndexField =
        AccessTools.Field(typeof(EventSynchronizer), "_pageIndex");

    /// <summary>Canonical event model, used only to log which event a decision belonged to.</summary>
    internal static readonly FieldInfo? EventCanonicalEventField =
        AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent");

    private static bool _warned;

    /// <summary>True when the map-path replacement has everything it needs (independent of the event side).</summary>
    internal static bool MapResolved =>
        AcceptingVotesFromSourceField != null && MoveToMapCoordMethod != null;

    /// <summary>True when the shared-event replacement has everything it needs (independent of the map side).</summary>
    internal static bool EventResolved =>
        ChooseSharedEventOptionMethod != null
        && ChooseOptionForSharedEventMethod != null
        && EventNetServiceField != null
        && EventMessageBufferField != null
        && EventPageIndexField != null;

    /// <summary>
    /// True when every handle the decision replacement needs resolved. Log-only handles (the canonical
    /// event) are intentionally excluded, so a cosmetic rename cannot disable the whole feature.
    /// </summary>
    internal static bool AllResolved => MapResolved && EventResolved;

    /// <summary>Logs the missing handles exactly once, so a downgraded install is obvious in the log.</summary>
    internal static void WarnOnceIfIncomplete()
    {
        if (AllResolved || _warned)
            return;

        _warned = true;
        var missing = new List<string>();
        if (AcceptingVotesFromSourceField == null) missing.Add("MapSelectionSynchronizer._acceptingVotesFromSource");
        if (MoveToMapCoordMethod == null) missing.Add("MapSelectionSynchronizer.MoveToMapCoord");
        if (ChooseSharedEventOptionMethod == null) missing.Add("EventSynchronizer.ChooseSharedEventOption");
        if (ChooseOptionForSharedEventMethod == null) missing.Add("EventSynchronizer.ChooseOptionForSharedEvent");
        if (EventNetServiceField == null) missing.Add("EventSynchronizer._netService");
        if (EventMessageBufferField == null) missing.Add("EventSynchronizer._messageBuffer");
        if (EventPageIndexField == null) missing.Add("EventSynchronizer._pageIndex");

        Entry.Logger.Warn(
            $"[conflict] conflict RPS disabled: {missing.Count} game member(s) could not be resolved, stock behaviour is kept: {string.Join(", ", missing)}");
    }
}
