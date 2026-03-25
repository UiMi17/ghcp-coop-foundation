using System;
using System.Collections.Generic;
using System.Diagnostics;
using GHPC;
using GHPC.AI.Interfaces;
using GHPC.Audio;
using GHPC.Effects;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Client: queue GHC from host, apply on main thread with per-frame budget (order preserved).</summary>
internal static class ClientCombatApplier
{
    private static readonly Queue<PendingCombatPacket> PendingHigh = new();
    private static readonly Queue<PendingCombatPacket> PendingLow = new();
    private static readonly HashSet<uint> CoalescedSeq = new();

    private static uint _lastCombatSeq;
    private static uint _seqGapCount;
    private static uint _maxSeqGap;
    private static uint _recvAcceptedCount;
    private static uint _struckApplyFailCount;
    private static uint _impactFxApplyFailCount;
    private static uint _damageStateApplyFailCount;
    private static uint _damageStateRecvCount;
    private static uint _damageStateApplyCount;
    private static uint _unitStateRecvCount;
    private static uint _unitStateApplyCount;
    private static uint _crewStateRecvCount;
    private static uint _crewStateApplyCount;
    private static uint _hitResolvedRecvCount;
    private static uint _compartmentStateRecvCount;
    private static uint _particleImpactRecvCount;
    private static uint _particleImpactApplyFailCount;
    private static uint _explosionRecvCount;
    private static readonly Dictionary<uint, bool> LastCompartmentFirePresentByUnitNetId = new();
    private static uint _damageStateCoalescedCount;
    private static uint _struckCoalescedCount;
    private static uint _damageStateDestroyParityMismatchCount;
    private static uint _budgetHitCount;
    private static float _nextSeqGapLogTime = float.NegativeInfinity;
    private static int _maxPendingDepth;
    private static float _nextQueuePressureLogTime = float.NegativeInfinity;
    private static float _nextBudgetHitLogTime = float.NegativeInfinity;

    private const int QueuePressureWarnThreshold = 128;
    private const int QueuePressureCriticalThreshold = 256;
    private const float HealthLogCooldownSeconds = 2f;

    private struct PendingCombatPacket
    {
        public byte[] Data;

        public int Length;
    }

    /// <summary>While true, host struck Harmony postfix must not re-broadcast to UDP.</summary>
    public static bool SuppressStruckBroadcast { get; private set; }

    public static int PendingCombatCount => PendingHigh.Count + PendingLow.Count;

    public static void ResetSession()
    {
        LogSessionSummaryIfAny();
        _lastCombatSeq = 0;
        _seqGapCount = 0;
        _maxSeqGap = 0;
        _recvAcceptedCount = 0;
        _struckApplyFailCount = 0;
        _impactFxApplyFailCount = 0;
        _damageStateApplyFailCount = 0;
        _damageStateRecvCount = 0;
        _damageStateApplyCount = 0;
        _unitStateRecvCount = 0;
        _unitStateApplyCount = 0;
        _crewStateRecvCount = 0;
        _crewStateApplyCount = 0;
        _hitResolvedRecvCount = 0;
        _compartmentStateRecvCount = 0;
        _particleImpactRecvCount = 0;
        _particleImpactApplyFailCount = 0;
        _explosionRecvCount = 0;
        LastCompartmentFirePresentByUnitNetId.Clear();
        CoopCompartmentFxReplay.ClearRemoteSmokeColumns();
        CoopRemoteFlammablesBurningAudio.ClearAll();
        CoopAtJetVisualReplay.ClearAll();
        _damageStateCoalescedCount = 0;
        _struckCoalescedCount = 0;
        _damageStateDestroyParityMismatchCount = 0;
        _budgetHitCount = 0;
        _maxPendingDepth = 0;
        _nextSeqGapLogTime = float.NegativeInfinity;
        _nextQueuePressureLogTime = float.NegativeInfinity;
        _nextBudgetHitLogTime = float.NegativeInfinity;
        PendingHigh.Clear();
        PendingLow.Clear();
        CoalescedSeq.Clear();
    }

    /// <summary>Drop queued GHC without resetting seq (e.g. combat replication pref off).</summary>
    public static void ClearPendingQueueOnly()
    {
        PendingHigh.Clear();
        PendingLow.Clear();
        CoalescedSeq.Clear();
    }

    /// <summary>Enqueue one GHC datagram; must run on main thread (after UDP dequeue). Drops if not Playing.</summary>
    public static void EnqueuePendingCombat(byte[] data, int length)
    {
        if (data == null || length < CoopCombatPacket.MinCombatDatagramLength)
            return;
        if (!CoopSessionState.IsPlaying)
            return;
        if (!CoopCombatPacket.IsCoopCombat(data, length))
            return;
        if (CoopCombatPacket.TryRead(data, length, out byte eventType, out uint eventSeq, out _, out _)
            && eventType == CoopCombatPacket.EventHitResolved)
        {
            // HitResolved is low-priority telemetry; mark seq as locally handled to avoid false network-gap noise.
            CoalescedSeq.Add(eventSeq);
            TryCoalesceHitResolvedLow(ref data, length);
            PendingLow.Enqueue(new PendingCombatPacket { Data = data, Length = length });
            if (PendingCombatCount > _maxPendingDepth)
                _maxPendingDepth = PendingCombatCount;
            return;
        }
        if (TryCoalesceUnitState(ref data, length))
            return;
        if (TryCoalesceCrewState(ref data, length))
            return;
        if (TryCoalesceStruck(ref data, length))
            return;
        if (TryCoalesceDamageState(ref data, length))
            return;
        PendingHigh.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (PendingCombatCount > _maxPendingDepth)
            _maxPendingDepth = PendingCombatCount;
    }

