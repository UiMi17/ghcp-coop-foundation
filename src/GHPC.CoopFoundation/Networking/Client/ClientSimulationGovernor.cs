using System;
using System.Collections.Generic;
using System.Linq;
using GHPC.Crew;
using GHPC;
using GHPC.CoopFoundation.Networking;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using MelonLoader;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Client;

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

        public readonly Vector3 WorldLinearVelocity;

        public readonly Vector3 WorldAngularVelocity;

        public readonly float BrakePresentation01;

        /// <summary>GHW v5 linear acceleration (m/s²); zero for v4 snapshots.</summary>
        public readonly Vector3 WorldLinearAcceleration;

        /// <summary>GHW v6: NWH motor axis (−1…1); zero if peer sends v5 or lower.</summary>
        public readonly float MotorInputVertical;

        public readonly float SampleTime;

        public SnapshotState(
            Vector3 position,
            Quaternion hullRotation,
            Quaternion turretWorldRotation,
            Quaternion gunWorldRotation,
            Vector3 worldLinearVelocity,
            Vector3 worldAngularVelocity,
            float brakePresentation01,
            Vector3 worldLinearAcceleration,
            float motorInputVertical,
            float sampleTime)
        {
            Position = position;
            HullRotation = hullRotation;
            TurretWorldRotation = turretWorldRotation;
            GunWorldRotation = gunWorldRotation;
            WorldLinearVelocity = worldLinearVelocity;
            WorldAngularVelocity = worldAngularVelocity;
            BrakePresentation01 = brakePresentation01;
            WorldLinearAcceleration = worldLinearAcceleration;
            MotorInputVertical = motorInputVertical;
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

    private static readonly Dictionary<uint, List<(VehicleController Vc, bool WasRiggingEnabled)>> SuppressedRiggingByNetId = new();

    /// <summary>Latest GHW world linear velocity per netId (for track / wheel presentation).</summary>
    private static readonly Dictionary<uint, Vector3> WireLinearVelocityByNetId = new();

    /// <summary>Latest GHW world angular velocity (rad/s) when buffer has not enough samples yet.</summary>
    private static readonly Dictionary<uint, Vector3> WireAngularVelocityByNetId = new();

    private static readonly Dictionary<uint, Vector3> WireLinearAccelerationByNetId = new();

    private static readonly Dictionary<uint, float> WireBrakePresentationByNetId = new();

    private static readonly Dictionary<uint, float> WireMotorVerticalByNetId = new();
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
    private static bool _lodEnabled = true;
    private static float _lodNearMeters = 220f;
    private static float _lodMidMeters = 550f;
    private static int _lodMidAimEveryFrames = 2;
    private static int _lodFarHullEveryFrames = 2;
    private static int _lodFarAimEveryFrames = 6;
    private static int _correctionBudgetMax = 72;
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

    /// <summary>GHW sample pairs with smaller displacement are treated as stationary (reduces bogus netVel / extrapolation jitter).</summary>
    private const float IdleDisplacementEpsilonMeters = 0.006f;

    /// <summary>Do not extrapolate buffered pose when estimated speed is below this (avoids cm rocking when idle).</summary>
    private const float ExtrapolationMinSpeedMetersPerSec = 0.22f;

    /// <summary>
    ///     Hermite position derivative includes (p1−p0) terms even when endpoint wire v≈0, so micro RB jitter on the
    ///     host produces bogus non-zero display velocity (slow track UV crawl). Above this chord we keep Hermite v.
    /// </summary>
    private const float TrackedIdleChordForWireVelocityBlendMeters = 0.045f;

    /// <summary>When both GHW endpoint speeds are below this, prefer wire velocity lerp over Hermite dPdu/dt near idle.</summary>
    private const float TrackedIdleWireSpeedMaxMetersPerSec = 0.55f;
    private const float MaxInterpolationBackTimeSeconds = 0.38f;
    private const float InterpolationBackTimeBaseSeconds = 0.12f;

    public static void Configure(
        bool enabled,
        float strength,
        bool softSuppressEnabled,
        bool log,
        bool safeMode,
        bool lodEnabled,
        float lodNearMeters,
        float lodMidMeters,
        int lodMidAimEveryFrames,
        int lodFarHullEveryFrames,
        int lodFarAimEveryFrames,
        int correctionBudgetMax)
    {
        _enabled = enabled;
        _strength = strength < 0f ? 0f : strength;
        _softSuppressEnabled = softSuppressEnabled;
        _log = log;
        _safeMode = safeMode;
        _lodEnabled = lodEnabled;
        _lodNearMeters = Mathf.Max(30f, lodNearMeters);
        _lodMidMeters = Mathf.Max(_lodNearMeters + 25f, lodMidMeters);
        _lodMidAimEveryFrames = Mathf.Clamp(lodMidAimEveryFrames, 1, 12);
        _lodFarHullEveryFrames = Mathf.Clamp(lodFarHullEveryFrames, 1, 8);
        _lodFarAimEveryFrames = Mathf.Clamp(lodFarAimEveryFrames, 0, 24);
        _correctionBudgetMax = Mathf.Clamp(correctionBudgetMax, 24, 128);
    }

    private static int LodMix(uint netId) => (int)(netId * 2654435761u);

    private static bool LodStrideHit(uint netId, int stride, int salt)
    {
        if (stride <= 1)
            return true;
        int mix = LodMix(netId) + salt;
        return ((Time.frameCount + mix) % stride + stride) % stride == 0;
    }

    private static int GetLodTier(uint netId, in Vector3 unitWorldPos)
    {
        if (!_lodEnabled)
            return 0;
        if (CoopRemoteState.HasData && netId == CoopRemoteState.RemoteUnitNetId)
            return 0;
        float d = MinDistanceToClientReferences(in unitWorldPos);
        if (d <= _lodNearMeters)
            return 0;
        if (d <= _lodMidMeters)
            return 1;
        return 2;
    }

    private static bool ShouldRunGovernorLodAim(int tier, uint netId)
    {
        if (!_lodEnabled)
            return true;
        if (tier <= 0)
            return true;
        if (tier == 1)
            return LodStrideHit(netId, _lodMidAimEveryFrames, 3);
        if (_lodFarAimEveryFrames <= 0)
            return false;
        return LodStrideHit(netId, _lodFarAimEveryFrames, 11);
    }

    private static bool ShouldRunGovernorLodHull(int tier, uint netId)
    {
        if (!_lodEnabled)
            return true;
        if (tier < 2)
            return true;
        return LodStrideHit(netId, _lodFarHullEveryFrames, 0);
    }

    /// <summary>Far LOD tier: skip work on some frames (salt separates Late vs Fixed cadence).</summary>
    internal static bool ShouldThrottleLodFarTierWork(uint netId, in Vector3 unitWorldPos, int salt)
    {
        if (!_lodEnabled)
            return false;
        if (GetLodTier(netId, in unitWorldPos) < 2)
            return false;
        int stride = Mathf.Max(2, _lodFarHullEveryFrames);
        return !LodStrideHit(netId, stride, salt);
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
        HeavyCorrectionHitsByNetId.Clear();
        MotionProfileByNetId.Clear();
        UnitTimingByNetId.Clear();
        SnapshotDtWindow.Clear();
        RestoreAllAimLoopsBestEffort();
        AimBindingByNetId.Clear();
        RestoreAllSuppressedUnitsBestEffort();
        CoopRemotePuppetPresentationCache.ClearSession();
        CoopPuppetWheelRegistry.ClearSession();
        SuppressedDriversByNetId.Clear();
        foreach (KeyValuePair<uint, List<(VehicleController Vc, bool WasRiggingEnabled)>> kv in SuppressedRiggingByNetId)
            CoopNwhRiggingSuppress.Restore(kv.Value);
        SuppressedRiggingByNetId.Clear();
        WireLinearVelocityByNetId.Clear();
        WireAngularVelocityByNetId.Clear();
        WireLinearAccelerationByNetId.Clear();
        WireBrakePresentationByNetId.Clear();
        WireMotorVerticalByNetId.Clear();
        SuppressedRigidbodyStateByNetId.Clear();
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
        CoopPlayerScenarioDiagnostics.NotifySessionReset();
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
            // Observer client: lobby peer hull is driven from GHP (ClientPeerUnitPuppet), not GHW interpolation.
            if (IsClientRemotePeerHullGhpOnly(e.NetId))
                continue;
            SeenInFrame.Add(e.NetId);
            WireLinearVelocityByNetId[e.NetId] = e.WorldLinearVelocity;
            WireAngularVelocityByNetId[e.NetId] = e.WorldAngularVelocity;
            WireLinearAccelerationByNetId[e.NetId] = e.WorldLinearAcceleration;
            SnapshotState snap = new(
                e.Position,
                e.HullRotation,
                e.TurretWorldRotation,
                e.GunWorldRotation,
                e.WorldLinearVelocity,
                e.WorldAngularVelocity,
                e.BrakePresentation01,
                e.WorldLinearAcceleration,
                e.MotorInputVertical,
                now);
            WireBrakePresentationByNetId[e.NetId] = e.BrakePresentation01;
            WireMotorVerticalByNetId[e.NetId] = e.MotorInputVertical;
            if (!BufferedByNetId.TryGetValue(e.NetId, out BufferedState buffered))
                buffered = default;
            if (buffered.HasNewer)
            {
                SnapshotState prev = buffered.Newer;
                float dt = now - prev.SampleTime;
                if (dt > 1e-4f && dt < 1.5f)
                {
                    Vector3 deltaPos = snap.Position - prev.Position;
                    float idleEpsSq = IdleDisplacementEpsilonMeters * IdleDisplacementEpsilonMeters;
                    Vector3 rawVel = deltaPos.sqrMagnitude < idleEpsSq ? Vector3.zero : deltaPos / dt;
                    Vector3 oldVel = NetVelocityByNetId.TryGetValue(e.NetId, out Vector3 v) ? v : Vector3.zero;
                    float blend = rawVel.sqrMagnitude < 1e-12f ? 0.45f : 0.35f;
                    NetVelocityByNetId[e.NetId] = Vector3.Lerp(oldVel, rawVel, blend);
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
            SuppressedRiggingByNetId.Remove(stale[i]);
            WireLinearVelocityByNetId.Remove(stale[i]);
            WireAngularVelocityByNetId.Remove(stale[i]);
            WireLinearAccelerationByNetId.Remove(stale[i]);
            WireBrakePresentationByNetId.Remove(stale[i]);
            WireMotorVerticalByNetId.Remove(stale[i]);
            HeavyCorrectionHitsByNetId.Remove(stale[i]);
            MotionProfileByNetId.Remove(stale[i]);
            UnitTimingByNetId.Remove(stale[i]);
            SuppressedRigidbodyStateByNetId.Remove(stale[i]);
            CoopRemotePuppetPresentationCache.Remove(stale[i]);
            CoopPuppetWheelRegistry.UnregisterNetId(stale[i]);
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

        // Host never merges GHW into this buffer (only the client does); skip the heavy per-entity loop entirely.
        if (CoopUdpTransport.IsHost && BufferedByNetId.Count == 0)
            return;

        // GHP-only peer hull: keep puppet suppress + wheel registry even when that netId is absent from the GHW buffer.
        if (CoopUdpTransport.IsClient && _softSuppressEnabled && !_degradedToSuppressOff && CoopRemoteState.HasData
            && CoopRemoteState.RemoteUnitNetId != 0)
        {
            uint rid = CoopRemoteState.RemoteUnitNetId;
            if (!IsGovernorSkippedAsLocalOwned(rid))
            {
                Unit? ru = CoopUnitLookup.TryFindByNetId(rid);
                if (ru != null)
                    EnsureSuppressed(ru, rid);
            }
        }

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
        int correctionBudgetPerFrame = Mathf.Clamp(16 + BufferedByNetId.Count / 3, 16, _correctionBudgetMax);
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
                {
                    CoopReplicationDiagnostics.LogGovernorUnitNotFound(netId);
                    continue;
                }

                if (_softSuppressEnabled && !_degradedToSuppressOff)
                {
                    EnsureSuppressed(unit, netId);
                }

                if (!TrySampleBuffered(netId, unit, kv.Value, renderTime, out SnapshotState targetState))
                {
                    _bufferMissCount++;
                    continue;
                }
                _interpSampleCount++;
                if (kv.Value.HasNewer && renderTime >= kv.Value.Newer.SampleTime)
                    _interpUnderrunCount++;

                int lodTier = GetLodTier(netId, unit.transform.position);
                bool runAim = ShouldRunGovernorLodAim(lodTier, netId);
                bool runHull = ShouldRunGovernorLodHull(lodTier, netId);
                if (!runAim && !runHull)
                    continue;

                if (runAim)
                    TryApplyAimables(netId, unit, targetState.TurretWorldRotation, targetState.GunWorldRotation, lerpT);

                if (!runHull)
                    continue;

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
                Vector3 extrapVel = profile == MotionProfile.Tracked
                    ? targetState.WorldLinearVelocity
                    : NetVelocityByNetId.TryGetValue(netId, out Vector3 nv) ? nv : Vector3.zero;
                float speed = extrapVel.magnitude;
                if (speed >= ExtrapolationMinSpeedMetersPerSec)
                {
                    float extrapolation = speed > 6f ? 0.022f : speed > 2.5f ? 0.035f : 0.052f;
                    if (profile == MotionProfile.Wheeled)
                        extrapolation *= 0.75f;
                    if (profile == MotionProfile.Tracked)
                        extrapolation *= 0.82f;
                    extrapolation = Mathf.Lerp(extrapolation, extrapolation * 0.6f, jitterWeight);
                    extrapolation = Mathf.Min(extrapolation, ExtrapolationClampSeconds);
                    Vector3 offset = extrapVel * extrapolation;
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

                float speedForDeadband = profile == MotionProfile.Tracked
                    ? targetState.WorldLinearVelocity.magnitude
                    : NetVelocityByNetId.TryGetValue(netId, out Vector3 sv) ? sv.magnitude : 0f;
                bool puppetVisual = SuppressedByNetId.TryGetValue(netId, out bool sup) && sup;
                float profileDeadband = profile == MotionProfile.Wheeled ? 0.22f : profile == MotionProfile.Tracked ? 0.13f : 0.16f;
                profileDeadband += Mathf.Clamp(speedForDeadband * 0.01f, 0f, 0.08f);
                float correctionDistSqAdaptive = Mathf.Max(correctionDistSq, profileDeadband * profileDeadband);
                if (puppetVisual && speedForDeadband < 4.5f)
                    correctionDistSqAdaptive *= 1.22f;
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
                    if (profile == MotionProfile.Tracked)
                    {
                        float b = targetState.BrakePresentation01;
                        profileSmoothTime *= 1f + b * (1.05f + Mathf.Clamp01(targetState.WorldLinearAcceleration.magnitude / 28f) * 0.45f);
                        float reverseIntent = Mathf.Clamp01(-Mathf.Min(0f, targetState.MotorInputVertical));
                        profileSmoothTime *= 1f + reverseIntent * 0.38f;
                    }

                    float smoothUpper = profile == MotionProfile.Tracked ? 0.44f : 0.30f;
                    profileSmoothTime = Mathf.Clamp(profileSmoothTime + jitterWeight * 0.04f, 0.05f, smoothUpper);
                    if (puppetVisual)
                    {
                        // Softer hull convergence: stowage / exterior props inherit root jitter from net corrections.
                        float puppetCap = profile == MotionProfile.Tracked ? 0.52f : 0.38f;
                        profileSmoothTime = Mathf.Min(profileSmoothTime * 1.28f, puppetCap);
                    }

                    Vector3 blendedPos = Vector3.SmoothDamp(
                        currentPos,
                        targetPos,
                        ref vel,
                        profileSmoothTime,
                        Mathf.Infinity,
                        deltaTime);
                    PositionVelByNetId[netId] = vel;
                    float rotT = profile == MotionProfile.Wheeled ? lerpT * 0.85f : lerpT;
                    if (puppetVisual)
                        rotT *= 0.56f;
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

    private static bool TrySampleBuffered(uint netId, Unit? unit, in BufferedState buffered, float renderTime, out SnapshotState sampled)
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
        MotionProfile profile = unit != null
            ? ResolveMotionProfile(netId, unit)
            : MotionProfileByNetId.TryGetValue(netId, out MotionProfile mp) ? mp : MotionProfile.Unknown;

        Vector3 pos;
        Vector3 linVel;
        if (profile == MotionProfile.Tracked)
        {
            HermiteTrackedPositionAndVelocity(a, b, t, dt, out pos, out linVel);
            float chordMag = (b.Position - a.Position).magnitude;
            float wireSpeedMax = Mathf.Max(a.WorldLinearVelocity.magnitude, b.WorldLinearVelocity.magnitude);
            if (wireSpeedMax < TrackedIdleWireSpeedMaxMetersPerSec && chordMag < TrackedIdleChordForWireVelocityBlendMeters)
                linVel = Vector3.Lerp(a.WorldLinearVelocity, b.WorldLinearVelocity, t);
        }
        else
        {
            pos = Vector3.Lerp(a.Position, b.Position, t);
            linVel = Vector3.Lerp(a.WorldLinearVelocity, b.WorldLinearVelocity, t);
        }

        sampled = new SnapshotState(
            pos,
            Quaternion.Slerp(a.HullRotation, b.HullRotation, t),
            Quaternion.Slerp(a.TurretWorldRotation, b.TurretWorldRotation, t),
            Quaternion.Slerp(a.GunWorldRotation, b.GunWorldRotation, t),
            linVel,
            Vector3.Lerp(a.WorldAngularVelocity, b.WorldAngularVelocity, t),
            Mathf.Lerp(a.BrakePresentation01, b.BrakePresentation01, t),
            Vector3.Lerp(a.WorldLinearAcceleration, b.WorldLinearAcceleration, t),
            Mathf.Lerp(a.MotorInputVertical, b.MotorInputVertical, t),
            renderTime);
        return true;
    }

    /// <summary>
    ///     Cubic Hermite between two host snapshots using endpoint world linear velocities as tangents (GHPC/NWH:
    ///     tracked vehicles coast and brake through forces — linear pos+vel lerp looks toy-like on hard stops).
    ///     Tangents are magnitude-clamped vs chord to limit overshoot when wire velocities disagree with displacement.
    /// </summary>
    private static void HermiteTrackedPositionAndVelocity(
        in SnapshotState a,
        in SnapshotState b,
        float u,
        float dtReal,
        out Vector3 position,
        out Vector3 worldLinearVelocity)
    {
        Vector3 p0 = a.Position;
        Vector3 p1 = b.Position;
        Vector3 v0 = a.WorldLinearVelocity;
        Vector3 v1 = b.WorldLinearVelocity;
        float dt = Mathf.Max(1e-4f, dtReal);
        Vector3 m0 = v0 * dt;
        Vector3 m1 = v1 * dt;
        Vector3 chord = p1 - p0;
        float chordMag = chord.magnitude;
        float tanCap = Mathf.Max(chordMag * 1.65f, ExtrapolationMinSpeedMetersPerSec * dt * 0.55f);
        tanCap = Mathf.Max(tanCap, 0.35f);
        if (m0.sqrMagnitude > tanCap * tanCap)
            m0 = m0.normalized * tanCap;
        if (m1.sqrMagnitude > tanCap * tanCap)
            m1 = m1.normalized * tanCap;

        float u2 = u * u;
        float u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f;
        float h10 = u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2;
        float h11 = u3 - u2;
        position = h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;

        float dh00 = 6f * u2 - 6f * u;
        float dh10 = 3f * u2 - 4f * u + 1f;
        float dh01 = -6f * u2 + 6f * u;
        float dh11 = 3f * u2 - 2f * u;
        Vector3 dPdu = dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
        worldLinearVelocity = dPdu / dt;
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
        if (CoopAimableSampler.TryGetTraverseAndGun(unit, out AimablePlatform? traverseAp, out AimablePlatform? gunAp))
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
        CoopRemotePuppetPresentationCache.Register(netId, unit);
        CoopPuppetWheelRegistry.RegisterWheelsForPuppet(netId, unit);
    }

    private static void EnsureRestored(Unit unit, uint netId)
    {
        if (!SuppressedByNetId.TryGetValue(netId, out bool wasSuppressed) || !wasSuppressed)
            return;
        TrySetCrewAiEnabled(unit, enabled: true);
        TrySetMovementDriversEnabled(unit, netId, enabled: true);
        CoopRemotePuppetPresentationCache.Remove(netId);
        CoopPuppetWheelRegistry.UnregisterNetId(netId);
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
            CoopRemotePuppetPresentationCache.Remove(kv.Key);
            CoopPuppetWheelRegistry.UnregisterNetId(kv.Key);
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

                // Same as host CoopVanillaVehicleDriverMute: NwhChassis.FixedUpdate still runs with VC off and
                // touches the chassis RB (parking brake / velocity), causing cm-scale wobble vs network pose.
                NwhChassis? nwh = unit.GetComponentInChildren<NwhChassis>(true);
                if (nwh != null && nwh.enabled)
                {
                    suppressed.Add((nwh, true));
                    nwh.enabled = false;
                }

                if (!CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
                    CoopNwhWheelControllerSuppress.DisableAllOnUnit(unit, suppressed);

                var rigging = new List<(VehicleController Vc, bool WasRiggingEnabled)>(2);
                if (!CoopNwhPuppetSettings.RiggingEnabledOnPuppets)
                    CoopNwhRiggingSuppress.DisableOnUnit(unit, rigging);
                if (rigging.Count > 0)
                    SuppressedRiggingByNetId[netId] = rigging;

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
            if (SuppressedRiggingByNetId.TryGetValue(netId, out List<(VehicleController Vc, bool WasRiggingEnabled)>? rig))
            {
                CoopNwhRiggingSuppress.Restore(rig);
                SuppressedRiggingByNetId.Remove(netId);
            }
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
        CoopRemotePuppetPresentationCache.TryGetVehicleController(netId, unit, out VehicleController? vc);
        if (vc != null && vc.tracks != null && vc.tracks.trackedVehicle)
        {
            MotionProfileByNetId[netId] = MotionProfile.Tracked;
            return MotionProfile.Tracked;
        }

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

    internal static IEnumerable<uint> EnumerateSuppressedNetIds()
    {
        foreach (KeyValuePair<uint, bool> kv in SuppressedByNetId)
        {
            if (kv.Value)
                yield return kv.Key;
        }
    }

    /// <summary>True when this netId is a remote client puppet (soft suppress + movement drivers off).</summary>
    internal static bool IsClientSuppressedPuppet(uint netId)
    {
        return SuppressedByNetId.TryGetValue(netId, out bool s) && s;
    }

    /// <summary>Min distance from <paramref name="world" /> to main camera and local controlled unit (for LOD).</summary>
    internal static float MinDistanceToClientReferences(in Vector3 world)
    {
        float d = float.MaxValue;
        var cam = global::UnityEngine.Camera.main;
        if (cam != null)
            d = Mathf.Min(d, Vector3.Distance(world, cam.transform.position));
        Unit? u = CoopSessionState.ControlledUnit;
        if (u != null)
            d = Mathf.Min(d, Vector3.Distance(world, u.transform.position));
        return d >= float.MaxValue * 0.5f ? 0f : d;
    }

    internal static bool TryGetNetIdWorldPosition(uint netId, out Vector3 world)
    {
        world = default;
        Unit? u = CoopUnitLookup.TryFindByNetId(netId);
        if (u == null)
            return false;
        world = u.transform.position;
        return true;
    }

    /// <summary>Last merged GHW wire velocities only (no Hermite resample).</summary>
    internal static bool TryGetWireVelocitiesOnly(uint netId, out Vector3 linear, out Vector3 angular)
    {
        linear = angular = default;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            linear = CoopRemoteState.RemoteWorldLinearVelocity;
            angular = CoopRemoteState.RemoteWorldAngularVelocity;
            return true;
        }

        bool okL = WireLinearVelocityByNetId.TryGetValue(netId, out linear);
        bool okA = WireAngularVelocityByNetId.TryGetValue(netId, out angular);
        return okL || okA;
    }

    /// <summary>
    ///     One buffered Hermite sample for both display velocities (NWH puppet FixedUpdate — avoids double
    ///     <see cref="TrySampleBuffered" /> vs separate linear/angular calls).
    /// </summary>
    internal static bool TryGetDisplayVelocities(uint netId, out Vector3 linear, out Vector3 angular)
    {
        linear = angular = default;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            linear = CoopRemoteState.RemoteWorldLinearVelocity;
            angular = CoopRemoteState.RemoteWorldAngularVelocity;
            return true;
        }

        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                linear = s.WorldLinearVelocity;
                angular = s.WorldAngularVelocity;
                return true;
            }
        }

        bool okL = WireLinearVelocityByNetId.TryGetValue(netId, out linear);
        bool okA = WireAngularVelocityByNetId.TryGetValue(netId, out angular);
        return okL || okA;
    }

    /// <summary>
    ///     One buffered sample for linear + angular + motor (synthetic track LateUpdate — avoids triple
    ///     <see cref="TrySampleBuffered" /> from vel + ang + motor calls).
    /// </summary>
    internal static bool TryGetDisplayLinAngMotor(uint netId, out Vector3 linear, out Vector3 angular, out float motorVertical)
    {
        linear = angular = default;
        motorVertical = 0f;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            linear = CoopRemoteState.RemoteWorldLinearVelocity;
            angular = CoopRemoteState.RemoteWorldAngularVelocity;
            WireMotorVerticalByNetId.TryGetValue(netId, out motorVertical);
            return true;
        }

        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                linear = s.WorldLinearVelocity;
                angular = s.WorldAngularVelocity;
                motorVertical = s.MotorInputVertical;
                return true;
            }
        }

        bool okL = WireLinearVelocityByNetId.TryGetValue(netId, out linear);
        bool okA = WireAngularVelocityByNetId.TryGetValue(netId, out angular);
        bool okM = WireMotorVerticalByNetId.TryGetValue(netId, out motorVertical);
        return okL || okA || okM;
    }

    /// <summary>Interpolated linear velocity from GHW buffer for smooth track UV.</summary>
    internal static bool TryGetDisplayLinearVelocity(uint netId, out Vector3 velocity)
    {
        velocity = default;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            velocity = CoopRemoteState.RemoteWorldLinearVelocity;
            return true;
        }

        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                velocity = s.WorldLinearVelocity;
                return true;
            }
        }

        return WireLinearVelocityByNetId.TryGetValue(netId, out velocity);
    }

    /// <summary>Interpolated angular velocity (rad/s) from GHW buffer for track differential / wheel rim speed.</summary>
    internal static bool TryGetDisplayAngularVelocity(uint netId, out Vector3 angularVelocity)
    {
        angularVelocity = default;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            angularVelocity = CoopRemoteState.RemoteWorldAngularVelocity;
            return true;
        }

        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                angularVelocity = s.WorldAngularVelocity;
                return true;
            }
        }

        return WireAngularVelocityByNetId.TryGetValue(netId, out angularVelocity);
    }

    internal static bool TryGetDisplayBrakePresentation01(uint netId, out float brake01)
    {
        brake01 = 0f;
        if (IsClientRemotePeerHullGhpOnly(netId))
        {
            brake01 = CoopRemoteState.RemoteBrakePresentation01;
            return true;
        }

        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                brake01 = s.BrakePresentation01;
                return true;
            }
        }

        return WireBrakePresentationByNetId.TryGetValue(netId, out brake01);
    }

    /// <summary>Interpolated linear acceleration (m/s²) from GHW v5 buffer; zero if v4-only peer.</summary>
    internal static bool TryGetDisplayLinearAcceleration(uint netId, out Vector3 acceleration)
    {
        acceleration = default;
        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                acceleration = s.WorldLinearAcceleration;
                return true;
            }
        }

        return WireLinearAccelerationByNetId.TryGetValue(netId, out acceleration);
    }

    /// <summary>Latest or interpolated GHW v6 motor axis (−1…1); 0 if buffer/v6 data missing.</summary>
    internal static bool TryGetDisplayMotorInputVertical(uint netId, out float motorVertical)
    {
        motorVertical = 0f;
        if (BufferedByNetId.TryGetValue(netId, out BufferedState buf))
        {
            float renderTime = Time.time - GetAdaptiveInterpolationBackTime();
            Unit? u = CoopUnitLookup.TryFindByNetId(netId);
            if (TrySampleBuffered(netId, u, buf, renderTime, out SnapshotState s))
            {
                motorVertical = s.MotorInputVertical;
                return true;
            }
        }

        return WireMotorVerticalByNetId.TryGetValue(netId, out motorVertical);
    }

    internal static IEnumerable<uint> EnumerateBufferedNetIds()
    {
        foreach (KeyValuePair<uint, BufferedState> kv in BufferedByNetId)
            yield return kv.Key;
    }

    internal static bool IsSoftSuppressionConfigured => _softSuppressEnabled;

    internal static bool IsSuppressDegraded => _degradedToSuppressOff;

    internal static bool IsCorrectionDegraded => _degradedToCorrectionOff;

    internal static bool IsGovernorEnabled => _enabled;

    internal static bool IsGovernorSkippedAsLocalOwned(uint netId)
    {
        uint localControlledNetId = 0;
        Unit? localControlled = CoopSessionState.ControlledUnit;
        if (localControlled != null)
            localControlledNetId = CoopUnitWireRegistry.GetWireId(localControlled);
        return netId == localControlledNetId || CoopVehicleOwnership.IsLocalOwner(netId);
    }

    /// <summary>True when this netId is the lobby peer on an observer client: hull motion is GHP-only (not GHW buffer).</summary>
    internal static bool IsClientRemotePeerHullGhpOnly(uint netId) =>
        CoopUdpTransport.IsClient && CoopRemoteState.HasData && CoopRemoteState.RemoteUnitNetId != 0
        && netId == CoopRemoteState.RemoteUnitNetId;

    internal static bool TryGetLatestWireLinearVelocity(uint netId, out Vector3 velocity) =>
        WireLinearVelocityByNetId.TryGetValue(netId, out velocity);

    internal static bool TryGetLatestWireAngularVelocity(uint netId, out Vector3 angularVelocity) =>
        WireAngularVelocityByNetId.TryGetValue(netId, out angularVelocity);
}
