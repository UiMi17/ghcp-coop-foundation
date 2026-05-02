using System;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Protocol;

/// <summary>
///     One replicated unit: GHW v1 pose; v2 linear vel; v3 angular vel; v4 +brake; v5 +world linear accel (m/s²);
///     v6 + NWH motor axis (brake/throttle intent).
/// </summary>
internal readonly struct WorldEntityWire
{
    public readonly uint NetId;

    public readonly Vector3 Position;

    public readonly Quaternion HullRotation;

    public readonly Quaternion TurretWorldRotation;

    public readonly Quaternion GunWorldRotation;

    /// <summary>World-space rigidbody linear velocity (m/s).</summary>
    public readonly Vector3 WorldLinearVelocity;

    /// <summary>World-space rigidbody angular velocity (rad/s).</summary>
    public readonly Vector3 WorldAngularVelocity;

    /// <summary>0–1 authoritative brake presentation (pedal/torque); from <see cref="CoopVehicleBrakeSampler" />.</summary>
    public readonly float BrakePresentation01;

    /// <summary>GHW v5: host-derived linear acceleration for client extrapolation / NWH injection (zero if v4).</summary>
    public readonly Vector3 WorldLinearAcceleration;

    /// <summary>GHW v6: NWH <c>VehicleController.input.Vertical</c> (−1…1 throttle / brake).</summary>
    public readonly float MotorInputVertical;

    public WorldEntityWire(
        uint netId,
        Vector3 position,
        Quaternion hullRotation,
        Quaternion turretWorldRotation,
        Quaternion gunWorldRotation,
        Vector3 worldLinearVelocity,
        Vector3 worldAngularVelocity = default,
        float brakePresentation01 = 0f,
        Vector3 worldLinearAcceleration = default,
        float motorInputVertical = 0f)
    {
        NetId = netId;
        Position = position;
        HullRotation = hullRotation;
        TurretWorldRotation = turretWorldRotation;
        GunWorldRotation = gunWorldRotation;
        WorldLinearVelocity = worldLinearVelocity;
        WorldAngularVelocity = worldAngularVelocity;
        BrakePresentation01 = brakePresentation01;
        WorldLinearAcceleration = worldLinearAcceleration;
        MotorInputVertical = motorInputVertical;
    }
}

/// <summary>Decoded GHW header + payload.</summary>
internal readonly struct CoopWorldPacketDecoded
{
    public readonly uint HostSeq;

    public readonly uint MissionToken;

    public readonly byte MissionPhase;

    public readonly byte PartIndex;

    public readonly byte PartCount;

    public readonly WorldEntityWire[] Entities;

    public CoopWorldPacketDecoded(
        uint hostSeq,
        uint missionToken,
        byte missionPhase,
        byte partIndex,
        byte partCount,
        WorldEntityWire[] entities)
    {
        HostSeq = hostSeq;
        MissionToken = missionToken;
        MissionPhase = missionPhase;
        PartIndex = partIndex;
        PartCount = partCount;
        Entities = entities;
    }
}

/// <summary>UDP world snapshot: magic GHW (v1 legacy … v5 +linear accel).</summary>
internal static class CoopWorldPacket
{
    public const byte Magic0 = (byte)'G';

    public const byte Magic1 = (byte)'H';

    public const byte Magic2 = (byte)'W';

    public const byte WireVersion1 = 1;

    public const byte WireVersion2 = 2;

    public const byte WireVersion3 = 3;

    public const byte WireVersion4 = 4;

    public const byte WireVersion5 = 5;

    public const byte WireVersion6 = 6;

    public const int HeaderLength = 18;

    public const int EntityStrideV1 = 64;

    public const int EntityStrideV2 = 76;

    public const int EntityStrideV3 = 88;

    public const int EntityStrideV4 = 92;

    /// <summary>v5 +12 B linear acceleration after brake.</summary>
    public const int EntityStrideV5 = 104;

    /// <summary>Kept small so a full part stays under ~1200 B MTU with v2 stride (18 + 15×76 = 1158).</summary>
    public const int MaxEntitiesPerPart = 15;

    /// <summary>v3 adds 12 B/entity; cap batch at 13 (18 + 13×88 = 1162).</summary>
    public const int MaxEntitiesPerPartV3 = 13;

    /// <summary>v4 +4 B brake; cap at 12 (18 + 12×92 = 1122).</summary>
    public const int MaxEntitiesPerPartV4 = 12;

    /// <summary>v5 +12 B; cap at 11 (18 + 11×104 = 1162).</summary>
    public const int MaxEntitiesPerPartV5 = 11;

