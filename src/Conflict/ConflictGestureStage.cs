using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using STS2BetterRockPaperScissors.Fight;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The hands that accompany a conflict fight, in two phases — the same language the relic fight uses:
///
/// <list type="number">
/// <item><b>Picking.</b> While the gesture picker is on screen every surviving player's hand is shown as the
/// <em>pointing</em> hand (<c>CharacterModel.ArmPointingTexture</c>), fingers aimed at the middle of the screen.
/// The stage sits one z-index below the picker, so the choice panel is drawn on top of the hands.</item>
/// <item><b>Throwing.</b> When the round resolves each hand shakes three times around its arm root (the pivot) and
/// then flips to the gesture that player played; after a short hold the eliminated hands shake once more and fade
/// away while the winners grow.</item>
/// </list>
///
/// Positioning: imagine a fixed-radius circle around the screen centre — every fingertip comes to rest exactly on
/// it. The wrist is the pivot and the art extends "up" by its own height, so parking the wrist at
/// <c>tipRadius + height</c> along the direction puts the fingertip on the circle no matter how long a given
/// character's arm art is (and the same holds again after the art is swapped for the throw, because the wrist is
/// repositioned for the new height).
///
/// Why the stage is built from stock Godot nodes instead of a scripted scene: a mod-defined class deriving from a
/// Godot node fails to JIT through the source-generated bridge under MonoMod (see
/// <see cref="Overlays.RpsPickerView"/>).
/// </summary>
internal static class ConflictGestureStage
{
    /// <summary>Radius of the invisible circle every fingertip lands on, as a fraction of the short screen side.</summary>
    private const float TipRadiusScale = 0.18f;

    /// <summary>Hand width, as a fraction of the short screen side, clamped to sane pixel sizes.</summary>
    private const float HandWidthScale = 0.38f;

    private const float MinHandWidth = 180f;
    private const float MaxHandWidth = 400f;

    private const double EnterSeconds = 0.42;
    private const double EnterStaggerSeconds = 0.05;
    /// <summary>
    /// Duration of each of the three wind-up swings. They accelerate on purpose (0.5s, 0.4s, 0.2s): the first is a
    /// slow wind-up, the last snaps, which reads like actually throwing rather than waving.
    /// </summary>
    private static readonly double[] ThrowSwingSeconds = { 0.5, 0.4, 0.2 };

    private const double HoldSeconds = 0.30;
    private const double LoseSeconds = 0.50;
    private const double TailSeconds = 0.16;

    private static readonly double ShakeSeconds = ThrowSwingSeconds.Sum();

    private static Control? _overlay;
    private static List<Hand> _hands = new();
    private static byte _sessionId;

    /// <summary>How long <see cref="ThrowAsync"/> takes, so the host can pace the next round.</summary>
    internal static double ThrowDuration => ShakeSeconds + HoldSeconds + LoseSeconds + TailSeconds;

    /// <summary>
    /// Shows the pointing hands for <paramref name="survivors"/> under the picker. Idempotent per session: calling
    /// it again while the same session's stage is up does nothing.
    /// </summary>
    internal static void Begin(ConflictRpsSession session, IReadOnlyCollection<int> survivors)
    {
        try
        {
            if (_overlay != null && GodotObject.IsInstanceValid(_overlay) && _sessionId == session.Id)
                return;

            Close();
            _sessionId = session.Id;
            Build(session, survivors);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[conflict] gesture stage could not be built: {e.Message}");
            Close();
        }
    }

