using GHPC;
using UnityEngine;

namespace GHPC.CoopFoundation.Sampling;

/// <summary>
///     Picks main traverse + gun <see cref="AimablePlatform" />s from <see cref="Unit.AimablePlatforms" /> (decompiled GHPC).
///     Traverse: first platform with <c>ParentPlatform == null</c> (inspector root), else index 0.
///     Gun: first other platform parented to traverse (<see cref="AimablePlatform.ParentPlatform" /> or Unity hierarchy).
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

    /// <summary>Host peer puppet + snapshot encode: same traverse/gun pick as <see cref="SampleWorldRotations" />.</summary>
    internal static bool TryGetTraverseAndGun(Unit unit, out AimablePlatform? traverse, out AimablePlatform? gun) =>
        TryPickAimables(unit, out traverse, out gun);
}
