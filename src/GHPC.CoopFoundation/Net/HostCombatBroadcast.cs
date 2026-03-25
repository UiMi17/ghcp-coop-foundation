using System;
using System.Collections.Generic;
using GHPC;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Host-only: pack GHC (v1 wire: Fired, Struck, ImpactFx) and send to peer.</summary>
internal static class HostCombatBroadcast
{
    private static byte[]? _buffer;
    private static readonly Dictionary<uint, CoopDamageStateSnapshot> LastDamageStateByNetId = new();
    private static readonly Dictionary<uint, float> NextDamageStateSendTimeByNetId = new();

    private const float DamageStateMinIntervalSeconds = 0.1f;

    public static bool CanEmit =>
        CoopUdpTransport.IsHostCombatReplicationActive;

    public static void TrySendWeaponFired(
        uint shooterNetId,
        uint ammoKey,
        Vector3 muzzle,
        Vector3 direction,
        uint targetNetId,
        bool logFired)
    {
        if (!CanEmit)
            return;
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteFired(
            _buffer!,
            seq,
            token,
            phase,
            shooterNetId,
            ammoKey,
            muzzle,
            direction,
            targetNetId);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logFired)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send Fired seq={seq} shooter={shooterNetId} ammoKey={ammoKey} target={targetNetId}");
        }
    }

    public static void TrySendUnitStruck(
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        bool isSpall,
        bool logStruckPerHit)
    {
        if (!CanEmit)
            return;
        if (victimNetId == 0)
            return;
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteStruck(
            _buffer!,
            seq,
            token,
            phase,
            victimNetId,
            shooterNetId,
            ammoKey,
            impact,
            isSpall);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logStruckPerHit)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send Struck seq={seq} victim={victimNetId} shooter={shooterNetId} ammoKey={ammoKey} spall={isSpall}");
        }
    }

    public static void TrySendImpactFx(
        ushort effectKind,
        Vector3 worldPos,
        Vector3 normal,
        uint ammoKey,
        uint victimNetId,
        byte flags,
        bool logImpactFx)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostImpactFxReplicationActive)
            return;
        if (ammoKey == 0 && effectKind != CoopImpactFxKind.ArmorPenPerspective)
            return;
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteImpactFx(
            _buffer!,
            seq,
            token,
            phase,
            effectKind,
            worldPos,
            normal,
            ammoKey,
            victimNetId,
            flags);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logImpactFx)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send ImpactFx seq={seq} kind={effectKind} ammoKey={ammoKey} victim={victimNetId} flags={flags}");
        }
    }

    public static void TrySendDamageState(Unit victim, bool force, bool logDamageState)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostDamageStateReplicationActive)
            return;
        if (victim == null)
            return;
        uint victimNetId = CoopUnitWireRegistry.GetWireId(victim);
        if (victimNetId == 0)
            return;
        if (!CoopDamageStateSnapshot.TryCapture(victim, out CoopDamageStateSnapshot snap))
            return;

        float now = Time.time;
        bool hasNext = NextDamageStateSendTimeByNetId.TryGetValue(victimNetId, out float nextTime);
        bool hasLast = LastDamageStateByNetId.TryGetValue(victimNetId, out CoopDamageStateSnapshot last);
        bool changed = !hasLast || !snap.NearlyEquals(last);
        if (!changed)
            return;
        if (!force && hasNext && now < nextTime)
            return;

        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteDamageState(
            _buffer!,
            seq,
            token,
            phase,
            victimNetId,
            snap);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len))
        {
            LastDamageStateByNetId[victimNetId] = snap;
            NextDamageStateSendTimeByNetId[victimNetId] = now + DamageStateMinIntervalSeconds;
            if (logDamageState)
            {
                MelonLogger.Msg(
                    $"[CoopNet] GHC send DamageState seq={seq} victim={victimNetId} destroyed={snap.UnitDestroyed} e/t/r/l/r={snap.EngineHpPct}/{snap.TransmissionHpPct}/{snap.RadiatorHpPct}/{snap.LeftTrackHpPct}/{snap.RightTrackHpPct}");
            }
        }
    }

    public static void ResetSession()
    {
        LastDamageStateByNetId.Clear();
        NextDamageStateSendTimeByNetId.Clear();
    }

    private static void EnsureBuffer()
    {
        int need = Math.Max(
            CoopCombatPacket.FiredTotalLength,
            Math.Max(CoopCombatPacket.StruckTotalLength, Math.Max(CoopCombatPacket.ImpactFxTotalLength, CoopCombatPacket.DamageStateTotalLength)));
        if (_buffer == null || _buffer.Length < need)
            _buffer = new byte[need];
    }
}
