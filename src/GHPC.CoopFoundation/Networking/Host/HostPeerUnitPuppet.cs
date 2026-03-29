using System.Collections.Generic;
using GHPC;
using GHPC.AI;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Host;

/// <summary>
///     Host-only: drive the real client <see cref="Unit" /> from <see cref="CoopRemoteState" /> so host simulation
///     (physics, LOS, <see cref="Unit.NotifyStruck" />) matches the remote player. Disables vanilla AI / driver
///     controllers that would fight network transforms.
/// </summary>
internal static class HostPeerUnitPuppet
{
    private static readonly List<(Behaviour behaviour, bool wasEnabled)> DisabledBehaviours = new();

    private static readonly List<AimablePlatform> AimPlatformsDisabled = new();

    private static Unit? _activeUnit;

    private static uint _activeNetId;

    private static bool _loggedSkip;

    public static bool Enabled { get; set; } = true;

    public static bool Log { get; set; }

    public static void Reset()
    {
        RestoreAll();
        _activeUnit = null;
        _activeNetId = 0;
        _loggedSkip = false;
    }

    /// <summary>Call from <see cref="UnityEngine.MonoBehaviour.FixedUpdate" /> pipeline (physics).</summary>
    public static void TickFixedUpdate()
    {
        if (!Enabled || !CoopUdpTransport.IsHost || !CoopSessionState.IsPlaying || !CoopUdpTransport.HostHasLobbyPeer)
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
            return;
        }

        // Do not compare Unity instance ids across processes — client snapshot ids are local to the client machine.

        if (!ShouldPuppetUnit(unit, CoopRemoteState.RemoteUnitNetId))
        {
            if (unit == _activeUnit)
                RestoreAll();
            return;
        }

        EnsureActiveUnit(unit, CoopRemoteState.RemoteUnitNetId);

        Vector3 pos = CoopRemoteState.RemotePosition;
        Quaternion hull = CoopRemoteState.RemoteHullRotation;

        IChassis? chassis = unit.Chassis;
        Rigidbody? rb = chassis?.Rigidbody;
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.MovePosition(pos);
            rb.MoveRotation(hull);
        }
        else
        {
            unit.transform.SetPositionAndRotation(pos, hull);
        }
    }

    /// <summary>Call from LateUpdate so aim overrides run after physics.</summary>
    public static void TickLateUpdate()
    {
        if (!Enabled || !CoopUdpTransport.IsHost || !CoopSessionState.IsPlaying || !CoopUdpTransport.HostHasLobbyPeer)
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
                MelonLogger.Msg("[CoopHostPuppet] Skipping: remote snapshot targets host CurrentPlayerUnit (unexpected).");
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
            MelonLogger.Msg($"[CoopHostPuppet] Puppeting netId={netId} unit=\"{unit.UniqueName}\".");
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
    }
}
