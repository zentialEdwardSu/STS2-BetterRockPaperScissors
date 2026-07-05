using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Net;
using STS2BetterRockPaperScissors.Overlays;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Fight;

/// <summary>
/// Replaces a relic fight's RNG-generated moves with player-chosen gestures, one elimination round
/// at a time, synchronized across peers.
///
/// Authority model:
/// - Singleplayer / fake-multiplayer: resolve entirely locally. The human fighter uses the picker
///   (RNG on timeout); bot fighters use RNG immediately. No messages.
/// - Host: collect each surviving human fighter's move (own from picker, remote from RpsNetHub),
///   run the authoritative countdown, fill missing/timed-out fighters via RNG, then broadcast the
///   resolved round. Apply it locally.
/// - Client: show the picker, send the local move, then await + use the host's broadcast verbatim.
///
/// In every mode each peer applies the SAME resolved moves through the stock elimination rule, so
/// the winner is identical everywhere — which keeps RelicCmd.Obtain (run per-peer) in sync.
/// </summary>
public static class InteractiveFightResolver
{
    /// <summary>
    /// Resolves one FoughtOver result in place: rewrites result.fight and result.player from chosen
    /// moves. <paramref name="relicKey"/> is the relic's index in CurrentRelics (the network key).
    /// Each round is animated the instant it resolves, via <paramref name="animator"/> (when
    /// available); the relic is granted to the winner at the end of the fight.
    /// </summary>
    public static async Task ResolveFight(
        RelicPickingResult result,
        byte relicKey,
        Control overlayParent,
        FightAnimator animator,
        CancellationToken token)
    {
        // The set of fighters, in a deterministic order shared by all peers (slot index order).
        IRunState runState = result.player?.RunState
                            ?? result.fight?.playersInvolved?.FirstOrDefault()?.RunState;
        List<Player> fighters = OrderFighters(result.fight.playersInvolved, runState);

        var fight = new RelicPickingFight();
        fight.playersInvolved.AddRange(fighters);

        // Survivors tracked by index into `fighters`.
        var survivors = new HashSet<int>(Enumerable.Range(0, fighters.Count));
        NetGameType netType = RunManager.Instance.NetService.Type;

        Entry.Logger.Info(
            $"[RPS] start fight for relic {relicKey} ({result.relic}): {fighters.Count} fighters [{string.Join(", ", fighters.Select(f => f.NetId.ToString()))}], net type {netType}, countdown {ModConfig.CountdownSeconds}s");

        // Fight intro: pull the relic to the backstop and flag fighters as in-fight. Holder is reused
        // for the winner's grab at the end. Null when the animator isn't available (we still resolve
        // the winner correctly; the stock replay can act as the visual fallback).
        object holder = animator != null && animator.Available
            ? await animator.BeginFight(result.relic, fighters)
            : null;

        byte round = 0;
        int animatedRound = 0;
        // Every round is an interactive pick. A round that ties (RPS can't eliminate on identical
        // picks or an all-distinct 3-way) simply re-prompts the survivors — no RNG. Only RNG-fill a
        // move when a specific fighter TIMES OUT (handled per-fighter inside ResolveRound).
        // EVERY round is recorded into fight.rounds — including draws — so state stays consistent;
        // the per-round animation below shows each throw (and any eliminations) before the next pick.
        // The network round counter advances every round so message keys stay unique across peers.
        // Animation runs in the loop body AFTER ResolveRound returns and BEFORE the next iteration:
        // the host broadcasts the next round-began only when it loops back into ResolveRound (after
        // animating), and the client opens its next picker only once that round-began arrives — so
        // peers animate in lockstep and no picker opens mid-animation.
        while (survivors.Count > 1)
        {
            RelicPickingFightMove?[] resolved =
                await ResolveRound(relicKey, round, fighters, survivors, netType, overlayParent, animator, token);

            int before = survivors.Count;
            var survivorsBefore = new HashSet<int>(survivors);
            ApplyElimination(resolved, survivors);
            bool decisive = survivors.Count < before;

            // Record the round whether or not it eliminated anyone (keeps fight state complete).
            var fightRound = new RelicPickingFightRound();
            for (int i = 0; i < fighters.Count; i++)
                fightRound.moves.Add(resolved[i]);
            fight.rounds.Add(fightRound);

            // Animate this round immediately: survivors-this-round throw their moves; whoever was
            // eliminated this round does the lose-shake.
            if (holder != null)
            {
                var survivorPlayers = new List<Player>();
                var moves = new Dictionary<Player, RelicPickingFightMove>();
                foreach (int i in survivorsBefore)
                {
                    survivorPlayers.Add(fighters[i]);
                    if (resolved[i].HasValue)
                        moves[fighters[i]] = resolved[i].Value;
                }
                var eliminated = survivorsBefore.Where(i => !survivors.Contains(i))
                    .Select(i => fighters[i]).ToList();
                await animator.AnimateRound(animatedRound, survivorPlayers, moves, eliminated);
                animatedRound++;
            }

            Entry.Logger.Info(
                $"RPS relic {relicKey} round {round}: {DescribeMoves(resolved, fighters)} -> survivors {before}->{survivors.Count}, {(decisive ? "decisive" : "draw (re-prompt)")} (recorded)");
            round++;

            // Safety valve: never loop unbounded if something pathological happens.
            if (round > 64)
                break;
        }

        int winnerIndex = survivors.Count > 0 ? survivors.First() : 0;
        Player winner = fighters[winnerIndex];
        result.fight = fight;
        result.player = winner;
        result.type = RelicPickingResultType.FoughtOver;
        Entry.Logger.Info(
            $"[RPS] fight for relic {relicKey} ({result.relic}) won by {winner.NetId} after {fight.rounds.Count} round(s)");

        // Fight outro + grant: winner grabs the relic, backstop fades out, relic is obtained. When the
        // animator isn't available the patch leaves this fight on the stock award path instead.
        if (holder != null)
        {
            await animator.GrabRelicForWinner(holder, winner, fighters);
            if (runState != null)
                animator.GrantRelic(result.relic, winner, runState, holder);
        }
    }

