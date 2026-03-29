using GHPC.Weaponry;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// <see cref="BasicGrenadeExplosionBehaviour"/> only spawns prefabs + FMOD — no <see cref="GHPC.Effects.Explosions.RegisterExplosion"/>.
/// AT grenades spawn <see cref="GHPC.Weapons.LiveRound"/> (handled elsewhere); smoke / frag get lightweight GHC <c>Explosion</c> rows.
/// </summary>
[HarmonyPatch(typeof(BasicGrenadeExplosionBehaviour), nameof(BasicGrenadeExplosionBehaviour.Explode))]
internal static class PatchBasicGrenadeExplosionCoopCosmetic
{
    [HarmonyPostfix]
    private static void PostfixExplode(Grenade grenade, string soundPath, GameObject effectPrefab)
    {
        _ = soundPath;
        _ = effectPrefab;
        if (!CoopUdpTransport.IsHost || !HostCombatBroadcast.CanEmit || !CoopSessionState.IsPlaying)
            return;
        if (grenade == null)
            return;
        if (grenade is AntiTankGrenade)
            return;

        Vector3 pos = grenade.transform.position;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(pos))
        {
            CoopCosmeticHealthCounters.RecordExplosionDroppedInterest();
            return;
        }

        if (!CosmeticExplosionThrottle.TryConsumeGlobal())
        {
            CoopCosmeticHealthCounters.RecordExplosionDroppedThrottle();
            return;
        }

        if (!CoopUdpTransport.IsHostExplosionReplicationActive)
            return;

        if (grenade is SmokeGrenade)
        {
            HostCombatBroadcast.TrySendExplosion(
                pos,
                0f,
                CoopCombatPacket.ExplosionFlagGrenadeSmoke,
                CoopUdpTransport.CombatReplicationLogImpactFx);
            return;
        }

        float tntKg = CoopFragGrenadeCosmeticTnt.ResolveTntKg(
            grenade,
            CoopUdpTransport.FragGrenadeCosmeticTntUseApRadius,
            CoopUdpTransport.FragGrenadeCosmeticTntFallbackKg);
        HostCombatBroadcast.TrySendExplosion(
            pos,
            tntKg,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}
