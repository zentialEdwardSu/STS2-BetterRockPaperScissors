using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace STS2BetterRockPaperScissors.Fight;

/// <summary>
/// Drives the stock relic-fight hand animation a single round at a time, so each RPS round is
/// animated the instant its moves are resolved (instead of replaying the whole fight at the end).
///
/// Wraps an NTreasureRoomRelicCollection instance: reflects out its private _hands / _fightBackstop /
/// _holdersInUse, then calls the game's PUBLIC animation primitives (NHandImage.DoFightMove /
/// DoLoseShake / GrabRelic, NHandImageCollection.BeforeFightStarted) to reproduce — round by round —
/// what NHandImageCollection.DoFight + the fight branch of AnimateRelicAwards do in one batch.
///
/// If the collection fields are unavailable, the caller uses the stock end-of-fight replay.
/// </summary>
public sealed class FightAnimator
{
    private static readonly FieldInfo? HandsField = AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_hands");
    private static readonly FieldInfo? BackstopField = AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_fightBackstop");
    private static readonly FieldInfo? HoldersField = AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");
    private static readonly FieldInfo? HandListField = AccessTools.Field(typeof(NHandImageCollection), "_hands");

    private readonly NHandImageCollection? _hands;
    private readonly Control? _backstop;
    private readonly IReadOnlyList<NTreasureRoomRelicHolder>? _holders;
    private readonly IReadOnlyList<NHandImage>? _handList;
    private readonly CancellationToken _token;
    private readonly bool _reparentHolder;
    private readonly HashSet<Tween> _activeTweens = new();
    private FightVisuals? _activeFight;

    private sealed record FightVisuals(
        NTreasureRoomRelicHolder Holder, Node Parent, int SiblingIndex, Vector2 Position, float Rotation, Vector2 Scale,
        int ZIndex, bool HolderVisible, Color BackstopColor, bool BackstopVisible, List<Player> Fighters);

    public FightAnimator(NTreasureRoomRelicCollection collection, bool reparentHolder, CancellationToken token)
    {
        _token = token;
        _reparentHolder = reparentHolder;
        _hands = HandsField?.GetValue(collection) as NHandImageCollection;
        _backstop = BackstopField?.GetValue(collection) as Control;
        _holders = HoldersField?.GetValue(collection) as IReadOnlyList<NTreasureRoomRelicHolder>;
        if (_hands != null)
            _handList = HandListField?.GetValue(_hands) as IReadOnlyList<NHandImage>;
    }

    /// <summary>
    /// Hides every hand that isn't a fighter in the current fight, so the other fights' frozen hands
    /// don't linger near screen center during this fight's (lengthy, interactive) picking phase.
    /// Uses Node.Visible directly: stock UpdateHandVisibility only toggles the internal IsShown flag +
    /// tween target and never touches Visible, so this stays hidden even if it re-runs mid-pick.
    /// </summary>
    private void ShowOnlyFighters(List<Player> fighters)
    {
        foreach (NHandImage hand in _handList!)
            hand.Visible = fighters.Contains(hand.Player);
    }

    /// <summary>Re-show all hands (undoes <see cref="ShowOnlyFighters"/>) for the stock award wrap-up.</summary>
    private void RestoreAllHands()
    {
        if (_handList == null)
            return;
        foreach (NHandImage hand in _handList)
        {
            if (GodotObject.IsInstanceValid(hand))
                hand.Visible = true;
        }
    }

    /// <summary>
    /// Freeze every hand near the screen edge so the local player's hand stops tracking the mouse
    /// before fights animate. Mirrors the BeforeRelicsAwarded call at the top of AnimateRelicAwards
    /// (NTreasureRoomRelicCollection.cs:281). Idempotent — stock will call it again, harmlessly.
    /// </summary>
    public void PreFreezeHands()
    {
        _hands?.BeforeRelicsAwarded();
    }

    /// <summary>
    /// Stock per-round duration curve from NHandImageCollection.DoFight: rounds get quicker as the
    /// fight goes on. roundIndex is 0-based.
    /// </summary>
    private static float RoundDuration(int roundIndex) => 1.5f * (float)(1.5 / (roundIndex + 1.5));

