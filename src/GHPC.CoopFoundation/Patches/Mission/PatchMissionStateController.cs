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
        if (state == MissionState.Playing)
        {
            if (CoopUdpTransport.IsHost)
            {
                CoopUdpTransport.HostBroadcastWorldEnvironmentToPeer();
                CoopUdpTransport.HostNotifyLocalMissionLoaded();
            }
            else if (CoopUdpTransport.IsClient)
            {
                CoopUdpTransport.TrySendClientLoadedAckForCurrentTransition();
            }
        }
        if (!HookDiagnostics.ShouldLog)
            return;
        MelonLogger.Msg($"[CoopDiag] MissionStateController.SetState → {state}");
    }
}
