using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

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
/// res://images/ui/hands/multiplayer_hand_{character}_{move}.png), no labels. When the player picks,
/// the two unchosen hands hide and the chosen one re-centers (the HBox auto-centers its one remaining
/// visible child); the chosen hand + the green→red countdown bar stay until the countdown ends, then
/// the whole overlay is freed.
/// </summary>
public sealed class RpsPickerView
{
    private static readonly Color CountdownFull = new(0.30f, 0.80f, 0.25f);  // green
    private static readonly Color CountdownEmpty = new(0.85f, 0.18f, 0.15f); // red

    private const int ContentMargin = 18;   // panel frame padding (keeps the hand off the border)
    private const int TextureMargin = 48;    // 9-slice margin for the reward-panel art
    private const int IconMaxWidth = 240;     // displayed hand width inside the panel
    private const float ButtonMinWidth = 280f;
    private const float ButtonMinHeight = 300f;

    private readonly TaskCompletionSource<RelicPickingFightMove?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Button[] _buttons = new Button[3]; // indexed by (int)RelicPickingFightMove
    private readonly ProgressBar _countdownBar;
    private readonly StyleBoxFlat _countdownFill;
    private readonly Control _root;
    private bool _resolved;
    private bool _closed;

    /// <summary>The root node to add to the scene tree.</summary>
    public Control Root => _root;

    /// <summary>Completes with the chosen move, or null when closed/cancelled without a pick.</summary>
    public Task<RelicPickingFightMove?> Result => _result.Task;

    /// <summary>True once a move was chosen (vs. timed out / closed).</summary>
    public bool HasPick => _result.Task.IsCompleted && _result.Task.Result.HasValue;

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

    public RpsPickerView(CharacterModel? character = null)
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
        };
        _countdownBar.AddThemeStyleboxOverride("fill", _countdownFill);
        _countdownBar.AddThemeStyleboxOverride("background", countdownBg);
        column.AddChild(_countdownBar);
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

        Texture2D? icon = CropTopTwoThirds(handTexture);
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

        button.Pressed += () => Resolve(move);
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
    /// Returns an AtlasTexture exposing only the top 2/3 of <paramref name="source"/> (hands point
    /// upward, so the wrist/lower third is dropped). Null if there's no source texture.
    /// </summary>
    private static Texture2D? CropTopTwoThirds(Texture2D? source)
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
            Region = new Rect2(0f, 0f, width, height * 2f / 3f),
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
        double fraction = total > 0.0 ? Math.Clamp(seconds / total, 0.0, 1.0) : 0.0;
        _countdownBar.Value = fraction;
        _countdownFill.BgColor = CountdownEmpty.Lerp(CountdownFull, (float)fraction);
    }

    /// <summary>
    /// Resolves the pick (idempotent). On a real pick the two unchosen hands hide and the chosen one
    /// re-centers (the HBox auto-centers its single remaining visible child); the chosen hand and the
    /// countdown bar stay visible until <see cref="Close"/>. Picking is also disabled so it can't change.
    /// </summary>
    public void Resolve(RelicPickingFightMove? move)
    {
        if (_resolved)
            return;
        _resolved = true;
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (move.HasValue)
        {
            // The game's standard UI click (FMOD event backed by ui_click.wav).
            SfxCmd.Play("event:/sfx/ui/clicks/ui_click");

            for (int i = 0; i < _buttons.Length; i++)
            {
                Button button = _buttons[i];
                if (button == null)
                    continue;
                if (i == (int)move.Value)
                    button.Disabled = true;   // keep visible, but no longer clickable
                else
                    button.Visible = false;   // hide the unchosen hands
            }
        }

        _result.TrySetResult(move);
    }

    /// <summary>Resolve to null (if not already) and free the node from the tree.</summary>
    public void Close()
    {
        _closed = true;
        Resolve(null);
        if (GodotObject.IsInstanceValid(_root))
            _root.QueueFree();
    }
}