    /// <summary>v6 +4 B motor axis after accel; cap at 10 (18 + 10×108 = 1098).</summary>
    public const int EntityStrideV6 = 108;

    public const int MaxEntitiesPerPartV6 = 10;

    /// <summary>Decode cap for legacy GHW v1 parts (64 B stride) from older peers.</summary>
    public const int LegacyV1MaxEntitiesPerPart = 16;

    public const int MaxPacketLength = HeaderLength + MaxEntitiesPerPartV5 * EntityStrideV5;

    public static bool IsCoopWorld(byte[] data, int length)
    {
        return length >= HeaderLength
               && data[0] == Magic0
               && data[1] == Magic1
               && data[2] == Magic2;
    }

    /// <summary>Writes GHW v4 (no linear acceleration field on wire).</summary>
    public static int WritePart(
        byte[] buffer,
        uint hostSeq,
        uint missionToken,
        byte missionPhase,
        byte partIndex,
        byte partCount,
        ReadOnlySpan<WorldEntityWire> entities)
    {
        return WritePart(buffer, hostSeq, missionToken, missionPhase, partIndex, partCount, entities, WireVersion4);
    }

    public static int WritePart(
        byte[] buffer,
        uint hostSeq,
        uint missionToken,
        byte missionPhase,
        byte partIndex,
        byte partCount,
        ReadOnlySpan<WorldEntityWire> entities,
        byte wireVersion)
    {
        if (wireVersion != WireVersion4 && wireVersion != WireVersion5 && wireVersion != WireVersion6)
            throw new ArgumentOutOfRangeException(nameof(wireVersion));
        int maxPerPart = wireVersion == WireVersion6
            ? MaxEntitiesPerPartV6
            : wireVersion == WireVersion5 ? MaxEntitiesPerPartV5 : MaxEntitiesPerPartV4;
        int stride = wireVersion == WireVersion6
            ? EntityStrideV6
            : wireVersion == WireVersion5 ? EntityStrideV5 : EntityStrideV4;
        if (entities.Length > maxPerPart)
            throw new ArgumentOutOfRangeException(nameof(entities));
        int need = HeaderLength + entities.Length * stride;
        if (buffer.Length < need)
            throw new ArgumentException("buffer too small", nameof(buffer));

        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = wireVersion;
        int o = 4;
        o = WriteU32(buffer, o, hostSeq);
        o = WriteU32(buffer, o, missionToken);
        buffer[o++] = missionPhase;
        buffer[o++] = 0;
        buffer[o++] = partIndex;
        buffer[o++] = partCount;
        o = WriteU16(buffer, o, (ushort)entities.Length);

        for (int i = 0; i < entities.Length; i++)
        {
            WorldEntityWire e = entities[i];
            o = WriteU32(buffer, o, e.NetId);
            o = WriteF32(buffer, o, e.Position.x);
            o = WriteF32(buffer, o, e.Position.y);
            o = WriteF32(buffer, o, e.Position.z);
            o = WriteQuat(buffer, o, e.HullRotation);
            o = WriteQuat(buffer, o, e.TurretWorldRotation);
            o = WriteQuat(buffer, o, e.GunWorldRotation);
            o = WriteF32(buffer, o, e.WorldLinearVelocity.x);
            o = WriteF32(buffer, o, e.WorldLinearVelocity.y);
            o = WriteF32(buffer, o, e.WorldLinearVelocity.z);
            o = WriteF32(buffer, o, e.WorldAngularVelocity.x);
            o = WriteF32(buffer, o, e.WorldAngularVelocity.y);
            o = WriteF32(buffer, o, e.WorldAngularVelocity.z);
            o = WriteF32(buffer, o, e.BrakePresentation01);
            if (wireVersion == WireVersion5 || wireVersion == WireVersion6)
            {
                o = WriteF32(buffer, o, e.WorldLinearAcceleration.x);
                o = WriteF32(buffer, o, e.WorldLinearAcceleration.y);
                o = WriteF32(buffer, o, e.WorldLinearAcceleration.z);
            }

            if (wireVersion == WireVersion6)
                o = WriteF32(buffer, o, e.MotorInputVertical);
        }

        return need;
    }

