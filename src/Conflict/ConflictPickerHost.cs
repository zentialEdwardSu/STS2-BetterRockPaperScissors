using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Random;
using STS2BetterRockPaperScissors.Fight;
using STS2BetterRockPaperScissors.Net;
using STS2BetterRockPaperScissors.Overlays;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The gesture picker + countdown for conflict RPS, on both sides of the wire.
///
/// Reuses <see cref="RpsPickerView"/> verbatim (same hands, same countdown bar, same optional reset button)
/// and re-implements the countdown loop that <c>InteractiveFightResolver</c> uses for relic fights, rather
/// than sharing it: that code is shipped and tested, and the conflict variant needs different "ready"
/// semantics (every survivor is a human, there are no bots to wait out). The loop is deliberately
/// frame-driven so the bar stays in lockstep with the host's authoritative timer and keeps draining while
/// the ESC menu is open.
/// </summary>
internal static class ConflictPickerHost
{
    /// <summary>
    /// Where the picker should be attached: the screen the player is looking at, so the overlay sits on top
    /// of exactly that screen. Falls back to the run's global UI when the screen node is not available.
    /// </summary>
    internal static Control? ResolveParent(ConflictKind kind)
    {
        Control? screen = kind == ConflictKind.MapPath ? NMapScreen.Instance : NEventRoom.Instance;
        return screen ?? NRun.Instance?.GlobalUi;
    }

    /// <summary>
    /// Host: shows the local picker, runs the authoritative countdown, and returns one gesture per
    /// participant (<see cref="RpsMove.None"/> for anyone already eliminated). Survivors that did not pick
    /// in time get an RNG gesture so a single idle player can never stall the whole team.
    /// </summary>
    internal static async Task<byte[]> RunHostRoundAsync(
        ConflictRpsSession session,
        int round,
        byte countdownSeconds,
        bool allowReset,
        CancellationToken token)
    {
        var moves = new byte[session.Participants.Count];
        Array.Fill(moves, RpsMove.None);

        int localSlot = session.LocalSlot();
        bool localAlive = localSlot >= 0 && session.Survivors.Contains(localSlot);
        RpsPickerView? overlay = localAlive ? ShowOverlay(session, allowReset) : null;

        try
        {
            await RunCountdownAsync(session, round, countdownSeconds, allowReset, localSlot, overlay, token);

            byte localPick = overlay is { HasPick: true } ? (byte)overlay.CurrentPick!.Value : RpsMove.None;
            var rng = Rng.Chaotic;

            foreach (int slot in session.Survivors)
            {
                if (slot == localSlot)
                {
                    if (localPick != RpsMove.None)
                    {
                        moves[slot] = localPick;
                        continue;
                    }

                    moves[slot] = (byte)rng.NextInt(3);
                    Entry.Logger.Info(
                        $"[conflict] local player did not pick in time — RNG auto-pick {(RelicPickingFightMove)moves[slot]} (session {session.Id} round {round})");
                    continue;
                }

                byte? received = ConflictRpsNetHub.TryGetMove(session.Id, round, slot);
                if (received.HasValue)
                {
                    moves[slot] = received.Value;
                    continue;
                }

                moves[slot] = (byte)rng.NextInt(3);
                Entry.Logger.Info(
                    $"[conflict] no gesture from slot {slot} — RNG auto-pick {(RelicPickingFightMove)moves[slot]} (session {session.Id} round {round})");
            }
        }
        finally
        {
            overlay?.Close();
        }

        return moves;
    }

