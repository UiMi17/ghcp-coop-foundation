using System.Collections.Generic;
using GHPC;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Networking.Transport;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Maps puppet <see cref="WheelController" /> instances to <c>netId</c> so Harmony hot paths avoid
///     <see cref="Component.GetComponentInParent{T}" /> and reflection on every wheel in the scene.
/// </summary>
internal static class CoopPuppetWheelRegistry
{
    private static readonly Dictionary<int, uint> InstanceIdToNetId = new();

    private static readonly Dictionary<uint, List<int>> NetIdToWheelInstanceIds = new();

    public static void RegisterWheelsForPuppet(uint netId, Unit unit)
    {
        if (netId == 0 || unit == null || !CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
            return;

        UnregisterNetId(netId);

        WheelController[] wcs = unit.GetComponentsInChildren<WheelController>(true);
        if (wcs == null || wcs.Length == 0)
            return;

        var ids = new List<int>(wcs.Length);
        for (int i = 0; i < wcs.Length; i++)
        {
            WheelController? wc = wcs[i];
            if (wc == null)
                continue;
            int id = wc.GetInstanceID();
            InstanceIdToNetId[id] = netId;
            ids.Add(id);
        }

        if (ids.Count > 0)
            NetIdToWheelInstanceIds[netId] = ids;
    }

    public static void UnregisterNetId(uint netId)
    {
        if (netId == 0)
            return;
        if (!NetIdToWheelInstanceIds.TryGetValue(netId, out List<int>? ids))
            return;
        NetIdToWheelInstanceIds.Remove(netId);
        for (int i = 0; i < ids.Count; i++)
            InstanceIdToNetId.Remove(ids[i]);
    }

    public static void ClearSession()
    {
        InstanceIdToNetId.Clear();
        NetIdToWheelInstanceIds.Clear();
    }

    public static bool TryGetPuppetNetId(WheelController wc, out uint netId)
    {
        netId = 0;
        if (wc == null)
            return false;
        return InstanceIdToNetId.TryGetValue(wc.GetInstanceID(), out netId) && netId != 0;
    }

    /// <summary>Fast path for Harmony: client + wheel visuals + wheel registered as puppet.</summary>
    public static bool IsRegisteredPuppetWheel(WheelController wc)
    {
        if (!CoopUdpTransport.IsClient || !CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
            return false;
        return TryGetPuppetNetId(wc, out _);
    }
}
