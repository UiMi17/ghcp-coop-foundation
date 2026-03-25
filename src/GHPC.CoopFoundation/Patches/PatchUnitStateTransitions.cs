using GHPC;
using GHPC.CoopFoundation.Net;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyIncapacitated))]
internal static class PatchUnitNotifyIncapacitated
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        HostCombatBroadcast.TrySendUnitState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCrewState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyAbandoned))]
internal static class PatchUnitNotifyAbandoned
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        HostCombatBroadcast.TrySendUnitState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCrewState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyCannotMove))]
internal static class PatchUnitNotifyCannotMove
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        HostCombatBroadcast.TrySendUnitState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyCannotShoot))]
internal static class PatchUnitNotifyCannotShoot
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        HostCombatBroadcast.TrySendUnitState(__instance, force: true, logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }
}
