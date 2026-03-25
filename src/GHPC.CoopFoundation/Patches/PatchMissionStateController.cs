using GHPC.CoopFoundation;
using GHPC.State;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(MissionStateController), "SetState", typeof(MissionState))]
internal static class PatchMissionStateController
{
    [HarmonyPostfix]
    private static void Postfix(MissionState state)
    {
        CoopSessionState.SetMissionState(state);
        if (!HookDiagnostics.ShouldLog)
            return;
        MelonLogger.Msg($"[CoopDiag] MissionStateController.SetState → {state}");
    }
}
