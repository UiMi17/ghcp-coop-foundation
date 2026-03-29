using GHPC;
using GHPC.AI.Interfaces;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(Unit), nameof(Unit.NotifyStruck), typeof(IUnit), typeof(AmmoType), typeof(Vector3), typeof(bool))]
internal static class PatchUnitNotifyStruck
{
    [HarmonyPostfix]
    private static void Postfix(Unit __instance, IUnit? shooter, AmmoType? ammoType, Vector3 impactWorldPosition, bool isSpall)
    {
        if (!HostCombatBroadcast.CanEmit || ClientCombatApplier.SuppressStruckBroadcast)
            return;
        if (!isSpall)
        {
            uint victimNetId = CoopUnitWireRegistry.GetWireId(__instance);
            uint shooterNetId = 0;
            if (shooter is Unit su)
                shooterNetId = CoopUnitWireRegistry.GetWireId(su);
            uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
            HostCombatBroadcast.TrySendUnitStruck(
                victimNetId,
                shooterNetId,
                ammoKey,
                impactWorldPosition,
                false,
                CoopUdpTransport.CombatReplicationLogStruckPerHit);
        }

        // Phase 4B: for both direct hits and spall, ship compact host-authoritative damage correction.
        HostCombatBroadcast.TrySendDamageState(
            __instance,
            force: isSpall,
            logDamageState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendUnitState(__instance, force: isSpall, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCrewState(__instance, force: isSpall, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendCompartmentState(__instance, force: isSpall, logState: CoopUdpTransport.CombatReplicationLogDamageState);
        HostCombatBroadcast.TrySendHitResolved(
            CoopUnitWireRegistry.GetWireId(__instance),
            shooter is Unit hu ? CoopUnitWireRegistry.GetWireId(hu) : 0,
            CoopAmmoKey.FromAmmoType(ammoType),
            impactWorldPosition,
            isSpall,
            CoopUdpTransport.CombatReplicationLogDamageState);
    }
}
