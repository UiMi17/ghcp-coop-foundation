using GHPC.Weaponry;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Client: skip prefab + FMOD from <see cref="BasicGrenadeExplosionBehaviour.Explode"/> for grenades thrown by the peer.
/// Derived behaviours (AP damage sphere, AT <see cref="LiveRound"/> spawn) still run after <c>base.Explode</c> when the prefix allows the original.
/// </summary>
[HarmonyPatch(typeof(BasicGrenadeExplosionBehaviour), nameof(BasicGrenadeExplosionBehaviour.Explode))]
internal static class PatchBasicGrenadeExplosionClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(Grenade grenade, string soundPath, GameObject effectPrefab)
    {
        _ = soundPath;
        _ = effectPrefab;
        return !CoopClientFxSuppression.ShouldSuppressGrenadeExplosionFx(grenade);
    }
}