    /// <summary>
    /// Client: shows the local picker for one round, sending the pick (and every re-pick) to the host, and
    /// keeps it on screen until the host's round result arrives, the countdown expires, or the session ends.
    /// The countdown is pre-drained by the one-way latency so the client's bar empties at the same wall-clock
    /// instant as the host's; the host's result remains authoritative either way.
    /// </summary>
    internal static async Task RunClientRoundAsync(
        ConflictRpsSession session,
        int round,
        byte countdownSeconds,
        double latencyOffset,
        bool allowReset,
        Task roundFinished,
        Control? parent,
        CancellationToken token)
    {
        if (parent == null)
        {
            Entry.Logger.Warn($"[conflict] no UI parent for the {session.Kind} picker — this player will time out");
            return;
        }

        RpsPickerView overlay = ShowOverlay(session, allowReset);
        double remaining = Math.Max(0.0, countdownSeconds - latencyOffset);
        overlay.SetRemaining(remaining, countdownSeconds);

        RelicPickingFightMove? lastSent = null;
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;

        while (remaining > 0.0 && !roundFinished.IsCompleted && !token.IsCancellationRequested)
        {
            if (overlay.HasPick && overlay.CurrentPick != lastSent)
            {
                ConflictRpsNetHub.SendMove(session.Id, round, (byte)overlay.CurrentPick!.Value);
                lastSent = overlay.CurrentPick;
            }

            if (tree == null)
            {
                await Task.WhenAny(overlay.Result, roundFinished, Task.Delay((int)(remaining * 1000.0)));
                break;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            remaining -= tree.Root.GetProcessDeltaTime();
            overlay.SetRemaining(remaining, countdownSeconds);
            overlay.SyncPauseVisibility();
        }

        // Catch a pick/re-pick that landed on the final frame before the loop ended.
        if (overlay.HasPick && overlay.CurrentPick != lastSent)
            ConflictRpsNetHub.SendMove(session.Id, round, (byte)overlay.CurrentPick!.Value);

        overlay.Close();
    }

    /// <summary>
    /// Host/client: rides the host-authoritative countdown. With re-choosing off the round resolves as soon
    /// as every survivor has a gesture; with it on the full countdown always runs so a pick stays changeable.
    /// </summary>
    private static async Task RunCountdownAsync(
        ConflictRpsSession session,
        int round,
        byte countdownSeconds,
        bool allowReset,
        int localSlot,
        RpsPickerView? overlay,
        CancellationToken token)
    {
        double remaining = countdownSeconds;
        overlay?.SetRemaining(remaining, countdownSeconds);

        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        while (remaining > 0.0 && !token.IsCancellationRequested)
        {
            if (!allowReset && AllSurvivorsReady(session, round, localSlot, overlay))
                return;

            if (tree == null)
            {
                await Task.Delay((int)(remaining * 1000.0));
                return;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            remaining -= tree.Root.GetProcessDeltaTime();
            overlay?.SetRemaining(remaining, countdownSeconds);
            // The ESC menu is a game-logic pause, not a scene-tree pause, so the countdown keeps draining
            // (correct: the host's timer never stops). Only hide the raised picker while a menu covers it.
            overlay?.SyncPauseVisibility();
        }
    }

    /// <summary>
    /// Host: true when every surviving participant has a gesture ready (local pick in, remote move received).
    /// Only consulted when re-choosing is disabled.
    /// </summary>
    private static bool AllSurvivorsReady(ConflictRpsSession session, int round, int localSlot, RpsPickerView? overlay)
    {
        foreach (int slot in session.Survivors)
        {
            if (slot == localSlot)
            {
                if (overlay == null || !overlay.HasPick)
                    return false;
                continue;
            }

            if (ConflictRpsNetHub.TryGetMove(session.Id, round, slot) == null)
                return false;
        }

        return true;
    }

    private static RpsPickerView ShowOverlay(ConflictRpsSession session, bool allowReset)
    {
        CharacterModel? character = null;
        foreach (var participant in session.Participants)
        {
            if (LocalContext.IsMe(participant))
            {
                character = participant.Character;
                break;
            }
        }

        var overlay = new RpsPickerView(character, allowReset);
        Control? parent = ResolveParent(session.Kind);
        if (parent != null)
        {
            overlay.AttachTo(parent);
            // Hands underneath the panel while choosing, exactly like the relic fight's pointing hands.
            ConflictGestureStage.Begin(session, session.Survivors);
        }
        return overlay;
    }
}
