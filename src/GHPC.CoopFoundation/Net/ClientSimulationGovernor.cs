using System;
using System.Collections.Generic;
using GHPC.Crew;
using GHPC;
using GHPC.Weapons;
using MelonLoader;
using UnityEngine;
using System.Linq;

namespace GHPC.CoopFoundation.Net;

/// <summary>
/// Phase 5 (M1/M4): correction-first governor that nudges non-local client units toward host world snapshots.
/// This avoids risky hard AI-disable as a first rollout step.
/// </summary>
internal static class ClientSimulationGovernor
{
    private enum MotionProfile
    {
        Unknown = 0,
        Wheeled = 1,
        Tracked = 2
    }

    private enum SuppressDegradeReason
    {
        None = 0,
        SuppressException = 1
    }

    private readonly struct SnapshotState
    {
        public readonly Vector3 Position;

        public readonly Quaternion HullRotation;

        public readonly Quaternion TurretWorldRotation;

        public readonly Quaternion GunWorldRotation;

        public readonly float SampleTime;

        public SnapshotState(
            Vector3 position,
            Quaternion hullRotation,
            Quaternion turretWorldRotation,
            Quaternion gunWorldRotation,
            float sampleTime)
        {
            Position = position;
            HullRotation = hullRotation;
            TurretWorldRotation = turretWorldRotation;
            GunWorldRotation = gunWorldRotation;
            SampleTime = sampleTime;
        }
    }

    private struct BufferedState
    {
        public bool HasOlder;
        public bool HasNewer;
        public SnapshotState Older;
        public SnapshotState Newer;

        public void Push(in SnapshotState state)
        {
            if (HasNewer)
            {
                Older = Newer;
                HasOlder = true;
            }

            Newer = state;
            HasNewer = true;
        }
    }

    private struct AimBinding
    {
        public AimablePlatform? TraverseAp;
        public AimablePlatform? GunAp;
        public Transform? Traverse;
        public Transform? Gun;
        public Transform? VisualTurret;
        public Transform? VisualGun;
        public bool AimLoopDisabled;
        public bool WarnedMissingVisual;
        public bool LoggedSelection;
        public bool Resolved;
    }

    private static readonly Dictionary<uint, BufferedState> BufferedByNetId = new();
    private static readonly HashSet<uint> SeenInFrame = new();
    private static readonly Dictionary<uint, AimBinding> AimBindingByNetId = new();
    private static readonly Dictionary<uint, Vector3> PositionVelByNetId = new();
    private static readonly Dictionary<uint, Vector3> NetVelocityByNetId = new();
    private static readonly Dictionary<uint, List<(Behaviour Behaviour, bool WasEnabled)>> SuppressedDriversByNetId = new();
    private static readonly Dictionary<uint, Rigidbody?> RigidBodyByNetId = new();
    private static readonly Dictionary<uint, (bool IsKinematic, RigidbodyInterpolation Interpolation)> SuppressedRigidbodyStateByNetId = new();
    private static readonly Dictionary<uint, (Vector3 Pos, Quaternion Rot, bool HardSnap)> PendingPhysicsApplyByNetId = new();
    private static readonly Dictionary<uint, uint> HeavyCorrectionHitsByNetId = new();
    private static readonly Dictionary<uint, MotionProfile> MotionProfileByNetId = new();
    private static readonly Dictionary<uint, (float DtEwma, float JitterEwma, float DtMax)> UnitTimingByNetId = new();

    private static bool _enabled = true;
    private static float _strength = 1f;
    private static bool _softSuppressEnabled;
    private static bool _log;
    private static bool _safeMode = true;
    private static bool _degradedToCorrectionOff;
    private static bool _degradedToSuppressOff;
    private static SuppressDegradeReason _suppressDegradeReason;
    private static float _suppressRecoveryAtTime = float.PositiveInfinity;

    private static readonly Dictionary<uint, bool> SuppressedByNetId = new();

    private static int _correctedThisFrame;
    private static int _maxCorrectedInFrame;
    private static int _suppressedThisFrame;
    private static int _restoredThisFrame;
    private static uint _totalCorrections;
    private static uint _snapCorrections;
    private static uint _suppressOpsTotal;
    private static uint _restoreOpsTotal;
    private static uint _unitsSeenUnique;
    private static uint _ownershipSkipCount;
    private static uint _belowThresholdSkipCount;
    private static uint _bufferMissCount;
    private static uint _aimApplyCount;
    private static uint _aimMissingCount;
    private static uint _aimBelowThresholdSkipCount;
    private static float _lastFrameAvgPosErr;
    private static float _lastFrameMaxPosErr;
    private static float _lastFrameAvgRotErr;
    private static float _lastFrameMaxRotErr;
    private static float _sessionMaxPosErr;
    private static float _sessionMaxRotErr;
    private static float _nextLogTime = float.NegativeInfinity;
    private static float _lastMergedSnapshotTime = float.NegativeInfinity;
    private static float _snapshotDtEwma = 0.2f;
    private static float _snapshotJitterEwma;
    private static float _snapshotDtMax;
    private static uint _snapshotDtSamples;
    private static readonly List<float> SnapshotDtWindow = new();
    private static uint _interpSampleCount;
    private static uint _interpUnderrunCount;
    private static uint _wheeledSamples;
    private static uint _trackedSamples;
    private static uint _unknownSamples;
    private static uint _wheeledCorrections;
    private static uint _trackedCorrections;
    private static uint _unknownCorrections;
    private static uint _budgetDeferrals;
    private static uint _overwriteRiskCount;
    private static uint _physicsApplyCount;
    private static uint _transformApplyCount;

