using System;
using System.Collections.Generic;
using System.Diagnostics;
using GHPC;
using GHPC.AI.Interfaces;
using GHPC.Audio;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Client: queue GHC from host, apply on main thread with per-frame budget (order preserved).</summary>
internal static class ClientCombatApplier
{
    private static readonly Queue<PendingCombatPacket> Pending = new();
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

    public static int PendingCombatCount => Pending.Count;

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
        _damageStateCoalescedCount = 0;
        _struckCoalescedCount = 0;
        _damageStateDestroyParityMismatchCount = 0;
        _budgetHitCount = 0;
        _maxPendingDepth = 0;
        _nextSeqGapLogTime = float.NegativeInfinity;
        _nextQueuePressureLogTime = float.NegativeInfinity;
        _nextBudgetHitLogTime = float.NegativeInfinity;
        Pending.Clear();
        CoalescedSeq.Clear();
    }

    /// <summary>Drop queued GHC without resetting seq (e.g. combat replication pref off).</summary>
    public static void ClearPendingQueueOnly()
    {
        Pending.Clear();
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
        if (TryCoalesceStruck(ref data, length))
            return;
        if (TryCoalesceDamageState(ref data, length))
            return;
        Pending.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (Pending.Count > _maxPendingDepth)
            _maxPendingDepth = Pending.Count;
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

        int count = Pending.Count;
        if (count == 0)
            return false;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        bool removedAny = false;
        while (Pending.Count > 0)
        {
            PendingCombatPacket p = Pending.Dequeue();
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
            Pending.Enqueue(rebuilt.Dequeue());

        Pending.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (Pending.Count > _maxPendingDepth)
            _maxPendingDepth = Pending.Count;
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

        int count = Pending.Count;
        if (count == 0)
            return false;
        var rebuilt = new Queue<PendingCombatPacket>(count);
        bool removedAny = false;
        while (Pending.Count > 0)
        {
            PendingCombatPacket p = Pending.Dequeue();
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
            Pending.Enqueue(rebuilt.Dequeue());

        Pending.Enqueue(new PendingCombatPacket { Data = data, Length = length });
        if (Pending.Count > _maxPendingDepth)
            _maxPendingDepth = Pending.Count;
        if (removedAny)
            CoalescedSeq.Remove(incomingSeq);
        return true;
    }

    /// <summary>Apply up to <paramref name="maxPackets" /> events and/or within <paramref name="maxMs" /> wall time. 0 = unlimited.</summary>
    public static void DrainPendingCombat(
        int maxPackets,
        float maxMs,
        bool logFired,
        bool logStruckPerHit,
        bool logImpactFx,
        bool logDamageState,
        bool logHealth)
    {
        if (!CoopSessionState.IsPlaying)
        {
            Pending.Clear();
            return;
        }

        bool capCount = maxPackets > 0;
        bool capTime = maxMs > 0f;
        int applied = 0;
        bool hitCountBudget = false;
        bool hitTimeBudget = false;
        Stopwatch? sw = capTime ? Stopwatch.StartNew() : null;
        while (Pending.Count > 0)
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
            PendingCombatPacket p = Pending.Dequeue();
            TryApplyPacket(p.Data, p.Length, logFired, logStruckPerHit, logImpactFx, logDamageState, logHealth);
            applied++;
        }

        if (!logHealth)
            return;

        float now = Time.time;
        int pendingDepth = Pending.Count;
        if (pendingDepth >= QueuePressureWarnThreshold && now >= _nextQueuePressureLogTime)
        {
            bool critical = pendingDepth >= QueuePressureCriticalThreshold;
            string level = critical ? "CRITICAL" : "WARN";
            MelonLogger.Warning(
                $"[CoopNet][Health] queue-pressure {level}: pending={pendingDepth} maxSeen={_maxPendingDepth} applied={applied} perFrameCap={maxPackets} msCap={maxMs:0.##}");
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
                    out uint targetNetId))
            {
                TryApplyFired(seq, token, phase, shooterNetId, ammoKey, muzzle, direction, targetNetId, logFired, logHealth);
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
                        $"[CoopNet][Health] seq-gap event={EventTypeName(eventType)} recv={seq} prev={_lastCombatSeq} gap={networkGap} totalGaps={_seqGapCount} maxGap={_maxSeqGap} pending={Pending.Count} coalesced={(missing - networkGap)}");
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
        bool logFired,
        bool logHealth)
    {
        if (!AcceptMission(token, phase) || !AcceptSeq(seq, CoopCombatPacket.EventWeaponFired, logHealth))
            return;
        if (logFired)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHC recv Fired seq={seq} shooter={shooterNetId} ammoKey={ammoKey} target={targetNetId}");
        }

        // MVP: no full LiveRound; optional future VFX at muzzle (phase 4).
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

    private static string EventTypeName(byte eventType)
    {
        return eventType switch
        {
            CoopCombatPacket.EventWeaponFired => "Fired",
            CoopCombatPacket.EventUnitStruck => "Struck",
            CoopCombatPacket.EventImpactFx => "ImpactFx",
            CoopCombatPacket.EventDamageState => "DamageState",
            _ => "Unknown"
        };
    }

    private static void LogSessionSummaryIfAny()
    {
        int pending = Pending.Count;
        if (_recvAcceptedCount == 0
            && _seqGapCount == 0
            && _maxPendingDepth == 0
            && _struckApplyFailCount == 0
            && _impactFxApplyFailCount == 0
            && _damageStateApplyFailCount == 0
            && _damageStateRecvCount == 0
            && _damageStateApplyCount == 0
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
    }
}
