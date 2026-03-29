using GHPC.Audio;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

internal static class ImpactSfxClientSuppressPrefix
{
    public static bool Allow()
    {
        if (!CoopUdpTransport.IsClient)
            return true;
        return !CoopClientFxSuppression.ShouldSuppressLiveRoundCosmetics(CoopClientFxSuppression.CurrentLiveRoundInDoUpdate);
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayTerrainImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(bool), typeof(bool))]
internal static class PatchImpactSfxTerrainClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayRicochetSFX), typeof(Vector3), typeof(AmmoType))]
internal static class PatchImpactSfxRicochetClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlaySmallCalImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(float))]
internal static class PatchImpactSfxSmallCalClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayLargeCalImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(float))]
internal static class PatchImpactSfxLargeCalClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayImpactPenIntPerspSFX), typeof(Vector3), typeof(float))]
internal static class PatchImpactSfxPenPerspClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlaySimpleImpactAudio), typeof(ImpactAudioType), typeof(Vector3), typeof(bool))]
internal static class PatchImpactSfxSimpleClientSuppress
{
    [HarmonyPrefix]
    private static bool Prefix() => ImpactSfxClientSuppressPrefix.Allow();
}
