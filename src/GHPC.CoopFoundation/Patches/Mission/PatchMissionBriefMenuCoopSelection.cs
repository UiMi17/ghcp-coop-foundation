using System.Collections.Generic;
using System.Reflection;
using GHPC.UI;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Tracks canonical scene map key for lobby host start (covers string and <see cref="SceneMissionKey" /> entry).</summary>
[HarmonyPatch(typeof(MissionBriefMenu), nameof(MissionBriefMenu.LoadMissionBriefing), typeof(string))]
internal static class PatchMissionBriefMenuCoopSelectionString
{
    [HarmonyPostfix]
    private static void Postfix(string sceneMapKey)
    {
        CoopLobbyMissionSelection.RecordSceneMapKey(sceneMapKey);
        CoopUdpTransport.NotifyHostLocalBriefingChangedIfNeeded(sceneMapKey);
    }
}

/// <summary>When the menu calls <c>LoadMissionBriefing(SceneMissionKey)</c> directly (no public type ref in mod compile).</summary>
[HarmonyPatch]
internal static class PatchMissionBriefMenuCoopSelectionSceneKey
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo m in typeof(MissionBriefMenu).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (m.Name != nameof(MissionBriefMenu.LoadMissionBriefing))
                continue;
            ParameterInfo[] p = m.GetParameters();
            if (p.Length != 1 || p[0].ParameterType.Name != "SceneMissionKey")
                continue;
            yield return m;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(object __0)
    {
        try
        {
            object sceneMissionKey = __0;
            System.Type kt = sceneMissionKey.GetType();
            string? tk = kt.GetProperty("TheaterKey")?.GetValue(sceneMissionKey) as string;
            string? mk = kt.GetProperty("MissionKey")?.GetValue(sceneMissionKey) as string;
            if (!string.IsNullOrEmpty(tk) && !string.IsNullOrEmpty(mk))
            {
                CoopLobbyMissionSelection.RecordSceneMapKeyFromParts(tk, mk);
                CoopUdpTransport.NotifyHostLocalBriefingChangedIfNeeded(CoopLobbyMissionSelection.LastSceneMapKey);
            }
        }
        catch
        {
            // ignored: API drift
        }
    }
}
