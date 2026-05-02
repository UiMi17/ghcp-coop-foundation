using System.Collections.Generic;
using GHPC;
using GHPC.AI;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Client;

/// <summary>
///     Client-only: drive the remote host's <see cref="Unit" /> hull and aim from <see cref="CoopRemoteState" /> (GHP),
///     symmetric to <see cref="GHPC.CoopFoundation.Networking.Host.HostPeerUnitPuppet" /> on the host. Excludes that netId from the GHW governor buffer
///     so low-rate world snapshots do not fight high-rate peer snapshots.
/// </summary>
internal static class ClientPeerUnitPuppet
{
    private static readonly List<(Behaviour behaviour, bool wasEnabled)> DisabledBehaviours = new();

    private static readonly List<AimablePlatform> AimPlatformsDisabled = new();

    private static Unit? _activeUnit;

    private static uint _activeNetId;

    private static bool _loggedSkip;

    private static CoopVanillaVehicleDriverMute? _driverMute;

    private static Vector3 _hullFollowPosVel;

    public static bool Enabled { get; set; } = true;

    public static bool Log { get; set; }

    public static void Reset()
    {
        RestoreAll();
        _activeUnit = null;
        _activeNetId = 0;
        _loggedSkip = false;
        _hullFollowPosVel = Vector3.zero;
    }

    internal static bool TryGetActivePuppet(out Unit? unit, out uint netId)
    {
        unit = _activeUnit;
        netId = _activeNetId;
        return unit != null && netId != 0;
    }

    /// <summary>Call from <see cref="UnityEngine.MonoBehaviour.FixedUpdate" /> (physics).</summary>
    public static void TickFixedUpdate()
    {
        if (!Enabled || !CoopUdpTransport.IsClient || !CoopUdpTransport.IsNetworkActive || !CoopSessionState.IsPlaying)
        {
            if (_activeUnit != null)
                RestoreAll();
            return;
        }

        if (!CoopRemoteState.HasData || CoopRemoteState.RemoteUnitNetId == 0)
        {
            if (_activeUnit != null)
                RestoreAll();
            return;
        }

        Unit? unit = CoopUnitLookup.TryFindByNetId(CoopRemoteState.RemoteUnitNetId);
        if (unit == null || unit.gameObject == null)
        {
            if (_activeUnit != null)
                RestoreAll();
            CoopReplicationDiagnostics.LogClientPeerUnitNotFound(CoopRemoteState.RemoteUnitNetId);
            return;
        }

        if (!ShouldPuppetUnit(unit, CoopRemoteState.RemoteUnitNetId))
        {
            if (unit == _activeUnit)
                RestoreAll();
            return;
        }

        EnsureActiveUnit(unit, CoopRemoteState.RemoteUnitNetId);

        Vector3 pos = CoopRemoteState.RemotePosition;
        Quaternion hull = CoopRemoteState.RemoteHullRotation;
        float dt = Time.fixedDeltaTime;

        IChassis? chassis = unit.Chassis;
        Rigidbody? rb = chassis?.Rigidbody;
        if (rb != null)
        {
            const float posSmoothSec = 0.088f;
            const float rotRate = 8.5f;
            Vector3 cur = rb.position;
            Quaternion curQ = rb.rotation;
            Vector3 next = Vector3.SmoothDamp(cur, pos, ref _hullFollowPosVel, posSmoothSec, Mathf.Infinity, dt);
            float rotT = Mathf.Clamp01(rotRate * dt);
            Quaternion nextQ = Quaternion.Slerp(curQ, hull, rotT);
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.MovePosition(next);
            rb.MoveRotation(nextQ);
        }
        else
        {
            Transform tr = unit.transform;
            Vector3 cur = tr.position;
            Quaternion curQ = tr.rotation;
            Vector3 next = Vector3.SmoothDamp(cur, pos, ref _hullFollowPosVel, 0.088f, Mathf.Infinity, dt);
            Quaternion nextQ = Quaternion.Slerp(curQ, hull, Mathf.Clamp01(8.5f * dt));
            tr.SetPositionAndRotation(next, nextQ);
        }
    }

