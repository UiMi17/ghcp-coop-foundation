using GHPC;
using GHPC.CoopFoundation.Networking;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Transport;
using NWH.VehiclePhysics;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.NwhPuppet;

/// <summary>
///     Runs vanilla <see cref="Tracks.Update" /> on puppets in FixedUpdate when wheel visuals are enabled;
///     <see cref="Tracks.UpdateVisual" /> / <see cref="WheelController.UpdateVisual" /> run from
///     <see cref="CoopRemotePuppetVisualLateOrchestrator" /> once per LateUpdate.
/// </summary>
internal static class CoopNwhPuppetTracksRelay
{
    public static bool ShouldSkipSyntheticChassisVisuals(Unit unit)
    {
        if (!CoopUdpTransport.IsClient || !CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
            return false;
        uint netId = CoopUnitWireRegistry.GetWireId(unit);
        if (netId == 0)
            return false;
        return ClientSimulationGovernor.IsClientSuppressedPuppet(netId);
    }

    public static void AfterNwhManagerFixedUpdate()
    {
        if (!CoopUdpTransport.IsClient || !CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
            return;

        foreach (uint netId in ClientSimulationGovernor.EnumerateSuppressedNetIds())
        {
            Unit? unit = CoopUnitLookup.TryFindByNetId(netId);
            if (unit == null)
                continue;
            if (ClientSimulationGovernor.ShouldThrottleLodFarTierWork(netId, unit.transform.position, salt: 41))
                continue;
            if (!CoopRemotePuppetPresentationCache.TryGetVehicleController(netId, unit, out VehicleController? vc)
                || vc == null
                || !vc.tracks.trackedVehicle)
                continue;
            vc.tracks.Update();
        }
    }

}
