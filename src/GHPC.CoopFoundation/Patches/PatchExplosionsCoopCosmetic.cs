using GHPC.CoopFoundation.Net;
using GHPC.Effects;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(Explosions), nameof(Explosions.RegisterExplosion), typeof(Vector3), typeof(float))]
internal static class PatchExplosionsCoopCosmetic
{
    [HarmonyPrefix]
    private static bool PrefixRegister(Vector3 worldPosition, float tntEquivalent)
    {
        _ = worldPosition;
        _ = tntEquivalent;
        if (!CoopUdpTransport.IsClient)
            return true;
        return !CoopClientFxSuppression.ShouldSuppressLiveRoundCosmetics(CoopClientFxSuppression.CurrentLiveRoundInDoUpdate);
    }

    [HarmonyPostfix]
    private static void PostfixRegister(Vector3 worldPosition, float tntEquivalent)
    {
        if (tntEquivalent <= 0f)
            return;
        if (!CoopUdpTransport.IsHost || !HostCombatBroadcast.CanEmit || !CoopUdpTransport.IsHostExplosionReplicationActive)
            return;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(worldPosition))
        {
            CoopCosmeticHealthCounters.RecordExplosionDroppedInterest();
            return;
        }

        if (!CosmeticExplosionThrottle.TryConsumeGlobal())
        {
            CoopCosmeticHealthCounters.RecordExplosionDroppedThrottle();
            return;
        }

        HostCombatBroadcast.TrySendExplosion(
            worldPosition,
            tntEquivalent,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}
