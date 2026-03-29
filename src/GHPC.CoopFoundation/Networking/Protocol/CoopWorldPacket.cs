using System;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Protocol;

/// <summary>One replicated unit in GHW wire v1.</summary>
internal readonly struct WorldEntityWire
{
    public readonly uint NetId;

    public readonly Vector3 Position;

    public readonly Quaternion HullRotation;

    public readonly Quaternion TurretWorldRotation;

    public readonly Quaternion GunWorldRotation;

    public WorldEntityWire(
        uint netId,
        Vector3 position,
        Quaternion hullRotation,
        Quaternion turretWorldRotation,
        Quaternion gunWorldRotation)
    {
        NetId = netId;
        Position = position;
        HullRotation = hullRotation;
        TurretWorldRotation = turretWorldRotation;
        GunWorldRotation = gunWorldRotation;
    }
}

/// <summary>Decoded GHW v1 header + payload.</summary>
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

/// <summary>UDP world snapshot: magic GHW, v1 layout (host authority).</summary>
internal static class CoopWorldPacket
{
    public const byte Magic0 = (byte)'G';

    public const byte Magic1 = (byte)'H';

    public const byte Magic2 = (byte)'W';

    public const byte WireVersion1 = 1;

    public const int HeaderLength = 18;

    public const int EntityStride = 64;

    /// <summary>Per UDP datagram cap to stay under typical MTU (~1200 safe).</summary>
    public const int MaxEntitiesPerPart = 16;

    public const int MaxPacketLength = HeaderLength + MaxEntitiesPerPart * EntityStride;

    public static bool IsCoopWorld(byte[] data, int length)
    {
        return length >= HeaderLength
               && data[0] == Magic0
               && data[1] == Magic1
               && data[2] == Magic2;
    }

    public static int WritePart(
        byte[] buffer,
        uint hostSeq,
        uint missionToken,
        byte missionPhase,
        byte partIndex,
        byte partCount,
        ReadOnlySpan<WorldEntityWire> entities)
    {
        if (entities.Length > MaxEntitiesPerPart)
            throw new ArgumentOutOfRangeException(nameof(entities));
        int need = HeaderLength + entities.Length * EntityStride;
        if (buffer.Length < need)
            throw new ArgumentException("buffer too small", nameof(buffer));

        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
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
        }

        return need;
    }

    public static bool TryRead(byte[] data, int length, out CoopWorldPacketDecoded decoded)
    {
        decoded = default;
        if (!IsCoopWorld(data, length))
            return false;
        if (data[3] != WireVersion1)
            return false;

        int o = 4;
        uint hostSeq = ReadU32(data, ref o);
        uint token = ReadU32(data, ref o);
        byte phase = data[o++];
        o++;
        byte partIndex = data[o++];
        byte partCount = data[o++];
        ushort entityCount = ReadU16(data, ref o);
        if (partCount == 0 || partIndex >= partCount || entityCount > MaxEntitiesPerPart)
            return false;
        int need = HeaderLength + entityCount * EntityStride;
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
            entities[i] = new WorldEntityWire(netId, new Vector3(px, py, pz), hull, tw, gw);
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