    private static bool TryCoalesceStruck(ref byte[] data, int length)
    {
        if (!CoopCombatPacket.TryRead(data, length, out byte eventType, out uint incomingSeq, out _, out _))
            return false;
        if (eventType != CoopCombatPacket.EventUnitStruck)
            return false;
        if (!CoopCombatPacket.TryReadStruck(
                data,
                length,
                out _,
                out _,
                out _,
                out uint incomingVictim,
                out _,
                out _,
                out _,
                out _))
        {
            return false;
        }

        int count = PendingHigh.Count;
        if (count == 0)
            return false;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        bool removedAny = false;
        while (PendingHigh.Count > 0)
        {
            PendingCombatPacket p = PendingHigh.Dequeue();
            if (!CoopCombatPacket.TryRead(p.Data, p.Length, out byte queuedType, out uint queuedSeq, out _, out _)
                || queuedType != CoopCombatPacket.EventUnitStruck)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            if (!CoopCombatPacket.TryReadStruck(
                    p.Data,
                    p.Length,
                    out _,
                    out _,
                    out _,
                    out uint queuedVictim,
                    out _,
                    out _,
                    out _,
                    out _)
                || queuedVictim != incomingVictim)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            removedAny = true;
            CoalescedSeq.Add(queuedSeq);
            _struckCoalescedCount++;
        }

        while (rebuilt.Count > 0)
            PendingHigh.Enqueue(rebuilt.Dequeue());

        PendingHigh.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (PendingCombatCount > _maxPendingDepth)
            _maxPendingDepth = PendingCombatCount;
        if (removedAny)
            CoalescedSeq.Remove(incomingSeq);
        return true;
    }

    private static bool TryCoalesceDamageState(ref byte[] data, int length)
    {
        if (!CoopCombatPacket.TryRead(data, length, out byte eventType, out uint incomingSeq, out _, out _))
            return false;
        if (eventType != CoopCombatPacket.EventDamageState)
            return false;
        if (!CoopCombatPacket.TryReadDamageState(
                data,
                length,
                out _,
                out _,
                out _,
                out uint incomingVictim,
                out _))
        {
            return false;
        }

        int count = PendingHigh.Count;
        if (count == 0)
            return false;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        bool removedAny = false;
        while (PendingHigh.Count > 0)
        {
            PendingCombatPacket p = PendingHigh.Dequeue();
            if (!CoopCombatPacket.TryRead(p.Data, p.Length, out byte queuedType, out uint queuedSeq, out _, out _)
                || queuedType != CoopCombatPacket.EventDamageState)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            if (!CoopCombatPacket.TryReadDamageState(
                    p.Data,
                    p.Length,
                    out _,
                    out _,
                    out _,
                    out uint queuedVictim,
                    out _)
                || queuedVictim != incomingVictim)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            removedAny = true;
            CoalescedSeq.Add(queuedSeq);
            _damageStateCoalescedCount++;
        }

        while (rebuilt.Count > 0)
            PendingHigh.Enqueue(rebuilt.Dequeue());

        PendingHigh.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (PendingCombatCount > _maxPendingDepth)
            _maxPendingDepth = PendingCombatCount;
        if (removedAny)
            CoalescedSeq.Remove(incomingSeq);
        return true;
    }

    private static bool TryCoalesceUnitState(ref byte[] data, int length)
    {
        if (!CoopCombatPacket.TryRead(data, length, out byte eventType, out uint incomingSeq, out _, out _))
            return false;
        if (eventType != CoopCombatPacket.EventUnitState)
            return false;
        if (!CoopCombatPacket.TryReadUnitState(data, length, out _, out _, out _, out uint incomingUnitNetId, out _))
            return false;
        return CoalesceByUnitNetId(ref data, length, incomingSeq, CoopCombatPacket.EventUnitState, incomingUnitNetId);
    }

    private static bool TryCoalesceCrewState(ref byte[] data, int length)
    {
        if (!CoopCombatPacket.TryRead(data, length, out byte eventType, out uint incomingSeq, out _, out _))
            return false;
        if (eventType != CoopCombatPacket.EventCrewState)
            return false;
        if (!CoopCombatPacket.TryReadCrewState(data, length, out _, out _, out _, out uint incomingUnitNetId, out _))
            return false;
        return CoalesceByUnitNetId(ref data, length, incomingSeq, CoopCombatPacket.EventCrewState, incomingUnitNetId);
    }

