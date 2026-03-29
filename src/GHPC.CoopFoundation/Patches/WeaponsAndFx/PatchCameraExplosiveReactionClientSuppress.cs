using GHPC.Camera;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(CameraJiggler), nameof(CameraJiggler.RequestExplosiveReaction), typeof(Vector3), typeof(float))]
internal static class PatchCameraJigglerExplosiveClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(Vector3 worldPosition, float kgTntEquivalent)
    {
        _ = worldPosition;
        _ = kgTntEquivalent;
        if (!CoopUdpTransport.IsClient)
            return true;
        return !CoopClientFxSuppression.ShouldSuppressLiveRoundCosmetics(CoopClientFxSuppression.CurrentLiveRoundInDoUpdate);
    }
}

[HarmonyPatch(typeof(BlurManager), nameof(BlurManager.RequestExplosiveReaction), typeof(Vector3), typeof(float))]
internal static class PatchBlurManagerExplosiveClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(Vector3 worldPosition, float kgTntEquivalent)
    {
        _ = worldPosition;
        _ = kgTntEquivalent;
        if (!CoopUdpTransport.IsClient)
            return true;
        return !CoopClientFxSuppression.ShouldSuppressLiveRoundCosmetics(CoopClientFxSuppression.CurrentLiveRoundInDoUpdate);
    }
}
