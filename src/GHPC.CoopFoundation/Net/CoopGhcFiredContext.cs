using GHPC;
using GHPC.AI.Interfaces;
using GHPC.Weapons;
using HarmonyLib;

namespace GHPC.CoopFoundation.Net;

/// <summary>Resolves GHC Fired payload fields after <see cref="WeaponSystem.Fire" /> — feed breech is often cleared before Harmony postfix runs.</summary>
internal static class CoopGhcFiredContext
{
    /// <summary>
    /// After a successful <see cref="WeaponSystem.Fire" />, <see cref="AmmoFeed.AmmoTypeInBreech" /> is usually already null:
    /// <c>AmmoFeed.WeaponFired</c> clears the breech inside the weapon's <c>Fired</c> event before <c>Fire</c> returns.
    /// The live round created for this shot still carries <see cref="LiveRound.Info" />.
    /// </summary>
    public static AmmoType? ResolveAmmoType(WeaponSystem weaponSystem)
    {
        if (weaponSystem.Feed != null)
        {
            AmmoType? breech = weaponSystem.Feed.AmmoTypeInBreech;
            if (IsUsableAmmo(breech))
                return breech;
        }

        LiveRound? last = Traverse.Create(weaponSystem).Field<LiveRound>("_lastRound").Value;
        if (IsUsableAmmo(last?.Info))
            return last!.Info;

        if (IsUsableAmmo(weaponSystem.CurrentAmmoType))
            return weaponSystem.CurrentAmmoType;

        return last?.Info ?? weaponSystem.Feed?.AmmoTypeInBreech ?? weaponSystem.CurrentAmmoType;
    }

    /// <summary>Player/AI often calls <c>Fire(null)</c>; use locked target when available.</summary>
    public static uint ResolveTargetNetId(Unit? shooter, IUnit? fireTargetParameter)
    {
        if (fireTargetParameter is Unit tu)
            return CoopUnitWireRegistry.GetWireId(tu);
        Unit? locked = shooter?.InfoBroker?.CurrentTarget?.Owner;
        if (locked != null)
            return CoopUnitWireRegistry.GetWireId(locked);
        return 0;
    }

    private static bool IsUsableAmmo(AmmoType? ammo) =>
        ammo != null && !string.IsNullOrEmpty(ammo.Name);
}
