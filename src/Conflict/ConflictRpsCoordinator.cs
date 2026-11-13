using MegaCrit.Sts2.Core.Map;
using STS2BetterRockPaperScissors.Net;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Process-wide state for the one conflict that can be in flight: the current session, the host's
/// "already deciding" latch, and the pending winner-reveal animation.
///
/// The latch is the load-bearing part. The stock host re-enters its decision method every time a vote
/// arrives after all votes were seen, and our replacement is asynchronous — so without the latch a single
/// vote change during the fight would start a second session and enqueue a second travel action.
/// </summary>
internal static class ConflictRpsCoordinator
{
    private static byte _nextSessionId = 1;
    private static ConflictRpsSession? _current;
    private static bool _hostBusy;
    private static Task _revealTask = Task.CompletedTask;

    /// <summary>The conflict currently in flight (host or client view), if any.</summary>
    internal static ConflictRpsSession? Current => _current;

    /// <summary>True while the host is resolving a conflict; duplicate decisions are swallowed.</summary>
    internal static bool HostBusy => _hostBusy;

    /// <summary>The 1s map winner reveal, so the stock animation seam can await it instead of spinning.</summary>
    internal static Task RevealTask => _revealTask;

    /// <summary>Next session id; skips 0 so "no session" stays expressible and wraps within a byte.</summary>
    internal static byte AllocateSessionId()
    {
        byte id = _nextSessionId;
        _nextSessionId = _nextSessionId == byte.MaxValue ? (byte)1 : (byte)(_nextSessionId + 1);
        return id;
    }

    /// <summary>Host: claims the decision latch. False when a session is already running.</summary>
    internal static bool TryBeginHostSession(ConflictRpsSession session)
    {
        if (_hostBusy)
            return false;

        _hostBusy = true;
        _revealTask = Task.CompletedTask;
        session.Status = ConflictSessionStatus.Deciding;
        _current = session;
        Entry.Logger.Info($"[conflict] host starting {session.Describe()}");
        return true;
    }

    /// <summary>Host: releases the latch once the decision was applied (or fell back to the stock one).</summary>
    internal static void EndHostSession()
    {
        _hostBusy = false;
        Entry.Logger.Debug("[conflict] host decision latch released");
    }

    /// <summary>
    /// Client: takes over the current session from a host announcement. Stale/duplicate announcements (an
    /// older session id, or the same one twice) are rejected so a late packet cannot restart a finished
    /// fight.
    /// </summary>
    internal static bool TryAcceptClientSession(ConflictRpsSession session)
    {
        ConflictRpsSession? current = _current;
        if (current != null && !IsNewer(session.Id, current.Id))
        {
            Entry.Logger.Debug($"[conflict] ignoring stale {session.Describe()} (current id {current.Id})");
            return false;
        }

        if (current != null && ConflictRpsNetHub.IsRegistered)
            ConflictRpsNetHub.ClearSession(current.Id);

        _revealTask = Task.CompletedTask;
        session.Status = ConflictSessionStatus.Deciding;
        _current = session;
        Entry.Logger.Info($"[conflict] client accepted {session.Describe()}");
        return true;
    }

    /// <summary>Marks the session decided on <paramref name="winnerSide"/> and, for map conflicts, starts the reveal.</summary>
    internal static void MarkDecided(ConflictRpsSession session, int winnerSide, bool startReveal)
    {
        // Idempotent on purpose: the network handler marks the session synchronously, and the client loop's
        // terminal handling may well reach the same verdict a moment later.
        if (session.Status == ConflictSessionStatus.Decided)
            return;

        session.RememberWinningSide(winnerSide);
        session.Status = ConflictSessionStatus.Decided;
        Entry.Logger.Info(
            $"[conflict] {session.Describe()} decided: winning side {winnerSide}, players on it [{string.Join(",", session.WinnerSlots)}]");

        if (startReveal && session.Kind == ConflictKind.MapPath && session.WinnerSlots.Count > 0)
            _revealTask = MapConflictReveal.Begin(session);
    }

    /// <summary>Marks the session abandoned; the caller then re-runs the stock decision.</summary>
    internal static void MarkAborted(ConflictRpsSession session)
    {
        if (session.Status == ConflictSessionStatus.Decided)
            return;

        session.Status = ConflictSessionStatus.Aborted;
        Entry.Logger.Warn($"[conflict] {session.Describe()} aborted — falling back to the stock random decision");
    }

    /// <summary>Forgets the current session (used when a client's session times out).</summary>
    internal static void ClearCurrent()
    {
        _current = null;
        _revealTask = Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the roulette when a completed RPS session chose this map coordinate.
    /// </summary>
    internal static bool TryClaimMapSkip(MapCoord coord, out Task reveal)
    {
        reveal = Task.CompletedTask;
        ConflictRpsSession? session = _current;

        if (session is not { Kind: ConflictKind.MapPath, Status: ConflictSessionStatus.Decided, SkipConsumed: false }
            || session.WinnerSide == ConflictRpsSession.NoSide
            || MapConflictAdapter.UnpackCoord(session.WinnerSide) != coord)
            return false;

        session.SkipConsumed = true;
        reveal = _revealTask;
        Entry.Logger.Info($"[conflict] suppressing stock map split-vote roulette, travelling to {coord}");
        return true;
    }

    /// <summary>Event seam: claims the right to skip the stock split-vote roulette exactly once.</summary>
    internal static bool TryClaimEventSkip()
    {
        ConflictRpsSession? session = _current;
        if (session is not { Kind: ConflictKind.EventOption, Status: ConflictSessionStatus.Decided, SkipConsumed: false })
            return false;

        session.SkipConsumed = true;
        Entry.Logger.Info("[conflict] suppressing stock event split-vote roulette");
        return true;
    }

    /// <summary>
    /// Explains why the stock map roulette was *not* suppressed. Silent unless a map session exists, so ordinary
    /// (stock-decided) travels stay quiet while a genuine suppression failure leaves a trail.
    /// </summary>
    internal static void LogMapSkipMiss(MapCoord coord)
    {
        ConflictRpsSession? session = _current;
        if (session == null || session.Kind != ConflictKind.MapPath)
            return;

        Entry.Logger.Warn(
            $"[conflict] stock map roulette is playing for {coord}: session {session.Id} status {session.Status}, winner slot {session.WinnerSlot}, already consumed {session.SkipConsumed}");
    }

    /// <summary>Same as <see cref="LogMapSkipMiss"/> for shared events.</summary>
    internal static void LogEventSkipMiss()
    {
        ConflictRpsSession? session = _current;
        if (session == null || session.Kind != ConflictKind.EventOption)
            return;

        Entry.Logger.Warn(
            $"[conflict] stock event roulette is playing: session {session.Id} status {session.Status}, winner slot {session.WinnerSlot}, already consumed {session.SkipConsumed}");
    }

    /// <summary>Serial-number comparison that behaves across the byte wrap-around.</summary>
    private static bool IsNewer(byte candidate, byte reference) =>
        candidate != reference && (byte)(candidate - reference) < 128;
}
