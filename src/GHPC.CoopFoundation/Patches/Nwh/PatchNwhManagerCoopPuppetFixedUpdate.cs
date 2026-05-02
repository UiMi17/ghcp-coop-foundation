using GHPC.CoopFoundation.Networking.NwhPuppet;
using HarmonyLib;
using NWH;

namespace GHPC.CoopFoundation.Patches.Nwh;

[HarmonyPatch(typeof(NWHManager), "FixedUpdate")]
internal static class PatchNwhManagerCoopPuppetFixedUpdate
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        CoopNwhPuppetContext.BeginNwhFixedFrame();
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        CoopNwhPuppetTracksRelay.AfterNwhManagerFixedUpdate();
    }
}
