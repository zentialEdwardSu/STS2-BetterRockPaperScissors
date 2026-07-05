using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace STS2BetterRockPaperScissors.Overlays;

/// <summary>
/// The gesture-picker overlay, built entirely from STOCK Godot nodes (Control/Button/Label/...).
///
/// Why not a custom Node subclass: a mod-defined Godot script class (deriving from Control with
/// _Ready/_Process overrides) fails to JIT through Godot's source-generated InvokeGodotClassMethod
/// bridge under the Harmony/MonoMod runtime — AddChild then silently produces an empty, broken node.
/// Stock GodotSharp node types are already registered/JITed by the running game, so building the UI
/// from them avoids the bridge entirely. This class is a plain object (NOT a Node), so no source
/// generation happens for it; the countdown is ticked by the resolver loop via <see cref="SetRemaining"/>.
///
/// Look: transparent backdrop (no dimmer/panel), flat buttons showing only the upper 2/3 of the local
/// player's PROFESSION hand art (CharacterModel.ArmRockTexture/ArmPaperTexture/ArmScissorsTexture →
/// res://images/ui/hands/multiplayer_hand_{character}_{move}.png), no labels. Picking is tentative and
/// changeable: clicking a gesture selects it (and reveals a reset button to clear it); clicking another
/// gesture re-chooses; the reset button clears back to no pick. The selection only commits when the
/// overlay closes (countdown end / round resolved), so the player can re-choose for the full countdown.
/// </summary>
public sealed class RpsPickerView
{
    private static readonly Color CountdownFull = new(0.30f, 0.80f, 0.25f);  // green
    private static readonly Color CountdownEmpty = new(0.85f, 0.18f, 0.15f); // red

    private const int ContentMargin = 18;   // panel frame padding (keeps the hand off the border)
    private const int TextureMargin = 48;    // 9-slice margin for the reward-panel art
    private const int IconMaxWidth = 240;     // displayed hand width inside the panel
    private const float ButtonMinWidth = 280f;
    private const float ButtonMinHeight = 160f;
    private const float ResetButtonWidth = 280f;
    private const float ResetButtonHeight = 140f;
    private const int ResetButtonPadding = 30;   // extra content padding so the ↺ button reads larger
    // Three hands (280 each) + 2×32 separation ≈ 904; keep the column at least this wide so it never
    // collapses to the single chosen hand's width after a pick.
    private const float ColumnMinWidth = 904f;

    private readonly TaskCompletionSource<RelicPickingFightMove?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly Color ResetGlyphColor = new(0.953f, 0.882f, 0.663f); // #f3e1a9

    private readonly Button[] _buttons = new Button[3]; // indexed by (int)RelicPickingFightMove
    private readonly ProgressBar _countdownBar;
    private readonly StyleBoxFlat _countdownFill;
    private readonly Button _resetButton;   // null when re-choosing is disabled
    private readonly Control _root;
    private RelicPickingFightMove? _pick; // the current (changeable) pick; committed to _result at Close
    private bool _closed;

    /// <summary>The root node to add to the scene tree.</summary>
    public Control Root => _root;

    /// <summary>
    /// Completes only when the overlay closes (countdown end / round resolved), with whatever pick was
    /// current at that moment (or null if none). The pick stays changeable until then.
    /// </summary>
    public Task<RelicPickingFightMove?> Result => _result.Task;

    /// <summary>The move currently selected, or null if none / cleared. Changeable until <see cref="Close"/>.</summary>
    public RelicPickingFightMove? CurrentPick => _pick;

    /// <summary>True while a move is currently selected (may still be changed or cleared).</summary>
    public bool HasPick => _pick.HasValue;

    /// <summary>
    /// Adds the overlay to <paramref name="parent"/> and forces it to the front — the fight backstop /
    /// relic holder raise their own z-index during the intro, so just being a child isn't enough.
    /// (Cursor visibility in the relic room is owned by <c>RelicRoomCursorPatch</c>, not here.)
    /// </summary>
    public void AttachTo(Control parent)
    {
        parent.AddChild(_root);
        _root.MoveToFront();
    }

