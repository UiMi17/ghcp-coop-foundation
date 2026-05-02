using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using GHPC;
using GHPC.CoopFoundation.Networking;
using GHPC.CoopFoundation.Networking.Protocol;
using MelonLoader;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Host;

/// <summary>Host: enumerate <see cref="Unit" />, assign stable net ids, emit GHW v4/v5/v6 UDP parts.</summary>
internal static class HostWorldReplication
{
    private static uint _hostWorldSeq;

    private static float _accum;

    private static byte[]? _sendBuffer;

    private static readonly WorldEntityWire[] ScratchEntities =
        new WorldEntityWire[
            Math.Max(
                CoopWorldPacket.MaxEntitiesPerPartV4,
                Math.Max(CoopWorldPacket.MaxEntitiesPerPartV5, CoopWorldPacket.MaxEntitiesPerPartV6))];

    private static readonly Dictionary<uint, Vector3> LastLinearVelocityByNetId = new();

    private static float _lastSendRealtime = -1f;

    private static bool _brakeBoostEnabled = true;

    private static float _brakeBoostHzMultiplier = 2.1f;

    private static float _brakeBoostSustainSeconds = 0.22f;

    private static float _brakeBoostUntilRealtime;

    public static void ConfigureBrakeBoost(bool enabled, float hzMultiplier, float sustainSeconds)
    {
        _brakeBoostEnabled = enabled;
        _brakeBoostHzMultiplier = hzMultiplier < 1.02f ? 1.02f : hzMultiplier;
        _brakeBoostSustainSeconds = sustainSeconds < 0.04f ? 0.04f : sustainSeconds;
    }

    public static void Reset()
    {
        _hostWorldSeq = 0;
        _accum = 0f;
        LastLinearVelocityByNetId.Clear();
        _lastSendRealtime = -1f;
        _brakeBoostUntilRealtime = 0f;
    }

