using GHPC.CoopFoundation.Net;
using GHPC.Player;
using GHPC.State;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
///     UMC Planning: only the coop host opens the tactical map; client stays in
///     <see cref="MissionState.Planning" /> until host sends COO mission-planning-complete.
/// </summary>
[HarmonyPatch(typeof(MissionStateController))]
internal static class PatchMissionPlanningHostAuthority
{
    [HarmonyPrefix]
    [HarmonyPatch("InitializeUMCMission")]
    private static bool InitializeUMCMission_Prefix(MissionStateController __instance)
    {
        if (!CoopUdpTransport.IsClient || !CoopNetSession.IsConnected)
            return true;
        if (!__instance.IsUMCMission())
            return true;

        PlayerInput? input = PlayerInput.Instance;
        if (input == null)
            return true;

        Traverse.Create(__instance).Method("SetState", MissionState.Planning).GetValue();
        input.BlockKeyboardInput = true;
        CoopClientPlanningGate.EnterWaitingForHost();

        if (HookDiagnostics.ShouldLog)
            MelonLogger.Msg("[CoopNet] client UMC Planning: map deferred to host; waiting for mission-planning-complete");

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("EndPlanningPhase")]
    private static void EndPlanningPhase_Prefix(ref bool __state)
    {
        __state = MissionStateController.CurrentState == MissionState.Planning;
    }

    [HarmonyPostfix]
    [HarmonyPatch("EndPlanningPhase")]
    private static void EndPlanningPhase_Postfix(bool __state)
    {
        if (!__state || !CoopUdpTransport.IsHost || !CoopNetSession.IsConnected)
            return;
        if (MissionStateController.CurrentState != MissionState.Playing)
            return;

        CoopUdpTransport.HostBroadcastMissionPlanningComplete();
    }
}
