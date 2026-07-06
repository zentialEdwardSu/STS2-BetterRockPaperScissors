using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;

namespace STS2BetterRockPaperScissors;

/// <summary>
/// Branch-agnostic "is a menu covering the run" check.
///
/// The beta branch exposes <c>RunManager.Instance.IsPaused</c>, but the stable branch's RunManager has
/// no such member, so binding to it fails to link on stable. Instead we read the capstone container,
/// which is byte-for-byte identical across both branches. <c>NCapstoneContainer.InUse</c> is true
/// whenever a capstone screen is open over the run — the ESC/pause menu (opened via
/// <c>SubmenuStack.ShowScreen(CapstoneSubmenuType.PauseMenu)</c>, which routes through this container),
/// plus settings/deck/compendium views — all of which should hide the raised RPS visuals and freeze the
/// pick window. This is the same signal the game's own top-bar pause button checks against
/// (NTopBarPauseButton.IsOpen). The static Instance is null-guarded, so this is safe to call every
/// frame, including before a run node exists.
/// </summary>
internal static class GamePauseState
{
    public static bool IsMenuOpen => NCapstoneContainer.Instance?.InUse ?? false;
}
