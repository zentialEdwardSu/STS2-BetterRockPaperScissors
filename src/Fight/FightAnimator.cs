using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
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
/// If any reflection handle is missing we degrade gracefully: the animation calls become no-ops and
/// the caller still resolves winners correctly; the stock end-of-fight replay can act as the visual
/// fallback.
/// </summary>
public sealed class FightAnimator
{
    private static readonly Type CollectionType = AccessTools.TypeByName(
        "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection");
    private static readonly FieldInfo HandsField =
        CollectionType != null ? AccessTools.Field(CollectionType, "_hands") : null;
    private static readonly FieldInfo BackstopField =
        CollectionType != null ? AccessTools.Field(CollectionType, "_fightBackstop") : null;
    private static readonly FieldInfo HoldersField =
        CollectionType != null ? AccessTools.Field(CollectionType, "_holdersInUse") : null;

    private readonly object _hands;            // NHandImageCollection
    private readonly Control _backstop;        // Control ("FightBackstop")
    private readonly IList _holders;            // List<NTreasureRoomRelicHolder>
    private readonly IList _handList;           // NHandImageCollection._hands (List<NHandImage>)
    private readonly CancellationToken _token;

    private Control _activeHolder;      // the relic holder pulled to center for the current fight
    private bool _fightVisualsActive;   // true between BeginFight and GrabRelicForWinner

    private readonly MethodInfo _getHand;       // NHandImageCollection.GetHand(ulong) -> NHandImage
    private readonly MethodInfo _beforeFight;   // NHandImageCollection.BeforeFightStarted(List<Player>)
    private readonly MethodInfo _beforeAwarded; // NHandImageCollection.BeforeRelicsAwarded()
    private readonly MethodInfo _doFightMove;   // NHandImage.DoFightMove(move, float) -> Tween
    private readonly MethodInfo _doLoseShake;   // NHandImage.DoLoseShake(float) -> Task
    private readonly MethodInfo _grabRelic;     // NHandImage.GrabRelic(holder) -> Task
    private readonly MethodInfo _setIsInFight;  // NHandImage.SetIsInFight(bool)

    /// <summary>True when every handle resolved and per-round animation can actually run.</summary>
    public bool Available { get; }

    public FightAnimator(object collectionInstance, CancellationToken token)
    {
        _token = token;
        _hands = HandsField?.GetValue(collectionInstance);
        _backstop = BackstopField?.GetValue(collectionInstance) as Control;
        _holders = HoldersField?.GetValue(collectionInstance) as IList;

        if (_hands != null)
        {
            Type handsType = _hands.GetType();
            _getHand = AccessTools.Method(handsType, "GetHand");
            _beforeFight = AccessTools.Method(handsType, "BeforeFightStarted");
            _beforeAwarded = AccessTools.Method(handsType, "BeforeRelicsAwarded");
            _handList = AccessTools.Field(handsType, "_hands")?.GetValue(_hands) as IList;
        }

        Type handType = AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NHandImage");
        if (handType != null)
        {
            _doFightMove = AccessTools.Method(handType, "DoFightMove");
            _doLoseShake = AccessTools.Method(handType, "DoLoseShake");
            _grabRelic = AccessTools.Method(handType, "GrabRelic");
            _setIsInFight = AccessTools.Method(handType, "SetIsInFight");
        }

        Available = _hands != null && _backstop != null && _holders != null && _handList != null
                    && _getHand != null && _beforeFight != null && _beforeAwarded != null
                    && _doFightMove != null && _doLoseShake != null && _grabRelic != null
                    && _setIsInFight != null;
    }

    /// <summary>
    /// Hides every hand that isn't a fighter in the current fight, so the other fights' frozen hands
    /// don't linger near screen center during this fight's (lengthy, interactive) picking phase.
    /// Uses Node.Visible directly: stock UpdateHandVisibility only toggles the internal IsShown flag +
    /// tween target and never touches Visible, so this stays hidden even if it re-runs mid-pick.
    /// </summary>
    public void ShowOnlyFighters(List<Player> fighters)
    {
        if (!Available)
            return;
        foreach (object hand in _handList)
        {
            if (hand is NHandImage h)
                h.Visible = fighters.Contains(h.Player);
        }
    }

    /// <summary>Re-show all hands (undoes <see cref="ShowOnlyFighters"/>) for the stock award wrap-up.</summary>
    public void RestoreAllHands()
    {
        if (!Available)
            return;
        foreach (object hand in _handList)
        {
            if (hand is NHandImage h)
                h.Visible = true;
        }
    }

    /// <summary>
    /// Freeze every hand near the screen edge so the local player's hand stops tracking the mouse
    /// before fights animate. Mirrors the BeforeRelicsAwarded call at the top of AnimateRelicAwards
    /// (NTreasureRoomRelicCollection.cs:281). Idempotent — stock will call it again, harmlessly.
    /// </summary>
    public void PreFreezeHands()
    {
        if (Available)
            _beforeAwarded.Invoke(_hands, null);
    }

    /// <summary>
    /// Stock per-round duration curve from NHandImageCollection.DoFight: rounds get quicker as the
    /// fight goes on. roundIndex is 0-based.
    /// </summary>
    private static float RoundDuration(int roundIndex) => 1.5f * (float)(1.5 / (roundIndex + 1.5));

