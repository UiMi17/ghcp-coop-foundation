using System.Collections.Generic;
using GHPC;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Remote / puppet vehicles use a kinematic chassis driven by network pose. NWH
///     <see cref="WheelController" /> still runs <see cref="WheelController.FixedUpdate" /> suspension when enabled,
///     fighting the parent rigidbody and shaking equipment parented under wheel / bogie hierarchies.
///     Unity MP: disable wheel simulation on non-authoritative visual proxies (keep chassis RB + colliders).
/// </summary>
internal static class CoopNwhWheelControllerSuppress
{
    public static void DisableAllOnUnit(Unit unit, List<(Behaviour Behaviour, bool WasEnabled)> into)
    {
        WheelController[] wcs = unit.GetComponentsInChildren<WheelController>(true);
        for (int i = 0; i < wcs.Length; i++)
        {
            WheelController? wc = wcs[i];
            if (wc == null || !wc.enabled)
                continue;
            into.Add((wc, true));
            wc.enabled = false;
        }
    }
}
