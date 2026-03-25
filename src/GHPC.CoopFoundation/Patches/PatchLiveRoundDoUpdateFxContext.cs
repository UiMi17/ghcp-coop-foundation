using GHPC.CoopFoundation.Net;
using GHPC.Weapons;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Track current <see cref="LiveRound"/> during <see cref="LiveRound.DoUpdate"/> for cosmetic suppression + host wire.</summary>
[HarmonyPatch(typeof(LiveRound), nameof(LiveRound.DoUpdate))]
internal static class PatchLiveRoundDoUpdateFxContext
{
    [HarmonyPrefix]
    private static void Prefix(LiveRound __instance)
    {
        CoopClientFxSuppression.EnterLiveRoundDoUpdate(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        CoopClientFxSuppression.ExitLiveRoundDoUpdate();
    }
}
