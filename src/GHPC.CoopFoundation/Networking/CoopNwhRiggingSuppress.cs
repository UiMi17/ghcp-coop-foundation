using System.Collections.Generic;
using GHPC;
using NWH.VehiclePhysics;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     NWH <see cref="NWH.NWHManager" /> calls <see cref="Rigging.Update" /> every frame for each
///     <see cref="VehicleController" /> in its list, moving axle/wheel bones toward
///     <see cref="NWH.WheelController3D.WheelController.SpringTravelPoint" />. On network puppets with disabled
///     <see cref="NWH.WheelController3D.WheelController" />, those points are stale and gear “whips” unnaturally.
///     Disable rigging on puppets; restore when returning to local sim.
/// </summary>
internal static class CoopNwhRiggingSuppress
{
    public static void DisableOnUnit(Unit unit, List<(VehicleController Vc, bool WasRiggingEnabled)> into)
    {
        VehicleController[] vcs = unit.GetComponentsInChildren<VehicleController>(true);
        for (int i = 0; i < vcs.Length; i++)
        {
            VehicleController? vc = vcs[i];
            if (vc == null || !vc.rigging.enabled)
                continue;
            into.Add((vc, true));
            vc.rigging.enabled = false;
        }
    }

    public static void Restore(List<(VehicleController Vc, bool WasRiggingEnabled)> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            (VehicleController vc, bool was) = list[i];
            if (vc != null && was)
                vc.rigging.enabled = true;
        }

        list.Clear();
    }
}
