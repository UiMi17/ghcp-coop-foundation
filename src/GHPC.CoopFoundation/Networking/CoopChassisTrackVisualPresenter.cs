using System.Collections.Generic;
using GHPC;
using GHPC.CoopFoundation.GameSession;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Host;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using GHPC.CoopFoundation.Networking.Transport;
using NWH.VehiclePhysics;
using NWH.WheelController3D;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Synthetic track UV / rollers when NWH wheel mode is off; client wire mode uses
///     <see cref="CoopRemotePuppetVisualLateOrchestrator" />. Mirrors NWH <see cref="Tracks.Update" /> + visuals: per-wheel RPM
///     is synthesized from replicated rigid-body velocity at each wheel (CoM linear + ω×r, roll axis =
///     <see cref="WheelController.transform.forward" />, radius = <see cref="WheelController.TireRadius" />), then
///     the winning signed RPM per side (largest |rpm|) drives UV and rollers exactly like vanilla.
///     <see cref="WheelController" /> is disabled on proxies (see <see cref="CoopNwhWheelControllerSuppress" />).
/// </summary>
internal static class CoopChassisTrackVisualPresenter
{
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    /// <summary>Blend wire velocity with transform derivative (hull is interpolated every frame on client).</summary>
    private const float KinematicVelocityBlend = 0.42f;

    /// <summary>Higher = snappier; softens replicated linear velocity only (vanilla RPM comes from FixedUpdate).</summary>
    private const float LinearVelocitySmoothing = 17f;

    /// <summary>Same as NWH <see cref="Tracks" /> private <c>rpm2rps</c> (1/60).</summary>
    private const float RpmToRps = 0.016667f;

    /// <summary>Matches NWH <see cref="Tracks" /> rpm→°/s constant.</summary>
    private const float RpmToDegPerSecond = 6.0001197f;

    private static readonly Dictionary<uint, Vector2> UvLeftByNetId = new();

    private static readonly Dictionary<uint, Vector2> UvRightByNetId = new();

    private static readonly Dictionary<uint, Material> MatLeftByNetId = new();

    private static readonly Dictionary<uint, Material> MatRightByNetId = new();

    private static readonly Dictionary<uint, Vector3> LastWorldPosForVisualByNetId = new();

    private static readonly Dictionary<uint, Vector3> SmoothedLinearVelocityByNetId = new();

    private static readonly Dictionary<uint, Vector3> SmoothedAngularVelocityByNetId = new();

    public static void ResetSession()
    {
        UvLeftByNetId.Clear();
        UvRightByNetId.Clear();
        MatLeftByNetId.Clear();
        MatRightByNetId.Clear();
        LastWorldPosForVisualByNetId.Clear();
        SmoothedLinearVelocityByNetId.Clear();
        SmoothedAngularVelocityByNetId.Clear();
    }

    /// <summary>Host-only: track UV / rollers for the lobby peer's unit from <see cref="CoopRemoteState" />.</summary>
    internal static void TickHostPeerPuppet(Unit pu, uint pNet, float deltaTime)
    {
        Vector3 driveVel = FilterDriveVelocity(pNet, pu, CoopRemoteState.RemoteWorldLinearVelocity, deltaTime);
        Vector3 angVel = FilterAngularVelocity(pNet, CoopRemoteState.RemoteWorldAngularVelocity, deltaTime);
        ApplyForUnit(pu, pNet, driveVel, angVel, deltaTime);
    }

    /// <summary>Client puppet when NWH wheel visuals are off: synthetic track from GHW velocities.</summary>
    internal static void TickSyntheticTrackForClientPuppet(uint netId, Unit unit, float deltaTime)
    {
        if (!ClientSimulationGovernor.TryGetDisplayLinAngMotor(netId, out Vector3 vel, out Vector3 ang, out float motorPf))
        {
            vel = Vector3.zero;
            ang = Vector3.zero;
            motorPf = 0f;
        }

        Vector3 driveVel = FilterDriveVelocity(netId, unit, vel, deltaTime, motorFromPrefetch: true, motorPrefetch: motorPf);
        Vector3 angVel = FilterAngularVelocity(netId, ang, deltaTime);
        ApplyForUnit(unit, netId, driveVel, angVel, deltaTime);
    }

