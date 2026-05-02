using GHPC;
using GHPC.CoopFoundation.GameSession;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Host;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Networking.Protocol;
using NWH.VehiclePhysics;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Synthetic rim rotation when NWH wheel visuals are off; otherwise <see cref="CoopRemotePuppetVisualLateOrchestrator" />
///     calls <see cref="WheelController.UpdateVisual" />. Skips tracked vehicles (handled by
///     <see cref="CoopChassisTrackVisualPresenter" />).
/// </summary>
internal static class CoopChassisWheelVisualPresenter
{
    private const float RpmToDegPerSecond = 6.0001197f;

    private const float RadsToRpm = 9.549296585513721f;

    public static void ResetSession()
    {
    }

    internal static void TickHostPeerPuppet(Unit pu, uint pNet, float deltaTime)
    {
        ApplyForUnit(
            pu,
            pNet,
            CoopRemoteState.RemoteWorldLinearVelocity,
            CoopRemoteState.RemoteWorldAngularVelocity,
            deltaTime);
    }

    internal static void TickSyntheticWheelsForClientPuppet(uint netId, Unit unit, float deltaTime)
    {
        if (!ClientSimulationGovernor.TryGetDisplayVelocities(netId, out Vector3 v, out Vector3 w))
        {
            v = Vector3.zero;
            w = Vector3.zero;
        }

        ApplyForUnit(unit, netId, v, w, deltaTime);
    }

    private static void ApplyForUnit(Unit unit, uint netId, Vector3 worldLinVel, Vector3 worldAngVel, float dt)
    {
        if (!CoopRemotePuppetPresentationCache.TryGetVehicleController(netId, unit, out VehicleController? vc)
            || vc == null
            || vc.tracks.trackedVehicle)
            return;

        if (CoopNwhPuppetTracksRelay.ShouldSkipSyntheticChassisVisuals(unit))
            return;

        Vector3 com = unit.transform.position;
        Rigidbody? rb = unit.Chassis?.Rigidbody;
        if (rb == null)
            rb = unit.GetComponentInParent<Rigidbody>();
        rb ??= unit.GetComponentInChildren<Rigidbody>();
        if (rb != null)
            com = rb.worldCenterOfMass;

        foreach (Wheel wheel in vc.wheels)
        {
            WheelController? wc = wheel.wheelController;
            if (wc == null)
                continue;
            GameObject? visGo = wc.Visual;
            if (visGo == null)
                continue;
            Vector3 r = wc.transform.position - com;
            Vector3 vSurf = worldLinVel + Vector3.Cross(worldAngVel, r);
            float tangent = Vector3.Dot(vSurf, wc.transform.forward);
            float radius = Mathf.Max(0.08f, wc.TireRadius);
            float omegaWheelRad = tangent / radius;
            float rpm = omegaWheelRad * RadsToRpm;
            float deg = rpm * RpmToDegPerSecond * dt;
            visGo.transform.Rotate(wc.transform.right, deg, Space.World);
        }
    }
}