    /// <summary>
    /// Fight intro: pull the relic holder to the backstop center, fade the backstop in and flag the
    /// fighters as in-fight. Mirrors the FoughtOver setup in AnimateRelicAwards
    /// (NTreasureRoomRelicCollection.cs:298-307). Returns the holder so the caller can pass it back to
    /// <see cref="GrabRelicForWinner"/>; null if the holder can't be located.
    /// </summary>
    public async Task<object> BeginFight(RelicModel relic, List<Player> fighters)
    {
        if (!Available)
            return null;

        object holder = FindHolder(relic);
        if (holder is not Control holderControl)
            return null;

        holderControl.ZIndex = 1;
        _backstop.Visible = true;
        _activeHolder = holderControl;
        _fightVisualsActive = true;

        Vector2 target = (_backstop.Size - holderControl.Size) * 0.5f;
        Tween tween = holderControl.CreateTween();
        tween.TweenProperty(holderControl, "global_position", target, 0.25)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        tween.TweenProperty(_backstop, "modulate:a", 1f, 0.25);

        _beforeFight.Invoke(_hands, new object[] { fighters });
        ShowOnlyFighters(fighters); // hide the other fights' frozen hands (the stray "third finger")

        await tween.AwaitFinished(_token);
        await Cmd.Wait(1f, _token);
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
        if (!Available)
            return;

        float duration = RoundDuration(roundIndex);

        var throwTweens = new List<Task>();
        foreach (Player p in survivors)
        {
            if (!moves.TryGetValue(p, out RelicPickingFightMove move))
                continue;
            object hand = GetHand(p);
            if (hand == null)
                continue;
            var tween = (Tween)_doFightMove.Invoke(hand, new object[] { move, duration });
            if (tween != null)
                throwTweens.Add(tween.AwaitFinished(_token));
        }
        if (throwTweens.Count > 0)
            await Task.WhenAll(throwTweens);

        var shakeTasks = new List<Task>();
        foreach (Player p in eliminated)
        {
            object hand = GetHand(p);
            if (hand == null)
                continue;
            shakeTasks.Add((Task)_doLoseShake.Invoke(hand, new object[] { Mathf.Max(duration, 0.5f) }));
        }

        if (shakeTasks.Count > 0)
            await Task.WhenAll(shakeTasks);
        else
            await Cmd.Wait(duration, _token);
    }

    /// <summary>
    /// Fight outro: the winner grabs the relic, the backstop fades out, fighters leave fight state.
    /// Mirrors the tail of DoFight + the backstop fade in AnimateRelicAwards.
    /// </summary>
    public async Task GrabRelicForWinner(object holder, Player winner, List<Player> fighters)
    {
        if (!Available || holder is not Control holderControl)
            return;

        await Cmd.Wait(0.5f, _token);
        object winnerHand = GetHand(winner);
        if (winnerHand != null)
        {
            var grab = (Task)_grabRelic.Invoke(winnerHand, new object[] { holder });
            if (grab != null)
                await grab;
        }

        Tween tween = holderControl.CreateTween();
        tween.TweenProperty(_backstop, "modulate:a", 0f, 0.25);
        await tween.AwaitFinished(_token);
        _backstop.Visible = false;
        holderControl.ZIndex = 0;
        _fightVisualsActive = false;
        _activeHolder = null;

        foreach (Player p in fighters)
        {
            object hand = GetHand(p);
            if (hand != null)
                _setIsInFight.Invoke(hand, new object[] { false });
        }
    }

    /// <summary>
    /// Keep the raised fight visuals (backstop + the relic holder pulled to center, both at raised
    /// z-index) hidden while the game is paused, so they don't draw over the pause/ESC menu during the
    /// mod's long interactive pick window. Called from the per-frame countdown loops; no-op outside a
    /// fight. Restores visibility on resume.
    /// </summary>
    public void SyncPauseVisibility()
    {
        if (!Available || !_fightVisualsActive)
            return;
        bool paused = GamePauseState.IsMenuOpen;
        _backstop.Visible = !paused;
        if (GodotObject.IsInstanceValid(_activeHolder))
            _activeHolder.Visible = !paused;
    }

    /// <summary>
    /// Grant a fought-over relic to its winner now that its fight is animated, mirroring the grant
    /// loop in AnimateRelicAwards (NTreasureRoomRelicCollection.cs:330-349) for this single relic.
    /// </summary>
    public void GrantRelic(RelicModel relic, Player winner, IRunState runState, object holder)
    {
        RelicModel mutable = relic.ToMutable();
        var holderControl = holder as NTreasureRoomRelicHolder;
        holderControl?.Disable();

        TaskHelperRunSafely(RelicCmd.Obtain(mutable, winner));

        if (LocalContext.IsMe(winner) && holderControl != null)
        {
            NRun.Instance?.GlobalUi.RelicInventory.AnimateRelic(
                mutable, holderControl.GlobalPosition, holderControl.Scale);
        }

        foreach (var p in runState.Players)
        {
            if (p != winner)
                p.RelicGrabBag.MoveToFallback(relic);
        }
    }

    private object FindHolder(RelicModel relic)
    {
        foreach (var holder in _holders)
        {
            if (holder is NTreasureRoomRelicHolder h && ReferenceEquals(h.Relic.Model, relic))
                return holder;
        }
        return null;
    }

    private object GetHand(Player player) => _getHand.Invoke(_hands, new object[] { player.NetId });

    private static void TaskHelperRunSafely(Task task)
    {
        _ = task.ContinueWith(
            t => Entry.Logger.Error($"[RPS] relic grant error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