    /// <summary>
    /// Fight intro: pull the relic holder to the backstop center, fade the backstop in and flag the
    /// fighters as in-fight. Returns the holder when the per-round animation can run.
    /// </summary>
    public async Task<NTreasureRoomRelicHolder?> BeginFight(int slot, RelicModel relic, List<Player> fighters)
    {
        if (_hands == null || _handList == null || _backstop == null || _holders == null)
            return null;

        if (slot >= _holders.Count || !ReferenceEquals(_holders[slot].Relic.Model, relic))
            return null;

        NTreasureRoomRelicHolder holder = _holders[slot];
        _activeFight = new FightVisuals(holder, holder.GetParent(), holder.GetIndex(), holder.Position, holder.Rotation, holder.Scale,
            holder.ZIndex, holder.Visible, _backstop.Modulate, _backstop.Visible, fighters);
        if (_reparentHolder)
        {
            Node fightLayer = _backstop.GetParent();
            if (holder.GetParent() != fightLayer)
                holder.Reparent(fightLayer);
            fightLayer.MoveChildSafely(holder, _backstop.GetIndex() + 1);
        }
        holder.ZIndex = 1;
        _backstop.Visible = true;

        Vector2 target = (_backstop.Size - holder.Size) * 0.5f;
        Tween tween = holder.CreateTween();
        _activeTweens.Add(tween);
        tween.TweenProperty(holder, "global_position", target, 0.25)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        tween.TweenProperty(_backstop, "modulate:a", 1f, 0.25);

        _hands.BeforeFightStarted(fighters);
        ShowOnlyFighters(fighters);

        await WaitForTween(tween, holder);
        await Cmd.Wait(1f, _token);
        _token.ThrowIfCancellationRequested();
        return holder;
    }

    /// <summary>
    /// Animate one round: all <paramref name="survivors"/> throw their move together; everyone in
    /// <paramref name="eliminated"/> then does the lose-shake. <paramref name="roundIndex"/> drives
    /// the stock speed-up curve.
    /// </summary>
    public async Task AnimateRound(
        int roundIndex,
        List<Player> survivors,
        IReadOnlyDictionary<Player, RelicPickingFightMove> moves,
        List<Player> eliminated)
    {
        float duration = RoundDuration(roundIndex);

        var throwTweens = new List<(Tween Tween, NHandImage Hand)>();
        foreach (Player p in survivors)
        {
            if (!moves.TryGetValue(p, out RelicPickingFightMove move))
                continue;
            NHandImage? hand = _hands!.GetHand(p.NetId);
            if (hand == null)
                continue;
            Tween tween = hand.DoFightMove(move, duration);
            _activeTweens.Add(tween);
            throwTweens.Add((tween, hand));
        }
        if (throwTweens.Count > 0)
            await Task.WhenAll(throwTweens.Select(t => WaitForTween(t.Tween, t.Hand)));

        var shakeTasks = new List<Task>();
        foreach (Player p in eliminated)
        {
            NHandImage? hand = _hands!.GetHand(p.NetId);
            if (hand != null)
                shakeTasks.Add(hand.DoLoseShake(Mathf.Max(duration, 0.5f)));
        }

        if (shakeTasks.Count > 0)
            await Task.WhenAll(shakeTasks).WaitAsync(_token);
        else
            await Cmd.Wait(duration, _token);
    }

    /// <summary>
    /// Fight outro: the winner grabs the relic, the backstop fades out, fighters leave fight state.
    /// Mirrors the tail of DoFight + the backstop fade in AnimateRelicAwards.
    /// </summary>
    public async Task GrabRelicForWinner(NTreasureRoomRelicHolder holder, Player winner)
    {
        await Cmd.Wait(0.5f, _token);
        _token.ThrowIfCancellationRequested();
        NHandImage? winnerHand = _hands!.GetHand(winner.NetId);
        if (winnerHand == null)
            throw new InvalidOperationException("The winner's hand is missing.");

        await winnerHand.GrabRelic(holder).WaitAsync(_token);
        _token.ThrowIfCancellationRequested();
        if (holder.GetParent() != winnerHand)
            throw new InvalidOperationException("The winner's hand did not grab the relic.");

        Tween tween = holder.CreateTween();
        _activeTweens.Add(tween);
        tween.TweenProperty(_backstop!, "modulate:a", 0f, 0.25);
        await WaitForTween(tween, holder);
    }

