using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>
/// Backfills the STABLE game branch's broken <c>NHandImage.AnimateIn</c> so a fighter hand that
/// retracted off-screen while a menu was open animates back on-screen when it re-appears.
///
/// The bug: during relic awards, hands are in <c>State.Frozen</c>. When a player opens the pause/ESC
/// menu, their synced screen state changes, so stock <c>NHandImageCollection.UpdateHandVisibility</c>
/// calls <c>AnimateAway()</c>, which overwrites the hand's private <c>_desiredPosition</c> with an
/// OFF-screen point. On resume it calls <c>AnimateIn()</c> — but on stable, <c>AnimateIn</c> only
/// re-runs the slide-in progress tween and never restores <c>_desiredPosition</c> for a frozen hand, so
/// <c>_Process</c> keeps driving the hand toward the off-screen target. The hand only comes back when
/// the relic grab later recomputes its position. In real multiplayer this happens per-hand, driven by
/// each peer's OWN synced screen state, so a remote player's paused hand stays hidden on EVERY peer's
/// screen — not just their own.
///
/// The BETA branch fixed this in-engine by adding, at the end of <c>AnimateIn</c>:
/// <code>if (_state == State.Frozen) _desiredPosition = GetFrozenPosition();</code>
/// This postfix reproduces exactly that. Patching <c>AnimateIn</c> itself (rather than watching the
/// local pause state) means it fires uniformly for local and remote hands, matching beta.
///
/// Gated by <see cref="ModConfig.RestoreHandsOnResume"/> — leave OFF on beta so we don't double-apply
/// the engine's own restore. If the target method or private field can't be resolved the patch simply
/// doesn't apply (TargetMethod throws are caught by Harmony's PatchAll only if present; here they're
/// guarded so the rest of the mod still loads).
/// </summary>
[HarmonyPatch]
public static class HandRestoreOnResumePatch
{
    private static readonly Type HandType = AccessTools.TypeByName(
        "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NHandImage");

    private static readonly FieldInfo DesiredPositionField =
        HandType != null ? AccessTools.Field(HandType, "_desiredPosition") : null;
    private static readonly FieldInfo StateField =
        HandType != null ? AccessTools.Field(HandType, "_state") : null;
    private static readonly object FrozenStateValue = ResolveFrozen();

    private static object ResolveFrozen()
    {
        Type stateType = StateField?.FieldType;
        if (stateType != null && stateType.IsEnum && Enum.IsDefined(stateType, "Frozen"))
            return Enum.Parse(stateType, "Frozen");
        return null;
    }

    /// <summary>True when every handle resolved, so the postfix can actually run.</summary>
    private static bool Available =>
        HandType != null && DesiredPositionField != null && StateField != null && FrozenStateValue != null;

    private static MethodBase TargetMethod()
    {
        if (HandType == null)
            throw new Exception("Cannot find NHandImage type");
        MethodInfo method = AccessTools.Method(HandType, "AnimateIn");
        if (method == null)
            throw new Exception("Cannot find NHandImage.AnimateIn method");
        return method;
    }

    private static void Postfix(NHandImage __instance)
    {
        if (!Available || !ModConfig.RestoreHandsOnResume)
            return;
        // Only touch hands the game has frozen for the awards phase; leave pointing/grabbing hands alone.
        if (!Equals(StateField.GetValue(__instance), FrozenStateValue))
            return;

        // Reproduce beta's GetFrozenPosition(): the resting spot the frozen hand slides in to. Matches
        // stock SetFrozenForRelicAwards' math exactly.
        Rect2 viewportRect = __instance.GetViewportRect();
        Vector2 down = Vector2.Down.Rotated(__instance.Rotation);
        Vector2 frozen = viewportRect.Size / 2f + viewportRect.Size * down * 0.1667f;
        DesiredPositionField.SetValue(__instance, frozen);
    }
}
