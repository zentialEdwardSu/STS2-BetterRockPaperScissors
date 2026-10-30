using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// The 1-second "who won" beat after a map conflict: the winning player's avatar grows while everybody else's
/// fades out, and then the stock travel sequence carries on into the winning room.
///
/// Target: the multiplayer HUD avatars (<c>NMultiplayerPlayerState</c>), not the little vote icons on the map.
/// That HUD is public API, the game itself flashes it from the map screen (map pings call
/// <c>FlashPlayerReady</c>), and its nodes are <c>%CharacterIcon</c> portraits — so the animation needs no
/// private reflection at all. Only node properties are tweened; no new Godot script class is introduced
/// (see the note in <see cref="Overlays.RpsPickerView"/> about MonoMod + source-generated Godot classes).
///
/// Everything is restored in a <c>finally</c>: unlike the map's vote icons, the HUD survives into the next room,
/// so a failed restore would leave a teammate invisible for the rest of the run.
/// </summary>
internal static class MapConflictReveal
{
    private const double RevealSeconds = 1.0;
    private const float WinnerScale = 1.35f;
    private const double WinnerGrowSeconds = 0.15;
    private const double LoserFadeSeconds = 0.2;

    /// <summary>
    /// Starts the reveal. The returned task completes once the beat is over and the HUD is restored; the stock
    /// animation seam awaits it, which is what turns "reveal then enter the room" into a single ordered flow.
    /// Never throws and never returns null.
    /// </summary>
    internal static Task Begin(ConflictRpsSession session)
    {
        if (session.Kind != ConflictKind.MapPath || session.WinnerSide == ConflictRpsSession.NoSide)
            return Task.CompletedTask;

        return AnimateAsync(session);
    }

    private static async Task AnimateAsync(ConflictRpsSession session)
    {
        var targets = new List<AvatarTarget>();
        try
        {
            NMultiplayerPlayerStateContainer? container = NRun.Instance?.GlobalUi.MultiplayerPlayerContainer;
            if (container != null && GodotObject.IsInstanceValid(container))
            {
                for (int i = 0; i < container.GetChildCount(); i++)
                {
                    if (container.GetChild(i) is not NMultiplayerPlayerState state || !GodotObject.IsInstanceValid(state))
                        continue;

                    int slot = session.IndexOf(state.Player);
                    if (slot < 0)
                        continue;

                    targets.Add(new AvatarTarget(state, session.Sides[slot] == session.WinnerSide));
                }
            }

            if (targets.Count == 0)
            {
                Entry.Logger.Warn("[conflict] no multiplayer HUD avatars found — skipping the map winner reveal");
                await ConflictTiming.WaitSecondsAsync(RevealSeconds, CancellationToken.None);
                return;
            }

            foreach (AvatarTarget target in targets)
                target.Play();

            await ConflictTiming.WaitSecondsAsync(RevealSeconds, CancellationToken.None);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[conflict] map winner reveal failed: {e.Message}");
        }
        finally
        {
            foreach (AvatarTarget target in targets)
                target.Restore();
        }
    }

    /// <summary>
    /// One HUD avatar plus the properties we borrowed. Kept as a plain class (never a Node) so nothing about it
    /// goes through Godot's source-generated script bridge.
    /// </summary>
    private sealed class AvatarTarget
    {
        private readonly NMultiplayerPlayerState _state;
        private readonly Control _icon;
        private readonly bool _winner;
        private readonly Vector2 _originalScale;
        private readonly Vector2 _originalPivot;
        private readonly Color _originalModulate;

        internal AvatarTarget(NMultiplayerPlayerState state, bool winner)
        {
            _state = state;
            _winner = winner;
            _icon = state.GetNodeOrNull<Control>("%CharacterIcon") ?? state;
            _originalScale = _icon.Scale;
            _originalPivot = _icon.PivotOffset;
            _originalModulate = state.Modulate;
        }

        internal void Play()
        {
            if (!GodotObject.IsInstanceValid(_state))
                return;

            if (_winner)
            {
                // Scale around the avatar's centre; the original pivot is restored with the scale.
                _icon.PivotOffset = _icon.Size * 0.5f;
                _icon.CreateTween()
                    .TweenProperty(_icon, "scale", Vector2.One * WinnerScale, WinnerGrowSeconds)
                    .SetTrans(Tween.TransitionType.Cubic)
                    .SetEase(Tween.EaseType.Out);
                return;
            }

            _state.CreateTween()
                .TweenProperty(_state, "modulate:a", 0.0f, LoserFadeSeconds)
                .SetEase(Tween.EaseType.In);
        }

        internal void Restore()
        {
            if (!GodotObject.IsInstanceValid(_state))
                return;

            if (GodotObject.IsInstanceValid(_icon))
            {
                _icon.Scale = _originalScale;
                _icon.PivotOffset = _originalPivot;
            }

            _state.Modulate = _originalModulate;
        }
    }
}
