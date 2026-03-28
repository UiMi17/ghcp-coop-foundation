using System;
using System.Collections.Generic;
using GHPC;
using GHPC.CoopFoundation.Net;
using GHPC.Infantry;
using GHPC.Mission;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation;

/// <summary>
/// After vanilla flex <see cref="PlayerInput.SetDefaultUnit" />, map lobby <see cref="CoopLobbyPlayerSlots" /> friendly row index
/// to a spawned <see cref="Unit" /> using Customize-ordered catalog keys (see <see cref="CoopFriendlyCustomizeRows" />).
/// </summary>
internal static class CoopCustomizeRowSpawnApply
{
    private static bool _applying;

    private static readonly List<Unit> PickScratch = new List<Unit>();

    public static void TryApplyAfterSetDefaultUnit(PlayerInput playerInput, Unit primaryUnit)
    {
        if (_applying)
            return;
        if (!CoopUdpTransport.IsNetworkActive || !CoopNetSession.IsConnected)
            return;
        if (!DynamicMissionComposer.DynamicMission)
            return;
        if (primaryUnit == null || primaryUnit.NoPlayerControl)
            return;
        MissionSceneMeta? meta = UnityEngine.Object.FindObjectOfType<MissionSceneMeta>();
        if (meta == null)
            return;

        byte want = CoopUdpTransport.IsHost
            ? CoopLobbyPlayerSlots.HostFriendlyUnitRowIndex
            : CoopLobbyPlayerSlots.ClientFriendlyUnitRowIndex;
        if (!CoopFriendlyCustomizeRows.TryGetFriendlyRowCatalogKeysForMission(out List<string> keys) || keys.Count == 0)
            return;

        int idx = Math.Max(0, Math.Min(want, keys.Count - 1));
        string wantKey = keys[idx];
        if (string.Equals(primaryUnit.UniqueName, wantKey, StringComparison.Ordinal))
            return;

        if (!TryPickUnitForCatalogKey(wantKey, meta, out Unit? pick)
            || pick == null
            || pick == primaryUnit
            || pick == playerInput.CurrentPlayerUnit)
            return;

        _applying = true;
        try
        {
            playerInput.SetDefaultUnit(pick);
        }
        finally
        {
            _applying = false;
        }
    }

    private static bool TryPickUnitForCatalogKey(string catalogKey, MissionSceneMeta meta, out Unit? unit)
    {
        unit = null;
        PickScratch.Clear();
        Unit[] all = UnityEngine.Object.FindObjectsOfType<Unit>();
        for (int i = 0; i < all.Length; i++)
        {
            Unit u = all[i];
            if (u == null || u.NoPlayerControl)
                continue;
            if (u is InfantryUnit)
                continue;
            if (!IsPlayerTeamUnit(u, meta))
                continue;
            if (!string.Equals(u.UniqueName, catalogKey, StringComparison.Ordinal))
                continue;
            PickScratch.Add(u);
        }

        if (PickScratch.Count == 0)
        {
            MelonLogger.Warning(
                $"[CoopCustomize] No playable unit matched catalog key \"{catalogKey}\" for starting row (keeping vanilla default).");
            return false;
        }

        PickScratch.Sort((a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));
        unit = PickScratch[0];
        return true;
    }

    private static bool IsPlayerTeamUnit(Unit u, MissionSceneMeta meta)
    {
        if (DynamicMissionComposer.GetIsFriendly(u.Allegiance))
            return true;
        Faction mf = meta.DynamicMetadata != null ? meta.DynamicMetadata.MissionData.PlayerFaction : Faction.Blue;
        return mf == Faction.Neutral && u.Allegiance == Faction.Neutral;
    }
}
