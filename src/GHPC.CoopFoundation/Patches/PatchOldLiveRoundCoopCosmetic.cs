using GHPC.CoopFoundation.Net;
using GHPC.Effects;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// <see cref="OldLiveRound"/> (e.g. <c>MainGun</c>) does not call <see cref="Explosions.RegisterExplosion"/> or PEM —
/// it instantiates legacy detonation prefabs only. Replicate host blast + particle wire like <see cref="LiveRound"/>.
/// </summary>
[HarmonyPatch(typeof(OldLiveRound), nameof(OldLiveRound.Detonate))]
internal static class PatchOldLiveRoundCoopCosmetic
{
    [HarmonyPostfix]
    private static void PostfixDetonate(OldLiveRound __instance)
    {
        if (!CoopUdpTransport.IsHost || !HostCombatBroadcast.CanEmit || !CoopSessionState.IsPlaying)
            return;

        AmmoType? info = __instance.Info;
        if (info == null || string.IsNullOrEmpty(info.Name) || info.Name == "spall")
            return;

        Vector3 pos = __instance.transform.position;
        var tr = Traverse.Create(__instance);
        bool terrainHit = tr.Field<bool>("_terrainHit").Value;
        bool ric = tr.Field<bool>("_ricochet").Value;
        bool isHeat = tr.Field<bool>("_isHeat").Value;
        bool isHe = tr.Field<bool>("_isHe").Value;

        if (info.TntEquivalentKg > 0f
            && CoopUdpTransport.IsHostExplosionReplicationActive
            && CoopCosmeticInterest.ShouldEmitToPeer(pos)
            && CosmeticExplosionThrottle.TryConsumeGlobal())
        {
            HostCombatBroadcast.TrySendExplosion(
                pos,
                info.TntEquivalentKg,
                0,
                CoopUdpTransport.CombatReplicationLogImpactFx);
        }

        if (!CoopUdpTransport.IsHostParticleImpactReplicationActive)
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(info);
        if (ammoKey == 0)
            return;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(pos) || !CosmeticParticleThrottle.TryConsumeGlobal())
            return;

        Vector3 forward = Vector3.forward;
        if (ric)
            forward = tr.Field<Vector3>("_normalizationVector").Value;
        else if (isHeat)
            forward = __instance.transform.forward;
        else
            forward = tr.Field<Vector3>("_impactNormal").Value;

        byte surf = (byte)(terrainHit
            ? ParticleEffectsManager.SurfaceMaterial.Dirt
            : ParticleEffectsManager.SurfaceMaterial.Steel);
        byte fuse = (byte)(isHe
            ? ParticleEffectsManager.FusedStatus.Fuzed
            : ParticleEffectsManager.FusedStatus.Unfuzed);
        byte cat = (byte)info.ImpactEffectDescriptor.ImpactCategory;
        byte ricType = (byte)info.ImpactEffectDescriptor.RicochetType;
        byte flags = (byte)(ric ? 1 : 0);
        bool simpleFuzed = fuse == (byte)ParticleEffectsManager.FusedStatus.Fuzed;

        HostCombatBroadcast.TrySendParticleImpact(
            ammoKey,
            pos,
            forward,
            surf,
            fuse,
            cat,
            ricType,
            flags,
            (byte)info.ImpactAudio,
            simpleFuzed,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}