    private const float LogCooldownSeconds = 2f;
    private const float SnapDistanceMeters = 30f;
    private const float SnapAngleDegrees = 65f;
    private const float CorrectDistanceThresholdMeters = 0.15f;
    private const float CorrectAngleThresholdDegrees = 1.2f;
    private const float AimCorrectAngleThresholdDegrees = 0.35f;
    private const float SuppressRecoveryCooldownSeconds = 8f;
    private const float ExtrapolationClampSeconds = 0.08f;
    private const float MinInterpolationBackTimeSeconds = 0.14f;
    private const float MaxInterpolationBackTimeSeconds = 0.38f;
    private const float InterpolationBackTimeBaseSeconds = 0.12f;

    public static void Configure(bool enabled, float strength, bool softSuppressEnabled, bool log, bool safeMode)
    {
        _enabled = enabled;
        _strength = strength < 0f ? 0f : strength;
        _softSuppressEnabled = softSuppressEnabled;
        _log = log;
        _safeMode = safeMode;
    }

    public static void ResetSession()
    {
        LogSessionSummaryIfAny();
        BufferedByNetId.Clear();
        SeenInFrame.Clear();
        PositionVelByNetId.Clear();
        NetVelocityByNetId.Clear();
        RigidBodyByNetId.Clear();
        PendingPhysicsApplyByNetId.Clear();
        SuppressedDriversByNetId.Clear();
        SuppressedRigidbodyStateByNetId.Clear();
        HeavyCorrectionHitsByNetId.Clear();
        MotionProfileByNetId.Clear();
        UnitTimingByNetId.Clear();
        SnapshotDtWindow.Clear();
        RestoreAllAimLoopsBestEffort();
        AimBindingByNetId.Clear();
        RestoreAllSuppressedUnitsBestEffort();
        SuppressedByNetId.Clear();
        _degradedToCorrectionOff = false;
        _degradedToSuppressOff = false;
        _suppressDegradeReason = SuppressDegradeReason.None;
        _suppressRecoveryAtTime = float.PositiveInfinity;
        _correctedThisFrame = 0;
        _maxCorrectedInFrame = 0;
        _suppressedThisFrame = 0;
        _restoredThisFrame = 0;
        _totalCorrections = 0;
        _snapCorrections = 0;
        _suppressOpsTotal = 0;
        _restoreOpsTotal = 0;
        _unitsSeenUnique = 0;
        _ownershipSkipCount = 0;
        _belowThresholdSkipCount = 0;
        _bufferMissCount = 0;
        _aimApplyCount = 0;
        _aimMissingCount = 0;
        _aimBelowThresholdSkipCount = 0;
        _lastFrameAvgPosErr = 0f;
        _lastFrameMaxPosErr = 0f;
        _lastFrameAvgRotErr = 0f;
        _lastFrameMaxRotErr = 0f;
        _sessionMaxPosErr = 0f;
        _sessionMaxRotErr = 0f;
        _nextLogTime = float.NegativeInfinity;
        _lastMergedSnapshotTime = float.NegativeInfinity;
        _snapshotDtEwma = 0.2f;
        _snapshotJitterEwma = 0f;
        _snapshotDtMax = 0f;
        _snapshotDtSamples = 0;
        _interpSampleCount = 0;
        _interpUnderrunCount = 0;
        _wheeledSamples = 0;
        _trackedSamples = 0;
        _unknownSamples = 0;
        _wheeledCorrections = 0;
        _trackedCorrections = 0;
        _unknownCorrections = 0;
        _budgetDeferrals = 0;
        _overwriteRiskCount = 0;
        _physicsApplyCount = 0;
        _transformApplyCount = 0;
    }

    public static void OnMergedWorldSnapshot(List<WorldEntityWire> entities)
    {
        if (entities == null || entities.Count == 0)
            return;
        SeenInFrame.Clear();
        float now = Time.time;
        if (_lastMergedSnapshotTime > 0f)
        {
            float dtMerge = now - _lastMergedSnapshotTime;
            if (dtMerge > 1e-4f && dtMerge < 2f)
            {
                if (_snapshotDtSamples == 0)
                    _snapshotDtEwma = dtMerge;
                else
                    _snapshotDtEwma = Mathf.Lerp(_snapshotDtEwma, dtMerge, 0.15f);
                float jitterAbs = Mathf.Abs(dtMerge - _snapshotDtEwma);
                _snapshotJitterEwma = Mathf.Lerp(_snapshotJitterEwma, jitterAbs, 0.18f);
                _snapshotDtMax = Mathf.Max(_snapshotDtMax, dtMerge);
                _snapshotDtSamples++;
                SnapshotDtWindow.Add(dtMerge);
                if (SnapshotDtWindow.Count > 512)
                    SnapshotDtWindow.RemoveAt(0);
            }
        }
        _lastMergedSnapshotTime = now;
        for (int i = 0; i < entities.Count; i++)
        {
            WorldEntityWire e = entities[i];
            if (e.NetId == 0)
                continue;
            SeenInFrame.Add(e.NetId);
            SnapshotState snap = new(
                e.Position,
                e.HullRotation,
                e.TurretWorldRotation,
                e.GunWorldRotation,
                now);
            if (!BufferedByNetId.TryGetValue(e.NetId, out BufferedState buffered))
                buffered = default;
            if (buffered.HasNewer)
            {
                SnapshotState prev = buffered.Newer;
                float dt = now - prev.SampleTime;
                if (dt > 1e-4f && dt < 1.5f)
                {
                    Vector3 rawVel = (snap.Position - prev.Position) / dt;
                    Vector3 oldVel = NetVelocityByNetId.TryGetValue(e.NetId, out Vector3 v) ? v : Vector3.zero;
                    NetVelocityByNetId[e.NetId] = Vector3.Lerp(oldVel, rawVel, 0.35f);
                    if (UnitTimingByNetId.TryGetValue(e.NetId, out var timing))
                    {
                        float dtEwma = timing.DtEwma <= 1e-4f ? dt : Mathf.Lerp(timing.DtEwma, dt, 0.2f);
                        float jitter = Mathf.Lerp(timing.JitterEwma, Mathf.Abs(dt - dtEwma), 0.25f);
                        UnitTimingByNetId[e.NetId] = (dtEwma, jitter, Mathf.Max(timing.DtMax, dt));
                    }
                    else
                    {
                        UnitTimingByNetId[e.NetId] = (dt, 0f, dt);
                    }
                }
            }
            else if (!NetVelocityByNetId.ContainsKey(e.NetId))
            {
                NetVelocityByNetId[e.NetId] = Vector3.zero;
            }
            buffered.Push(snap);
            BufferedByNetId[e.NetId] = buffered;
        }

        // Keep dictionary bounded to currently replicated world.
        if (BufferedByNetId.Count == 0)
            return;
        var stale = new List<uint>();
        foreach (uint id in BufferedByNetId.Keys)
        {
            if (!SeenInFrame.Contains(id))
                stale.Add(id);
        }

        for (int i = 0; i < stale.Count; i++)
        {
            BufferedByNetId.Remove(stale[i]);
            AimBindingByNetId.Remove(stale[i]);
            PositionVelByNetId.Remove(stale[i]);
            NetVelocityByNetId.Remove(stale[i]);
            RigidBodyByNetId.Remove(stale[i]);
            PendingPhysicsApplyByNetId.Remove(stale[i]);
            SuppressedDriversByNetId.Remove(stale[i]);
            HeavyCorrectionHitsByNetId.Remove(stale[i]);
            MotionProfileByNetId.Remove(stale[i]);
            UnitTimingByNetId.Remove(stale[i]);
            SuppressedRigidbodyStateByNetId.Remove(stale[i]);
        }
    }

