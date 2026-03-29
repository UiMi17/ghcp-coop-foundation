using GHPC.Weaponry;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Client: do not spawn gameplay <see cref="GHPC.Weapons.LiveRound"/> from a peer&apos;s AT grenade — host sim + GHC remains authoritative.
/// <see cref="BasicGrenadeExplosionBehaviour.Explode"/> (prefab/audio) is unchanged; uses the same remote-owner gates as <see cref="CoopClientFxSuppression.ShouldSuppressGrenadeExplosionFx"/>.
/// </summary>
[HarmonyPatch(
    typeof(AntiTankGrenadeExplosionBehaviour),
    "InstantiateNewLiveRoundGameObject",
    new[] { typeof(Grenade), typeof(Vector3), typeof(Vector3) })]
internal static class PatchAntiTankGrenadeLiveRoundClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(Grenade grenade, Vector3 newPosition, Vector3 forward)
    {
        _ = newPosition;
        _ = forward;
        return !CoopClientFxSuppression.ShouldSuppressGrenadeExplosionFx(grenade);
    }
}
