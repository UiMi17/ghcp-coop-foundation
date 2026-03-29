using GHPC;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyDestroyed))]
internal static class PatchUnitNotifyDestroyed
{
    /// <summary>
    /// Capture flammables <em>before</em> vanilla teardown often zeros <see cref="GHPC.Effects.FlammablesManager" /> fire —
    /// aligns client exit VFX with host (authority snapshot before state is stripped).
    /// </summary>
    [HarmonyPrefix]
    private static void PrefixCompartmentBeforeTeardown(Unit __instance)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        HostCombatBroadcast.TrySendCompartmentState(
            __instance,
            force: true,
            logState: CoopUdpTransport.CombatReplicationLogDamageState);
    }

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
    }
}