    public static void TickUpdate(float deltaTime)
    {
        _ = deltaTime;
        _correctedThisFrame = 0;
        _suppressedThisFrame = 0;
        _restoredThisFrame = 0;
        if (!_enabled || !CoopSessionState.IsPlaying)
            return;
        if (_degradedToSuppressOff && Time.time >= _suppressRecoveryAtTime)
        {
            _degradedToSuppressOff = false;
            _suppressDegradeReason = SuppressDegradeReason.None;
            _suppressRecoveryAtTime = float.PositiveInfinity;
            if (_log)
                MelonLogger.Msg("[CoopSim] safe-mode recovery: re-enabling phase5 soft-suppress.");
        }
    }

    public static void TickLateUpdate(float deltaTime)
    {
        if (!_enabled || _degradedToCorrectionOff || !CoopSessionState.IsPlaying)
            return;
        if (!CoopUdpTransport.IsNetworkActive)
            return;
        if (BufferedByNetId.Count == 0)
            return;

        float interpolationBackTime = GetAdaptiveInterpolationBackTime();
        float renderTime = Time.time - interpolationBackTime;
        uint localControlledNetId = 0;
        Unit? localControlled = CoopSessionState.ControlledUnit;
        if (localControlled != null)
            localControlledNetId = CoopUnitWireRegistry.GetWireId(localControlled);

        float snapDistSq = SnapDistanceMeters * SnapDistanceMeters;
        float correctionDistSq = CorrectDistanceThresholdMeters * CorrectDistanceThresholdMeters;
        float lerpT = Mathf.Clamp01(deltaTime * Mathf.Max(0f, _strength) * 10f);
        float smoothTime = Mathf.Clamp(0.12f / Mathf.Max(0.25f, _strength), 0.05f, 0.22f);
        int correctionBudgetPerFrame = Mathf.Clamp(16 + BufferedByNetId.Count / 3, 16, 56);
        float posErrSum = 0f;
        float posErrMax = 0f;
        float rotErrSum = 0f;
        float rotErrMax = 0f;
        int sampleCount = 0;
        uint seenThisFrame = 0;
        try
        {
            foreach (KeyValuePair<uint, BufferedState> kv in BufferedByNetId)
            {
                uint netId = kv.Key;
                if (netId == 0)
                    continue;
                seenThisFrame++;
                bool isLocalOwned = netId == localControlledNetId || CoopVehicleOwnership.IsLocalOwner(netId);
                if (isLocalOwned)
                {
                    _ownershipSkipCount++;
                    PositionVelByNetId.Remove(netId);
                    NetVelocityByNetId.Remove(netId);
                    PendingPhysicsApplyByNetId.Remove(netId);
                    continue;
                }
                Unit? unit = CoopUnitLookup.TryFindByNetId(netId);
                if (unit == null)
                    continue;
                if (_softSuppressEnabled && !_degradedToSuppressOff)
                {
                    EnsureSuppressed(unit, netId);
                }

                if (!TrySampleBuffered(kv.Value, renderTime, out SnapshotState targetState))
                {
                    _bufferMissCount++;
                    continue;
                }
                _interpSampleCount++;
                if (kv.Value.HasNewer && renderTime >= kv.Value.Newer.SampleTime)
                    _interpUnderrunCount++;

                Transform t = unit.transform;
                MotionProfile profile = ResolveMotionProfile(netId, unit);
                if (profile == MotionProfile.Wheeled)
                    _wheeledSamples++;
                else if (profile == MotionProfile.Tracked)
                    _trackedSamples++;
                else
                    _unknownSamples++;
                Vector3 currentPos = t.position;
                Quaternion currentRot = t.rotation;
                Vector3 targetPos = targetState.Position;
                float unitBackTime = GetPerUnitInterpolationBackTime(netId);
                float jitterWeight = Mathf.Clamp01((unitBackTime - MinInterpolationBackTimeSeconds) / 0.24f);
                if (NetVelocityByNetId.TryGetValue(netId, out Vector3 netVel))
                {
                    float speed = netVel.magnitude;
                    float extrapolation = speed > 6f ? 0.022f : speed > 2.5f ? 0.035f : 0.052f;
                    if (profile == MotionProfile.Wheeled)
                        extrapolation *= 0.75f;
                    extrapolation = Mathf.Lerp(extrapolation, extrapolation * 0.6f, jitterWeight);
                    extrapolation = Mathf.Min(extrapolation, ExtrapolationClampSeconds);
                    Vector3 offset = netVel * extrapolation;
                    if (offset.sqrMagnitude > 0.09f)
                        offset = offset.normalized * 0.3f;
                    targetPos += offset;
                }
                Quaternion targetRot = targetState.HullRotation;

                float posErrSq = (currentPos - targetPos).sqrMagnitude;
                float rotErr = Quaternion.Angle(currentRot, targetRot);
                float posErr = Mathf.Sqrt(posErrSq);
                if (posErr > 0.5f)
                    HeavyCorrectionHitsByNetId[netId] = HeavyCorrectionHitsByNetId.TryGetValue(netId, out uint c) ? c + 1 : 1;
                posErrSum += posErr;
                rotErrSum += rotErr;
                if (posErr > posErrMax)
                    posErrMax = posErr;
                if (rotErr > rotErrMax)
                    rotErrMax = rotErr;
                sampleCount++;

                // Aim sync must be independent from hull correction decision; otherwise turret/gun can freeze
                // whenever hull drift is below threshold.
                TryApplyAimables(netId, unit, targetState.TurretWorldRotation, targetState.GunWorldRotation, lerpT);

                float speedForDeadband = NetVelocityByNetId.TryGetValue(netId, out Vector3 sv) ? sv.magnitude : 0f;
                float profileDeadband = profile == MotionProfile.Wheeled ? 0.22f : profile == MotionProfile.Tracked ? 0.13f : 0.16f;
                profileDeadband += Mathf.Clamp(speedForDeadband * 0.01f, 0f, 0.08f);
                float correctionDistSqAdaptive = Mathf.Max(correctionDistSq, profileDeadband * profileDeadband);
                bool needCorrect = posErrSq >= correctionDistSqAdaptive || rotErr >= CorrectAngleThresholdDegrees;
                if (!needCorrect)
                {
                    _belowThresholdSkipCount++;
                    continue;
                }
                if (_correctedThisFrame >= correctionBudgetPerFrame
                    && posErr < 0.9f
                    && rotErr < 4f)
                {
                    _budgetDeferrals++;
                    continue;
                }

                bool hardSnap = posErrSq >= snapDistSq || rotErr >= SnapAngleDegrees;
                if (hardSnap)
                {
                    if (TryQueuePhysicsApply(unit, netId, targetPos, targetRot, hardSnap: true))
                    {
                        // Physics owner will apply in FixedUpdate.
                    }
                    else
                    {
                        t.SetPositionAndRotation(targetPos, targetRot);
                        _transformApplyCount++;
                        if (profile == MotionProfile.Wheeled || profile == MotionProfile.Tracked)
                            _overwriteRiskCount++;
                    }
                    PositionVelByNetId[netId] = Vector3.zero;
                    _snapCorrections++;
                }
                else
                {
                    // Motion-grade correction: damped positional convergence removes high-frequency sawtooth on fast movers.
                    Vector3 vel = PositionVelByNetId.TryGetValue(netId, out Vector3 existingVel)
                        ? existingVel
                        : Vector3.zero;
                    float profileSmoothTime = profile == MotionProfile.Wheeled
                        ? smoothTime * 1.25f
                        : profile == MotionProfile.Tracked
                            ? smoothTime * 0.92f
                            : smoothTime;
                    profileSmoothTime = Mathf.Clamp(profileSmoothTime + jitterWeight * 0.04f, 0.05f, 0.30f);
                    Vector3 blendedPos = Vector3.SmoothDamp(
                        currentPos,
                        targetPos,
                        ref vel,
                        profileSmoothTime,
                        Mathf.Infinity,
                        deltaTime);
                    PositionVelByNetId[netId] = vel;
                    float rotT = profile == MotionProfile.Wheeled ? lerpT * 0.85f : lerpT;
                    Quaternion blendedRot = Quaternion.Slerp(currentRot, targetRot, rotT);
                    if (TryQueuePhysicsApply(unit, netId, blendedPos, blendedRot, hardSnap: false))
                    {
                        // Physics owner will apply in FixedUpdate.
                    }
                    else
                    {
                        t.SetPositionAndRotation(blendedPos, blendedRot);
                        _transformApplyCount++;
                        if (profile == MotionProfile.Wheeled || profile == MotionProfile.Tracked)
                            _overwriteRiskCount++;
                    }
                }

                _correctedThisFrame++;
                _totalCorrections++;
                if (profile == MotionProfile.Wheeled)
                    _wheeledCorrections++;
                else if (profile == MotionProfile.Tracked)
                    _trackedCorrections++;
                else
                    _unknownCorrections++;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopSim] correction failed: {ex.Message}");
            if (_safeMode)
            {
                _degradedToCorrectionOff = true;
                MelonLogger.Warning("[CoopSim] safe-mode: disabling phase5 correction for this session.");
            }
            return;
        }

        if (sampleCount > 0)
        {
            _lastFrameAvgPosErr = posErrSum / sampleCount;
            _lastFrameAvgRotErr = rotErrSum / sampleCount;
            _lastFrameMaxPosErr = posErrMax;
            _lastFrameMaxRotErr = rotErrMax;
            if (posErrMax > _sessionMaxPosErr)
                _sessionMaxPosErr = posErrMax;
            if (rotErrMax > _sessionMaxRotErr)
                _sessionMaxRotErr = rotErrMax;
        }
        else
        {
            _lastFrameAvgPosErr = 0f;
            _lastFrameAvgRotErr = 0f;
            _lastFrameMaxPosErr = 0f;
            _lastFrameMaxRotErr = 0f;
        }
        _unitsSeenUnique += seenThisFrame;

        if (_correctedThisFrame > _maxCorrectedInFrame)
            _maxCorrectedInFrame = _correctedThisFrame;
        if (_log && Time.time >= _nextLogTime && _correctedThisFrame > 0)
        {
            MelonLogger.Msg(
                $"[CoopSim] corrected={_correctedThisFrame} total={_totalCorrections} snaps={_snapCorrections} suppress={_suppressedThisFrame}/{_suppressOpsTotal} restore={_restoredThisFrame}/{_restoreOpsTotal} desired={BufferedByNetId.Count} maxFrame={_maxCorrectedInFrame} strength={_strength:0.##} avgErrPos={_lastFrameAvgPosErr:0.##}m maxErrPos={_lastFrameMaxPosErr:0.##}m avgErrRot={_lastFrameAvgRotErr:0.#}deg maxErrRot={_lastFrameMaxRotErr:0.#}deg bufferMiss={_bufferMissCount} budgetDefers={_budgetDeferrals}");
            _nextLogTime = Time.time + LogCooldownSeconds;
        }
    }