    private static bool CoalesceByUnitNetId(ref byte[] data, int length, uint incomingSeq, byte targetEventType, uint incomingUnitNetId)
    {
        int count = PendingHigh.Count;
        if (count == 0)
            return false;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        bool removedAny = false;
        while (PendingHigh.Count > 0)
        {
            PendingCombatPacket p = PendingHigh.Dequeue();
            if (!CoopCombatPacket.TryRead(p.Data, p.Length, out byte queuedType, out uint queuedSeq, out _, out _)
                || queuedType != targetEventType)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            uint queuedUnit = 0;
            bool parsed = targetEventType switch
            {
                CoopCombatPacket.EventUnitState => CoopCombatPacket.TryReadUnitState(p.Data, p.Length, out _, out _, out _, out queuedUnit, out _),
                CoopCombatPacket.EventCrewState => CoopCombatPacket.TryReadCrewState(p.Data, p.Length, out _, out _, out _, out queuedUnit, out _),
                _ => false
            };
            if (!parsed || queuedUnit != incomingUnitNetId)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            removedAny = true;
            CoalescedSeq.Add(queuedSeq);
        }

        while (rebuilt.Count > 0)
            PendingHigh.Enqueue(rebuilt.Dequeue());

        PendingHigh.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (PendingCombatCount > _maxPendingDepth)
            _maxPendingDepth = PendingCombatCount;
        if (removedAny)
            CoalescedSeq.Remove(incomingSeq);
        return true;
    }

    private static void TryCoalesceHitResolvedLow(ref byte[] data, int length)
    {
        if (!CoopCombatPacket.TryReadHitResolved(
                data,
                length,
                out _,
                out _,
                out _,
                out _,
                out uint incomingVictim,
                out _,
                out _,
                out _,
                out _,
                out _))
        {
            return;
        }

        int count = PendingLow.Count;
        if (count == 0)
            return;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        while (PendingLow.Count > 0)
        {
            PendingCombatPacket p = PendingLow.Dequeue();
            if (!CoopCombatPacket.TryRead(p.Data, p.Length, out byte queuedType, out uint queuedSeq, out _, out _)
                || queuedType != CoopCombatPacket.EventHitResolved)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            if (!CoopCombatPacket.TryReadHitResolved(
                    p.Data,
                    p.Length,
                    out _,
                    out _,
                    out _,
                    out _,
                    out uint queuedVictim,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _)
                || queuedVictim != incomingVictim)
            {
                rebuilt.Enqueue(p);
                continue;
            }

            // Replaced by newer HitResolved for same victim in low queue.
            CoalescedSeq.Add(queuedSeq);
        }

        while (rebuilt.Count > 0)
            PendingLow.Enqueue(rebuilt.Dequeue());
    }

    /// <summary>Apply up to <paramref name="maxPackets" /> events and/or within <paramref name="maxMs" /> wall time. 0 = unlimited.</summary>
    public static void DrainPendingCombat(
        int maxPackets,
        int maxLowPriorityPackets,
        float maxMs,
        bool logFired,
        bool logStruckPerHit,
        bool logImpactFx,
        bool logDamageState,
        bool logHealth)
    {
        if (!CoopSessionState.IsPlaying)
        {
            PendingHigh.Clear();
            PendingLow.Clear();
            return;
        }

        bool capCount = maxPackets > 0;
        bool capTime = maxMs > 0f;
        int applied = 0;
        bool hitCountBudget = false;
        bool hitTimeBudget = false;
        Stopwatch? sw = capTime ? Stopwatch.StartNew() : null;
        while (PendingHigh.Count > 0)
        {
            if (capCount && applied >= maxPackets)
            {
                hitCountBudget = true;
                break;
            }
            if (capTime && sw!.Elapsed.TotalMilliseconds >= maxMs)
            {
                hitTimeBudget = true;
                break;
            }
            PendingCombatPacket p = PendingHigh.Dequeue();
            TryApplyPacket(p.Data, p.Length, logFired, logStruckPerHit, logImpactFx, logDamageState, logHealth);
            applied++;
        }

        int lowCap = maxLowPriorityPackets < 0 ? 0 : maxLowPriorityPackets;
        int lowApplied = 0;
        while (PendingLow.Count > 0)
        {
            if (capCount && applied >= maxPackets)
            {
                hitCountBudget = true;
                break;
            }
            if (lowCap > 0 && lowApplied >= lowCap)
                break;
            if (capTime && sw!.Elapsed.TotalMilliseconds >= maxMs)
            {
                hitTimeBudget = true;
                break;
            }

            PendingCombatPacket p = PendingLow.Dequeue();
            TryApplyPacket(p.Data, p.Length, logFired, logStruckPerHit, logImpactFx, logDamageState, logHealth);
            applied++;
            lowApplied++;
        }

        if (!logHealth)
            return;

        float now = Time.time;
        int pendingDepth = PendingCombatCount;
        if (pendingDepth >= QueuePressureWarnThreshold && now >= _nextQueuePressureLogTime)
        {
            bool critical = pendingDepth >= QueuePressureCriticalThreshold;
            string level = critical ? "CRITICAL" : "WARN";
            MelonLogger.Warning(
                $"[CoopNet][Health] queue-pressure {level}: pending={pendingDepth} (high={PendingHigh.Count}, low={PendingLow.Count}) maxSeen={_maxPendingDepth} applied={applied} perFrameCap={maxPackets} lowCap={lowCap} msCap={maxMs:0.##}");
            _nextQueuePressureLogTime = now + HealthLogCooldownSeconds;
        }

        if ((hitCountBudget || hitTimeBudget) && pendingDepth > 0 && now >= _nextBudgetHitLogTime)
        {
            string reason = hitCountBudget && hitTimeBudget ? "count+time" : hitCountBudget ? "count" : "time";
            double elapsedMs = sw?.Elapsed.TotalMilliseconds ?? 0d;
            MelonLogger.Msg(
                $"[CoopNet][Health] budget-hit reason={reason} applied={applied} pending={pendingDepth} elapsedMs={elapsedMs:0.##} capPackets={maxPackets} capMs={maxMs:0.##}");
            _nextBudgetHitLogTime = now + HealthLogCooldownSeconds;
        }

        if (hitCountBudget || hitTimeBudget)
            _budgetHitCount++;
    }