    private static Vector3 FilterDriveVelocity(
        uint netId,
        Unit unit,
        Vector3 wireVelocity,
        float dt,
        bool motorFromPrefetch = false,
        float motorPrefetch = 0f)
    {
        Vector3 pos = unit.transform.position;
        Vector3 kinematicVel = Vector3.zero;
        if (LastWorldPosForVisualByNetId.TryGetValue(netId, out Vector3 prev) && dt > 1e-6f)
            kinematicVel = (pos - prev) / dt;
        LastWorldPosForVisualByNetId[netId] = pos;

        Vector3 target = Vector3.Lerp(wireVelocity, kinematicVel, KinematicVelocityBlend);
        if (!SmoothedLinearVelocityByNetId.TryGetValue(netId, out Vector3 smooth))
            smooth = target;
        float a = 1f - Mathf.Exp(-LinearVelocitySmoothing * dt);
        float motor;
        if (motorFromPrefetch)
            motor = motorPrefetch;
        else if (ClientSimulationGovernor.TryGetDisplayMotorInputVertical(netId, out float m))
            motor = m;
        else
            motor = 0f;

        if (motor < -0.07f)
            a = Mathf.Min(1f, a * (1f + Mathf.Clamp01(-motor) * 0.28f));
        if (target.sqrMagnitude < smooth.sqrMagnitude * 0.985f)
            a = Mathf.Min(1f, a * 1.75f);
        smooth = Vector3.Lerp(smooth, target, Mathf.Clamp01(a));
        SmoothedLinearVelocityByNetId[netId] = smooth;
        return smooth;
    }

    /// <summary>Low-pass replicated angular velocity (rad/s) so track differential does not chase quaternion Slerp noise.</summary>
    private static Vector3 FilterAngularVelocity(uint netId, Vector3 wireAngularVelocity, float dt)
    {
        if (!SmoothedAngularVelocityByNetId.TryGetValue(netId, out Vector3 s))
            s = wireAngularVelocity;
        float a = 1f - Mathf.Exp(-16f * dt);
        s = Vector3.Lerp(s, wireAngularVelocity, Mathf.Clamp01(a));
        SmoothedAngularVelocityByNetId[netId] = s;
        return s;
    }

    /// <summary>
    ///     Same per-side selection as <see cref="Tracks.Update" />: keep signed RPM of the wheel with largest |rpm|.
    ///     RPM is no-slip from rigid-body speed along each wheel's forward axis (NWH <c>NoSlipRPM</c> uses the same
    ///     radius denominator: speed / (2π r) in rev/s → ×60 for RPM).
    /// </summary>
    private static void ComputeMaxRpmPerSideLikeTracksUpdate(
        VehicleController vc,
        Vector3 worldLinearVelocity,
        Vector3 worldAngularVelocity,
        out float maxLeftRpm,
        out float maxRightRpm)
    {
        maxLeftRpm = 0f;
        maxRightRpm = 0f;
        float bestAbsLeft = 0f;
        float bestAbsRight = 0f;

        Vector3 pivot = vc.vehicleRigidbody != null
            ? vc.vehicleRigidbody.worldCenterOfMass
            : vc.transform.position;

        foreach (Wheel wheel in vc.wheels)
        {
            WheelController? wc = wheel.wheelController;
            if (wc == null || wc.Visual == null)
                continue;

            float tireR = Mathf.Max(0.05f, wc.TireRadius);
            Vector3 r = wc.Visual.transform.position - pivot;
            Vector3 vAtWheel = worldLinearVelocity + Vector3.Cross(worldAngularVelocity, r);
            Vector3 rollForward = wc.transform.forward;
            float speedAlongRoll = Vector3.Dot(vAtWheel, rollForward);
            float rpm = speedAlongRoll / (Mathf.PI * 2f * tireR) * 60f;
            float absR = Mathf.Abs(rpm);

            switch (wc.VehicleSide)
            {
                case WheelController.Side.Left:
                    if (absR > bestAbsLeft)
                    {
                        bestAbsLeft = absR;
                        maxLeftRpm = rpm;
                    }

                    break;
                case WheelController.Side.Right:
                    if (absR > bestAbsRight)
                    {
                        bestAbsRight = absR;
                        maxRightRpm = rpm;
                    }

                    break;
            }
        }
    }