    public static void TickFixedUpdate()
    {
        if (PendingPhysicsApplyByNetId.Count == 0 || !CoopSessionState.IsPlaying || !_enabled || _degradedToCorrectionOff)
            return;
        foreach (KeyValuePair<uint, (Vector3 Pos, Quaternion Rot, bool HardSnap)> kv in PendingPhysicsApplyByNetId)
        {
            uint netId = kv.Key;
            Unit? unit = CoopUnitLookup.TryFindByNetId(netId);
            if (unit == null)
                continue;
            Rigidbody? rb = ResolveRigidbody(netId, unit);
            if (rb == null)
                continue;
            var st = kv.Value;
            if (st.HardSnap)
            {
                rb.position = st.Pos;
                rb.rotation = st.Rot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            else
            {
                rb.MovePosition(st.Pos);
                rb.MoveRotation(st.Rot);
            }
        }
        PendingPhysicsApplyByNetId.Clear();
    }

    private static void LogSessionSummaryIfAny()
    {
        if (_totalCorrections == 0 && _snapCorrections == 0 && _maxCorrectedInFrame == 0 && !_degradedToCorrectionOff)
            return;
        MelonLogger.Msg(
            $"[CoopSim][Summary] corrections={_totalCorrections} snaps={_snapCorrections} suppressOps={_suppressOpsTotal} restoreOps={_restoreOpsTotal} maxFrame={_maxCorrectedInFrame} degradedCorrection={_degradedToCorrectionOff} degradedSuppress={_degradedToSuppressOff} suppressReason={_suppressDegradeReason} desiredAtReset={BufferedByNetId.Count} sessionMaxPosErr={_sessionMaxPosErr:0.##}m sessionMaxRotErr={_sessionMaxRotErr:0.#}deg bufferMiss={_bufferMissCount}");
        MelonLogger.Msg(
            $"[CoopSim][Summary] seenUnits={_unitsSeenUnique} ownershipSkips={_ownershipSkipCount} thresholdSkips={_belowThresholdSkipCount} aimApplied={_aimApplyCount} aimMissing={_aimMissingCount} aimThresholdSkips={_aimBelowThresholdSkipCount}");
        float p95Dt = GetSnapshotP95();
        float underrunRatio = _interpSampleCount > 0 ? (float)_interpUnderrunCount / _interpSampleCount : 0f;
        MelonLogger.Msg(
            $"[CoopSim][Summary] timing dtAvg={_snapshotDtEwma:0.000}s dtP95={p95Dt:0.000}s dtMax={_snapshotDtMax:0.000}s jitter={_snapshotJitterEwma:0.000}s interpBack={GetAdaptiveInterpolationBackTime():0.000}s underrun={_interpUnderrunCount}/{_interpSampleCount} ({underrunRatio * 100f:0.#}%)");
        MelonLogger.Msg(
            $"[CoopSim][Summary] classes samples(w/t/u)={_wheeledSamples}/{_trackedSamples}/{_unknownSamples} corrections(w/t/u)={_wheeledCorrections}/{_trackedCorrections}/{_unknownCorrections} budgetDefers={_budgetDeferrals} apply(phys/xfm)={_physicsApplyCount}/{_transformApplyCount} overwriteRisk={_overwriteRiskCount}");
        if (HeavyCorrectionHitsByNetId.Count > 0)
        {
            string top = string.Join(
                ", ",
                HeavyCorrectionHitsByNetId.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}:{kv.Value}"));
            MelonLogger.Msg($"[CoopSim][Summary] heavyCorrectionTop={top}");
        }
    }

    private static bool TrySampleBuffered(in BufferedState buffered, float renderTime, out SnapshotState sampled)
    {
        sampled = default;
        if (!buffered.HasNewer)
            return false;
        if (!buffered.HasOlder)
        {
            sampled = buffered.Newer;
            return true;
        }

        SnapshotState a = buffered.Older;
        SnapshotState b = buffered.Newer;
        if (renderTime <= a.SampleTime)
        {
            sampled = a;
            return true;
        }

        float dt = b.SampleTime - a.SampleTime;
        if (dt <= 0.0001f)
        {
            sampled = b;
            return true;
        }

        if (renderTime >= b.SampleTime)
        {
            sampled = b;
            return true;
        }

        float t = Mathf.Clamp01((renderTime - a.SampleTime) / dt);
        sampled = new SnapshotState(
            Vector3.Lerp(a.Position, b.Position, t),
            Quaternion.Slerp(a.HullRotation, b.HullRotation, t),
            Quaternion.Slerp(a.TurretWorldRotation, b.TurretWorldRotation, t),
            Quaternion.Slerp(a.GunWorldRotation, b.GunWorldRotation, t),
            renderTime);
        return true;
    }

    private static void TryApplyAimables(uint netId, Unit unit, Quaternion turretWorld, Quaternion gunWorld, float lerpT)
    {
        if (!TryResolveAimBinding(netId, unit, out AimBinding binding))
        {
            _aimMissingCount++;
            return;
        }
        if (!binding.AimLoopDisabled)
        {
            binding.TraverseAp?.DisableAiming();
            binding.GunAp?.DisableAiming();
            binding.AimLoopDisabled = true;
            AimBindingByNetId[netId] = binding;
        }
        Transform? traverseTf = binding.VisualTurret ?? binding.Traverse;
        Transform? gunTf = binding.VisualGun ?? binding.Gun;
        if (traverseTf == null)
        {
            _aimMissingCount++;
            return;
        }

        float turretErr = Quaternion.Angle(traverseTf.rotation, turretWorld);
        bool applyTurret = turretErr >= AimCorrectAngleThresholdDegrees;
        bool applyGun = false;
        if (gunTf != null)
        {
            float gunErr = Quaternion.Angle(gunTf.rotation, gunWorld);
            applyGun = gunErr >= AimCorrectAngleThresholdDegrees;
        }

        if (!applyTurret && !applyGun)
        {
            _aimBelowThresholdSkipCount++;
            return;
        }

        // Prefer local-space application so child constraints/hierarchy stay coherent.
        if (applyTurret)
        {
            if (binding.TraverseAp != null)
            {
                Quaternion blendedTurret = Quaternion.Slerp(binding.TraverseAp.Rotation, turretWorld, lerpT);
                binding.TraverseAp.ForceAimVectorNow(blendedTurret * Vector3.forward);
                AimOverwriteProbe.RecordExpected(netId, binding.TraverseAp.Transform, blendedTurret, "turret-ap");
                AimOverwriteProbe.RecordExpected(netId, binding.VisualTurret, blendedTurret, "turret-visual");
            }
            else
            {
                Transform parent = traverseTf.parent;
                Quaternion targetLocal = parent != null
                    ? Quaternion.Inverse(parent.rotation) * turretWorld
                    : turretWorld;
                traverseTf.localRotation = Quaternion.Slerp(traverseTf.localRotation, targetLocal, lerpT);
                AimOverwriteProbe.RecordExpected(netId, traverseTf, traverseTf.rotation, "turret-local");
            }
        }

        if (applyGun && gunTf != null)
        {
            if (binding.GunAp != null)
            {
                Quaternion blendedGun = Quaternion.Slerp(binding.GunAp.Rotation, gunWorld, lerpT);
                binding.GunAp.ForceAimVectorNow(blendedGun * Vector3.forward);
                AimOverwriteProbe.RecordExpected(netId, binding.GunAp.Transform, blendedGun, "gun-ap");
                AimOverwriteProbe.RecordExpected(netId, binding.VisualGun, blendedGun, "gun-visual");
            }
            else
            {
                Quaternion targetGunLocal = Quaternion.Inverse(traverseTf.rotation) * gunWorld;
                gunTf.localRotation = Quaternion.Slerp(gunTf.localRotation, targetGunLocal, lerpT);
                AimOverwriteProbe.RecordExpected(netId, gunTf, gunTf.rotation, "gun-local");
            }
        }

        _aimApplyCount++;
    }

    private static bool TryResolveAimBinding(uint netId, Unit unit, out AimBinding binding)
    {
        if (AimBindingByNetId.TryGetValue(netId, out binding))
        {
            if (binding.Resolved)
                return binding.Traverse != null || binding.VisualTurret != null;
        }

        // First-time (or explicit unresolved) resolution. Keep prior warn-state if any.
        bool warnedMissingVisual = binding.WarnedMissingVisual;
        binding = default;
        binding.WarnedMissingVisual = warnedMissingVisual;
        if (TryPickAimables(unit, out AimablePlatform? traverseAp, out AimablePlatform? gunAp))
        {
            binding.TraverseAp = traverseAp;
            binding.GunAp = gunAp;
            binding.Traverse = traverseAp?.Transform;
            binding.Gun = gunAp?.Transform;
        }

        // Fallback to visible hierarchy pivots on units where AimablePlatform transform is not the rendered pivot.
        Transform root = unit.transform;
        binding.VisualTurret = FindFirstChildContains(root, "turret");
        binding.VisualGun = FindFirstChildContains(root, "gun") ?? FindFirstChildContains(root, "barrel");
        if (binding.VisualGun == null && binding.VisualTurret != null)
            binding.VisualGun = FindFirstChildContains(binding.VisualTurret, "gun") ?? FindFirstChildContains(binding.VisualTurret, "barrel");

        if (binding.VisualTurret == null && !binding.WarnedMissingVisual && _log)
        {
            MelonLogger.Warning($"[CoopSim] aim visual fallback not found for netId={netId}; using AimablePlatform only.");
            binding.WarnedMissingVisual = true;
        }

        if (!binding.LoggedSelection && _log)
        {
            string trApName = binding.TraverseAp?.name ?? "<null>";
            string gunApName = binding.GunAp?.name ?? "<null>";
            string trTfName = binding.Traverse != null ? binding.Traverse.name : "<null>";
            string gunTfName = binding.Gun != null ? binding.Gun.name : "<null>";
            MelonLogger.Msg(
                $"[CoopSim] aim bind netId={netId} traverseAp={trApName} gunAp={gunApName} traverseTf={trTfName} gunTf={gunTfName}");
            binding.LoggedSelection = true;
        }

        binding.Resolved = true;
        AimBindingByNetId[netId] = binding;
        return binding.Traverse != null || binding.VisualTurret != null || binding.TraverseAp != null;
    }

    private static Transform? FindFirstChildContains(Transform root, string token)
    {
        if (root == null)
            return null;
        string needle = token.ToLowerInvariant();
        for (int i = 0; i < root.childCount; i++)
        {
            Transform c = root.GetChild(i);
            if (c.name != null && c.name.ToLowerInvariant().Contains(needle))
                return c;
            Transform? nested = FindFirstChildContains(c, token);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static bool TryPickAimables(Unit unit, out AimablePlatform? traverse, out AimablePlatform? gun)
    {
        traverse = null;
        gun = null;

        // Prefer main-weapon mount chain from FCS to avoid selecting unrelated aimables (e.g. roof MG).
        FireControlSystem? fcs = unit.InfoBroker?.FCS;
        if (fcs != null && TryPickAimablesFromFcs(fcs, out traverse, out gun))
            return true;

        AimablePlatform[]? aps = unit.AimablePlatforms;
        if (aps == null || aps.Length == 0)
            return false;
        foreach (AimablePlatform? ap in aps)
        {
            if (ap == null || ap.Transform == null)
                continue;
            if (ap.ParentPlatform == null)
            {
                traverse = ap;
                break;
            }
        }

        traverse ??= aps[0];
        if (traverse == null || traverse.Transform == null)
            return false;
        foreach (AimablePlatform? ap in aps)
        {
            if (ap == null || ap == traverse || ap.Transform == null)
                continue;
            if (ap.ParentPlatform == traverse || ap.Transform.IsChildOf(traverse.Transform))
            {
                gun = ap;
                break;
            }
        }

        return true;
    }

    private static bool TryPickAimablesFromFcs(FireControlSystem fcs, out AimablePlatform? traverse, out AimablePlatform? gun)
    {
        traverse = null;
        gun = null;
        AimablePlatform[]? mounts = fcs.Mounts;
        if (mounts == null || mounts.Length == 0)
            return false;

        if (fcs.TurretPlatform != null)
            traverse = fcs.TurretPlatform;
        else
            traverse = mounts[0];

        if (mounts.Length >= 2)
            gun = mounts[1];
        else
        {
            for (int i = 0; i < mounts.Length; i++)
            {
                AimablePlatform? ap = mounts[i];
                if (ap == null || ap == traverse)
                    continue;
                if (ap.ParentPlatform == traverse)
                {
                    gun = ap;
                    break;
                }
            }
        }

        return traverse != null;
    }

    private static void EnsureSuppressed(Unit unit, uint netId)
    {
        if (SuppressedByNetId.TryGetValue(netId, out bool already) && already)
            return;
        bool aiOk = TrySetCrewAiEnabled(unit, enabled: false);
        bool driversOk = TrySetMovementDriversEnabled(unit, netId, enabled: false);
        if (!aiOk && !driversOk)
            return;
        SuppressedByNetId[netId] = true;
        _suppressedThisFrame++;
        _suppressOpsTotal++;
    }

    private static void EnsureRestored(Unit unit, uint netId)
    {
        if (!SuppressedByNetId.TryGetValue(netId, out bool wasSuppressed) || !wasSuppressed)
            return;
        TrySetCrewAiEnabled(unit, enabled: true);
        TrySetMovementDriversEnabled(unit, netId, enabled: true);
        SuppressedByNetId[netId] = false;
        _restoredThisFrame++;
        _restoreOpsTotal++;
    }

    private static bool TrySetCrewAiEnabled(Unit unit, bool enabled)
    {
        if (unit == null)
            return false;
        var ai = unit.InfoBroker?.AI;
        if (ai == null)
            return false;
        try
        {
            ai.SetCrewAIEnabled(CrewPosition.Driver, enabled);
            ai.SetCrewAIEnabled(CrewPosition.Gunner, enabled);
            ai.SetCrewAIEnabled(CrewPosition.Commander, enabled);
            // Multiplayer authority rule: for non-owned units on client, stop local AI brain updates
            // and let host snapshots drive presentation.
            if (ai is Behaviour aiBehaviour)
                aiBehaviour.enabled = enabled;
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopSim] soft-suppress failed: {ex.Message}");
            if (_safeMode)
            {
                _degradedToSuppressOff = true;
                _suppressDegradeReason = SuppressDegradeReason.SuppressException;
                _suppressRecoveryAtTime = Time.time + SuppressRecoveryCooldownSeconds;
                MelonLogger.Warning(
                    $"[CoopSim] safe-mode: disabling phase5 soft-suppress for {SuppressRecoveryCooldownSeconds:0.#}s (reason={_suppressDegradeReason}).");
            }

            return false;
        }
    }

    private static void RestoreAllSuppressedUnitsBestEffort()
    {
        foreach (KeyValuePair<uint, bool> kv in SuppressedByNetId)
        {
            if (!kv.Value)
                continue;
            Unit? u = CoopUnitLookup.TryFindByNetId(kv.Key);
            if (u == null)
                continue;
            TrySetCrewAiEnabled(u, enabled: true);
            TrySetMovementDriversEnabled(u, kv.Key, enabled: true);
        }
    }

    private static void RestoreAllAimLoopsBestEffort()
    {
        foreach (KeyValuePair<uint, AimBinding> kv in AimBindingByNetId)
        {
            AimBinding b = kv.Value;
            if (!b.AimLoopDisabled)
                continue;
            b.TraverseAp?.EnableAiming();
            b.GunAp?.EnableAiming();
        }
    }

    private static float GetAdaptiveInterpolationBackTime()
    {
        float adaptive = Mathf.Max(InterpolationBackTimeBaseSeconds, _snapshotDtEwma + _snapshotJitterEwma * 1.5f);
        return Mathf.Clamp(adaptive, MinInterpolationBackTimeSeconds, MaxInterpolationBackTimeSeconds);
    }

    private static float GetPerUnitInterpolationBackTime(uint netId)
    {
        float baseBackTime = GetAdaptiveInterpolationBackTime();
        if (!UnitTimingByNetId.TryGetValue(netId, out var timing))
            return baseBackTime;
        float adaptive = Mathf.Max(InterpolationBackTimeBaseSeconds, timing.DtEwma + timing.JitterEwma * 1.25f);
        adaptive = Mathf.Clamp(adaptive, MinInterpolationBackTimeSeconds, MaxInterpolationBackTimeSeconds);
        return Mathf.Max(baseBackTime * 0.92f, adaptive);
    }

    private static float GetSnapshotP95()
    {
        if (SnapshotDtWindow.Count == 0)
            return 0f;
        var arr = SnapshotDtWindow.ToArray();
        Array.Sort(arr);
        int idx = Mathf.Clamp(Mathf.CeilToInt(arr.Length * 0.95f) - 1, 0, arr.Length - 1);
        return arr[idx];
    }

    private static bool TryQueuePhysicsApply(Unit unit, uint netId, Vector3 pos, Quaternion rot, bool hardSnap)
    {
        Rigidbody? rb = ResolveRigidbody(netId, unit);
        if (rb == null || rb.isKinematic)
            return false;
        PendingPhysicsApplyByNetId[netId] = (pos, rot, hardSnap);
        _physicsApplyCount++;
        return true;
    }

    private static Rigidbody? ResolveRigidbody(uint netId, Unit unit)
    {
        if (RigidBodyByNetId.TryGetValue(netId, out Rigidbody? cached))
            return cached;
        Rigidbody? rb = unit.GetComponentInParent<Rigidbody>();
        rb ??= unit.GetComponentInChildren<Rigidbody>();
        RigidBodyByNetId[netId] = rb;
        return rb;
    }

    private static bool TrySetMovementDriversEnabled(Unit unit, uint netId, bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (SuppressedDriversByNetId.ContainsKey(netId))
                    return true;
                Behaviour[] drivers = unit.GetComponentsInChildren<Behaviour>(true);
                var suppressed = new List<(Behaviour Behaviour, bool WasEnabled)>(8);
                for (int i = 0; i < drivers.Length; i++)
                {
                    Behaviour b = drivers[i];
                    if (b == null || !b.enabled)
                        continue;
                    string? n = b.GetType().FullName;
                    if (string.IsNullOrEmpty(n))
                        continue;
                    if (!n.Contains("VehicleController")
                        && !n.Contains("DriverAI")
                        && !n.Contains("DriverBrain")
                        && !n.Contains("Navigator")
                        && !n.Contains("PathDelayHandler"))
                        continue;
                    suppressed.Add((b, true));
                    b.enabled = false;
                }
                SuppressedDriversByNetId[netId] = suppressed;
                Rigidbody? rb = ResolveRigidbody(netId, unit);
                if (rb != null)
                {
                    SuppressedRigidbodyStateByNetId[netId] = (rb.isKinematic, rb.interpolation);
                    rb.isKinematic = true;
                    rb.interpolation = RigidbodyInterpolation.None;
                }
                return suppressed.Count > 0;
            }

            if (!SuppressedDriversByNetId.TryGetValue(netId, out List<(Behaviour Behaviour, bool WasEnabled)>? list))
                return true;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it.Behaviour == null)
                    continue;
                if (it.WasEnabled)
                    it.Behaviour.enabled = true;
            }
            SuppressedDriversByNetId.Remove(netId);
            if (SuppressedRigidbodyStateByNetId.TryGetValue(netId, out var rbState))
            {
                Rigidbody? rb = ResolveRigidbody(netId, unit);
                if (rb != null)
                {
                    rb.isKinematic = rbState.IsKinematic;
                    rb.interpolation = rbState.Interpolation;
                }
                SuppressedRigidbodyStateByNetId.Remove(netId);
            }
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopSim] movement-driver suppress failed: {ex.Message}");
            return false;
        }
    }

    private static MotionProfile ResolveMotionProfile(uint netId, Unit unit)
    {
        if (MotionProfileByNetId.TryGetValue(netId, out MotionProfile existing))
            return existing;
        MotionProfile profile = MotionProfile.Unknown;
        Behaviour[] all = unit.GetComponentsInChildren<Behaviour>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Behaviour b = all[i];
            if (b == null)
                continue;
            string? n = b.GetType().FullName;
            if (string.IsNullOrEmpty(n))
                continue;
            if (n.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Wheeled", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("VehicleController", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                profile = MotionProfile.Wheeled;
                break;
            }
            if (n.IndexOf("Track", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Tracked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                profile = MotionProfile.Tracked;
            }
        }
        MotionProfileByNetId[netId] = profile;
        return profile;
    }
}