    public static void TryApplyPacket(byte[] data, int length, bool logFired, bool logStruckPerHit, bool logImpactFx, bool logDamageState, bool logHealth)
    {
        if (!CoopSessionState.IsPlaying)
            return;
        if (length < CoopCombatPacket.MinCombatDatagramLength)
            return;
        if (!CoopCombatPacket.TryRead(data, length, out byte eventType, out _, out _, out _))
            return;
        if (eventType == CoopCombatPacket.EventWeaponFired)
        {
            if (CoopCombatPacket.TryReadFired(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint shooterNetId,
                    out uint ammoKey,
                    out Vector3 muzzle,
                    out Vector3 direction,
                    out uint targetNetId,
                    out uint weaponNetKey))
            {
                TryApplyFired(
                    seq,
                    token,
                    phase,
                    shooterNetId,
                    ammoKey,
                    muzzle,
                    direction,
                    targetNetId,
                    weaponNetKey,
                    logFired,
                    logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventUnitStruck)
        {
            if (CoopCombatPacket.TryReadStruck(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint victimNetId,
                    out uint shooterNetId,
                    out uint ammoKey,
                    out Vector3 impact,
                    out bool isSpall))
            {
                TryApplyStruck(
                    seq,
                    token,
                    phase,
                    victimNetId,
                    shooterNetId,
                    ammoKey,
                    impact,
                    isSpall,
                    logStruckPerHit,
                    logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventImpactFx)
        {
            if (CoopCombatPacket.TryReadImpactFx(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out ushort effectKind,
                    out Vector3 worldPos,
                    out Vector3 normal,
                    out uint ammoKey,
                    out uint victimNetId,
                    out byte flags))
            {
                TryApplyImpactFx(
                    seq,
                    token,
                    phase,
                    effectKind,
                    worldPos,
                    normal,
                    ammoKey,
                    victimNetId,
                    flags,
                    logImpactFx,
                    logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventDamageState)
        {
            if (CoopCombatPacket.TryReadDamageState(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint victimNetId,
                    out CoopDamageStateSnapshot state))
            {
                TryApplyDamageState(seq, token, phase, victimNetId, state, logDamageState, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventUnitState)
        {
            if (CoopCombatPacket.TryReadUnitState(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint unitNetId,
                    out CoopUnitStateSnapshot state))
            {
                TryApplyUnitState(seq, token, phase, unitNetId, state, logDamageState, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventCrewState)
        {
            if (CoopCombatPacket.TryReadCrewState(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint unitNetId,
                    out CoopCrewStateSnapshot state))
            {
                TryApplyCrewState(seq, token, phase, unitNetId, state, logDamageState, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventHitResolved)
        {
            if (CoopCombatPacket.TryReadHitResolved(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint shotId,
                    out uint victimNetId,
                    out uint shooterNetId,
                    out uint ammoKey,
                    out Vector3 impact,
                    out byte hitKind,
                    out byte flags))
            {
                TryApplyHitResolved(seq, token, phase, shotId, victimNetId, shooterNetId, ammoKey, impact, hitKind, flags, logDamageState, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventCompartmentState)
        {
            if (CoopCombatPacket.TryReadCompartmentState(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint unitNetId,
                    out CoopCompartmentStateSnapshot state))
            {
                TryApplyCompartmentState(seq, token, phase, unitNetId, state, logDamageState, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventParticleImpact)
        {
            if (CoopCombatPacket.TryReadParticleImpact(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint ammoKey,
                    out Vector3 worldPos,
                    out Vector3 forward,
                    out byte surfaceMaterial,
                    out byte fusedStatus,
                    out byte category,
                    out byte ricochetType,
                    out byte flags,
                    out byte impactAudioType,
                    out bool simpleAudioFuzed))
            {
                _ = category;
                _ = ricochetType;
                TryApplyParticleImpact(
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
                    simpleAudioFuzed,
                    logImpactFx,
                    logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventExplosion)
        {
            if (CoopCombatPacket.TryReadExplosion(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out Vector3 worldPos,
                    out float tntKg,
                    out byte explosionFlags))
            {
                _ = explosionFlags;
                TryApplyExplosion(seq, token, phase, worldPos, tntKg, explosionFlags, logImpactFx, logHealth);
            }
        }
        else if (eventType == CoopCombatPacket.EventGrenadeJetVisual)
        {
            if (CoopCombatPacket.TryReadGrenadeJetVisual(
                    data,
                    length,
                    out uint seq,
                    out uint token,
                    out byte phase,
                    out uint ammoKeyJet,
                    out uint shooterNetIdJet,
                    out Vector3 jetPos,
                    out Vector3 jetVel,
                    out bool jetGrav,
                    out byte maxLifeDs))
            {
                _ = ammoKeyJet;
                TryApplyGrenadeJetVisual(
                    seq,
                    token,
                    phase,
                    jetPos,
                    jetVel,
                    jetGrav,
                    maxLifeDs,
                    shooterNetIdJet,
                    logImpactFx,
                    logHealth);
            }
        }
        else
        {
            if (!CoopCombatPacket.TryRead(data, length, out _, out uint seqSkip, out uint tokenSkip, out byte phaseSkip))
                return;
            if (AcceptMission(tokenSkip, phaseSkip) && AcceptSeq(seqSkip, eventType, logHealth))
            {
                MelonLogger.Warning(
                    $"[CoopNet] GHC: skipped unsupported eventType={eventType} seq={seqSkip} — update mod or align versions with host");
            }
        }
    }

    private static bool AcceptMission(uint token, byte phase)
    {
        if (token == 0 || CoopSessionState.MissionCoherenceToken != token)
            return false;
        return phase == 2;
    }

    private static bool AcceptSeq(uint seq, byte eventType, bool logHealth)
    {
        if (seq <= _lastCombatSeq)
            return false;
        if (_lastCombatSeq != 0 && seq > _lastCombatSeq + 1)
        {
            uint missing = seq - _lastCombatSeq - 1;
            uint networkGap = 0;
            for (uint s = _lastCombatSeq + 1; s < seq; s++)
            {
                if (!CoalescedSeq.Remove(s))
                    networkGap++;
            }

            if (networkGap > 0)
            {
                _seqGapCount += networkGap;
                if (networkGap > _maxSeqGap)
                    _maxSeqGap = networkGap;
                if (logHealth && Time.time >= _nextSeqGapLogTime)
                {
                    MelonLogger.Warning(
                        $"[CoopNet][Health] seq-gap event={EventTypeName(eventType)} recv={seq} prev={_lastCombatSeq} gap={networkGap} totalGaps={_seqGapCount} maxGap={_maxSeqGap} pending={PendingCombatCount} coalesced={(missing - networkGap)}");
                    _nextSeqGapLogTime = Time.time + HealthLogCooldownSeconds;
                }
            }
        }
        _lastCombatSeq = seq;
        _recvAcceptedCount++;
        return true;
    }

    private static void TryApplyFired(
        uint seq,
        uint token,
        byte phase,
        uint shooterNetId,
        uint ammoKey,
        Vector3 muzzle,
        Vector3 direction,
        uint targetNetId,
        uint weaponNetKey,
        bool logFired,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventWeaponFired, logHealth))
            return;
        if (logFired)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv Fired seq={seq} shooter={shooterNetId} ammoKey={ammoKey} target={targetNetId} weaponKey={weaponNetKey}");
        }

        CoopMuzzleFxReplay.TryReplay(shooterNetId, ammoKey, weaponNetKey, muzzle, direction);
        _ = targetNetId;
    }

    private static void TryApplyStruck(
        uint seq,
        uint token,
        byte phase,
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        bool isSpall,
        bool logStruckPerHit,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventUnitStruck, logHealth))
            return;
        Unit? victim = CoopUnitLookup.TryFindByNetId(victimNetId);
        if (victim == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC Struck: victim netId={victimNetId} not found");
            return;
        }

        IUnit? shooter = null;
        if (shooterNetId != 0)
        {
            Unit? su = CoopUnitLookup.TryFindByNetId(shooterNetId);
            shooter = su;
        }

        if (!CoopAmmoResolver.TryResolve(ammoKey, out AmmoType? ammo) || ammo == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC Struck: ammoKey={ammoKey} not resolved — skip NotifyStruck");
            return;
        }

        SuppressStruckBroadcast = true;
        try
        {
            victim.NotifyStruck(shooter, ammo, impact, isSpall);
        }
        catch (Exception ex)
        {
            _struckApplyFailCount++;
            MelonLogger.Warning($"[CoopNet] GHC Struck apply failed: {ex.Message}");
            return;
        }
        finally
        {
            SuppressStruckBroadcast = false;
        }

        if (logStruckPerHit)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv Struck seq={seq} victim={victimNetId} shooter={shooterNetId} ammoKey={ammoKey} spall={isSpall}");
        }
    }

    private static void TryApplyImpactFx(
        uint seq,
        uint token,
        byte phase,
        ushort effectKind,
        Vector3 worldPos,
        Vector3 normal,
        uint ammoKey,
        uint victimNetId,
        byte flags,
        bool logImpactFx,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventImpactFx, logHealth))
            return;
        _ = victimNetId;

        ImpactSFXManager? sfx = ImpactSFXManager.Instance;
        if (sfx == null)
        {
            if (logImpactFx)
            {
                MelonLogger.Msg(
                    $"[CoopNet] GHC recv ImpactFx seq={seq} kind={effectKind} ammoKey={ammoKey} flags={flags} (no ImpactSFXManager)");
            }

            return;
        }

        AmmoType? ammo = null;
        if (effectKind != CoopImpactFxKind.ArmorPenPerspective)
        {
            if (!CoopAmmoResolver.TryResolve(ammoKey, out ammo) || ammo == null)
            {
                MelonLogger.Warning($"[CoopNet] GHC ImpactFx: ammoKey={ammoKey} not resolved — skip");
                return;
            }
        }

        try
        {
            switch (effectKind)
            {
                case CoopImpactFxKind.Terrain:
                {
                    bool isTree = (flags & CoopCombatPacket.ImpactFxFlagTree) != 0;
                    bool isSpall = (flags & CoopCombatPacket.ImpactFxFlagSpallHint) != 0;
                    sfx.PlayTerrainImpactSFX(worldPos, ammo!, isTree, isSpall);
                    break;
                }
                case CoopImpactFxKind.Ricochet:
                    sfx.PlayRicochetSFX(worldPos, ammo!);
                    break;
                case CoopImpactFxKind.ArmorSmallCal:
                    sfx.PlaySmallCalImpactSFX(worldPos, ammo!, normal.x);
                    break;
                case CoopImpactFxKind.ArmorLargeCal:
                    sfx.PlayLargeCalImpactSFX(worldPos, ammo!, normal.x);
                    break;
                case CoopImpactFxKind.ArmorPenPerspective:
                    sfx.PlayImpactPenIntPerspSFX(worldPos, normal.x);
                    break;
            }
        }
        catch (Exception ex)
        {
            _impactFxApplyFailCount++;
            MelonLogger.Warning($"[CoopNet] GHC ImpactFx apply failed: {ex.Message}");
        }

        if (logImpactFx)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv ImpactFx seq={seq} kind={effectKind} ammoKey={ammoKey} flags={flags}");
        }
    }

    private static void TryApplyParticleImpact(
        uint seq,
        uint token,
        byte phase,
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
        bool logParticleFx,
        bool logHealth)
    {
        _ = category;
        _ = ricochetType;
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventParticleImpact, logHealth))
            return;
        _particleImpactRecvCount++;
        ParticleEffectsManager? pem = ParticleEffectsManager.Instance;
        if (pem == null)
            return;
        if (!CoopAmmoResolver.TryResolve(ammoKey, out AmmoType? ammo) || ammo == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC ParticleImpact: ammoKey={ammoKey} not resolved");
            return;
        }

        bool isRicochet = (flags & 1) != 0;
        Vector3 fwd = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
        try
        {
            GameObject? go = pem.CreateImpactEffectOfType(
                ammo,
                (ParticleEffectsManager.FusedStatus)fusedStatus,
                (ParticleEffectsManager.SurfaceMaterial)surfaceMaterial,
                isRicochet,
                worldPos);
            if (go != null)
                go.transform.forward = fwd;
        }
        catch (Exception ex)
        {
            _particleImpactApplyFailCount++;
            MelonLogger.Warning($"[CoopNet] GHC ParticleImpact apply failed: {ex.Message}");
            return;
        }

        try
        {
            ImpactSFXManager.Instance?.PlaySimpleImpactAudio(
                (ImpactAudioType)impactAudioType,
                worldPos,
                simpleAudioFuzed);
        }
        catch (Exception ex)
        {
            _particleImpactApplyFailCount++;
            MelonLogger.Warning($"[CoopNet] GHC ParticleImpact audio failed: {ex.Message}");
        }

        if (logParticleFx)
        {
            MelonLogger.Msg($"[CoopNet] GHC recv ParticleImpact seq={seq} ammoKey={ammoKey} ric={isRicochet}");
        }
    }

    private static void TryApplyGrenadeJetVisual(
        uint seq,
        uint token,
        byte phase,
        Vector3 worldPos,
        Vector3 velocity,
        bool useGravity,
        byte maxLifeDs,
        uint shooterNetId,
        bool logJet,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventGrenadeJetVisual, logHealth))
            return;
        CoopAtJetVisualReplay.TrySpawn(worldPos, velocity, useGravity, maxLifeDs, shooterNetId);
        if (logJet)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv GrenadeJetVisual seq={seq} shooter={shooterNetId} grav={useGravity} lifeDs={maxLifeDs}");
        }
    }

    private static void TryApplyExplosion(
        uint seq,
        uint token,
        byte phase,
        Vector3 worldPos,
        float tntKg,
        byte explosionFlags,
        bool logExplosion,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventExplosion, logHealth))
            return;
        _explosionRecvCount++;
        bool smokeGrenade = (explosionFlags & CoopCombatPacket.ExplosionFlagGrenadeSmoke) != 0;
        if (smokeGrenade)
        {
            CoopGrenadeSmokeReplay.TrySpawn(worldPos);
        }
        else
        {
            try
            {
                Explosions.RegisterExplosion(worldPos, tntKg);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CoopNet] GHC Explosion apply failed: {ex.Message}");
                return;
            }
        }

        float minTnt = CoopUdpTransport.ExplosionCameraMinTntKg;
        UnityEngine.Camera? cam = UnityEngine.Camera.main;
        float cameraTnt = smokeGrenade ? Mathf.Max(minTnt, 0.02f) : tntKg;
        if (minTnt > 0f && cameraTnt >= minTnt && cam != null)
        {
            float d = Vector3.Distance(worldPos, cam.transform.position);
            if (d <= CoopUdpTransport.ExplosionCameraMaxDistanceMeters)
            {
                GHPC.Camera.CameraJiggler.RequestExplosiveReaction(worldPos, cameraTnt);
                GHPC.Camera.BlurManager.RequestExplosiveReaction(worldPos, cameraTnt);
            }
        }

        if (logExplosion)
        {
            MelonLogger.Msg(
                smokeGrenade
                    ? $"[CoopNet] GHC recv Explosion seq={seq} smokeGrenade flags={explosionFlags}"
                    : $"[CoopNet] GHC recv Explosion seq={seq} tnt={tntKg:F2}");
        }
    }

    private static void TryApplyDamageState(
        uint seq,
        uint token,
        byte phase,
        uint victimNetId,
        in CoopDamageStateSnapshot state,
        bool logDamageState,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventDamageState, logHealth))
            return;
        _damageStateRecvCount++;
        Unit? victim = CoopUnitLookup.TryFindByNetId(victimNetId);
        if (victim == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC DamageState: victim netId={victimNetId} not found");
            return;
        }

