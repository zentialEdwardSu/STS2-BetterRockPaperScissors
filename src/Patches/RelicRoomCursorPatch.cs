using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Keeps the mouse cursor visible inside the treasure-relic room.
///
/// Stock <c>NHandImageCollection.UpdateHandVisibility</c> hides the OS cursor for the whole
/// <c>SharedRelicPicking</c> screen (it uses pointing-hand art instead) and re-runs on EVERY input-
/// state change. That breaks this mod two ways: the cursor is gone from room entry until relics are
/// distributed, and clicking a gesture fires an input change that re-hides the cursor mid-pick. A
/// one-shot SetCursorShown from the picker can't win against a method the game keeps re-running.
///
/// So we postfix it and force the cursor back on. <c>RefreshCursorShown</c> still gates on controller
/// use, so controller players are unaffected; we only override the mouse-hidden case.
/// </summary>
[HarmonyPatch]
public static class RelicRoomCursorPatch
{
    private static readonly Type CollectionType = AccessTools.TypeByName(
        "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NHandImageCollection");

    private static MethodBase TargetMethod()
    {
        if (CollectionType == null)
            throw new Exception("Cannot find NHandImageCollection type");
        MethodInfo method = AccessTools.Method(CollectionType, "UpdateHandVisibility");
        if (method == null)
            throw new Exception("Cannot find UpdateHandVisibility method");
        return method;
    }

    private static void Postfix()
    {
        // Re-assert after stock hides it for SharedRelicPicking. SetCursorShown→RefreshCursorShown
        // still respects controller mode, so this is a no-op when a controller is in use.
        NGame.Instance?.CursorManager.SetCursorShown(true);
    }
}
