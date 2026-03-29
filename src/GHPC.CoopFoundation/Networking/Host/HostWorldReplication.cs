using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using GHPC;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Host;

/// <summary>Host: enumerate <see cref="Unit" />, assign stable net ids, emit GHW v1 UDP parts.</summary>
internal static class HostWorldReplication
{
    private static uint _hostWorldSeq;

    private static float _accum;

    private static byte[]? _sendBuffer;

    private static readonly WorldEntityWire[] ScratchEntities = new WorldEntityWire[CoopWorldPacket.MaxEntitiesPerPart];

    public static void Reset()
    {
        _hostWorldSeq = 0;
        _accum = 0f;
    }

    public static void TickSend(
        float deltaTime,
        float intervalSeconds,
        bool logWorld,
        UdpClient? udp,
        IPEndPoint? peer)
    {
        if (intervalSeconds <= 0f || udp == null || peer == null)
            return;
        _accum += deltaTime;
        if (_accum < intervalSeconds)
            return;
        _accum = 0f;

        if (!CoopSessionState.IsPlaying)
            return;

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
            wires.Add(new WorldEntityWire(netId, t.position, hull, tw, gw));
        }

        if (wires.Count == 0)
            return;

        int maxEntitiesTotal = CoopWorldPacket.MaxEntitiesPerPart * byte.MaxValue;
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

        int partCount = (wires.Count + CoopWorldPacket.MaxEntitiesPerPart - 1) / CoopWorldPacket.MaxEntitiesPerPart;
        if (partCount > byte.MaxValue)
        {
            MelonLogger.Warning($"[CoopNet] GHW: too many units ({wires.Count}); truncating to {byte.MaxValue * CoopWorldPacket.MaxEntitiesPerPart}.");
            partCount = byte.MaxValue;
        }

        byte partCountB = (byte)partCount;
        uint seq = _hostWorldSeq;

        for (int p = 0; p < partCount; p++)
        {
            int start = p * CoopWorldPacket.MaxEntitiesPerPart;
            int remaining = wires.Count - start;
            if (remaining <= 0)
                break;
            int count = Math.Min(CoopWorldPacket.MaxEntitiesPerPart, remaining);
            for (int i = 0; i < count; i++)
                ScratchEntities[i] = wires[start + i];

            int len = CoopWorldPacket.WritePart(
                _sendBuffer,
                seq,
                token,
                phase,
                (byte)p,
                partCountB,
                new ReadOnlySpan<WorldEntityWire>(ScratchEntities, 0, count));

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
                    $"[CoopNet] GHW send seq={seq} parts={partCountB} entities={wires.Count} (firstPart={count})");
            }
        }
    }
}
