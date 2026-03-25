using System.Collections;
using System.Reflection;
using GHPC.Constraints;
using GHPC.CoopFoundation.Net;
using GHPC.Utility;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(LateFollow), "Sync")]
internal static class PatchLateFollowSyncAimTrace
{
    [HarmonyPostfix]
    private static void Postfix(LateFollow __instance)
    {
        AimOverwriteProbe.CheckOverwrite("LateFollow.Sync", __instance.transform);
    }
}

[HarmonyPatch(typeof(RotationConstraint), nameof(RotationConstraint.ProcessConstraints))]
internal static class PatchRotationConstraintProcessAimTrace
{
    private static readonly FieldInfo? ConstraintsField =
        AccessTools.Field(typeof(RotationConstraint), "constraints");

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (ConstraintsField == null)
            return;
        if (ConstraintsField.GetValue(null) is not IDictionary dict)
            return;

        foreach (DictionaryEntry entry in dict)
        {
            object? value = entry.Value;
            if (value == null)
                continue;
            FieldInfo? tfField = value.GetType().GetField("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tfField?.GetValue(value) is Transform tf)
                AimOverwriteProbe.CheckOverwrite("RotationConstraint.ProcessConstraints", tf);
        }
    }
}
