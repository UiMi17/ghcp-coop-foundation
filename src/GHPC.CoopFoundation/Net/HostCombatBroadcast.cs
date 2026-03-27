using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Weaponry;
using GHPC.Weapons;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Host-only: pack GHC (v1 wire: Fired, Struck, ImpactFx) and send to peer.</summary>
internal static class HostCombatBroadcast
{
    private static byte[]? _buffer;
    private static readonly Dictionary<uint, CoopDamageStateSnapshot> LastDamageStateByNetId = new();
    private static readonly Dictionary<uint, CoopUnitStateSnapshot> LastUnitStateByNetId = new();
    private static readonly Dictionary<uint, CoopCrewStateSnapshot> LastCrewStateByNetId = new();
    private static readonly Dictionary<uint, CoopCompartmentStateSnapshot> LastCompartmentStateByNetId = new();
    private static readonly Dictionary<uint, float> NextDamageStateSendTimeByNetId = new();
    private static readonly Dictionary<uint, float> NextUnitStateSendTimeByNetId = new();
    private static readonly Dictionary<uint, float> NextCrewStateSendTimeByNetId = new();
    private static readonly Dictionary<uint, float> NextCompartmentStateSendTimeByNetId = new();

    private const float DamageStateMinIntervalSeconds = 0.1f;
    private const float UnitStateMinIntervalSeconds = 0.05f;
    private const float CrewStateMinIntervalSeconds = 0.1f;
    private const float CompartmentStateMinIntervalSeconds = 0.2f;
    private const int DestroyedStateRedundantSends = 3;
    private static float _hitResolvedWindowStart = float.NegativeInfinity;
    private static int _hitResolvedSentInWindow;

    private readonly struct HitResolvedPending
    {
        public readonly uint ShooterNetId;
        public readonly uint AmmoKey;
        public readonly Vector3 Impact;
        public readonly bool IsSpall;
        public readonly bool Log;

        public HitResolvedPending(uint shooterNetId, uint ammoKey, Vector3 impact, bool isSpall, bool log)
        {
            ShooterNetId = shooterNetId;
            AmmoKey = ammoKey;
            Impact = impact;
            IsSpall = isSpall;
            Log = log;
        }
    }

    private static readonly Dictionary<uint, HitResolvedPending> HitResolvedPendingByVictim = new();

    public static bool CanEmit =>
        CoopUdpTransport.IsHostCombatReplicationActive;

