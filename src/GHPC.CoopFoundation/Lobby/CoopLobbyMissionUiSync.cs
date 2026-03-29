using System.Reflection;
using GHPC.UI;
using GHPC.UI.Menu;
using UnityEngine;

namespace GHPC.CoopFoundation.Lobby;

/// <summary>Apply host briefing / refresh Customize UI on the main thread (network → menu).</summary>
internal static class CoopLobbyMissionUiSync
{
    private static int _networkBriefingApplyDepth;

    public static bool ShouldSkipClientBriefingGate => _networkBriefingApplyDepth > 0;

    public static void ApplyHostBriefingFromNetwork(string sceneMapKey)
    {
        if (string.IsNullOrEmpty(sceneMapKey))
            return;
        _networkBriefingApplyDepth++;
        try
        {
            MissionBriefMenu[] menus = Object.FindObjectsOfType<MissionBriefMenu>(true);
            if (menus.Length > 0)
                menus[0].LoadMissionBriefing(sceneMapKey);
        }
        finally
        {
            _networkBriefingApplyDepth--;
        }

        CoopNetSession.SetAuthoritativeHostBriefingSceneKey(sceneMapKey);
    }

    public static void RefreshMissionConfigPanelsAndSlots()
    {
        MethodInfo? m = typeof(MissionConfigMenuPanel).GetMethod(
            "RefreshPanelData",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (m != null)
        {
            MissionConfigMenuPanel[] panels = Object.FindObjectsOfType<MissionConfigMenuPanel>(true);
            for (int i = 0; i < panels.Length; i++)
            {
                try
                {
                    m.Invoke(panels[i], null);
                }
                catch
                {
                    // ignored
                }
            }
        }

        CoopLobbyPlayerSlots.NotifySlotsRefresh();
    }
}
