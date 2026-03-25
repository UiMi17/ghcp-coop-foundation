using System;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Decoded snapshot fields shared by wire versions.</summary>
internal readonly struct CoopSnapshotWire
{
    public readonly uint Sequence;
    public readonly int InstanceId;
    public readonly Vector3 Position;
    public readonly Quaternion HullRotation;
    public readonly uint MissionToken;
    public readonly byte MissionPhaseWire;
    public readonly bool LegacyV1;
    public readonly Quaternion TurretWorldRotation;
    public readonly Quaternion GunWorldRotation;
    public readonly uint UnitNetId;

    public CoopSnapshotWire(
        uint sequence,
        int instanceId,
        Vector3 position,
        Quaternion hullRotation,
        uint missionToken,
        byte missionPhaseWire,
        bool legacyV1,
        Quaternion turretWorldRotation,
        Quaternion gunWorldRotation,
        uint unitNetId)
    {
        Sequence = sequence;
        InstanceId = instanceId;
        Position = position;
        HullRotation = hullRotation;
        MissionToken = missionToken;
        MissionPhaseWire = missionPhaseWire;
        LegacyV1 = legacyV1;
        TurretWorldRotation = turretWorldRotation;
        GunWorldRotation = gunWorldRotation;
        UnitNetId = unitNetId;
    }
}

/// <summary>
///     Binary snapshot: v1 40 B; v2 48 B (+ mission); v3 84 B (+ turret/gun world rot + unitNetId).
/// </summary>
internal static class CoopNetPacket
{
    public const int LengthV1 = 40;

    public const int LengthV2 = 48;

    public const int LengthV3 = 84;

    public const int MinIncomingLength = LengthV1;

    public const byte WireVersion1 = 1;

    public const byte WireVersion2 = 2;

    public const byte WireVersion3 = 3;

    /// <summary>Phase byte when packet is v1 (skip mission coherence).</summary>
    public const byte LegacyPhaseMarker = 255;

    public const byte Magic0 = (byte)'G';
    public const byte Magic1 = (byte)'H';
    public const byte Magic2 = (byte)'P';

    public static void WriteV3(
        byte[] buffer,
        uint sequence,
        int instanceId,
        Vector3 position,
        Quaternion hullRotation,
        uint missionToken,
        byte missionPhaseWire,
        Quaternion turretWorld,
        Quaternion gunWorld,
        uint unitNetId)
    {
        if (buffer.Length < LengthV3)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion3;
        int o = 4;
        o = WriteU32(buffer, o, sequence);
        o = WriteI32(buffer, o, instanceId);
        o = WriteF32(buffer, o, position.x);
        o = WriteF32(buffer, o, position.y);
        o = WriteF32(buffer, o, position.z);
        o = WriteF32(buffer, o, hullRotation.x);
        o = WriteF32(buffer, o, hullRotation.y);
        o = WriteF32(buffer, o, hullRotation.z);
        o = WriteF32(buffer, o, hullRotation.w);
        o = WriteU32(buffer, o, missionToken);
        buffer[o] = missionPhaseWire;
        o++;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o++] = 0;
        o = WriteF32(buffer, o, turretWorld.x);
        o = WriteF32(buffer, o, turretWorld.y);
        o = WriteF32(buffer, o, turretWorld.z);
        o = WriteF32(buffer, o, turretWorld.w);
        o = WriteF32(buffer, o, gunWorld.x);
        o = WriteF32(buffer, o, gunWorld.y);
        o = WriteF32(buffer, o, gunWorld.z);
        o = WriteF32(buffer, o, gunWorld.w);
        WriteU32(buffer, o, unitNetId);
    }

    public static bool TryRead(byte[] data, int length, out CoopSnapshotWire wire)
    {
        wire = default;
        if (data == null || length < MinIncomingLength)
            return false;
        if (data[0] != Magic0 || data[1] != Magic1 || data[2] != Magic2)
            return false;
        byte ver = data[3];
        if (ver == WireVersion1 && length >= LengthV1)
        {
            int o = 4;
            uint seq = ReadU32(data, ref o);
            int id = ReadI32(data, ref o);
            float px = ReadF32(data, ref o);
            float py = ReadF32(data, ref o);
            float pz = ReadF32(data, ref o);
            Quaternion hull = ReadQuat(data, ref o);
            hull.Normalize();
            wire = new CoopSnapshotWire(
                seq,
                id,
                new Vector3(px, py, pz),
                hull,
                0,
                LegacyPhaseMarker,
                legacyV1: true,
                hull,
                hull,
                0);
            return true;
        }

        if (ver == WireVersion2 && length >= LengthV2)
        {
            int o = 4;
            uint seq = ReadU32(data, ref o);
            int id = ReadI32(data, ref o);
            float px = ReadF32(data, ref o);
            float py = ReadF32(data, ref o);
            float pz = ReadF32(data, ref o);
            Quaternion hull = ReadQuat(data, ref o);
            hull.Normalize();
            uint token = ReadU32(data, ref o);
            byte phase = data[o];
            wire = new CoopSnapshotWire(
                seq,
                id,
                new Vector3(px, py, pz),
                hull,
                token,
                phase,
                legacyV1: false,
                hull,
                hull,
                0);
            return true;
        }

        if (ver == WireVersion3 && length >= LengthV3)
        {
            int o = 4;
            uint seq = ReadU32(data, ref o);
            int id = ReadI32(data, ref o);
            float px = ReadF32(data, ref o);
            float py = ReadF32(data, ref o);
            float pz = ReadF32(data, ref o);
            Quaternion hull = ReadQuat(data, ref o);
            hull.Normalize();
            uint token = ReadU32(data, ref o);
            byte phase = data[o];
            o += 4;
            Quaternion turret = ReadQuat(data, ref o);
            turret.Normalize();
            Quaternion gun = ReadQuat(data, ref o);
            gun.Normalize();
            uint netId = ReadU32(data, ref o);
            wire = new CoopSnapshotWire(
                seq,
                id,
                new Vector3(px, py, pz),
                hull,
                token,
                phase,
                legacyV1: false,
                turret,
                gun,
                netId);
            return true;
        }

        return false;
    }

    private static Quaternion ReadQuat(byte[] data, ref int o)
    {
        float x = ReadF32(data, ref o);
        float y = ReadF32(data, ref o);
        float z = ReadF32(data, ref o);
        float w = ReadF32(data, ref o);
        return new Quaternion(x, y, z, w);
    }

    private static int WriteU32(byte[] b, int o, uint v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static int WriteI32(byte[] b, int o, int v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static int WriteF32(byte[] b, int o, float v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static uint ReadU32(byte[] b, ref int o)
    {
        uint v = BitConverter.ToUInt32(b, o);
        o += 4;
        return v;
    }

    private static int ReadI32(byte[] b, ref int o)
    {
        int v = BitConverter.ToInt32(b, o);
        o += 4;
        return v;
    }

    private static float ReadF32(byte[] b, ref int o)
    {
        float v = BitConverter.ToSingle(b, o);
        o += 4;
        return v;
    }
}
