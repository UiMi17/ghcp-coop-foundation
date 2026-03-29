using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Client: skip legacy detonation prefabs / ricochet visual for <see cref="OldLiveRound"/> fired by the peer (host sends GHC explosion / particle replay).
/// <see cref="OldLiveRound.Detonate"/> still runs (<c>_detonated</c>, terrain flags); only <c>doImpactEffect</c> is skipped.
/// </summary>
[HarmonyPatch(typeof(OldLiveRound), "doImpactEffect", typeof(bool))]
internal static class PatchOldLiveRoundDoImpactEffectClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(OldLiveRound __instance)
    {
        return !CoopClientFxSuppression.ShouldSuppressOldLiveRoundImpactFx(__instance);
    }
}
