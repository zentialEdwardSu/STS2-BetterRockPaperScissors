using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Patches;

/// <summary>Restores the resting position of a frozen relic-fight hand as it slides back in.</summary>
[HarmonyPatch]
public static class HandRestoreOnResumePatch
{
    private static readonly MethodInfo? AnimateInMethod =
        AccessTools.DeclaredMethod(typeof(NHandImage), nameof(NHandImage.AnimateIn), Type.EmptyTypes);
    private static readonly FieldInfo? StateField = AccessTools.Field(typeof(NHandImage), "_state");
    private static readonly FieldInfo? DesiredPositionField = AccessTools.Field(typeof(NHandImage), "_desiredPosition");
    private static readonly object? FrozenState = StateField?.FieldType is { IsEnum: true } stateType &&
        Enum.TryParse(stateType, "Frozen", out object? value) ? value : null;

    [HarmonyPrepare]
    private static bool Prepare() =>
        AnimateInMethod != null && FrozenState != null && DesiredPositionField?.FieldType == typeof(Vector2);

    private static MethodBase TargetMethod() => AnimateInMethod!;

    private static void Postfix(NHandImage __instance)
    {
        if (!ModConfig.RestoreHandsOnResume || !Equals(StateField!.GetValue(__instance), FrozenState))
            return;

        // Match the game's frozen resting position. Builds that already reset it keep their result.
        Vector2 viewportSize = __instance.GetViewportRect().Size;
        Vector2 direction = Vector2.Down.Rotated(__instance.Rotation);
        Vector2 restingPosition = viewportSize * 0.5f + viewportSize * direction * 0.1667f;
        if (DesiredPositionField!.GetValue(__instance) is Vector2 target && !target.IsEqualApprox(restingPosition))
            DesiredPositionField.SetValue(__instance, restingPosition);
    }
}
