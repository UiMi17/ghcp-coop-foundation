using GHPC;
using GHPC.Player;
using GHPC.Weapons;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CoopMuzzleFxReplay
{
    public static void TryReplay(uint shooterNetId, uint ammoKey, uint weaponNetKey, Vector3 muzzleFallback, Vector3 directionFallback)
    {
        if (!CoopUdpTransport.IsClient || !CoopUdpTransport.MuzzleCosmeticReplayEnabled)
            return;
        if (!CoopSessionState.IsPlaying)
            return;
        Unit? shooter = CoopUnitLookup.TryFindByNetId(shooterNetId);
        if (shooter == null)
            return;
        PlayerInput? input = PlayerInput.Instance;
        Unit? local = input != null ? input.CurrentPlayerUnit : null;
        if (local != null && shooter == local)
            return;
        if (!CoopAmmoResolver.TryResolve(ammoKey, out AmmoType? ammo) || ammo == null)
            return;
        WeaponSystem[] systems = shooter.GetComponentsInChildren<WeaponSystem>();
        WeaponSystem? pick = null;
        if (weaponNetKey != 0)
        {
            foreach (WeaponSystem ws in systems)
            {
                if (CoopWeaponKey.FromWeaponSystem(ws) == weaponNetKey)
                {
                    pick = ws;
                    break;
                }
            }
        }

        if (pick == null)
        {
            foreach (WeaponSystem ws in systems)
            {
                if (ws.Feed != null
                    && ws.Feed.AmmoTypeInBreech != null
                    && CoopAmmoKey.FromAmmoType(ws.Feed.AmmoTypeInBreech) == ammoKey)
                {
                    pick = ws;
                    break;
                }
            }
        }

        if (pick == null && systems.Length > 0)
            pick = systems[0];
        if (pick == null)
            return;

        Transform muzzleTf = pick.MuzzleIdentity;
        Vector3 muzzlePos = muzzleTf != null ? muzzleTf.position : muzzleFallback;
        if (muzzleTf == null && muzzlePos == Vector3.zero)
            muzzlePos = shooter.transform.position;

        if (ammo.NoMuzzleEffects)
            return;

        ParticleSystem[] muzzleFx = pick.MuzzleEffects;
        if (muzzleFx != null)
        {
            foreach (ParticleSystem particleSystem in muzzleFx)
            {
                if (particleSystem == null)
                    continue;
                if (!pick.UsesLoopingEffects || !particleSystem.isPlaying)
                    particleSystem.Play();
            }
        }

        GameObject[] prefabs = pick.MuzzleEffectPrefabs;
        if (prefabs != null && muzzleTf != null)
        {
            Quaternion rot = muzzleTf.rotation;
            for (int j = 0; j < prefabs.Length; j++)
            {
                if (prefabs[j] != null)
                    Object.Instantiate(prefabs[j], muzzlePos, rot);
            }
        }

        _ = directionFallback;
    }
}
