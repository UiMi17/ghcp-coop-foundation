#pragma warning disable CS0612 // VehicleInfo marked obsolete in game assembly; still the runtime type for SetPlayerVehicle.
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(WorldScript), nameof(WorldScript.SetPlayerVehicle))]
internal static class PatchWorldScriptPlayerVehicle
{
    [HarmonyPrefix]
    private static void Prefix(VehicleInfo? info)
    {
        if (!HookDiagnostics.ShouldLog)
            return;
        string label = info == null ? "null" : info.gameObject.name;
        MelonLogger.Msg($"[CoopDiag] WorldScript.SetPlayerVehicle → {label}");
    }
}

#pragma warning restore CS0612
