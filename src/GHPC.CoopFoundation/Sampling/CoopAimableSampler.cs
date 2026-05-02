using GHPC;
using GHPC.Weapons;
using UnityEngine;

namespace GHPC.CoopFoundation.Sampling;

/// <summary>
///     Picks main traverse + gun <see cref="AimablePlatform" />s. Prefers <see cref="FireControlSystem" /> mounts when
///     present (same rule as <c>ClientSimulationGovernor</c>) so roof MGs / extra aimables are not mistaken for the main
///     gun; otherwise falls back to <see cref="Unit.AimablePlatforms" /> (root traverse, then child gun).
/// </summary>
internal static class CoopAimableSampler
{
    public static void SampleWorldRotations(Unit unit, Quaternion hullWorldRotation, out Quaternion turretWorld, out Quaternion gunWorld)
    {
        if (!TryPickAimables(unit, out AimablePlatform? traverse, out AimablePlatform? gun))
        {
            turretWorld = hullWorldRotation;
            gunWorld = hullWorldRotation;
            return;
        }

        turretWorld = traverse!.Transform.rotation;
        gunWorld = gun != null ? gun.Transform.rotation : turretWorld;
    }

    private static bool TryPickAimables(Unit unit, out AimablePlatform? traverse, out AimablePlatform? gun)
    {
        traverse = null;
        gun = null;

        FireControlSystem? fcs = unit.InfoBroker?.FCS;
        if (fcs != null && TryPickAimablesFromFcs(fcs, out traverse, out gun))
            return true;

        AimablePlatform[]? aps = unit.AimablePlatforms;
        if (aps == null || aps.Length == 0)
            return false;

        foreach (AimablePlatform? ap in aps)
        {
            if (ap == null || ap.Transform == null)
                continue;
            if (ap.ParentPlatform == null)
            {
                traverse = ap;
                break;
            }
        }

        traverse ??= aps[0];
        if (traverse == null || traverse.Transform == null)
            return false;

        foreach (AimablePlatform? ap in aps)
        {
            if (ap == null || ap == traverse || ap.Transform == null)
                continue;
            if (ap.ParentPlatform == traverse || ap.Transform.IsChildOf(traverse.Transform))
            {
                gun = ap;
                break;
            }
        }

        return true;
    }

    private static bool TryPickAimablesFromFcs(FireControlSystem fcs, out AimablePlatform? traverse, out AimablePlatform? gun)
    {
        traverse = null;
        gun = null;
        AimablePlatform[]? mounts = fcs.Mounts;
        if (mounts == null || mounts.Length == 0)
            return false;

        if (fcs.TurretPlatform != null)
            traverse = fcs.TurretPlatform;
        else
            traverse = mounts[0];

        if (mounts.Length >= 2)
            gun = mounts[1];
        else
        {
            for (int i = 0; i < mounts.Length; i++)
            {
                AimablePlatform? ap = mounts[i];
                if (ap == null || ap == traverse)
                    continue;
                if (ap.ParentPlatform == traverse)
                {
                    gun = ap;
                    break;
                }
            }
        }

        return traverse != null;
    }

    /// <summary>Host/client peer puppet, snapshot encode, governor aim bind: one consistent traverse/gun choice.</summary>
    internal static bool TryGetTraverseAndGun(Unit unit, out AimablePlatform? traverse, out AimablePlatform? gun) =>
        TryPickAimables(unit, out traverse, out gun);
}
