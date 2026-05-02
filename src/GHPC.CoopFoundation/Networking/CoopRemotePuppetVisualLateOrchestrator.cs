using GHPC;
using GHPC.CoopFoundation.GameSession;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Host;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Networking.Transport;
using NWH.VehiclePhysics;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Single LateUpdate pass for remote puppet chassis visuals: NWH <see cref="Tracks.UpdateVisual" /> /
///     <see cref="WheelController.UpdateVisual" /> when wire mode is on, otherwise synthetic track UV / wheel rim
///     rotation (see <see cref="CoopChassisTrackVisualPresenter" />, <see cref="CoopChassisWheelVisualPresenter" />).
///     Host peer puppet track + wheel ticks are folded here to avoid three separate enumerations.
/// </summary>
internal static class CoopRemotePuppetVisualLateOrchestrator
{
    public static void Tick(float deltaTime)
    {
        if (!CoopSessionState.IsPlaying || deltaTime <= 1e-6f)
            return;

        if (CoopUdpTransport.IsHost
            && HostPeerUnitPuppet.TryGetActivePuppet(out Unit? pu, out uint pNet)
            && pu != null)
        {
            CoopChassisTrackVisualPresenter.TickHostPeerPuppet(pu, pNet, deltaTime);
            CoopChassisWheelVisualPresenter.TickHostPeerPuppet(pu, pNet, deltaTime);
        }

        if (!CoopUdpTransport.IsClient)
            return;

        bool wireVc = CoopNwhPuppetSettings.WheelControllerVisualsEnabled;
        foreach (uint netId in ClientSimulationGovernor.EnumerateSuppressedNetIds())
        {
            Unit? unit = CoopUnitLookup.TryFindByNetId(netId);
            if (unit == null)
                continue;
            if (ClientSimulationGovernor.ShouldThrottleLodFarTierWork(netId, unit.transform.position, salt: 29))
                continue;
            if (!CoopRemotePuppetPresentationCache.TryGetVehicleController(netId, unit, out VehicleController? vc)
                || vc == null)
                continue;

            if (wireVc)
            {
                if (vc.tracks != null && vc.tracks.trackedVehicle)
                    vc.tracks.UpdateVisual();
                foreach (Wheel wheel in vc.wheels)
                {
                    WheelController? wc = wheel.wheelController;
                    if (wc != null)
                        wc.UpdateVisual();
                }
            }
            else
            {
                if (vc.tracks != null && vc.tracks.trackedVehicle)
                    CoopChassisTrackVisualPresenter.TickSyntheticTrackForClientPuppet(netId, unit, deltaTime);
                else
                    CoopChassisWheelVisualPresenter.TickSyntheticWheelsForClientPuppet(netId, unit, deltaTime);
            }
        }
    }
}