    /// <summary>Call from LateUpdate so aim overrides run after physics.</summary>
    public static void TickLateUpdate()
    {
        if (!Enabled || !CoopUdpTransport.IsClient || !CoopUdpTransport.IsNetworkActive || !CoopSessionState.IsPlaying)
            return;
        if (!CoopRemoteState.HasData || CoopRemoteState.RemoteUnitNetId == 0)
            return;

        Unit? unit = CoopUnitLookup.TryFindByNetId(CoopRemoteState.RemoteUnitNetId);
        if (unit == null || unit != _activeUnit)
            return;

        ApplyAim(unit, CoopRemoteState.RemoteTurretWorldRotation, CoopRemoteState.RemoteGunWorldRotation);
    }

    private static bool ShouldPuppetUnit(Unit unit, uint netId)
    {
        PlayerInput? input = PlayerInput.Instance;
        if (input != null && input.CurrentPlayerUnit == unit)
        {
            if (Log && !_loggedSkip)
            {
                _loggedSkip = true;
                MelonLogger.Msg("[CoopClientPuppet] Skipping: remote snapshot targets client CurrentPlayerUnit (unexpected).");
            }

            return false;
        }

        _loggedSkip = false;

        byte owner = CoopVehicleOwnership.GetOwnerPeer(netId);
        byte local = CoopVehicleOwnership.LocalPeerId;
        if (owner != 0 && owner == local)
            return false;

        return true;
    }

    private static void EnsureActiveUnit(Unit unit, uint netId)
    {
        if (_activeUnit == unit && _activeNetId == netId)
            return;

        RestoreAll();
        _activeUnit = unit;
        _activeNetId = netId;
        _hullFollowPosVel = Vector3.zero;

        UnitInfoBroker? broker = unit.InfoBroker;
        if (broker == null)
            return;

        if (broker.AI != null)
        {
            Behaviour b = broker.AI;
            DisabledBehaviours.Add((b, b.enabled));
            b.enabled = false;
        }

        if (broker.DriverAI is Behaviour driverB)
        {
            DisabledBehaviours.Add((driverB, driverB.enabled));
            driverB.enabled = false;
        }

        HelicopterAIController? heliAi = broker.HelicopterAI;
        if (heliAi != null)
        {
            DisabledBehaviours.Add((heliAi, heliAi.enabled));
            heliAi.enabled = false;
        }

        CoopVanillaVehicleDriverMute.TryBegin(unit, out _driverMute);
        if (Log && _driverMute == null)
            MelonLogger.Msg("[CoopClientPuppet] No NWH drivers muted (unusual for tracked vehicle).");

        if (!CoopAimableSampler.TryGetTraverseAndGun(unit, out AimablePlatform? traverse, out AimablePlatform? gun))
            return;

        if (traverse != null)
        {
            traverse.DisableAiming();
            AimPlatformsDisabled.Add(traverse);
        }

        if (gun != null && gun != traverse)
        {
            gun.DisableAiming();
            AimPlatformsDisabled.Add(gun);
        }

        if (Log)
            MelonLogger.Msg($"[CoopClientPuppet] Puppeting netId={netId} unit=\"{unit.UniqueName}\".");
    }

    private static void ApplyAim(Unit unit, Quaternion turretWorld, Quaternion gunWorld)
    {
        if (!CoopAimableSampler.TryGetTraverseAndGun(unit, out AimablePlatform? traverse, out AimablePlatform? gun))
            return;

        Vector3 turretForward = turretWorld * Vector3.forward;
        if (turretForward.sqrMagnitude > 1e-8f)
            traverse?.ForceAimVectorNow(turretForward);

        if (gun != null)
        {
            Vector3 gunForward = gunWorld * Vector3.forward;
            if (gunForward.sqrMagnitude > 1e-8f)
                gun.ForceAimVectorNow(gunForward);
        }
    }

    private static void RestoreAll()
    {
        _driverMute?.Restore();
        _driverMute = null;

        for (int i = 0; i < DisabledBehaviours.Count; i++)
        {
            (Behaviour behaviour, bool wasEnabled) = DisabledBehaviours[i];
            if (behaviour != null)
                behaviour.enabled = wasEnabled;
        }

        DisabledBehaviours.Clear();

        for (int i = 0; i < AimPlatformsDisabled.Count; i++)
        {
            AimablePlatform? ap = AimPlatformsDisabled[i];
            ap?.EnableAiming();
        }

        AimPlatformsDisabled.Clear();
        _activeUnit = null;
        _activeNetId = 0;
        _hullFollowPosVel = Vector3.zero;
    }
}