    /// <summary>Resolves the moves for a single round across all peers; returns moves per fighter index.</summary>
    private static async Task<RelicPickingFightMove?[]> ResolveRound(
        byte relicKey,
        byte round,
        List<Player> fighters,
        HashSet<int> survivors,
        NetGameType netType,
        Control overlayParent,
        FightAnimator animator,
        CancellationToken token)
    {
        var resolved = new RelicPickingFightMove?[fighters.Count];

        // Is the local player a surviving fighter this round?
        int localIndex = -1;
        for (int i = 0; i < fighters.Count; i++)
        {
            if (survivors.Contains(i) && LocalContext.IsMe(fighters[i]))
            {
                localIndex = i;
                break;
            }
        }

        if (netType == NetGameType.Client)
        {
            // Wait for the host to announce the round + its AUTHORITATIVE countdown, then show the
            // picker for exactly that long. The picker also closes the instant the host's resolved
            // round arrives, so the client never lags behind the host's resolution.
            (byte countdown, bool clientAllowReset) = await RpsNetHub.AwaitRoundBegan(relicKey, round);
            Task<List<byte>> resolvedTask = RpsNetHub.AwaitResolvedRound(relicKey, round);

            if (localIndex >= 0)
            {
                // The round-began message took ~one-way latency to arrive, so the host's countdown
                // bar is already that far drained. Start ours pre-drained by the same amount so both
                // bars empty at the same wall-clock instant (purely cosmetic; the host's resolved
                // round is still authoritative and closes the picker either way). PromptLocalMove
                // sends the move itself the instant it's picked, then keeps the picker on screen.
                double latencyOffset = RpsNetHub.OneWayLatencySeconds();
                await PromptLocalMove(overlayParent, LocalCharacter(fighters), relicKey, round, countdown, latencyOffset, clientAllowReset, animator, resolvedTask, token);
            }

            List<byte> authoritative = await resolvedTask;
            ApplyAuthoritative(authoritative, survivors, resolved);
            return resolved;
        }

        // Host or Singleplayer: this peer is authoritative for the round.
        bool realMultiplayer = netType == NetGameType.Host;
        var rng = Rng.Chaotic;

        byte hostCountdown = (byte)ModConfig.CountdownSeconds;
        bool allowReset = ModConfig.AllowReset;

        // Tell clients the round has begun, how long they get, and whether re-choosing is enabled
        // (all host-authoritative).
        if (realMultiplayer)
            RpsNetHub.BroadcastRoundBegan(relicKey, round, hostCountdown, allowReset);

        RpsPickerView overlay = null;
        if (localIndex >= 0)
            overlay = ShowOverlay(overlayParent, LocalCharacter(fighters), allowReset);

        await RunAuthoritativeCountdown(relicKey, round, fighters, survivors, localIndex, realMultiplayer, overlay, hostCountdown, allowReset, animator);

        // Gather final moves for survivors; fill the rest via RNG.
        var authoritativeMoves = new List<byte>(fighters.Count);
        for (int i = 0; i < fighters.Count; i++)
        {
            if (!survivors.Contains(i))
            {
                authoritativeMoves.Add(RpsMove.None);
                continue;
            }

            byte move;
            if (i == localIndex && overlay != null && overlay.HasPick)
            {
                move = (byte)overlay.CurrentPick.Value;
                Entry.Logger.Info($"[RPS] local player {fighters[i].NetId} picked {(RelicPickingFightMove)move} (relic {relicKey} round {round})");
            }
            else if (realMultiplayer && !LocalContext.IsMe(fighters[i]))
            {
                byte? received = RpsNetHub.TryGetReceivedMove(relicKey, round, fighters[i].NetId);
                move = received ?? (byte)rng.NextInt(3);
                if (received == null)
                    Entry.Logger.Info($"[RPS] no move from client {fighters[i].NetId} (relic {relicKey} round {round}) — RNG auto-pick {(RelicPickingFightMove)move}");
            }
            else
            {
                // Local human who timed out, or a bot in singleplayer/fake-multiplayer.
                move = (byte)rng.NextInt(3);
                bool isLocalTimeout = i == localIndex;
                Entry.Logger.Info(
                    $"[RPS] {(isLocalTimeout ? "local player timed out" : "bot")} {fighters[i].NetId} — RNG auto-pick {(RelicPickingFightMove)move} (relic {relicKey} round {round})");
            }

            authoritativeMoves.Add(move);
            resolved[i] = (RelicPickingFightMove)move;
        }

        overlay?.Close();

        if (realMultiplayer)
            RpsNetHub.BroadcastResolvedRound(relicKey, round, authoritativeMoves);

        return resolved;
    }