    public RpsPickerView(CharacterModel? character = null, bool allowReset = false)
    {
        _root = new Control { Name = "RpsPickerOverlay" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop; // block misclicks on the relic screen behind
        _root.ZIndex = 4096;

        // Transparent backdrop: no dimmer, just centered content over the live scene.
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 24);
        // Pin a min width so the column (and the countdown bar / reset button under it) doesn't collapse
        // to a narrow strip once a pick hides the two unchosen hands and leaves one slim hand.
        column.CustomMinimumSize = new Vector2(ColumnMinWidth, 0f);
        center.AddChild(column);

        var buttonRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttonRow.AddThemeConstantOverride("separation", 32);
        column.AddChild(buttonRow);

        buttonRow.AddChild(MakeButton("✊", character?.ArmRockTexture, RelicPickingFightMove.Rock));
        buttonRow.AddChild(MakeButton("✋", character?.ArmPaperTexture, RelicPickingFightMove.Paper));
        buttonRow.AddChild(MakeButton("✌", character?.ArmScissorsTexture, RelicPickingFightMove.Scissors));

        _countdownFill = new StyleBoxFlat
        {
            BgColor = CountdownFull,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        var countdownBg = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.45f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        _countdownBar = new ProgressBar
        {
            MinValue = 0.0,
            MaxValue = 1.0,
            Value = 1.0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(360f, 30f),
            SizeFlagsHorizontal = Control.SizeFlags.Fill, // stretch across the pinned column width
        };
        _countdownBar.AddThemeStyleboxOverride("fill", _countdownFill);
        _countdownBar.AddThemeStyleboxOverride("background", countdownBg);
        column.AddChild(_countdownBar);

        // Reset button (host-optional): clears the current pick so the player can re-choose. Only built
        // when re-choosing is enabled; hidden until a pick is made. Language-neutral glyph, backed by the
        // end-turn button art.
        if (allowReset)
        {
            _resetButton = new Button
            {
                Text = "↺",
                Visible = false,
                CustomMinimumSize = new Vector2(ResetButtonWidth, ResetButtonHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            ApplyResetButtonStyles(_resetButton);
            _resetButton.AddThemeFontSizeOverride("font_size", 72);
            foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                _resetButton.AddThemeColorOverride(state, ResetGlyphColor);
            _resetButton.Pressed += ClearChoice;
            column.AddChild(_resetButton);
        }
    }

    private Button MakeButton(string glyph, Texture2D? handTexture, RelicPickingFightMove move)
    {
        var button = new Button
        {
            CustomMinimumSize = new Vector2(ButtonMinWidth, ButtonMinHeight),
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
            ExpandIcon = true,
            ClipContents = true,   // never let the hand art draw past the panel background
        };

        // Reward-panel art as the button's own background (no border, no flat fill).
        ApplyButtonStyles(button);

        Texture2D? icon = CropTopHalf(handTexture);
        if (icon != null)
        {
            // Only the upper 2/3 of the hand art, no label.
            button.Icon = icon;
            button.AddThemeConstantOverride("icon_max_width", IconMaxWidth);

            // Grow the button (and thus its panel background) to wrap the whole hand: the icon is
            // drawn at icon_max_width wide keeping aspect, so a tall hand would otherwise overflow
            // the fixed-height button and spill onto the bare scene below the panel.
            int iconW = icon.GetWidth();
            int iconH = icon.GetHeight();
            if (iconW > 0 && iconH > 0)
            {
                float displayedH = IconMaxWidth * (float)iconH / iconW;
                float needH = displayedH + 2 * (ContentMargin + TextureMargin);
                button.CustomMinimumSize = new Vector2(
                    ButtonMinWidth,
                    Math.Max(ButtonMinHeight, needH));
            }
        }
        else
        {
            // Fallback when the texture is unavailable (e.g. no character supplied).
            button.Text = glyph;
            button.AddThemeFontSizeOverride("font_size", 96);
        }

        button.Pressed += () => Choose(move);
        _buttons[(int)move] = button;
        return button;
    }

    /// <summary>
    /// Backs the button with the game's reward-panel art (res://images/ui/reward_screen/reward_panel.png)
    /// across all states — no border, no flat background. Hover/pressed/disabled tint the texture so
    /// there's still feedback. No-op if the texture can't be loaded (button stays default/transparent).
    /// </summary>
    private static void ApplyButtonStyles(Button button)
    {
        // just cant find a resource loading wrapper, so just load it with MegaCrit's 
        // Assets loader
        var texture = ResourceLoader.Load<Texture2D>(
            ImageHelper.GetImagePath("ui/reward_screen/reward_panel.png"));
        if (texture == null)
            return;

        StyleBoxTexture Make(Color modulate)
        {
            var box = new StyleBoxTexture { Texture = texture, ModulateColor = modulate };
            box.SetTextureMarginAll(TextureMargin);   // 9-slice so corners don't distort
            box.SetContentMarginAll(ContentMargin);    // keep the hand off the panel frame
            return box;
        }

        button.AddThemeStyleboxOverride("normal", Make(Colors.White));
        button.AddThemeStyleboxOverride("hover", Make(new Color(1f, 0.96f, 0.80f)));   // warm highlight
        button.AddThemeStyleboxOverride("pressed", Make(new Color(0.85f, 0.85f, 0.85f)));
        button.AddThemeStyleboxOverride("focus", Make(Colors.White));
        button.AddThemeStyleboxOverride("disabled", Make(new Color(1f, 0.96f, 0.80f))); // chosen hand
    }

    /// <summary>
    /// Backs the reset button with the game's end-turn button art
    /// (res://images/packed/combat_ui/end_turn_button.png) across all states. No-op if it can't load.
    /// </summary>
    private static void ApplyResetButtonStyles(Button button)
    {
        var texture = ResourceLoader.Load<Texture2D>(
            ImageHelper.GetImagePath("packed/combat_ui/end_turn_button.png"));
        if (texture == null)
            return;

        StyleBoxTexture Make(Color modulate)
        {
            var box = new StyleBoxTexture { Texture = texture, ModulateColor = modulate };
            box.SetTextureMarginAll(TextureMargin);   // 9-slice so corners don't distort
            // Generous content padding around the glyph so the button reads noticeably larger.
            box.SetContentMarginAll(ContentMargin + ResetButtonPadding);
            return box;
        }

        button.AddThemeStyleboxOverride("normal", Make(Colors.White));
        button.AddThemeStyleboxOverride("hover", Make(new Color(1f, 0.96f, 0.80f)));
        button.AddThemeStyleboxOverride("pressed", Make(new Color(0.85f, 0.85f, 0.85f)));
        button.AddThemeStyleboxOverride("focus", Make(Colors.White));
    }

    /// <summary>
    /// Returns an AtlasTexture exposing only the top half of <paramref name="source"/> (hands point
    /// upward, so the wrist/lower half is dropped). Null if there's no source texture. The button
    /// height is derived from this cropped height, so the panel background is cropped to match.
    /// </summary>
    private static Texture2D? CropTopHalf(Texture2D? source)
    {
        if (source == null)
            return null;
        int width = source.GetWidth();
        int height = source.GetHeight();
        if (width <= 0 || height <= 0)
            return source;
        return new AtlasTexture
        {
            Atlas = source,
            Region = new Rect2(0f, 0f, width, height / 2f),
            FilterClip = true,
        };
    }

    /// <summary>
    /// Updates the countdown progress bar. <paramref name="total"/> is the full countdown so the bar
    /// can show a fraction and lerp its fill color green→red as time runs out. Driven by the
    /// resolver's per-frame loop. Keeps ticking after a pick (the chosen hand + bar stay on screen
    /// until the countdown ends).
    /// </summary>
    public void SetRemaining(double seconds, double total)
    {
        if (_closed)
            return;
        // The picker sits at max ZIndex so it stays above the fight backstop, which also means it would
        // otherwise draw over the pause/ESC menu (that menu uses a game-logic pause, not a scene-tree
        // pause, so it shares this canvas layer). Hide the whole overlay while the game is paused.
        _root.Visible = !RunManager.Instance.IsPaused;
        double fraction = total > 0.0 ? Math.Clamp(seconds / total, 0.0, 1.0) : 0.0;
        _countdownBar.Value = fraction;
        _countdownFill.BgColor = CountdownEmpty.Lerp(CountdownFull, (float)fraction);
    }

    /// <summary>
    /// Selects a move. Tentative and changeable: the two unchosen hands hide and the chosen one remains
    /// (the HBox re-centers its single visible child), and the reset button is revealed so the player can
    /// clear back to all three gestures and re-choose. Nothing commits until <see cref="Close"/>.
    /// </summary>
    public void Choose(RelicPickingFightMove? move)
    {
        if (_closed || !move.HasValue)
            return;

        _pick = move;

        // The game's standard UI click (FMOD event backed by ui_click.wav).
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");

        for (int i = 0; i < _buttons.Length; i++)
        {
            Button button = _buttons[i];
            if (button == null)
                continue;
            // Keep only the chosen hand visible (disabled so it reads as selected); hide the others.
            bool chosen = i == (int)move.Value;
            button.Disabled = chosen;
            button.Visible = chosen;
        }

        if (_resetButton != null)
            _resetButton.Visible = true;
    }

    /// <summary>Clears the pick and restores all three gesture buttons so the player can choose again.</summary>
    public void ClearChoice()
    {
        if (_closed)
            return;

        _pick = null;
        foreach (Button button in _buttons)
        {
            if (button == null)
                continue;
            button.Disabled = false;
            button.Visible = true;
        }
        if (_resetButton != null)
            _resetButton.Visible = false;
    }

    /// <summary>Commit the current pick (if any) to <see cref="Result"/> and free the node from the tree.</summary>
    public void Close()
    {
        _closed = true;
        _result.TrySetResult(_pick);
        if (GodotObject.IsInstanceValid(_root))
            _root.QueueFree();
    }
}
