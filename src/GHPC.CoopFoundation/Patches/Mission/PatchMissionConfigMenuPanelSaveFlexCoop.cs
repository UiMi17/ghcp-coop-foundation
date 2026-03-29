using System.Reflection;
using GHPC.Mission.Data;
using GHPC.UI.Menu;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Host: after Customize Apply, push flex blob to connected client.</summary>
[HarmonyPatch(typeof(MissionConfigMenuPanel), nameof(MissionConfigMenuPanel.SavePanelData))]
internal static class PatchMissionConfigMenuPanelSaveFlexCoop
{
    [HarmonyPostfix]
    private static void Postfix(MissionConfigMenuPanel __instance)
    {
        if (!CoopUdpTransport.IsHost || !CoopNetSession.HandshakeOk)
            return;
        MissionMetaData? mission = typeof(MissionConfigMenuPanel)
            .GetProperty("SelectedMission", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(__instance) as MissionMetaData;
        if (mission == null || !mission.IsFlexMission)
            return;
        CoopUdpTransport.NotifyHostFlexOverridesChangedFromMenu();
    }
}
