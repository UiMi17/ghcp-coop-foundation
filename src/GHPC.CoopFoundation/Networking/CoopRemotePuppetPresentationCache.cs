using System.Collections.Generic;
using GHPC;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     One <see cref="VehicleController" /> resolve per remote puppet <c>netId</c> (avoids per-frame
///     <see cref="GameObject.GetComponentInChildren{T}(bool)" /> across track relay + visual presenters).
/// </summary>
internal static class CoopRemotePuppetPresentationCache
{
    private static readonly Dictionary<uint, VehicleController?> VehicleControllerByNetId = new();

    /// <summary>Called when a unit becomes a client-side network puppet (after drivers suppressed).</summary>
    public static void Register(uint netId, Unit unit)
    {
        if (netId == 0 || unit == null)
            return;
        VehicleController? vc = unit.GetComponentInChildren<VehicleController>(true);
        VehicleControllerByNetId[netId] = vc;
    }

    public static void Remove(uint netId)
    {
        VehicleControllerByNetId.Remove(netId);
    }

    public static void ClearSession()
    {
        VehicleControllerByNetId.Clear();
    }

    /// <summary>
    ///     Returns cached <see cref="VehicleController" />; if missing or destroyed, resolves once from
    ///     <paramref name="unit" /> when non-null and refreshes the cache.
    /// </summary>
    public static bool TryGetVehicleController(uint netId, Unit? unit, out VehicleController? vc)
    {
        vc = null;
        if (netId == 0)
            return false;
        if (VehicleControllerByNetId.TryGetValue(netId, out vc) && vc != null)
            return true;

        if (unit == null)
        {
            VehicleControllerByNetId.Remove(netId);
            return false;
        }

        vc = unit.GetComponentInChildren<VehicleController>(true);
        VehicleControllerByNetId[netId] = vc;
        return vc != null;
    }
}