    public void EndFight(bool grabbed)
    {
        if (_activeFight is not { } fight)
            return;
        _activeFight = null;

        try
        {
            RestoreFightVisuals(fight, grabbed);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[RPS] Could not restore the fight visuals: {e}");
        }
    }

    private void RestoreFightVisuals(FightVisuals fight, bool grabbed)
    {
        foreach (Tween tween in _activeTweens.ToArray())
        {
            if (GodotObject.IsInstanceValid(tween) && tween.IsValid())
                tween.Kill();
        }
        _activeTweens.Clear();
        if (_token.IsCancellationRequested)
            return;

        if (GodotObject.IsInstanceValid(_backstop))
        {
            _backstop!.Modulate = fight.BackstopColor;
            _backstop.Visible = fight.BackstopVisible;
        }
        if (GodotObject.IsInstanceValid(fight.Holder))
        {
            fight.Holder.ZIndex = fight.ZIndex;
            fight.Holder.Visible = fight.HolderVisible;
            if (!grabbed && GodotObject.IsInstanceValid(fight.Parent))
            {
                if (fight.Holder.GetParent() != fight.Parent)
                    fight.Holder.Reparent(fight.Parent);
                fight.Parent.MoveChild(fight.Holder, fight.SiblingIndex);
                fight.Holder.Position = fight.Position;
                fight.Holder.Rotation = fight.Rotation;
                fight.Holder.Scale = fight.Scale;
            }
        }
        foreach (Player player in fight.Fighters)
        {
            NHandImage? hand = _hands!.GetHand(player.NetId);
            if (GodotObject.IsInstanceValid(hand))
                hand!.SetIsInFight(false);
        }
        RestoreAllHands();
    }

    /// <summary>
    /// Keep the raised fight visuals (backstop + the relic holder pulled to center, both at raised
    /// z-index) hidden while the game is paused, so they don't draw over the pause/ESC menu during the
    /// mod's long interactive pick window. Called from the per-frame countdown loops; no-op outside a
    /// fight. Restores visibility on resume.
    /// </summary>
    public void SyncPauseVisibility()
    {
        if (_activeFight is not { } fight)
            return;
        bool paused = GamePauseState.IsMenuOpen;
        _backstop!.Visible = !paused;
        if (GodotObject.IsInstanceValid(fight.Holder))
            fight.Holder.Visible = !paused;
    }

    /// <summary>
    /// Grant a fought-over relic to its winner now that its fight is animated, mirroring the grant
    /// loop in AnimateRelicAwards (NTreasureRoomRelicCollection.cs:330-349) for this single relic.
    /// </summary>
    public bool GrantRelic(RelicModel relic, Player winner, IRunState runState, NTreasureRoomRelicHolder holder)
    {
        RelicModel mutable = relic.ToMutable();
        TaskHelperRunSafely(RelicCmd.Obtain(mutable, winner));
        // Obtain adds the relic before its first await on both branches. If a later effect fails,
        // handing this entry back to the game would grant the same relic again.
        if (!winner.Relics.Contains(mutable))
            return false;

        try
        {
            foreach (Player player in runState.Players)
            {
                if (player != winner)
                    player.RelicGrabBag.MoveToFallback(relic);
            }
            if (LocalContext.IsMe(winner))
                NRun.Instance?.GlobalUi.RelicInventory.AnimateRelic(mutable, holder.GlobalPosition, holder.Scale);
            holder.AnimateAwayVotes();
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[RPS] Relic was granted but award cleanup failed: {e}");
        }
        return true;
    }

    private async Task WaitForTween(Tween tween, Node owner)
    {
        bool finished = false;
        void OnFinished() => finished = true;
        tween.Finished += OnFinished;
        try
        {
            // Godot does not emit Finished when a tween is killed. Poll validity once per frame
            // and use the shared node helper to end the wait if the owner leaves the tree.
            while (!finished && GodotObject.IsInstanceValid(tween) && tween.IsValid())
                await owner.AwaitProcessFrame(_token);

            _token.ThrowIfCancellationRequested();
            if (!finished)
                throw new InvalidOperationException("A relic fight tween stopped before it finished.");
            _activeTweens.Remove(tween);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
        }
    }

    private static void TaskHelperRunSafely(Task task)
    {
        _ = task.ContinueWith(
            t => Entry.Logger.Error($"[RPS] relic grant error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