    public static void TrySendWeaponFired(
        uint shooterNetId,
        uint ammoKey,
        Vector3 muzzle,
        Vector3 direction,
        uint targetNetId,
        uint weaponNetKey,
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
            targetNetId,
            weaponNetKey);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logFired)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send Fired seq={seq} shooter={shooterNetId} ammoKey={ammoKey} target={targetNetId} weaponKey={weaponNetKey}");
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

    public static void TrySendParticleImpact(
        uint ammoKey,
        Vector3 worldPos,
        Vector3 forward,
        byte surfaceMaterial,
        byte fusedStatus,
        byte category,
        byte ricochetType,
        byte flags,
        byte impactAudioType,
        bool simpleAudioFuzed,
        bool logCosmetic)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostParticleImpactReplicationActive)
            return;
        if (ammoKey == 0)
            return;
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteParticleImpact(
            _buffer!,
            seq,
            token,
            phase,
            ammoKey,
            worldPos,
            forward,
            surfaceMaterial,
            fusedStatus,
            category,
            ricochetType,
            flags,
            impactAudioType,
            simpleAudioFuzed);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logCosmetic)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send ParticleImpact seq={seq} ammoKey={ammoKey} surf={surfaceMaterial} fuse={fusedStatus}");
        }
    }

    public static void TrySendExplosion(Vector3 worldPos, float tntKg, byte flags, bool logCosmetic)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostExplosionReplicationActive)
            return;
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteExplosion(_buffer!, seq, token, phase, worldPos, tntKg, flags);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logCosmetic)
        {
            MelonLogger.Msg($"[CoopNet] GHC send Explosion seq={seq} tnt={tntKg:F2} flags={flags}");
        }
    }

    public static void TrySendGrenadeJetVisual(
        Grenade grenade,
        LiveRound liveRound,
        bool logCosmetic)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostAtGrenadeJetVisualActive)
            return;
        if (grenade == null || liveRound == null || liveRound.Info == null)
            return;
        Unit? owner = grenade.Owner;
        if (owner == null)
            return;
        uint shooterNetId = CoopUnitWireRegistry.GetWireId(owner);
        if (shooterNetId == 0)
            return;
        uint ammoKey = CoopAmmoKey.FromAmmoType(liveRound.Info);
        if (ammoKey == 0)
            return;
        Vector3 pos = liveRound.transform.position;
        if (!CoopCosmeticInterest.ShouldEmitToPeer(pos))
            return;
        Vector3 vel = liveRound.transform.forward * liveRound.CurrentSpeed;
        bool useGravity = liveRound.UseGravity;
        float speed = liveRound.CurrentSpeed;
        float gy = Mathf.Abs(Physics.gravity.y);
        float estLife = useGravity && gy > 0.01f
            ? Mathf.Clamp(2.2f * speed / gy, 1f, 12f)
            : Mathf.Clamp(speed / 80f + 1.5f, 2f, 10f);
        byte maxLifeDs = (byte)Mathf.Clamp(Mathf.RoundToInt(estLife * 10f), 10, 120);
        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int len = CoopCombatPacket.WriteGrenadeJetVisual(
            _buffer!,
            seq,
            token,
            phase,
            ammoKey,
            shooterNetId,
            pos,
            vel,
            useGravity,
            maxLifeDs);
        if (CoopUdpTransport.TryHostSendCombat(_buffer!, len) && logCosmetic)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send GrenadeJetVisual seq={seq} shooter={shooterNetId} ammoKey={ammoKey} grav={useGravity} lifeDs={maxLifeDs}");
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
        bool destroyedEdge = snap.UnitDestroyed && (!hasLast || !last.UnitDestroyed);
        if (hasNext && now < nextTime && !destroyedEdge)
            return;

        EnsureBuffer();
        uint token = CoopSessionState.MissionCoherenceToken;
        byte phase = CoopSessionState.MissionStateToWirePhase();
        int sends = snap.UnitDestroyed ? DestroyedStateRedundantSends : 1;
        bool anySent = false;
        uint firstSeq = 0;
        uint lastSeq = 0;
        for (int i = 0; i < sends; i++)
        {
            uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
            if (i == 0)
                firstSeq = seq;
            lastSeq = seq;
            int len = CoopCombatPacket.WriteDamageState(
                _buffer!,
                seq,
                token,
                phase,
                victimNetId,
                snap);
            if (CoopUdpTransport.TryHostSendCombat(_buffer!, len))
            {
                anySent = true;
            }
        }

        if (!anySent)
            return;
        LastDamageStateByNetId[victimNetId] = snap;
        NextDamageStateSendTimeByNetId[victimNetId] = now + DamageStateMinIntervalSeconds;
        if (logDamageState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send DamageState seq={firstSeq}{(lastSeq != firstSeq ? $"..{lastSeq}" : string.Empty)} victim={victimNetId} destroyed={snap.UnitDestroyed} sends={sends} e/t/r/l/r={snap.EngineHpPct}/{snap.TransmissionHpPct}/{snap.RadiatorHpPct}/{snap.LeftTrackHpPct}/{snap.RightTrackHpPct}");
        }
    }

    public static void TrySendUnitState(Unit unit, bool force, bool logState)
    {
        if (!CanEmit || unit == null)
            return;
        uint unitNetId = CoopUnitWireRegistry.GetWireId(unit);
        if (unitNetId == 0)
            return;
        if (!CoopUnitStateSnapshot.TryCapture(unit, out CoopUnitStateSnapshot snap))
            return;
        float now = Time.time;
        bool hasNext = NextUnitStateSendTimeByNetId.TryGetValue(unitNetId, out float nextTime);
        bool hasLast = LastUnitStateByNetId.TryGetValue(unitNetId, out CoopUnitStateSnapshot last);
        bool changed = !hasLast || !snap.NearlyEquals(last);
        if (!changed)
            return;
        if (hasNext && now < nextTime)
            return;

        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        int len = CoopCombatPacket.WriteUnitState(
            _buffer!,
            seq,
            CoopSessionState.MissionCoherenceToken,
            CoopSessionState.MissionStateToWirePhase(),
            unitNetId,
            snap);
        if (!CoopUdpTransport.TryHostSendCombat(_buffer!, len))
            return;
        LastUnitStateByNetId[unitNetId] = snap;
        NextUnitStateSendTimeByNetId[unitNetId] = now + UnitStateMinIntervalSeconds;
        if (logState)
            MelonLogger.Msg($"[CoopNet] GHC send UnitState seq={seq} unit={unitNetId} flags={snap.Flags}");
    }

    public static void TrySendCrewState(Unit unit, bool force, bool logState)
    {
        if (!CanEmit || unit == null)
            return;
        uint unitNetId = CoopUnitWireRegistry.GetWireId(unit);
        if (unitNetId == 0)
            return;
        if (!CoopCrewStateSnapshot.TryCapture(unit, out CoopCrewStateSnapshot snap))
            return;
        float now = Time.time;
        bool hasNext = NextCrewStateSendTimeByNetId.TryGetValue(unitNetId, out float nextTime);
        bool hasLast = LastCrewStateByNetId.TryGetValue(unitNetId, out CoopCrewStateSnapshot last);
        bool changed = !hasLast || !snap.NearlyEquals(last);
        if (!changed)
            return;
        if (hasNext && now < nextTime)
            return;

        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        int len = CoopCombatPacket.WriteCrewState(
            _buffer!,
            seq,
            CoopSessionState.MissionCoherenceToken,
            CoopSessionState.MissionStateToWirePhase(),
            unitNetId,
            snap);
        if (!CoopUdpTransport.TryHostSendCombat(_buffer!, len))
            return;
        LastCrewStateByNetId[unitNetId] = snap;
        NextCrewStateSendTimeByNetId[unitNetId] = now + CrewStateMinIntervalSeconds;
        if (logState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send CrewState seq={seq} unit={unitNetId} present/dead/incap/evac/susp={snap.PresentMask}/{snap.DeadMask}/{snap.IncapacitatedMask}/{snap.EvacuatedMask}/{snap.SuspendedMask}");
        }
    }

    /// <summary>Queue one HitResolved (latest wins per <paramref name="victimNetId" /> until <see cref="FlushPendingHitResolved" />).</summary>
    public static void TrySendHitResolved(
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        bool isSpall,
        bool logEvent)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostHitResolvedReplicationActive || victimNetId == 0)
            return;
        bool log = logEvent;
        if (HitResolvedPendingByVictim.TryGetValue(victimNetId, out HitResolvedPending prev))
            log |= prev.Log;
        HitResolvedPendingByVictim[victimNetId] = new HitResolvedPending(
            shooterNetId,
            ammoKey,
            impact,
            isSpall,
            log);
    }

    /// <summary>Host: drain coalesced HitResolved queue once per frame (LateUpdate). Spreads UDP and respects per-second budget.</summary>
    public static void FlushPendingHitResolved(bool logDamageState)
    {
        if (!CanEmit || !CoopUdpTransport.IsHostHitResolvedReplicationActive || HitResolvedPendingByVictim.Count == 0)
            return;

        int maxPerFrame = CoopUdpTransport.HitResolvedHostMaxPerFrame;
        var keys = new List<uint>(HitResolvedPendingByVictim.Keys);
        keys.Sort();
        int sent = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (maxPerFrame > 0 && sent >= maxPerFrame)
                break;
            if (!CanSendHitResolvedThisSecond())
                break;
            uint victimNetId = keys[i];
            if (!HitResolvedPendingByVictim.TryGetValue(victimNetId, out HitResolvedPending p))
                continue;

            EnsureBuffer();
            uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
            int len = CoopCombatPacket.WriteHitResolved(
                _buffer!,
                seq,
                CoopSessionState.MissionCoherenceToken,
                CoopSessionState.MissionStateToWirePhase(),
                seq,
                victimNetId,
                p.ShooterNetId,
                p.AmmoKey,
                p.Impact,
                p.IsSpall ? (byte)2 : (byte)1,
                p.IsSpall ? (byte)1 : (byte)0);
            if (!CoopUdpTransport.TryHostSendCombat(_buffer!, len))
                continue;
            NoteHitResolvedSent();
            HitResolvedPendingByVictim.Remove(victimNetId);
            sent++;
            if (logDamageState && p.Log)
            {
                MelonLogger.Msg(
                    $"[CoopNet] GHC send HitResolved seq={seq} victim={victimNetId} shooter={p.ShooterNetId} ammoKey={p.AmmoKey} spall={p.IsSpall}");
            }
        }
    }

    private static bool CanSendHitResolvedThisSecond()
    {
        int maxPerSecond = CoopUdpTransport.HitResolvedMaxPerSecond;
        if (maxPerSecond <= 0)
            return true;
        float now = Time.time;
        if (float.IsNegativeInfinity(_hitResolvedWindowStart) || now - _hitResolvedWindowStart >= 1f)
        {
            _hitResolvedWindowStart = now;
            _hitResolvedSentInWindow = 0;
        }

        return _hitResolvedSentInWindow < maxPerSecond;
    }

    private static void NoteHitResolvedSent()
    {
        if (CoopUdpTransport.HitResolvedMaxPerSecond > 0)
            _hitResolvedSentInWindow++;
    }

    public static void TrySendCompartmentState(Unit unit, bool force, bool logState)
    {
        if (!CanEmit || unit == null)
            return;
        uint unitNetId = CoopUnitWireRegistry.GetWireId(unit);
        if (unitNetId == 0)
            return;
        if (!CoopCompartmentStateSnapshot.TryCapture(unit, out CoopCompartmentStateSnapshot snap))
            return;
        float now = Time.time;
        bool hasNext = NextCompartmentStateSendTimeByNetId.TryGetValue(unitNetId, out float nextTime);
        bool hasLast = LastCompartmentStateByNetId.TryGetValue(unitNetId, out CoopCompartmentStateSnapshot last);
        bool changed = !hasLast || !snap.NearlyEquals(last);
        if (!changed && !force)
            return;
        bool fireStarted = snap.FirePresent && (!hasLast || !last.FirePresent);
        if (hasNext && now < nextTime && !fireStarted)
            return;

        EnsureBuffer();
        uint seq = CoopUdpTransport.TakeNextHostCombatSeq();
        int len = CoopCombatPacket.WriteCompartmentState(
            _buffer!,
            seq,
            CoopSessionState.MissionCoherenceToken,
            CoopSessionState.MissionStateToWirePhase(),
            unitNetId,
            snap);
        if (!CoopUdpTransport.TryHostSendCombat(_buffer!, len))
            return;
        LastCompartmentStateByNetId[unitNetId] = snap;
        NextCompartmentStateSendTimeByNetId[unitNetId] = now + CompartmentStateMinIntervalSeconds;
        if (logState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC send CompartmentState seq={seq} unit={unitNetId} fire={snap.FirePresent} unsecured={snap.UnsecuredFirePresent} flamePct={snap.CombinedFlameHeightPct} tempPct={snap.InternalTemperaturePct} scorch={snap.ScorchPct} smokeCol={snap.SmokeColumnPct}");
        }
    }

    public static void ResetSession()
    {
        LastDamageStateByNetId.Clear();
        LastUnitStateByNetId.Clear();
        LastCrewStateByNetId.Clear();
        LastCompartmentStateByNetId.Clear();
        NextDamageStateSendTimeByNetId.Clear();
        NextUnitStateSendTimeByNetId.Clear();
        NextCrewStateSendTimeByNetId.Clear();
        NextCompartmentStateSendTimeByNetId.Clear();
        _hitResolvedWindowStart = float.NegativeInfinity;
        _hitResolvedSentInWindow = 0;
        HitResolvedPendingByVictim.Clear();
    }

    private static void EnsureBuffer()
    {
        int need = 0;
        int[] lengths =
        {
            CoopCombatPacket.FiredTotalLength,
            CoopCombatPacket.StruckTotalLength,
            CoopCombatPacket.ImpactFxTotalLength,
            CoopCombatPacket.DamageStateTotalLength,
            CoopCombatPacket.UnitStateTotalLength,
            CoopCombatPacket.CrewStateTotalLength,
            CoopCombatPacket.HitResolvedTotalLength,
            CoopCombatPacket.CompartmentStateTotalLength,
            CoopCombatPacket.ParticleImpactTotalLength,
            CoopCombatPacket.ExplosionTotalLength,
            CoopCombatPacket.GrenadeJetVisualTotalLength
        };
        foreach (int len in lengths)
            need = Math.Max(need, len);
        if (_buffer == null || _buffer.Length < need)
            _buffer = new byte[need];
    }
}
