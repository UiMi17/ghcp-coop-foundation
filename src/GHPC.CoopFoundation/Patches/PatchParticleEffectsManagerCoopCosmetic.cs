using GHPC.CoopFoundation.Net;
using GHPC.Effects;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(
    typeof(ParticleEffectsManager),
    nameof(ParticleEffectsManager.CreateImpactEffectOfType),
    typeof(AmmoType),
    typeof(ParticleEffectsManager.FusedStatus),
    typeof(ParticleEffectsManager.SurfaceMaterial),
    typeof(bool),
    typeof(Vector3),
    typeof(Transform))]
internal static class PatchParticleEffectsManagerCoopCosmetic
{
    private const byte FlagIsRicochet = 1;

    [HarmonyPrefix]
    private static bool PrefixCreateImpact(
        ref GameObject __result,
        AmmoType ammoType,
        ParticleEffectsManager.FusedStatus fusedStatus,
        ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
        bool isRicochet,
        Vector3 worldPosition,
        Transform parent)
    {
        _ = parent;
        if (!CoopUdpTransport.IsClient
            || !CoopClientFxSuppression.ShouldSuppressLiveRoundCosmetics(CoopClientFxSuppression.CurrentLiveRoundInDoUpdate))
        {
            return true;
        }

        __result = null!;
        return false;
    }

    [HarmonyPostfix]
    private static void PostfixCreateImpact(
        GameObject __result,
        AmmoType ammoType,
        ParticleEffectsManager.FusedStatus fusedStatus,
        ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
        bool isRicochet,
        Vector3 worldPosition,
        Transform parent)
    {
        _ = __result;
        _ = parent;
        if (!CoopUdpTransport.IsHost || !HostCombatBroadcast.CanEmit || !CoopUdpTransport.IsHostParticleImpactReplicationActive)
            return;
        if (ammoType == null || ammoType.Name == "spall")
            return;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(worldPosition))
        {
            CoopCosmeticHealthCounters.RecordParticleDroppedInterest();
            return;
        }

        if (!CosmeticParticleThrottle.TryConsumeGlobal())
        {
            CoopCosmeticHealthCounters.RecordParticleDroppedThrottle();
            return;
        }

        uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
        if (ammoKey == 0)
            return;

        LiveRound? lr = CoopClientFxSuppression.CurrentLiveRoundInDoUpdate;
        Vector3 forward = ResolveForward(lr, isRicochet);
        byte flags = (byte)(isRicochet ? FlagIsRicochet : 0);
        byte cat = (byte)ammoType.ImpactEffectDescriptor.ImpactCategory;
        byte ric = (byte)ammoType.ImpactEffectDescriptor.RicochetType;
        bool simpleFuzed = fusedStatus == ParticleEffectsManager.FusedStatus.Fuzed;
        HostCombatBroadcast.TrySendParticleImpact(
            ammoKey,
            worldPosition,
            forward,
            (byte)surfaceMaterial,
            (byte)fusedStatus,
            cat,
            ric,
            flags,
            (byte)ammoType.ImpactAudio,
            simpleFuzed,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }

    private static Vector3 ResolveForward(LiveRound? lr, bool isRicochet)
    {
        if (lr == null)
            return Vector3.forward;
        Traverse tr = Traverse.Create(lr);
        if (isRicochet)
            return tr.Field<Vector3>("_normalizationVector").Value;
        if (tr.Field<bool>("_isHeat").Value)
            return lr.transform.forward;
        return tr.Field<Vector3>("_impactNormal").Value;
    }
}
