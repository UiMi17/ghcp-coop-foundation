using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Last accepted remote snapshot (main thread only).</summary>
internal static class CoopRemoteState
{
    public static bool HasData { get; private set; }

    public static uint LastSequence { get; private set; }

    public static int RemoteUnitInstanceId { get; private set; }

    public static Vector3 RemotePosition { get; private set; }

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
        uint unitNetId)
    {
        HasData = true;
        LastSequence = sequence;
        RemoteUnitInstanceId = instanceId;
        RemotePosition = position;
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
        RemoteHullRotation = default;
        RemoteTurretWorldRotation = Quaternion.identity;
        RemoteGunWorldRotation = Quaternion.identity;
        RemoteUnitNetId = 0;
    }
}