    public static void TickSend(
        float deltaTime,
        float intervalSeconds,
        bool logWorld,
        bool sendGhwV5Acceleration,
        bool sendGhwV6MotorInput,
        UdpClient? udp,
        IPEndPoint? peer)
    {
        if (intervalSeconds <= 0f || udp == null || peer == null)
            return;

        bool v6 = sendGhwV6MotorInput && sendGhwV5Acceleration;
        float interval = intervalSeconds;
        if (_brakeBoostEnabled && Time.realtimeSinceStartup < _brakeBoostUntilRealtime)
            interval /= Mathf.Clamp(_brakeBoostHzMultiplier, 1.02f, 6f);

        _accum += deltaTime;
        if (_accum < interval)
            return;
        _accum = 0f;

        if (!CoopSessionState.IsPlaying)
            return;

        float realtime = Time.realtimeSinceStartup;
        float accelDt = _lastSendRealtime > 0f ? Mathf.Clamp(realtime - _lastSendRealtime, 0.02f, 0.8f) : 0.1f;
        _lastSendRealtime = realtime;

        uint token = CoopSessionState.MissionCoherenceToken;
        if (token == 0)
            return;

        byte phase = CoopSessionState.MissionStateToWirePhase();
        if (phase != 2)
            return;

        Unit[] units = UnityEngine.Object.FindObjectsOfType<Unit>();
        if (units == null || units.Length == 0)
            return;

        CoopUnitWireRegistry.EnsureRefreshedThisFrame();

        uint excludeNetId = 0;
        if (CoopRemoteState.HasData && CoopRemoteState.RemoteUnitNetId != 0)
            excludeNetId = CoopRemoteState.RemoteUnitNetId;

        var wires = new List<WorldEntityWire>(units.Length);

        foreach (Unit? unit in units)
        {
            if (unit == null || unit.gameObject == null)
                continue;
            Transform t = unit.transform;
            if (t == null)
                continue;

            uint netId = CoopUnitWireRegistry.GetWireId(unit);
            if (excludeNetId != 0 && netId == excludeNetId)
                continue;

            Quaternion hull = t.rotation;
            CoopAimableSampler.SampleWorldRotations(unit, hull, out Quaternion tw, out Quaternion gw);
            Vector3 vel = Vector3.zero;
            Vector3 ang = Vector3.zero;
            Rigidbody? rb = unit.Chassis?.Rigidbody;
            if (rb == null)
                rb = unit.GetComponentInParent<Rigidbody>();
            rb ??= unit.GetComponentInChildren<Rigidbody>();
            if (rb != null)
            {
                vel = rb.velocity;
                ang = rb.angularVelocity;
            }

            VehicleController? vc = unit.GetComponentInChildren<VehicleController>(true);
            float brake01 = CoopVehicleBrakeSampler.SampleBrake01(vc);
            float motorV = 0f;
            if (v6 && vc != null)
                motorV = Mathf.Clamp(vc.input.Vertical, -1f, 1f);

            Vector3 accel = Vector3.zero;
            if (sendGhwV5Acceleration)
            {
                if (LastLinearVelocityByNetId.TryGetValue(netId, out Vector3 lv))
                    accel = (vel - lv) / accelDt;
                LastLinearVelocityByNetId[netId] = vel;
            }

            wires.Add(new WorldEntityWire(netId, t.position, hull, tw, gw, vel, ang, brake01, accel, motorV));
        }

        if (wires.Count == 0)
            return;

        RefreshBrakeBoostWindow(wires, realtime);

        byte wireVer = v6
            ? CoopWorldPacket.WireVersion6
            : sendGhwV5Acceleration
                ? CoopWorldPacket.WireVersion5
                : CoopWorldPacket.WireVersion4;
        int maxPerPart = v6
            ? CoopWorldPacket.MaxEntitiesPerPartV6
            : sendGhwV5Acceleration
                ? CoopWorldPacket.MaxEntitiesPerPartV5
                : CoopWorldPacket.MaxEntitiesPerPartV4;
        int maxEntitiesTotal = maxPerPart * byte.MaxValue;
        if (wires.Count > maxEntitiesTotal)
        {
            MelonLogger.Warning(
                $"[CoopNet] GHW: truncating unit list from {wires.Count} to {maxEntitiesTotal} (wire limit).");
            wires.RemoveRange(maxEntitiesTotal, wires.Count - maxEntitiesTotal);
        }

        if (_sendBuffer == null || _sendBuffer.Length < CoopWorldPacket.MaxPacketLength)
            _sendBuffer = new byte[CoopWorldPacket.MaxPacketLength];

        unchecked
        {
            _hostWorldSeq++;
        }

        uint seq = _hostWorldSeq;
        if (CoopReplicationDiagnostics.Enabled && wires.Count > 0)
        {
            int n = Math.Min(12, wires.Count);
            var sample = new uint[n];
            for (int i = 0; i < n; i++)
                sample[i] = wires[i].NetId;
            CoopReplicationDiagnostics.LogGhwSendHost(
                seq,
                wires.Count,
                excludeNetId,
                CoopRemoteState.HasData,
                CoopRemoteState.RemoteUnitNetId,
                sample);
        }

        int partCount = (wires.Count + maxPerPart - 1) / maxPerPart;
        if (partCount > byte.MaxValue)
        {
            MelonLogger.Warning($"[CoopNet] GHW: too many units ({wires.Count}); truncating to {byte.MaxValue * maxPerPart}.");
            partCount = byte.MaxValue;
        }

        byte partCountB = (byte)partCount;

        for (int p = 0; p < partCount; p++)
        {
            int start = p * maxPerPart;
            int remaining = wires.Count - start;
            if (remaining <= 0)
                break;
            int count = Math.Min(maxPerPart, remaining);
            for (int i = 0; i < count; i++)
                ScratchEntities[i] = wires[start + i];

            int len = CoopWorldPacket.WritePart(
                _sendBuffer,
                seq,
                token,
                phase,
                (byte)p,
                partCountB,
                new ReadOnlySpan<WorldEntityWire>(ScratchEntities, 0, count),
                wireVer);

            try
            {
                udp.Send(_sendBuffer, len, peer);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CoopNet] GHW send failed: {ex.Message}");
                return;
            }

            if (logWorld && p == 0)
            {
                MelonLogger.Msg(
                    $"[CoopNet] GHW send seq={seq} parts={partCountB} entities={wires.Count} (firstPart={count}) wireVer={wireVer}");
            }
        }
    }

    private static void RefreshBrakeBoostWindow(List<WorldEntityWire> wires, float realtimeNow)
    {
        if (!_brakeBoostEnabled || wires.Count == 0)
            return;

        float extend = realtimeNow + _brakeBoostSustainSeconds;
        for (int i = 0; i < wires.Count; i++)
        {
            WorldEntityWire w = wires[i];
            if (w.BrakePresentation01 >= 0.2f)
            {
                _brakeBoostUntilRealtime = Mathf.Max(_brakeBoostUntilRealtime, extend);
                return;
            }

            if (w.WorldLinearVelocity.sqrMagnitude > 2f && w.WorldLinearAcceleration.sqrMagnitude > 36f)
            {
                float vm = w.WorldLinearVelocity.magnitude;
                if (vm > 1.2f)
                {
                    float longitudinal = Vector3.Dot(w.WorldLinearAcceleration, w.WorldLinearVelocity / vm);
                    if (longitudinal < -8f)
                    {
                        _brakeBoostUntilRealtime = Mathf.Max(_brakeBoostUntilRealtime, extend);
                        return;
                    }
                }
            }

            if (w.MotorInputVertical < -0.12f && w.WorldLinearVelocity.sqrMagnitude > 1f)
            {
                _brakeBoostUntilRealtime = Mathf.Max(_brakeBoostUntilRealtime, realtimeNow + _brakeBoostSustainSeconds * 0.85f);
                return;
            }
        }
    }
}
