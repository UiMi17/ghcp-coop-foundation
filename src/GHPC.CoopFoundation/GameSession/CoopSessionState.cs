using GHPC;
using GHPC.CoopFoundation.Networking;
using GHPC.State;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.GameSession;

/// <summary>
///     Runtime facts for coop / networking. Updated from Harmony patches (always, not only when diag logs are on).
/// </summary>
internal static class CoopSessionState
{
    private const float SampleHz = 10f;

    private static readonly float SampleInterval = 1f / SampleHz;

    private static float _sampleAccum;

    public static MissionState? LastMissionState { get; private set; }

    /// <summary>From <see cref="MissionInitializer.MissionSceneName" /> (coherence token source).</summary>
    public static string MissionSceneKey { get; private set; } = "";

    public static bool IsPlaying => LastMissionState == MissionState.Playing;

    public static uint MissionCoherenceToken => CoopMissionHash.Token(MissionSceneKey);

    /// <summary>Last unit passed to SetPlayerUnit / SetDefaultUnit (may differ briefly from <see cref="PlayerInput.CurrentPlayerUnit" />).</summary>
    public static Unit? ControlledUnit { get; private set; }

    public static Vector3 LastSampledPosition { get; private set; }

    public static Vector3 LastSampledWorldLinearVelocity { get; private set; }

    /// <summary>World-space angular velocity (rad/s) from chassis rigidbody for GHP/GHW replication.</summary>
    public static Vector3 LastSampledWorldAngularVelocity { get; private set; }

    /// <summary>0–1 brake hint from local <see cref="VehicleController" /> for GHP v6.</summary>
    public static float LastSampledBrakePresentation01 { get; private set; }

    public static Quaternion LastSampledRotation { get; private set; }

    /// <summary>World rotation of main traverse <see cref="GHPC.AimablePlatform" /> (see <see cref="CoopAimableSampler" />).</summary>
    public static Quaternion LastSampledTurretWorldRotation { get; private set; }

    /// <summary>World rotation of gun elevation platform (or traverse if indistinguishable).</summary>
    public static Quaternion LastSampledGunWorldRotation { get; private set; }

    /// <summary>Wire net id (<see cref="CoopUnitWireRegistry" /> / GHW), not Unity instance id.</summary>
    public static uint LastSampledUnitNetId { get; private set; }

    public static int LastSampledUnitInstanceId { get; private set; }

    public static string LastSampledGoName { get; private set; } = "";

    public static string LastSampledFriendlyName { get; private set; } = "";

    public static void SetMissionState(MissionState state)
    {
        LastMissionState = state;
    }

    public static void SetMissionSceneKey(string? missionSceneName)
    {
        MissionSceneKey = missionSceneName ?? "";
    }

    /// <summary>Wire mission phase byte (v2/v3 payloads; 0=none, 1=Planning, 2=Playing, 3=Finished).</summary>
    public static byte MissionStateToWirePhase()
    {
        return LastMissionState switch
        {
            MissionState.Planning => 1,
            MissionState.Playing => 2,
            MissionState.Finished => 3,
            _ => 0
        };
    }

    public static void SetControlledUnit(Unit? unit)
    {
        ControlledUnit = unit;
    }

    /// <summary>Call from <see cref="MelonMod.OnSceneWasLoaded" /> to drop state when returning to menus.</summary>
    public static void NotifySceneLoaded(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
            return;
        if (sceneName.IndexOf("MainMenu", System.StringComparison.OrdinalIgnoreCase) >= 0
            || sceneName.IndexOf("LOADER_MENU", System.StringComparison.OrdinalIgnoreCase) >= 0
            || sceneName.IndexOf("LOADER_INITIAL", System.StringComparison.OrdinalIgnoreCase) >= 0)
            Clear();
    }

    public static void Clear()
    {
        LastMissionState = null;
        MissionSceneKey = "";
        ControlledUnit = null;
        LastSampledPosition = default;
        LastSampledWorldLinearVelocity = default;
        LastSampledWorldAngularVelocity = default;
        LastSampledBrakePresentation01 = 0f;
        LastSampledRotation = default;
        LastSampledTurretWorldRotation = Quaternion.identity;
        LastSampledGunWorldRotation = Quaternion.identity;
        LastSampledUnitNetId = 0;
        LastSampledUnitInstanceId = 0;
        LastSampledGoName = "";
        LastSampledFriendlyName = "";
        _sampleAccum = 0f;
        CoopUdpTransport.OnSessionCleared();
    }

    /// <summary>Throttled transform sampling while in Playing. Returns true if a new sample was taken this tick.</summary>
    public static bool TryAdvanceSampling(float deltaTime, Unit? unit)
    {
        if (!IsPlaying || unit == null)
            return false;
        _sampleAccum += deltaTime;
        if (_sampleAccum < SampleInterval)
            return false;
        _sampleAccum = 0f;
        Transform t = unit.transform;
        LastSampledPosition = t.position;
        LastSampledRotation = t.rotation;
        Vector3 vel = Vector3.zero;
        Rigidbody? rb = unit.Chassis?.Rigidbody;
        if (rb == null)
            rb = unit.GetComponentInParent<Rigidbody>();
        rb ??= unit.GetComponentInChildren<Rigidbody>();
        Vector3 ang = Vector3.zero;
        if (rb != null)
        {
            vel = rb.velocity;
            ang = rb.angularVelocity;
        }

        LastSampledWorldLinearVelocity = vel;
        LastSampledWorldAngularVelocity = ang;
        VehicleController? vc = unit.GetComponentInChildren<VehicleController>(true);
        LastSampledBrakePresentation01 = CoopVehicleBrakeSampler.SampleBrake01(vc);
        CoopAimableSampler.SampleWorldRotations(unit, t.rotation, out Quaternion tw, out Quaternion gw);
        LastSampledTurretWorldRotation = tw;
        LastSampledGunWorldRotation = gw;
        LastSampledUnitNetId = CoopUnitWireRegistry.GetWireId(unit);
        LastSampledUnitInstanceId = unit.GetInstanceID();
        LastSampledGoName = unit.gameObject.name;
        LastSampledFriendlyName = unit.FriendlyName;
        return true;
    }
}
