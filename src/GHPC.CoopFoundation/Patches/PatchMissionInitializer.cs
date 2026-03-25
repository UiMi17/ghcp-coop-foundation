using GHPC.CoopFoundation;
using GHPC.Mission;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(MissionInitializer), "Awake")]
internal static class PatchMissionInitializer
{
    [HarmonyPostfix]
    private static void Postfix(MissionInitializer __instance)
    {
        string scene = __instance.MissionSceneName ?? "";
        CoopSessionState.SetMissionSceneKey(scene);
        if (!HookDiagnostics.ShouldLog)
            return;
        MelonLogger.Msg(string.IsNullOrEmpty(scene)
            ? "[CoopDiag] MissionInitializer.Awake (MissionSceneName empty yet)"
            : $"[CoopDiag] MissionInitializer.Awake MissionSceneName={scene}");
    }
}
