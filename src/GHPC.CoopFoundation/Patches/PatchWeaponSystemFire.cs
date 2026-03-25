using GHPC;
using GHPC.AI.Interfaces;
using GHPC.CoopFoundation.Net;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(WeaponSystem), nameof(WeaponSystem.Fire), typeof(IUnit))]
internal static class PatchWeaponSystemFire
{
    [HarmonyPostfix]
    private static void Postfix(WeaponSystem __instance, bool __result, IUnit? target)
    {
        if (!__result || !HostCombatBroadcast.CanEmit)
            return;
        Traverse tr = Traverse.Create(__instance);
        Unit? unit = tr.Field<Unit>("_unit").Value;
        if (unit == null)
            return;
        uint shooterNetId = CoopUnitWireRegistry.GetWireId(unit);
        AmmoType? ammo = CoopGhcFiredContext.ResolveAmmoType(__instance);
        uint ammoKey = CoopAmmoKey.FromAmmoType(ammo);
        Transform mz = __instance.MuzzleIdentity;
        Vector3 muzzle = mz != null ? mz.position : unit.transform.position;
        Vector3 direction = mz != null ? mz.forward : unit.transform.forward;
        uint targetNetId = CoopGhcFiredContext.ResolveTargetNetId(unit, target);
        HostCombatBroadcast.TrySendWeaponFired(
            shooterNetId,
            ammoKey,
            muzzle,
            direction,
            targetNetId,
            CoopUdpTransport.CombatReplicationLogFired);
    }
}