    public static bool TryRead(byte[] data, int length, out CoopWorldPacketDecoded decoded)
    {
        decoded = default;
        if (!IsCoopWorld(data, length))
            return false;
        byte ver = data[3];
        if (ver != WireVersion1 && ver != WireVersion2 && ver != WireVersion3 && ver != WireVersion4
            && ver != WireVersion5 && ver != WireVersion6)
            return false;

        int o = 4;
        uint hostSeq = ReadU32(data, ref o);
        uint token = ReadU32(data, ref o);
        byte phase = data[o++];
        o++;
        byte partIndex = data[o++];
        byte partCount = data[o++];
        ushort entityCount = ReadU16(data, ref o);
        int maxEntityDecode = ver switch
        {
            WireVersion1 => LegacyV1MaxEntitiesPerPart,
            WireVersion2 => MaxEntitiesPerPart,
            WireVersion3 => MaxEntitiesPerPartV3,
            WireVersion4 => MaxEntitiesPerPartV4,
            WireVersion5 => MaxEntitiesPerPartV5,
            _ => MaxEntitiesPerPartV6
        };
        if (partCount == 0 || partIndex >= partCount || entityCount > maxEntityDecode)
            return false;

        int stride = ver switch
        {
            WireVersion1 => EntityStrideV1,
            WireVersion2 => EntityStrideV2,
            WireVersion3 => EntityStrideV3,
            WireVersion4 => EntityStrideV4,
            WireVersion5 => EntityStrideV5,
            _ => EntityStrideV6
        };
        int need = HeaderLength + entityCount * stride;
        if (length < need)
            return false;

        var entities = new WorldEntityWire[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            uint netId = ReadU32(data, ref o);
            float px = ReadF32(data, ref o);
            float py = ReadF32(data, ref o);
            float pz = ReadF32(data, ref o);
            Quaternion hull = ReadQuat(data, ref o);
            Quaternion tw = ReadQuat(data, ref o);
            Quaternion gw = ReadQuat(data, ref o);
            hull.Normalize();
            tw.Normalize();
            gw.Normalize();
            Vector3 vel = Vector3.zero;
            Vector3 ang = Vector3.zero;
            // v5 uses the same velocity/angular layout as v4 before brake+accel; omitting v5 here misaligned the stream and corrupted netIds on clients.
            if (ver == WireVersion2 || ver == WireVersion3 || ver == WireVersion4 || ver == WireVersion5
                || ver == WireVersion6)
            {
                float vx = ReadF32(data, ref o);
                float vy = ReadF32(data, ref o);
                float vz = ReadF32(data, ref o);
                vel = new Vector3(vx, vy, vz);
            }

            if (ver == WireVersion3 || ver == WireVersion4 || ver == WireVersion5 || ver == WireVersion6)
            {
                float ax = ReadF32(data, ref o);
                float ay = ReadF32(data, ref o);
                float az = ReadF32(data, ref o);
                ang = new Vector3(ax, ay, az);
            }

            float brake01 = 0f;
            if (ver == WireVersion4 || ver == WireVersion5 || ver == WireVersion6)
                brake01 = ReadF32(data, ref o);

            Vector3 accel = Vector3.zero;
            if (ver == WireVersion5 || ver == WireVersion6)
            {
                float ax = ReadF32(data, ref o);
                float ay = ReadF32(data, ref o);
                float az = ReadF32(data, ref o);
                accel = new Vector3(ax, ay, az);
            }

            float motorV = 0f;
            if (ver == WireVersion6)
                motorV = ReadF32(data, ref o);

            entities[i] = new WorldEntityWire(netId, new Vector3(px, py, pz), hull, tw, gw, vel, ang, brake01, accel, motorV);
        }

        decoded = new CoopWorldPacketDecoded(hostSeq, token, phase, partIndex, partCount, entities);
        return true;
    }

    private static int WriteU32(byte[] b, int o, uint v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static int WriteU16(byte[] b, int o, ushort v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 2;
    }

    private static int WriteF32(byte[] b, int o, float v)
    {
        BitConverter.GetBytes(v).CopyTo(b, o);
        return o + 4;
    }

    private static int WriteQuat(byte[] b, int o, Quaternion q)
    {
        o = WriteF32(b, o, q.x);
        o = WriteF32(b, o, q.y);
        o = WriteF32(b, o, q.z);
        return WriteF32(b, o, q.w);
    }

    private static uint ReadU32(byte[] b, ref int o)
    {
        uint v = BitConverter.ToUInt32(b, o);
        o += 4;
        return v;
    }

    private static ushort ReadU16(byte[] b, ref int o)
    {
        ushort v = BitConverter.ToUInt16(b, o);
        o += 2;
        return v;
    }

    private static float ReadF32(byte[] b, ref int o)
    {
        float v = BitConverter.ToSingle(b, o);
        o += 4;
        return v;
    }

    private static Quaternion ReadQuat(byte[] b, ref int o)
    {
        float x = ReadF32(b, ref o);
        float y = ReadF32(b, ref o);
        float z = ReadF32(b, ref o);
        float w = ReadF32(b, ref o);
        return new Quaternion(x, y, z, w);
    }
}