    /// <summary>
    /// Plays the throw: shake three times, flip to the played gesture, then dismiss the losers. Reuses the pointing
    /// hands if <see cref="Begin"/> already put them up (the normal path), otherwise builds them on the fly.
    /// Never throws and always takes <see cref="ThrowDuration"/>.
    /// </summary>
    internal static async Task ThrowAsync(
        ConflictRpsSession session,
        IReadOnlyList<byte> moves,
        IReadOnlyCollection<int> survivorsBefore,
        ISet<int> eliminated,
        CancellationToken token)
    {
        if (token.IsCancellationRequested || survivorsBefore.Count == 0)
            return;

        try
        {
            if (_overlay == null || !GodotObject.IsInstanceValid(_overlay)
                || _sessionId != session.Id || _hands.Count != survivorsBefore.Count)
            {
                Close();
                _sessionId = session.Id;
                Build(session, survivorsBefore);
            }

            List<Hand> hands = _hands;
            foreach (Hand hand in hands)
                hand.StartThrow(moves[hand.Slot]);

            double outcomeDelay = ShakeSeconds + HoldSeconds;
            foreach (Hand hand in hands)
            {
                hand.Lost = eliminated.Contains(hand.Slot);
                if (hand.Lost)
                    hand.StartLose(outcomeDelay);
                else
                    hand.StartWin(outcomeDelay);
            }

            if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            {
                Tween fade = _overlay.CreateTween();
                fade.TweenProperty(_overlay, "modulate:a", 0f, TailSeconds)
                    .SetDelay(outcomeDelay + LoseSeconds);
            }

            await ConflictTiming.WaitSecondsAsync(ThrowDuration, token);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[conflict] gesture throw animation failed: {e.Message}");
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Removes the stage immediately (aborted session, screen teardown).</summary>
    internal static void Close()
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();

        _overlay = null;
        _hands = new List<Hand>();
    }

    private static void Build(ConflictRpsSession session, IReadOnlyCollection<int> survivors)
    {
        Control? parent = ConflictPickerHost.ResolveParent(session.Kind);
        if (parent == null || !GodotObject.IsInstanceValid(parent))
            return;

        var overlay = new Control { Name = "ConflictGestureStage", MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlay.ZIndex = 4090; // the gesture picker uses 4096, so the choice panel stays on top of the hands
        parent.AddChild(overlay);

        Vector2 viewport = overlay.GetViewportRect().Size;
        Vector2 center = viewport * 0.5f;
        float shortSide = Mathf.Min(viewport.X, viewport.Y);
        float handWidth = Mathf.Clamp(shortSide * HandWidthScale, MinHandWidth, MaxHandWidth);
        float tipRadius = shortSide * TipRadiusScale;

        // Slot order keeps the fan identical on every peer (a HashSet's order is not stable).
        List<int> slots = survivors
            .Where(slot => slot >= 0 && slot < session.Participants.Count)
            .OrderBy(slot => slot)
            .ToList();

        var hands = new List<Hand>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            int slot = slots[i];
            Player player = session.Participants[slot];

            // 360 / N spread, starting at the bottom like the relic fight's slot 0 and going clockwise.
            float phi = Mathf.Pi + Mathf.Tau * i / slots.Count;
            var direction = new Vector2(Mathf.Sin(phi), -Mathf.Cos(phi));

            Hand hand = Hand.Create(slot, player.Character, direction, center, tipRadius, handWidth);
            overlay.AddChild(hand.Node);
            hand.PlayEnter(i, shortSide);

            hands.Add(hand);
        }

        _overlay = overlay;
        _hands = hands;
    }

    /// <summary>One player's hand: the node plus what is needed to reposition/reskin it around its arm root.</summary>
    private sealed class Hand
    {
        private readonly TextureRect? _art;
        private readonly Label? _label;
        private readonly CharacterModel? _character;
        private readonly Vector2 _center;
        private readonly Vector2 _direction;
        private readonly float _tipRadius;
        private readonly float _handWidth;
        private readonly float _baseRotation;

        private Hand(
            int slot,
            Control node,
            TextureRect? art,
            Label? label,
            CharacterModel? character,
            Vector2 direction,
            Vector2 center,
            float tipRadius,
            float handWidth)
        {
            Slot = slot;
            Node = node;
            _art = art;
            _label = label;
            _character = character;
            _direction = direction;
            _center = center;
            _tipRadius = tipRadius;
            _handWidth = handWidth;
            _baseRotation = Mathf.Atan2(-direction.X, direction.Y);
        }

        internal int Slot { get; }

        internal Control Node { get; }

        internal bool Lost { get; set; }

        internal static Hand Create(
            int slot,
            CharacterModel? character,
            Vector2 direction,
            Vector2 center,
            float tipRadius,
            float handWidth)
        {
            Texture2D? pointing = character?.ArmPointingTexture;
            Control node;
            TextureRect? art = null;
            Label? label = null;

            if (pointing != null && pointing.GetWidth() > 0)
            {
                art = new TextureRect
                {
                    Texture = pointing,
                    // ExpandMode must be set before Size: with the default (KeepSize) the texture's own pixel size
                    // becomes the control's minimum size, so assigning Size gets clamped up to the raw art size.
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                art.Size = new Vector2(handWidth, handWidth * pointing.GetHeight() / pointing.GetWidth());
                node = art;
            }
            else
            {
                label = new Label
                {
                    Text = "?",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                label.AddThemeFontSizeOverride("font_size", 88);
                label.Size = new Vector2(handWidth, handWidth);
                node = label;
            }

            var hand = new Hand(slot, node, art, label, character, direction, center, tipRadius, handWidth);
            hand.SeatOnFingertipCircle();
            node.Rotation = hand._baseRotation;
            return hand;
        }

        /// <summary>
        /// Places the wrist so that the fingertip sits exactly on the fixed circle around the screen centre. Called
        /// again after the art is swapped, because the gesture art can be a different height than the pointing art.
        /// </summary>
        private void SeatOnFingertipCircle()
        {
            float height = Node.Size.Y;
            var pivot = new Vector2(Node.Size.X * 0.5f, height);
            Node.PivotOffset = pivot;
            Vector2 wrist = _center + _direction * (_tipRadius + height);
            Node.Position = wrist - pivot;
        }

        /// <summary>
        /// Entrance: the arm slides in from outside the screen along its own direction and stops on the fingertip
        /// circle — it must not fade into place, otherwise it reads as a floating picture rather than an arm
        /// reaching in. The alpha just follows the movement so the arm does not pop at the border.
        /// </summary>
        internal void PlayEnter(int index, float shortSide)
        {
            Vector2 rest = Node.Position;
            // Start far enough out that the whole art is hidden: the hand's own length plus a bit of the screen.
            Node.Position = rest + _direction * (Node.Size.Y + shortSide * 0.45f);
            Node.Modulate = new Color(1f, 1f, 1f, 0f);

            double delay = index * EnterStaggerSeconds;
            Tween enter = Node.CreateTween().SetParallel();
            enter.TweenProperty(Node, "position", rest, EnterSeconds)
                .SetDelay(delay)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            enter.TweenProperty(Node, "modulate:a", 1f, EnterSeconds * 0.5)
                .SetDelay(delay);
        }

        /// <summary>
        /// Shakes the hand three times around its arm root, then swaps in the played gesture. The swings accelerate
        /// (<see cref="ThrowSwingSeconds"/>): a slow wind-up followed by a snap, so it reads as a throw.
        /// </summary>
        internal void StartThrow(byte move)
        {
            Tween tween = Node.CreateTween();
            foreach (double swing in ThrowSwingSeconds)
            {
                tween.TweenProperty(Node, "rotation", _baseRotation - 0.16f, swing * 0.5)
                    .SetTrans(Tween.TransitionType.Sine)
                    .SetEase(Tween.EaseType.InOut);
                tween.TweenProperty(Node, "rotation", _baseRotation, swing * 0.5)
                    .SetTrans(Tween.TransitionType.Sine)
                    .SetEase(Tween.EaseType.InOut);
            }

            tween.TweenCallback(Callable.From((Action)(() => ShowMove(move))));
        }

        private void ShowMove(byte move)
        {
            if (_art != null && _character != null)
            {
                Texture2D? texture = GestureTexture(_character, move);
                if (texture == null || texture.GetWidth() <= 0)
                    return;

                _art.Texture = texture;
                _art.Size = new Vector2(_handWidth, _handWidth * texture.GetHeight() / texture.GetWidth());
                SeatOnFingertipCircle();
                return;
            }

            if (_label != null)
                _label.Text = Glyph(move);
        }

        /// <summary>Loser: one more shake, then the hand disappears.</summary>
        internal void StartLose(double delay)
        {
            const double step = 0.09;
            Tween lose = Node.CreateTween();
            lose.TweenProperty(Node, "rotation", _baseRotation - 0.07f, step)
                .SetDelay(delay)
                .SetTrans(Tween.TransitionType.Sine);
            lose.TweenProperty(Node, "rotation", _baseRotation + 0.07f, step)
                .SetTrans(Tween.TransitionType.Sine);
            lose.TweenProperty(Node, "rotation", _baseRotation - 0.05f, step)
                .SetTrans(Tween.TransitionType.Sine);
            lose.TweenProperty(Node, "rotation", _baseRotation, step)
                .SetTrans(Tween.TransitionType.Sine);
            lose.TweenProperty(Node, "modulate:a", 0f, Math.Max(0.14, LoseSeconds - step * 4))
                .SetEase(Tween.EaseType.In);
        }

        /// <summary>Winner: a short grow-and-settle pulse.</summary>
        internal void StartWin(double delay)
        {
            Tween pulse = Node.CreateTween();
            pulse.TweenProperty(Node, "scale", Vector2.One * 1.12f, 0.12)
                .SetDelay(delay)
                .SetTrans(Tween.TransitionType.Back)
                .SetEase(Tween.EaseType.Out);
            pulse.TweenProperty(Node, "scale", Vector2.One, 0.14);
        }
    }

    private static Texture2D? GestureTexture(CharacterModel character, byte move)
    {
        return move switch
        {
            (byte)RelicPickingFightMove.Rock => character.ArmRockTexture,
            (byte)RelicPickingFightMove.Paper => character.ArmPaperTexture,
            (byte)RelicPickingFightMove.Scissors => character.ArmScissorsTexture,
            _ => null,
        };
    }

    private static string Glyph(byte move) => move switch
    {
        (byte)RelicPickingFightMove.Rock => "✊",
        (byte)RelicPickingFightMove.Paper => "✋",
        (byte)RelicPickingFightMove.Scissors => "✌",
        _ => "?",
    };
}
