using GHPC.CoopFoundation;
using GHPC.CoopFoundation.Net;
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
        if (state == MissionState.Playing && CoopUdpTransport.IsHost)
            CoopUdpTransport.HostBroadcastWorldEnvironmentToPeer();
        if (!HookDiagnostics.ShouldLog)
            return;
        MelonLogger.Msg($"[CoopDiag] MissionStateController.SetState → {state}");
    }
}