    private static void ApplyForUnit(
        Unit unit,
        uint netId,
        Vector3 worldLinearVelocity,
        Vector3 worldAngularVelocity,
        float deltaTime)
    {
        if (!CoopRemotePuppetPresentationCache.TryGetVehicleController(netId, unit, out VehicleController? vc)
            || vc == null
            || !vc.tracks.trackedVehicle)
            return;

        if (CoopNwhPuppetTracksRelay.ShouldSkipSyntheticChassisVisuals(unit))
            return;

        Tracks tr = vc.tracks;
        Renderer? leftR = tr.leftTrackRenderer;
        Renderer? rightR = tr.rightTrackRenderer;

        ComputeMaxRpmPerSideLikeTracksUpdate(vc, worldLinearVelocity, worldAngularVelocity, out float maxLeftRpm, out float maxRightRpm);

        float avgR = Mathf.Max(0.2f, vc.AvgWheelRadius);
        float enl = Mathf.Max(1f, tr.wheelEnlargementCoefficient);
        float num4 = Mathf.PI * -2f * avgR * enl;
        float leftTrackVel = num4 * maxLeftRpm * RpmToRps;
        float rightTrackVel = num4 * maxRightRpm * RpmToRps;

        RotateTrackedVisuals(vc, tr, maxLeftRpm, maxRightRpm, deltaTime);

        Vector2 uvDir = tr.uvDirection;
        if (leftR != null)
            ScrollRenderer(netId, leftR, UvLeftByNetId, MatLeftByNetId, uvDir * (leftTrackVel * deltaTime));
        if (rightR != null)
            ScrollRenderer(netId, rightR, UvRightByNetId, MatRightByNetId, uvDir * (rightTrackVel * deltaTime));
    }

    private static void RotateTrackedVisuals(VehicleController vc, Tracks tr, float rpmL, float rpmR, float dt)
    {
        float enl = Mathf.Max(1f, tr.wheelEnlargementCoefficient);
        float rollerStepL = rpmL * enl * RpmToDegPerSecond * dt;
        float rollerStepR = rpmR * enl * RpmToDegPerSecond * dt;
        RotateSprocketWheels(tr.leftSprocketWheels, rpmL, dt);
        RotateSprocketWheels(tr.rightSprocketWheels, rpmR, dt);

        foreach (Wheel wheel in vc.wheels)
        {
            if (wheel?.wheelController == null)
                continue;
            GameObject? visGo = wheel.wheelController.Visual;
            if (visGo == null)
                continue;
            Transform t = visGo.transform;
            switch (wheel.wheelController.VehicleSide)
            {
                case WheelController.Side.Left:
                    t.Rotate(rollerStepL, 0f, 0f);
                    break;
                case WheelController.Side.Right:
                    t.Rotate(rollerStepR, 0f, 0f);
                    break;
            }
        }
    }

    private static void RotateSprocketWheels(Tracks.WheelTransform[]? sprockets, float rpm, float dt)
    {
        if (sprockets == null || sprockets.Length == 0)
            return;
        float step = rpm * RpmToDegPerSecond * dt;
        for (int i = 0; i < sprockets.Length; i++)
        {
            Tracks.WheelTransform wt = sprockets[i];
            if (wt.transform == null)
                continue;
            wt.transform.Rotate(step * wt.coef, 0f, 0f);
        }
    }

    private static void ScrollRenderer(
        uint netId,
        Renderer r,
        Dictionary<uint, Vector2> uvStore,
        Dictionary<uint, Material> matStore,
        Vector2 delta)
    {
        if (!matStore.TryGetValue(netId, out Material? mat))
        {
            mat = r.material;
            matStore[netId] = mat;
        }

        if (!uvStore.TryGetValue(netId, out Vector2 off))
            off = mat.mainTextureOffset;
        off += delta;
        uvStore[netId] = off;
        mat.SetTextureOffset(MainTexId, off);
    }
}
