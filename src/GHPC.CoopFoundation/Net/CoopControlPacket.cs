using System;

namespace GHPC.CoopFoundation.Net;

/// <summary>UDP control: vehicle ownership + session (COO wire).</summary>
internal static class CoopControlPacket
{
    public const byte Magic0 = (byte)'C';
    public const byte Magic1 = (byte)'O';
    public const byte Magic2 = (byte)'O';

    /// <summary>Single on-wire control version (Hello/Welcome/Heartbeat use new opcodes on the same byte).</summary>
    public const byte WireVersion1 = 1;

    /// <summary>Reserved; writers use <see cref="WireVersion1" /> so 0.2.x peers still parse Switch/Sync.</summary>
    public const byte WireVersion2 = 2;

    public const byte OpSync = 3;

    public const byte OpSwitch = 4;

    public const byte OpHello = 5;

    public const byte OpWelcome = 6;

    public const byte OpHeartbeat = 7;

    public const int FixedControlPayloadLength = 16;

    public const int SyncHeaderLength = 8;

    public const int MaxSyncEntries = 32;

    /// <summary>Mod control schema (Hello nonce / protocol).</summary>
    public const byte SessionProtocolVersion = 1;

    public static bool IsCoopControl(byte[] data, int length) =>
        data != null
        && length >= 4
        && data[0] == Magic0
        && data[1] == Magic1
        && data[2] == Magic2
        && (data[3] == WireVersion1 || data[3] == WireVersion2);

    public static void WriteSwitch(byte[] buffer, byte peerId, uint oldNetId, uint newNetId)
    {
        if (buffer.Length < FixedControlPayloadLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpSwitch;
        buffer[5] = peerId;
        buffer[6] = 0;
        buffer[7] = 0;
        BitConverter.GetBytes(oldNetId).CopyTo(buffer, 8);
        BitConverter.GetBytes(newNetId).CopyTo(buffer, 12);
    }

    public static bool TryReadSwitch(byte[] data, int length, out byte peerId, out uint oldNetId, out uint newNetId)
    {
        peerId = 0;
        oldNetId = 0;
        newNetId = 0;
        if (!IsCoopControl(data, length) || length < FixedControlPayloadLength || data[4] != OpSwitch)
            return false;
        peerId = data[5];
        oldNetId = BitConverter.ToUInt32(data, 8);
        newNetId = BitConverter.ToUInt32(data, 12);
        return true;
    }

    public static void WriteHello(byte[] buffer, uint nonce)
    {
        if (buffer.Length < FixedControlPayloadLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpHello;
        buffer[5] = SessionProtocolVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BitConverter.GetBytes(nonce).CopyTo(buffer, 8);
        for (int i = 12; i < FixedControlPayloadLength; i++)
            buffer[i] = 0;
    }

    public static bool TryReadHello(byte[] data, int length, out uint nonce, out byte protocolVer)
    {
        nonce = 0;
        protocolVer = 0;
        if (!IsCoopControl(data, length) || length < FixedControlPayloadLength || data[4] != OpHello)
            return false;
        protocolVer = data[5];
        nonce = BitConverter.ToUInt32(data, 8);
        return true;
    }

    public static void WriteWelcome(byte[] buffer, byte assignedPeerId, uint nonceEcho)
    {
        if (buffer.Length < FixedControlPayloadLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpWelcome;
        buffer[5] = assignedPeerId;
        buffer[6] = SessionProtocolVersion;
        buffer[7] = 0;
        BitConverter.GetBytes(nonceEcho).CopyTo(buffer, 8);
        for (int i = 12; i < FixedControlPayloadLength; i++)
            buffer[i] = 0;
    }

    public static bool TryReadWelcome(byte[] data, int length, out byte assignedPeerId, out uint nonceEcho)
    {
        assignedPeerId = 0;
        nonceEcho = 0;
        if (!IsCoopControl(data, length) || length < FixedControlPayloadLength || data[4] != OpWelcome)
            return false;
        assignedPeerId = data[5];
        nonceEcho = BitConverter.ToUInt32(data, 8);
        return true;
    }

    public static void WriteHeartbeat(byte[] buffer, byte senderPeerId, uint seq)
    {
        if (buffer.Length < FixedControlPayloadLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpHeartbeat;
        buffer[5] = senderPeerId;
        buffer[6] = 0;
        buffer[7] = 0;
        BitConverter.GetBytes(seq).CopyTo(buffer, 8);
        for (int i = 12; i < FixedControlPayloadLength; i++)
            buffer[i] = 0;
    }

    public static bool TryReadHeartbeat(byte[] data, int length, out byte senderPeerId, out uint seq)
    {
        senderPeerId = 0;
        seq = 0;
        if (!IsCoopControl(data, length) || length < FixedControlPayloadLength || data[4] != OpHeartbeat)
            return false;
        senderPeerId = data[5];
        seq = BitConverter.ToUInt32(data, 8);
        return true;
    }

    /// <summary>Host builds OwnerSync: header + count * (uint32 id, byte owner, 3 pad).</summary>
    public static int WriteOwnerSync(byte[] buffer, (uint netId, byte peerId)[] entries)
    {
        int n = Math.Min(entries.Length, MaxSyncEntries);
        int need = SyncHeaderLength + n * 8;
        if (buffer.Length < need)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpSync;
        buffer[5] = (byte)n;
        buffer[6] = 0;
        buffer[7] = 0;
        int o = SyncHeaderLength;
        for (int i = 0; i < n; i++)
        {
            BitConverter.GetBytes(entries[i].netId).CopyTo(buffer, o);
            o += 4;
            buffer[o] = entries[i].peerId;
            buffer[o + 1] = 0;
            buffer[o + 2] = 0;
            buffer[o + 3] = 0;
            o += 4;
        }

        return need;
    }

    public static bool TryReadOwnerSync(byte[] data, int length, out (uint netId, byte peerId)[] entries)
    {
        entries = Array.Empty<(uint, byte)>();
        if (!IsCoopControl(data, length) || length < SyncHeaderLength || data[4] != OpSync)
            return false;
        int n = data[5];
        if (n > MaxSyncEntries)
            return false;
        if (length < SyncHeaderLength + n * 8)
            return false;
        var list = new (uint, byte)[n];
        int o = SyncHeaderLength;
        for (int i = 0; i < n; i++)
        {
            uint id = BitConverter.ToUInt32(data, o);
            o += 4;
            byte p = data[o];
            o += 4;
            list[i] = (id, p);
        }

        entries = list;
        return true;
    }
}
