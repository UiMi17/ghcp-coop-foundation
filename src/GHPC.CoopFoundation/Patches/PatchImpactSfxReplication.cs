using GHPC;
using GHPC.Audio;
using GHPC.CoopFoundation.Net;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Phase 4 host: replicate <see cref="ImpactSFXManager" /> SFX hits to client via GHC <see cref="CoopCombatPacket.EventImpactFx" />.</summary>
internal static class ImpactSfxReplicationThrottle
{
    /// <summary>Caps wire rate per category; full local SFX still plays on host.</summary>
    public const float MinIntervalSeconds = 0.05f;

    public static bool TryConsume(ref float nextSendTime)
    {
        float time = Time.time;
        if (time < nextSendTime)
            return false;
        nextSendTime = time + MinIntervalSeconds;
        return true;
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayTerrainImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(bool), typeof(bool))]
internal static class PatchImpactSfxTerrainReplication
{
    private static float _nextSendTime;

    [HarmonyPostfix]
    private static void Postfix(Vector3 t, AmmoType ammoType, bool isTree, bool isSpall)
    {
        if (!CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (!ImpactSfxReplicationThrottle.TryConsume(ref _nextSendTime))
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
        if (ammoKey == 0)
            return;
        byte flags = (byte)((isTree ? CoopCombatPacket.ImpactFxFlagTree : 0) | (isSpall ? CoopCombatPacket.ImpactFxFlagSpallHint : 0));
        HostCombatBroadcast.TrySendImpactFx(
            CoopImpactFxKind.Terrain,
            t,
            Vector3.zero,
            ammoKey,
            0,
            flags,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayRicochetSFX), typeof(Vector3), typeof(AmmoType))]
internal static class PatchImpactSfxRicochetReplication
{
    private static float _nextSendTime;

    [HarmonyPostfix]
    private static void Postfix(Vector3 t, AmmoType ammoType)
    {
        if (!CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (!ImpactSfxReplicationThrottle.TryConsume(ref _nextSendTime))
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
        if (ammoKey == 0)
            return;
        HostCombatBroadcast.TrySendImpactFx(
            CoopImpactFxKind.Ricochet,
            t,
            Vector3.zero,
            ammoKey,
            0,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlaySmallCalImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(float))]
internal static class PatchImpactSfxSmallCalReplication
{
    private static float _nextSendTime;

    [HarmonyPostfix]
    private static void Postfix(Vector3 t, AmmoType ammoType, float armorThickness)
    {
        if (!CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (!ImpactSfxReplicationThrottle.TryConsume(ref _nextSendTime))
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
        if (ammoKey == 0)
            return;
        HostCombatBroadcast.TrySendImpactFx(
            CoopImpactFxKind.ArmorSmallCal,
            t,
            new Vector3(armorThickness, 0f, 0f),
            ammoKey,
            0,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayLargeCalImpactSFX), typeof(Vector3), typeof(AmmoType), typeof(float))]
internal static class PatchImpactSfxLargeCalReplication
{
    private static float _nextSendTime;

    [HarmonyPostfix]
    private static void Postfix(Vector3 t, AmmoType ammoType, float armorThickness)
    {
        if (!CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (!ImpactSfxReplicationThrottle.TryConsume(ref _nextSendTime))
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(ammoType);
        if (ammoKey == 0)
            return;
        HostCombatBroadcast.TrySendImpactFx(
            CoopImpactFxKind.ArmorLargeCal,
            t,
            new Vector3(armorThickness, 0f, 0f),
            ammoKey,
            0,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}

[HarmonyPatch(typeof(ImpactSFXManager), nameof(ImpactSFXManager.PlayImpactPenIntPerspSFX), typeof(Vector3), typeof(float))]
internal static class PatchImpactSfxPenPerspReplication
{
    private static float _nextSendTime;

    [HarmonyPostfix]
    private static void Postfix(Vector3 t, float armorThickness)
    {
        if (!CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (!ImpactSfxReplicationThrottle.TryConsume(ref _nextSendTime))
            return;
        HostCombatBroadcast.TrySendImpactFx(
            CoopImpactFxKind.ArmorPenPerspective,
            t,
            new Vector3(armorThickness, 0f, 0f),
            0,
            0,
            0,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}
