using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Protocol;

/// <summary>Last accepted remote snapshot (main thread only).</summary>
internal static class CoopRemoteState
{
    public static bool HasData { get; private set; }

    public static uint LastSequence { get; private set; }

    public static int RemoteUnitInstanceId { get; private set; }

    public static Vector3 RemotePosition { get; private set; }

    /// <summary>World linear velocity (m/s) from peer GHP v4 snapshot.</summary>
    public static Vector3 RemoteWorldLinearVelocity { get; private set; }

    /// <summary>World angular velocity (rad/s) from peer GHP v5 snapshot.</summary>
    public static Vector3 RemoteWorldAngularVelocity { get; private set; }

    /// <summary>Brake presentation 0–1 from peer GHP v6.</summary>
    public static float RemoteBrakePresentation01 { get; private set; }

    public static Quaternion RemoteHullRotation { get; private set; }

    public static Quaternion RemoteTurretWorldRotation { get; private set; }

    public static Quaternion RemoteGunWorldRotation { get; private set; }

    public static uint RemoteUnitNetId { get; private set; }

    public static void Apply(
        uint sequence,
        int instanceId,
        Vector3 position,
        Quaternion hullRotation,
        Quaternion turretWorldRotation,
        Quaternion gunWorldRotation,
        uint unitNetId,
        Vector3 worldLinearVelocity,
        Vector3 worldAngularVelocity,
        float brakePresentation01)
    {
        HasData = true;
        LastSequence = sequence;
        RemoteUnitInstanceId = instanceId;
        RemotePosition = position;
        RemoteWorldLinearVelocity = worldLinearVelocity;
        RemoteWorldAngularVelocity = worldAngularVelocity;
        RemoteBrakePresentation01 = brakePresentation01;
        RemoteHullRotation = hullRotation;
        RemoteTurretWorldRotation = turretWorldRotation;
        RemoteGunWorldRotation = gunWorldRotation;
        RemoteUnitNetId = unitNetId;
    }

    public static void Clear()
    {
        HasData = false;
        LastSequence = 0;
        RemoteUnitInstanceId = 0;
        RemotePosition = default;
        RemoteWorldLinearVelocity = default;
        RemoteWorldAngularVelocity = default;
        RemoteBrakePresentation01 = 0f;
        RemoteHullRotation = default;
        RemoteTurretWorldRotation = Quaternion.identity;
        RemoteGunWorldRotation = Quaternion.identity;
        RemoteUnitNetId = 0;
    }
}
