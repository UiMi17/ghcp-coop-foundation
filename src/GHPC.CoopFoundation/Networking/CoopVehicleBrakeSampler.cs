using NWH.VehiclePhysics;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Authoritative brake hint for remote track/wheel presentation (NWH <see cref="Brakes" /> internals are private).
/// </summary>
internal static class CoopVehicleBrakeSampler
{
    /// <summary>Returns 0–1: max normalized wheel brake torque + pedal/handbrake hints.</summary>
    public static float SampleBrake01(VehicleController? vc)
    {
        if (vc == null || !vc.isActiveAndEnabled)
            return 0f;

        float maxNorm = 0f;
        foreach (Wheel w in vc.wheels)
        {
            WheelController? wc = w?.wheelController;
            if (wc == null)
                continue;
            float cap = Mathf.Max(500f, wc.MaxPutDownForce * Mathf.Max(0.06f, wc.Radius));
            maxNorm = Mathf.Max(maxNorm, Mathf.Clamp01(Mathf.Abs(wc.BrakeTorque) / cap));
        }

        InputStates input = vc.input;
        float vert = input.Vertical;
        if (vert < -0.04f)
            maxNorm = Mathf.Max(maxNorm, Mathf.Clamp01(-vert));
        if (input.Handbrake > 0.06f)
            maxNorm = Mathf.Max(maxNorm, input.Handbrake);

        if (vc.brakes.Active)
            maxNorm = Mathf.Max(maxNorm, 0.28f);

        return Mathf.Clamp01(maxNorm);
    }
}
