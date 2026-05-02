using System.Collections.Generic;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Transport;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.NwhPuppet;

/// <summary>
///     Per-fixed-frame replicated linear/angular velocity for client puppets, consumed by Harmony patches on NWH
///     <see cref="NWH.WheelController3D.WheelController" /> before wheel jobs run.
/// </summary>
internal static class CoopNwhPuppetContext
{
    private static readonly Dictionary<uint, Vector3> LinearVelocityByNetId = new();

    private static readonly Dictionary<uint, Vector3> AngularVelocityByNetId = new();

    /// <summary>
    ///     Called from Harmony prefix on <see cref="NWH.NWHManager.FixedUpdate" /> (before wheel ray batch).
    ///     Uses the same buffered GHW sample as <see cref="ClientSimulationGovernor.TryGetDisplayLinearVelocity" />.
    /// </summary>
    public static void BeginNwhFixedFrame()
    {
        if (!CoopUdpTransport.IsClient || !CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
            return;

        LinearVelocityByNetId.Clear();
        AngularVelocityByNetId.Clear();

        foreach (uint netId in ClientSimulationGovernor.EnumerateSuppressedNetIds())
        {
            Vector3 lin;
            Vector3 ang;
            if (!ClientSimulationGovernor.TryGetNetIdWorldPosition(netId, out Vector3 unitPos))
            {
                if (!ClientSimulationGovernor.TryGetDisplayVelocities(netId, out lin, out ang))
                {
                    lin = Vector3.zero;
                    ang = Vector3.zero;
                }
            }
            else
            {
                bool wireOnly = (CoopNwhPuppetSettings.WheelWireOnlyBeyondMeters > 1f
                        && ClientSimulationGovernor.MinDistanceToClientReferences(in unitPos)
                        >= CoopNwhPuppetSettings.WheelWireOnlyBeyondMeters)
                    || ClientSimulationGovernor.ShouldThrottleLodFarTierWork(netId, unitPos, salt: 53);
                if (wireOnly && ClientSimulationGovernor.TryGetWireVelocitiesOnly(netId, out lin, out ang))
                {
                }
                else if (!ClientSimulationGovernor.TryGetDisplayVelocities(netId, out lin, out ang))
                {
                    lin = Vector3.zero;
                    ang = Vector3.zero;
                }
            }

            LinearVelocityByNetId[netId] = lin;
            AngularVelocityByNetId[netId] = ang;
        }
    }

    internal static bool TryGetVelocitiesForNetId(uint netId, out Vector3 linear, out Vector3 angular)
    {
        linear = default;
        angular = default;
        if (!LinearVelocityByNetId.TryGetValue(netId, out linear))
            return false;
        return AngularVelocityByNetId.TryGetValue(netId, out angular);
    }
}
