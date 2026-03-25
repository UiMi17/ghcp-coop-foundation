using GHPC;
using GHPC.CoopFoundation.Net;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyDestroyed))]
internal static class PatchUnitNotifyDestroyed
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;

        // P0 death parity: always send a final authoritative damage snapshot on destruction.
        HostCombatBroadcast.TrySendDamageState(
            __instance,
            force: true,
            logDamageState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendUnitState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCrewState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCompartmentState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }
}
