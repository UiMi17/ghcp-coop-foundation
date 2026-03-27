using GHPC.CoopFoundation.Net;
using GHPC.Mission;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Client local <see cref="RandomEnvironment.RandomizeNow" /> overrides host weather; re-apply last COO snapshot after it runs.</summary>
[HarmonyPatch(typeof(RandomEnvironment), nameof(RandomEnvironment.RandomizeNow))]
internal static class PatchRandomEnvironmentCoopClientWeather
{
    private static void Postfix()
    {
        if (!CoopUdpTransport.IsClient || !CoopUdpTransport.WorldEnvironmentReplicationEnabledPublic)
            return;
        CoopWorldEnvironmentReplication.ClientReapplyStoredHostWeather();
    }
}