    /// <summary>
    /// Host/SP: rides the (host-authoritative) countdown, ticking the local overlay's bar each frame.
    /// When re-choosing (<paramref name="allowReset"/>) is off, resolves early the moment every
    /// survivor is ready (the classic behavior). When on, always rides the full countdown so a pick
    /// stays changeable right up until the timer ends.
    /// </summary>
    private static async Task RunAuthoritativeCountdown(
        byte relicKey,
        byte round,
        List<Player> fighters,
        HashSet<int> survivors,
        int localIndex,
        bool realMultiplayer,
        RpsPickerView overlay,
        double countdownSeconds,
        bool allowReset,
        FightAnimator animator)
    {
        double remaining = countdownSeconds;
        SceneTree tree = Engine.GetMainLoop() as SceneTree;
        overlay?.SetRemaining(remaining, countdownSeconds);

        while (remaining > 0.0)
        {
            // Without re-choosing, a completed pick is final — end the round as soon as everyone's in.
            if (!allowReset
                && AllSurvivorsReady(relicKey, round, fighters, survivors, localIndex, realMultiplayer, overlay))
                return;

            if (tree == null)
            {
                await Task.Delay((int)(remaining * 1000));
                return;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            remaining -= tree.Root.GetProcessDeltaTime();
            overlay?.SetRemaining(remaining, countdownSeconds);
            animator?.SyncPauseVisibility(); // hide the raised fight visuals while the ESC menu is up
        }
    }

    /// <summary>
    /// Host/SP: true when every surviving fighter has a move ready (local pick in, remote move received,
    /// with bots never "ready" so the countdown rides out to the RNG fill). Only consulted when
    /// re-choosing is disabled.
    /// </summary>
    private static bool AllSurvivorsReady(
        byte relicKey,
        byte round,
        List<Player> fighters,
        HashSet<int> survivors,
        int localIndex,
        bool realMultiplayer,
        RpsPickerView overlay)
    {
        foreach (int i in survivors)
        {
            if (i == localIndex)
            {
                if (overlay == null || !overlay.HasPick)
                    return false;
            }
            else if (realMultiplayer && !LocalContext.IsMe(fighters[i]))
            {
                if (RpsNetHub.TryGetReceivedMove(relicKey, round, fighters[i].NetId) == null)
                    return false;
            }
            else
            {
                // Bot fighter — never "ready", forces us to ride the countdown then RNG-fill.
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Client: show the picker using the HOST's countdown. Sends the local move to the host the
    /// instant it's chosen, then keeps the chosen hand + countdown bar on screen until the countdown
    /// ends or the host's resolved round arrives (whichever first), so the client stays in lockstep
    /// with the host regardless of local settings.
    /// </summary>
    private static async Task PromptLocalMove(
        Control overlayParent,
        CharacterModel? character,
        byte relicKey,
        byte round,
        double countdownSeconds,
        double initialElapsed,
        bool allowReset,
        FightAnimator animator,
        Task<List<byte>> resolvedTask,
        CancellationToken token)
    {
        RpsPickerView overlay = ShowOverlay(overlayParent, character, allowReset);
        // Pre-drain by the latency offset so the bar is phase-aligned with the host's; total stays the
        // full countdown so the displayed fraction is correct.
        double remaining = countdownSeconds - initialElapsed;
        if (remaining < 0.0)
            remaining = 0.0;
        SceneTree tree = Engine.GetMainLoop() as SceneTree;
        overlay.SetRemaining(remaining, countdownSeconds);

        // Track the last move we sent so a re-choose re-sends (the host overwrites to the latest). A
        // cancel back to blank does NOT retract on the wire — if nothing else is picked before the
        // countdown ends the host RNG-fills, matching the timeout-to-random behavior.
        RelicPickingFightMove? lastSent = null;
        while (remaining > 0.0
               && !resolvedTask.IsCompleted
               && !token.IsCancellationRequested)
        {
            // Send the current pick whenever it changes, keeping the picker visible/ticking so the
            // player can still re-choose or clear it.
            if (overlay.HasPick && overlay.CurrentPick != lastSent)
            {
                RpsNetHub.SendMove(relicKey, round, (byte)overlay.CurrentPick.Value);
                lastSent = overlay.CurrentPick;
            }

            if (tree == null)
            {
                await Task.WhenAny(overlay.Result, resolvedTask, Task.Delay((int)(remaining * 1000)));
                break;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            remaining -= tree.Root.GetProcessDeltaTime();
            overlay.SetRemaining(remaining, countdownSeconds);
            animator?.SyncPauseVisibility(); // hide the raised fight visuals while the ESC menu is up
        }

        // Catch a pick/re-choose that landed on the final frame before the loop ended.
        if (overlay.HasPick && overlay.CurrentPick != lastSent)
            RpsNetHub.SendMove(relicKey, round, (byte)overlay.CurrentPick.Value);

        overlay.Close();
    }

    private static RpsPickerView ShowOverlay(Control overlayParent, CharacterModel? character, bool allowReset)
    {
        // Use the local player's profession hand art on the move buttons.
        var overlay = new RpsPickerView(character, allowReset);
        overlay.AttachTo(overlayParent); // adds, raises to front, shows the cursor
        return overlay;
    }

    /// <summary>The local player's character (for profession hand art), or null if not a fighter.</summary>
    private static CharacterModel? LocalCharacter(List<Player> fighters)
    {
        foreach (Player p in fighters)
        {
            if (LocalContext.IsMe(p))
                return p.Character;
        }
        return null;
    }

    private static void ApplyAuthoritative(List<byte> authoritative, HashSet<int> survivors, RelicPickingFightMove?[] resolved)
    {
        if (authoritative == null)
            return;
        int count = System.Math.Min(authoritative.Count, resolved.Length);
        for (int i = 0; i < count; i++)
        {
            if (survivors.Contains(i))
                resolved[i] = RpsMove.FromByte(authoritative[i]);
        }
    }

    /// <summary>
    /// Stock STS2 elimination rule: a round only resolves when exactly two distinct moves are present
    /// among survivors. Everyone who played the losing move is eliminated. Otherwise it's a tie.
    /// </summary>
    private static void ApplyElimination(RelicPickingFightMove?[] resolved, HashSet<int> survivors)
    {
        var distinct = survivors
            .Select(i => resolved[i])
            .Where(m => m.HasValue)
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        if (distinct.Count != 2)
            return; // tie -> survivors unchanged, play another round

        RelicPickingFightMove losing = GetLosingMove(distinct[0], distinct[1]);
        var eliminated = survivors.Where(i => resolved[i] == losing).ToList();
        foreach (int i in eliminated)
            survivors.Remove(i);
    }

    private static string DescribeMoves(RelicPickingFightMove?[] resolved, List<Player> fighters)
    {
        var parts = new List<string>();
        for (int i = 0; i < resolved.Length; i++)
        {
            if (resolved[i].HasValue)
                parts.Add($"{fighters[i].NetId}:{resolved[i].Value}");
        }
        return string.Join(", ", parts);
    }

    private static RelicPickingFightMove GetLosingMove(RelicPickingFightMove a, RelicPickingFightMove b)
    {
        // Matches RelicPickingResult.GetLosingMove: a loses if (a+1)%3 == b.
        return (RelicPickingFightMove)(((int)a + 1) % 3) == b ? a : b;
    }

    private static List<Player> OrderFighters(List<Player> players, IRunState runState)
    {
        if (runState == null)
            return players.ToList();
        return players.OrderBy(p => runState.GetPlayerSlotIndex(p)).ToList();
    }
}
