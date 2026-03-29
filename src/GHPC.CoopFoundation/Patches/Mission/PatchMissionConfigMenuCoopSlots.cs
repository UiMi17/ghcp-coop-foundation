using GHPC.CoopFoundation.UI;
using GHPC.Mission.Data;
using GHPC.UI.Menu;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// <see cref="MissionConfigMenuPanel" />: <c>RefreshPanelData</c> is private; <c>SelectedMission</c> has a private getter — read mission via Harmony <see cref="Traverse" /> in this patch only.
/// </summary>
[HarmonyPatch(typeof(MissionConfigMenuPanel), "RefreshPanelData")]
internal static class PatchMissionConfigMenuCoopSlotsRefresh
{
    [HarmonyPostfix]
    private static void Postfix(MissionConfigMenuPanel __instance)
    {
        MissionMetaData? mission = Traverse.Create(__instance).Property<MissionMetaData>("SelectedMission").Value;
        bool isFlex = mission != null && mission.IsFlexMission;
        string missionLabel = mission == null ? "(null)" : mission.MissionName;
        MelonLogger.Msg(
            $"[CoopCustomize] RefreshPanelData postfix panel={__instance.name} SelectedMission={missionLabel} isFlex={isFlex}");
        CoopCustomizeSlotSection.AttachIfNeeded(__instance, isFlex);
        CoopCustomizeSlotSection.ApplyClientVanillaSelectLockIfNeeded(__instance);
    }
}

/// <summary>Cancel in Customize calls vanilla <see cref="MissionConfigMenuPanel.RevertPanelData" /> (then Refresh). Log for repro trails.</summary>
[HarmonyPatch(typeof(MissionConfigMenuPanel), nameof(MissionConfigMenuPanel.RevertPanelData))]
internal static class PatchMissionConfigMenuCoopRevertLog
{
    [HarmonyPrefix]
    private static void Prefix(MissionConfigMenuPanel __instance)
    {
        MelonLogger.Msg($"[CoopCustomize] RevertPanelData (Cancel path) panel={__instance.name} frame={UnityEngine.Time.frameCount}");
    }
}

/// <summary>
/// Only the panel that owns the co-op UI may destroy it. Vanilla can have multiple <see cref="MissionConfigMenuPanel" />
/// instances (inactive clones / other menus); a parameterless OnDisable previously nuked co-op on the active panel without a refresh.
/// </summary>
[HarmonyPatch(typeof(MissionConfigMenuPanel), "OnDisable")]
internal static class PatchMissionConfigMenuCoopSlotsOnDisable
{
    [HarmonyPostfix]
    private static void Postfix(MissionConfigMenuPanel __instance)
    {
        if (!CoopCustomizeSlotSection.IsCoopSectionOwner(__instance))
            return;
        MelonLogger.Msg($"[CoopCustomize] MissionConfigMenuPanel.OnDisable owner={__instance.name} → DestroyIfPresent");
        CoopCustomizeSlotSection.DestroyIfPresent();
    }
}

/// <summary>
/// If co-op rows were removed without this panel disabling (e.g. stray destroy fixed above, or future edge cases), re-attach after enable.
/// </summary>
[HarmonyPatch(typeof(MissionConfigMenuPanel), "OnEnable")]
internal static class PatchMissionConfigMenuCoopSlotsOnEnable
{
    [HarmonyPostfix]
    private static void Postfix(MissionConfigMenuPanel __instance)
    {
        if (CoopCustomizeSlotSection.HasAttachedRoot())
            return;
        MissionMetaData? mission = Traverse.Create(__instance).Property<MissionMetaData>("SelectedMission").Value;
        bool isFlex = mission != null && mission.IsFlexMission;
        if (!isFlex)
            return;
        bool net = CoopUdpTransport.IsNetworkActive;
        bool host = CoopUdpTransport.IsHost;
        bool client = CoopUdpTransport.IsClient;
        if (!net || (!host && !client))
            return;
        MelonLogger.Msg($"[CoopCustomize] OnEnable recovery: re-AttachIfNeeded panel={__instance.name}");
        CoopCustomizeSlotSection.AttachIfNeeded(__instance, true);
        CoopCustomizeSlotSection.ApplyClientVanillaSelectLockIfNeeded(__instance);
    }
}
