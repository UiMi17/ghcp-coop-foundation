using System.Collections.Generic;
using GHPC.Mission;
using GHPC.UI.Menu;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Lobby;

/// <summary>
/// Friendly <see cref="MissionConfigUnitSelectionEntry" /> rows in Customize panel order (vanilla <c>RefreshPanelData</c>),
/// and the matching flex catalog keys in <see cref="FactionSpawnInfo.SpawnOrder" /> order used at spawn time.
/// </summary>
internal static class CoopFriendlyCustomizeRows
{
    /// <summary>
    /// Same order as vanilla <see cref="MissionConfigMenuPanel" /> <c>AddUnitEntry</c> for friendly rows — uses private
    /// <c>_unitDataObjects</c>, not <c>_friendlyUnitDataParent</c> child scan (extra transforms there can carry duplicate
    /// <see cref="MissionConfigUnitSelectionEntry" /> and double dropdown options).
    /// </summary>
    public static List<MissionConfigUnitSelectionEntry> GetOrderedFriendlyUnitEntries(MissionConfigMenuPanel panel)
    {
        var list = new List<MissionConfigUnitSelectionEntry>();
        List<GameObject>? unitObjects = Traverse.Create(panel).Field<List<GameObject>>("_unitDataObjects").Value;
        if (unitObjects == null)
            return list;
        for (int i = 0; i < unitObjects.Count; i++)
        {
            GameObject? go = unitObjects[i];
            if (go == null)
                continue;
            MissionConfigUnitSelectionEntry? e = go.GetComponent<MissionConfigUnitSelectionEntry>();
            if (e != null && e.IsFriendly)
                list.Add(e);
        }

        return list;
    }

    /// <summary>Runtime: same key list as Customize friendly unit rows (non-blocked spawn orders), after flex overrides.</summary>
    public static bool TryGetFriendlyRowCatalogKeysForMission(out List<string> keys)
    {
        keys = new List<string>();
        MissionSceneMeta? meta = UnityEngine.Object.FindObjectOfType<MissionSceneMeta>();
        DynamicMissionMetadata? dm = meta?.DynamicMetadata?.MissionData;
        if (dm?.FriendlySpawnInfo?.SpawnOrders == null)
            return false;
        DynamicMissionMetadataOverrides? ov =
            DynamicMissionLauncher.GetFlexOverrides(DynamicMissionComposer.CurrentMissionNameKey);
        IList<FactionSpawnInfo.SpawnOrder> orders = dm.FriendlySpawnInfo.SpawnOrders;
        for (int i = 0; i < orders.Count; i++)
        {
            FactionSpawnInfo.SpawnOrder so = orders[i];
            if (so.BlockCustomization)
                continue;
            string k = ov != null
                ? ov.GetOverriddenUnitKey(so, friendlyTeam: true, so.Key)
                : so.Key;
            keys.Add(k);
        }

        return keys.Count > 0;
    }
}