        bool beforeDestroyed = victim.Destroyed;
        try
        {
            state.ApplyTo(victim);
        }
        catch (Exception ex)
        {
            _damageStateApplyFailCount++;
            MelonLogger.Warning($"[CoopNet] GHC DamageState apply failed: {ex.Message}");
            return;
        }
        _damageStateApplyCount++;
        bool afterDestroyed = victim.Destroyed;

        if (state.UnitDestroyed && !afterDestroyed)
        {
            _damageStateDestroyParityMismatchCount++;
            MelonLogger.Warning(
                $"[CoopNet] GHC DamageState parity mismatch: victim={victimNetId} seq={seq} recvDestroyed=true but victim.Destroyed=false (before={beforeDestroyed})");
        }

        if (logDamageState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv DamageState seq={seq} victim={victimNetId} destroyed={state.UnitDestroyed} before={beforeDestroyed} after={afterDestroyed} e/t/r/l/r={state.EngineHpPct}/{state.TransmissionHpPct}/{state.RadiatorHpPct}/{state.LeftTrackHpPct}/{state.RightTrackHpPct}");
        }
    }

    private static void TryApplyUnitState(
        uint seq,
        uint token,
        byte phase,
        uint unitNetId,
        in CoopUnitStateSnapshot state,
        bool logUnitState,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventUnitState, logHealth))
            return;
        _unitStateRecvCount++;
        Unit? unit = CoopUnitLookup.TryFindByNetId(unitNetId);
        if (unit == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC UnitState: unit netId={unitNetId} not found");
            return;
        }

        try
        {
            state.ApplyTo(unit);
            _unitStateApplyCount++;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] GHC UnitState apply failed: {ex.Message}");
            return;
        }

        if (logUnitState)
            MelonLogger.Msg($"[CoopNet] GHC recv UnitState seq={seq} unit={unitNetId} flags={state.Flags}");
    }

    private static void TryApplyCrewState(
        uint seq,
        uint token,
        byte phase,
        uint unitNetId,
        in CoopCrewStateSnapshot state,
        bool logCrewState,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventCrewState, logHealth))
            return;
        _crewStateRecvCount++;
        Unit? unit = CoopUnitLookup.TryFindByNetId(unitNetId);
        if (unit == null)
        {
            MelonLogger.Warning($"[CoopNet] GHC CrewState: unit netId={unitNetId} not found");
            return;
        }

        try
        {
            state.ApplyTo(unit);
            _crewStateApplyCount++;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] GHC CrewState apply failed: {ex.Message}");
            return;
        }

        if (logCrewState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv CrewState seq={seq} unit={unitNetId} present/dead/incap/evac/susp={state.PresentMask}/{state.DeadMask}/{state.IncapacitatedMask}/{state.EvacuatedMask}/{state.SuspendedMask}");
        }
    }

    private static void TryApplyHitResolved(
        uint seq,
        uint token,
        byte phase,
        uint shotId,
        uint victimNetId,
        uint shooterNetId,
        uint ammoKey,
        Vector3 impact,
        byte hitKind,
        byte flags,
        bool logHitResolved,
        bool logHealth)
    {
        // Low-priority telemetry event: do not compete with authoritative global seq gate.
        if (!AcceptMission(token, phase))
            return;
        _ = logHealth;
        _hitResolvedRecvCount++;
        _ = impact;
        if (logHitResolved)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv HitResolved seq={seq} shot={shotId} victim={victimNetId} shooter={shooterNetId} ammoKey={ammoKey} hitKind={hitKind} flags={flags}");
        }
    }

    private static void TryApplyCompartmentState(
        uint seq,
        uint token,
        byte phase,
        uint unitNetId,
        in CoopCompartmentStateSnapshot state,
        bool logCompartmentState,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventCompartmentState, logHealth))
            return;
        _compartmentStateRecvCount++;
        bool hadPrev = LastCompartmentFirePresentByUnitNetId.TryGetValue(unitNetId, out bool prevFire);
        bool prev = hadPrev && prevFire;
        LastCompartmentFirePresentByUnitNetId[unitNetId] = state.FirePresent;
        Unit? unit = CoopUnitLookup.TryFindByNetId(unitNetId);
        if (unit != null)
        {
            bool localVehicle = PlayerInput.Instance != null
                && PlayerInput.Instance.CurrentPlayerUnit == unit;
            if (!localVehicle)
                CoopCompartmentFxReplay.TryOpenExitsWhenCompartmentHasNoVentilation(unit, in state);
            if (state.FirePresent && !prev)
                CoopCompartmentFxReplay.TryFireBurstFx(unit, Mathf.Max(0.5f, state.CombinedFlameHeightPct / 25f));
            CoopCompartmentFxReplay.TryApplySustainedFireAndSmoke(unit, in state);
            FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
            if (fm != null && !localVehicle)
            {
                CoopCompartmentFxReplay.TryApplyScorchAndRemoteSmokeColumn(fm, state.ScorchPct, state.SmokeColumnPct);
                CoopRemoteFlammablesBurningAudio.TrySync(fm, in state);
            }
        }

        if (logCompartmentState)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv CompartmentState seq={seq} unit={unitNetId} fire={state.FirePresent} unsecured={state.UnsecuredFirePresent} flamePct={state.CombinedFlameHeightPct} tempPct={state.InternalTemperaturePct} scorch={state.ScorchPct} smokeCol={state.SmokeColumnPct}");
        }
    }

    private static string EventTypeName(byte eventType)
    {
        return eventType switch
        {
            CoopCombatPacket.EventWeaponFired => "Fired",
            CoopCombatPacket.EventUnitStruck => "Struck",
            CoopCombatPacket.EventImpactFx => "ImpactFx",
            CoopCombatPacket.EventDamageState => "DamageState",
            CoopCombatPacket.EventUnitState => "UnitState",
            CoopCombatPacket.EventCrewState => "CrewState",
            CoopCombatPacket.EventHitResolved => "HitResolved",
            CoopCombatPacket.EventCompartmentState => "CompartmentState",
            CoopCombatPacket.EventParticleImpact => "ParticleImpact",
            CoopCombatPacket.EventExplosion => "Explosion",
            CoopCombatPacket.EventGrenadeJetVisual => "GrenadeJetVisual",
            _ => "Unknown"
        };
    }

    private static void LogSessionSummaryIfAny()
    {
        int pending = PendingCombatCount;
        if (_recvAcceptedCount == 0
            && _seqGapCount == 0
            && _maxPendingDepth == 0
            && _struckApplyFailCount == 0
            && _impactFxApplyFailCount == 0
            && _damageStateApplyFailCount == 0
            && _damageStateRecvCount == 0
            && _damageStateApplyCount == 0
            && _unitStateRecvCount == 0
            && _unitStateApplyCount == 0
            && _crewStateRecvCount == 0
            && _crewStateApplyCount == 0
            && _hitResolvedRecvCount == 0
            && _compartmentStateRecvCount == 0
            && _particleImpactRecvCount == 0
            && _particleImpactApplyFailCount == 0
            && _explosionRecvCount == 0
            && _damageStateCoalescedCount == 0
            && _struckCoalescedCount == 0
            && _damageStateDestroyParityMismatchCount == 0
            && _budgetHitCount == 0
            && pending == 0)
        {
            return;
        }

        MelonLogger.Msg(
            $"[CoopNet][Summary] GHC session accepted={_recvAcceptedCount} seqGaps={_seqGapCount} maxGap={_maxSeqGap} maxPending={_maxPendingDepth} budgetHits={_budgetHitCount} pendingAtReset={pending}");
        MelonLogger.Msg(
            $"[CoopNet][Summary] GHC apply-fail struck={_struckApplyFailCount} impactFx={_impactFxApplyFailCount} damageState={_damageStateApplyFailCount}");
        MelonLogger.Msg(
            $"[CoopNet][Summary] GHC damage-state recv={_damageStateRecvCount} applied={_damageStateApplyCount} coalesced={_damageStateCoalescedCount} destroyParityMismatch={_damageStateDestroyParityMismatchCount} struckCoalesced={_struckCoalescedCount}");
        MelonLogger.Msg(
            $"[CoopNet][Summary] GHC unit-state recv={_unitStateRecvCount} applied={_unitStateApplyCount} crew-state recv={_crewStateRecvCount} applied={_crewStateApplyCount} hitResolved recv={_hitResolvedRecvCount} compartment-state recv={_compartmentStateRecvCount}");
        MelonLogger.Msg(
            $"[CoopNet][Summary] GHC cosmetic particleImpact recv={_particleImpactRecvCount} fail={_particleImpactApplyFailCount} explosion recv={_explosionRecvCount}");
    }
}
