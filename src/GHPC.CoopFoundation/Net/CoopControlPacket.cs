using System;
using System.Text;

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

    /// <summary>Host→client: <see cref="GHPC.World.WorldEnvironmentManager" /> snapshot (v1 fixed layout).</summary>
    public const byte OpWorldEnv = 8;
    public const byte OpLobbySnapshot = 20;
    public const byte OpLobbySetReadyRequest = 21;
    public const byte OpLobbyStartRequest = 22;
    public const byte OpLobbyTransition = 23;
    public const byte OpLobbyLoadMission = 24;
    public const byte OpLobbyClientLoadedAck = 25;
    public const byte OpLobbyStartApproved = 26;
    public const byte OpLobbyMissionLaunchInfo = 27;

    public const int FixedControlPayloadLength = 16;
    public const int LobbyControlPayloadLength = 32;

    /// <summary>COO magic (8) + session/revision/seq (16) + key length (2) + UTF-8 key (≤ <see cref="MissionLaunchInfoMaxKeyUtf8Bytes" />).</summary>
    public const int MissionLaunchInfoFixedLength = 26;

    public const int MissionLaunchInfoMaxKeyUtf8Bytes = 200;

    public const int MissionLaunchInfoMaxTotalLength = MissionLaunchInfoFixedLength + MissionLaunchInfoMaxKeyUtf8Bytes;

    public const int SyncHeaderLength = 8;

    /// <summary>COO header (8) + 4×float + flags byte (matches <see cref="OpWorldEnv" /> v1).</summary>
    public const int WorldEnvV1TotalLength = SyncHeaderLength + 16 + 1;

    /// <summary>v2: v1 payload + flags (night/weatherValid/useDynamic) + 4×float rain/cloud/wind/cloudBias.</summary>
    public const int WorldEnvV2TotalLength = SyncHeaderLength + 16 + 1 + 16;

    public const int MaxSyncEntries = 32;

    public const byte WorldEnvSchemaV1 = 1;

    public const byte WorldEnvSchemaV2 = 2;

    /// <summary>v3: v2 + cloud layer index + <see cref="GHPC.World.Weather" /> <c>_cloudSpeed</c> (sky dome / storm visuals).</summary>
    public const byte WorldEnvSchemaV3 = 3;

    /// <summary>v4: v3 + cloud shadow direction vector (x,y) for deterministic sky drift.</summary>
    public const byte WorldEnvSchemaV4 = 4;

    /// <summary>v2 bytes + cloudCondition (1) + cloudSpeed (4).</summary>
    public const int WorldEnvV3TotalLength = WorldEnvV2TotalLength + 1 + 4;

    /// <summary>v3 bytes + cloudDirX (4) + cloudDirY (4).</summary>
    public const int WorldEnvV4TotalLength = WorldEnvV3TotalLength + 8;

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

    public static void WriteLobbySetReadyRequest(byte[] buffer, ulong sessionId, uint revision, bool ready)
    {
        WriteLobbyPacketCore(buffer, OpLobbySetReadyRequest, sessionId, revision, 0, ready ? 1u : 0u, 0u);
    }

    public static bool TryReadLobbySetReadyRequest(byte[] data, int length, out ulong sessionId, out uint revision, out bool ready)
    {
        ready = false;
        if (!TryReadLobbyPacketCore(data, length, OpLobbySetReadyRequest, out sessionId, out revision, out _, out uint flags, out _))
            return false;
        ready = (flags & 1u) != 0;
        return true;
    }

    public static void WriteLobbyStartRequest(byte[] buffer, ulong sessionId, uint revision)
    {
        WriteLobbyPacketCore(buffer, OpLobbyStartRequest, sessionId, revision, 0, 0u, 0u);
    }

    public static bool TryReadLobbyStartRequest(byte[] data, int length, out ulong sessionId, out uint revision)
    {
        return TryReadLobbyPacketCore(data, length, OpLobbyStartRequest, out sessionId, out revision, out _, out _, out _);
    }

    public static void WriteLobbySnapshot(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq, uint readyMask, byte transitionKind)
    {
        WriteLobbyPacketCore(buffer, OpLobbySnapshot, sessionId, revision, transitionSeq, readyMask, transitionKind);
    }

    public static bool TryReadLobbySnapshot(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out uint readyMask,
        out byte transitionKind)
    {
        if (!TryReadLobbyPacketCore(data, length, OpLobbySnapshot, out sessionId, out revision, out transitionSeq, out readyMask, out uint aux))
        {
            transitionKind = 0;
            return false;
        }

        transitionKind = (byte)(aux & 0xFF);
        return true;
    }

    public static void WriteLobbyTransition(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq, byte transitionKind)
    {
        WriteLobbyPacketCore(buffer, OpLobbyTransition, sessionId, revision, transitionSeq, 0u, transitionKind);
    }

    public static bool TryReadLobbyTransition(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out byte transitionKind)
    {
        if (!TryReadLobbyPacketCore(data, length, OpLobbyTransition, out sessionId, out revision, out transitionSeq, out _, out uint aux))
        {
            transitionKind = 0;
            return false;
        }

        transitionKind = (byte)(aux & 0xFF);
        return true;
    }

    public static void WriteLobbyLoadMission(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq, uint missionToken)
    {
        WriteLobbyPacketCore(buffer, OpLobbyLoadMission, sessionId, revision, transitionSeq, missionToken, 0u);
    }

    public static bool TryReadLobbyLoadMission(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out uint missionToken)
    {
        return TryReadLobbyPacketCore(data, length, OpLobbyLoadMission, out sessionId, out revision, out transitionSeq, out missionToken, out _);
    }

    public static void WriteLobbyClientLoadedAck(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq)
    {
        WriteLobbyPacketCore(buffer, OpLobbyClientLoadedAck, sessionId, revision, transitionSeq, 1u, 0u);
    }

    public static bool TryReadLobbyClientLoadedAck(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq)
    {
        return TryReadLobbyPacketCore(data, length, OpLobbyClientLoadedAck, out sessionId, out revision, out transitionSeq, out _, out _);
    }

    public static void WriteLobbyStartApproved(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq, uint missionToken)
    {
        WriteLobbyPacketCore(buffer, OpLobbyStartApproved, sessionId, revision, transitionSeq, missionToken, 0u);
    }

    public static bool TryReadLobbyStartApproved(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out uint missionToken)
    {
        return TryReadLobbyPacketCore(data, length, OpLobbyStartApproved, out sessionId, out revision, out transitionSeq, out missionToken, out _);
    }

    /// <returns>Total datagram length, or 0 if key too long / buffer too small.</returns>
    public static int WriteLobbyMissionLaunchInfo(byte[] buffer, ulong sessionId, uint revision, uint transitionSeq, string sceneMapKey)
    {
        if (buffer.Length < MissionLaunchInfoFixedLength)
            return 0;
        byte[] keyUtf8 = Encoding.UTF8.GetBytes(sceneMapKey ?? "");
        if (keyUtf8.Length == 0 || keyUtf8.Length > MissionLaunchInfoMaxKeyUtf8Bytes)
            return 0;
        if (buffer.Length < MissionLaunchInfoFixedLength + keyUtf8.Length)
            return 0;
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpLobbyMissionLaunchInfo;
        buffer[5] = 0;
        buffer[6] = 0;
        buffer[7] = 0;
        BitConverter.GetBytes(sessionId).CopyTo(buffer, 8);
        BitConverter.GetBytes(revision).CopyTo(buffer, 16);
        BitConverter.GetBytes(transitionSeq).CopyTo(buffer, 20);
        BitConverter.GetBytes((ushort)keyUtf8.Length).CopyTo(buffer, 24);
        Buffer.BlockCopy(keyUtf8, 0, buffer, MissionLaunchInfoFixedLength, keyUtf8.Length);
        return MissionLaunchInfoFixedLength + keyUtf8.Length;
    }

    public static bool TryReadLobbyMissionLaunchInfo(
        byte[] data,
        int length,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out string sceneMapKey)
    {
        sessionId = 0;
        revision = 0;
        transitionSeq = 0;
        sceneMapKey = "";
        if (!IsCoopControl(data, length) || data[4] != OpLobbyMissionLaunchInfo)
            return false;
        if (length < MissionLaunchInfoFixedLength)
            return false;
        sessionId = BitConverter.ToUInt64(data, 8);
        revision = BitConverter.ToUInt32(data, 16);
        transitionSeq = BitConverter.ToUInt32(data, 20);
        int keyLen = BitConverter.ToUInt16(data, 24);
        if (keyLen <= 0 || keyLen > MissionLaunchInfoMaxKeyUtf8Bytes)
            return false;
        if (length < MissionLaunchInfoFixedLength + keyLen)
            return false;
        sceneMapKey = Encoding.UTF8.GetString(data, MissionLaunchInfoFixedLength, keyLen);
        return !string.IsNullOrEmpty(sceneMapKey);
    }

    private static void WriteLobbyPacketCore(byte[] buffer, byte op, ulong sessionId, uint revision, uint transitionSeq, uint flags, uint aux)
    {
        if (buffer.Length < LobbyControlPayloadLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = op;
        buffer[5] = 0;
        buffer[6] = 0;
        buffer[7] = 0;
        BitConverter.GetBytes(sessionId).CopyTo(buffer, 8);
        BitConverter.GetBytes(revision).CopyTo(buffer, 16);
        BitConverter.GetBytes(transitionSeq).CopyTo(buffer, 20);
        BitConverter.GetBytes(flags).CopyTo(buffer, 24);
        BitConverter.GetBytes(aux).CopyTo(buffer, 28);
    }

    private static bool TryReadLobbyPacketCore(
        byte[] data,
        int length,
        byte expectedOp,
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out uint flags,
        out uint aux)
    {
        sessionId = 0;
        revision = 0;
        transitionSeq = 0;
        flags = 0;
        aux = 0;
        if (!IsCoopControl(data, length) || length < LobbyControlPayloadLength || data[4] != expectedOp)
            return false;
        sessionId = BitConverter.ToUInt64(data, 8);
        revision = BitConverter.ToUInt32(data, 16);
        transitionSeq = BitConverter.ToUInt32(data, 20);
        flags = BitConverter.ToUInt32(data, 24);
        aux = BitConverter.ToUInt32(data, 28);
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

    /// <summary>v1 wire: [5]=schema 1, [8..23] floats, [24] flags bit0=night.</summary>
    public static int WriteWorldEnvV1(
        byte[] buffer,
        float tempCelsius,
        float airDensity,
        float ammoTempFahrenheit,
        float airCoefficient,
        bool night)
    {
        if (buffer.Length < WorldEnvV1TotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpWorldEnv;
        buffer[5] = 1;
        buffer[6] = 0;
        buffer[7] = 0;
        int o = SyncHeaderLength;
        BitConverter.GetBytes(tempCelsius).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(airDensity).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(ammoTempFahrenheit).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(airCoefficient).CopyTo(buffer, o);
        o += 4;
        buffer[o] = (byte)(night ? 1 : 0);
        return WorldEnvV1TotalLength;
    }

    public static bool TryReadWorldEnvV1(
        byte[] data,
        int length,
        out float tempCelsius,
        out float airDensity,
        out float ammoTempFahrenheit,
        out float airCoefficient,
        out bool night)
    {
        tempCelsius = 0f;
        airDensity = 0f;
        ammoTempFahrenheit = 0f;
        airCoefficient = 0f;
        night = false;
        if (!IsCoopControl(data, length) || length < WorldEnvV1TotalLength || data[4] != OpWorldEnv)
            return false;
        if (data[5] != 1)
            return false;
        int o = SyncHeaderLength;
        tempCelsius = BitConverter.ToSingle(data, o);
        o += 4;
        airDensity = BitConverter.ToSingle(data, o);
        o += 4;
        ammoTempFahrenheit = BitConverter.ToSingle(data, o);
        o += 4;
        airCoefficient = BitConverter.ToSingle(data, o);
        o += 4;
        night = (data[o] & 1) != 0;
        return true;
    }

    /// <summary>v2 wire: same as v1 through [24], then raininess, cloudiness, windiness, CloudBias. Flags: bit0 night, bit1 weatherValid, bit2 useDynamicWeather.</summary>
    public static int WriteWorldEnvV2(
        byte[] buffer,
        float tempCelsius,
        float airDensity,
        float ammoTempFahrenheit,
        float airCoefficient,
        bool night,
        bool weatherValid,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias)
    {
        if (buffer.Length < WorldEnvV2TotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Magic2;
        buffer[3] = WireVersion1;
        buffer[4] = OpWorldEnv;
        buffer[5] = WorldEnvSchemaV2;
        buffer[6] = 0;
        buffer[7] = 0;
        int o = SyncHeaderLength;
        BitConverter.GetBytes(tempCelsius).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(airDensity).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(ammoTempFahrenheit).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(airCoefficient).CopyTo(buffer, o);
        o += 4;
        byte flags = 0;
        if (night)
            flags |= 1;
        if (weatherValid)
            flags |= 2;
        if (useDynamicWeather)
            flags |= 4;
        buffer[o] = flags;
        o += 1;
        BitConverter.GetBytes(raininess).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(cloudiness).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(windiness).CopyTo(buffer, o);
        o += 4;
        BitConverter.GetBytes(cloudBias).CopyTo(buffer, o);
        return WorldEnvV2TotalLength;
    }

    /// <inheritdoc cref="WriteWorldEnvV2" />
    public static int WriteWorldEnvV3(
        byte[] buffer,
        float tempCelsius,
        float airDensity,
        float ammoTempFahrenheit,
        float airCoefficient,
        bool night,
        bool weatherValid,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias,
        byte cloudCondition,
        float cloudSpeed)
    {
        if (buffer.Length < WorldEnvV3TotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteWorldEnvV2(
            buffer,
            tempCelsius,
            airDensity,
            ammoTempFahrenheit,
            airCoefficient,
            night,
            weatherValid,
            useDynamicWeather,
            raininess,
            cloudiness,
            windiness,
            cloudBias);
        buffer[5] = WorldEnvSchemaV3;
        buffer[WorldEnvV2TotalLength] = cloudCondition;
        BitConverter.GetBytes(cloudSpeed).CopyTo(buffer, WorldEnvV2TotalLength + 1);
        return WorldEnvV3TotalLength;
    }

    /// <inheritdoc cref="WriteWorldEnvV3" />
    public static int WriteWorldEnvV4(
        byte[] buffer,
        float tempCelsius,
        float airDensity,
        float ammoTempFahrenheit,
        float airCoefficient,
        bool night,
        bool weatherValid,
        bool useDynamicWeather,
        float raininess,
        float cloudiness,
        float windiness,
        float cloudBias,
        byte cloudCondition,
        float cloudSpeed,
        float cloudDirX,
        float cloudDirY)
    {
        if (buffer.Length < WorldEnvV4TotalLength)
            throw new ArgumentException("buffer too small", nameof(buffer));
        WriteWorldEnvV3(
            buffer,
            tempCelsius,
            airDensity,
            ammoTempFahrenheit,
            airCoefficient,
            night,
            weatherValid,
            useDynamicWeather,
            raininess,
            cloudiness,
            windiness,
            cloudBias,
            cloudCondition,
            cloudSpeed);
        buffer[5] = WorldEnvSchemaV4;
        BitConverter.GetBytes(cloudDirX).CopyTo(buffer, WorldEnvV3TotalLength);
        BitConverter.GetBytes(cloudDirY).CopyTo(buffer, WorldEnvV3TotalLength + 4);
        return WorldEnvV4TotalLength;
    }

    /// <summary>Parses v1 (no weather block), v2/v3 (weather + cloud layer), or v4 (plus cloud drift direction).</summary>
    public static bool TryParseWorldEnv(
        byte[] data,
        int length,
        out float tempCelsius,
        out float airDensity,
        out float ammoTempFahrenheit,
        out float airCoefficient,
        out bool night,
        out bool weatherValid,
        out bool useDynamicWeather,
        out float raininess,
        out float cloudiness,
        out float windiness,
        out float cloudBias,
        out bool cloudLayerFromHost,
        out byte cloudCondition,
        out float cloudSpeed,
        out bool cloudDirectionFromHost,
        out float cloudDirX,
        out float cloudDirY)
    {
        tempCelsius = 0f;
        airDensity = 0f;
        ammoTempFahrenheit = 0f;
        airCoefficient = 0f;
        night = false;
        weatherValid = false;
        useDynamicWeather = false;
        raininess = 0f;
        cloudiness = 0f;
        windiness = 0f;
        cloudBias = 0f;
        cloudLayerFromHost = false;
        cloudCondition = 0;
        cloudSpeed = 0f;
        cloudDirectionFromHost = false;
        cloudDirX = 0f;
        cloudDirY = 0f;
        if (!IsCoopControl(data, length) || length < WorldEnvV1TotalLength || data[4] != OpWorldEnv)
            return false;
        byte schema = data[5];
        int o = SyncHeaderLength;
        tempCelsius = BitConverter.ToSingle(data, o);
        o += 4;
        airDensity = BitConverter.ToSingle(data, o);
        o += 4;
        ammoTempFahrenheit = BitConverter.ToSingle(data, o);
        o += 4;
        airCoefficient = BitConverter.ToSingle(data, o);
        o += 4;
        byte flags = data[o];
        night = (flags & 1) != 0;
        if (schema == WorldEnvSchemaV1)
            return true;
        if (schema != WorldEnvSchemaV2 && schema != WorldEnvSchemaV3 && schema != WorldEnvSchemaV4)
            return false;
        if (length < WorldEnvV2TotalLength)
            return false;
        weatherValid = (flags & 2) != 0;
        useDynamicWeather = (flags & 4) != 0;
        o += 1;
        raininess = BitConverter.ToSingle(data, o);
        o += 4;
        cloudiness = BitConverter.ToSingle(data, o);
        o += 4;
        windiness = BitConverter.ToSingle(data, o);
        o += 4;
        cloudBias = BitConverter.ToSingle(data, o);
        if ((schema == WorldEnvSchemaV3 || schema == WorldEnvSchemaV4) && length >= WorldEnvV3TotalLength)
        {
            cloudLayerFromHost = true;
            cloudCondition = data[WorldEnvV2TotalLength];
            cloudSpeed = BitConverter.ToSingle(data, WorldEnvV2TotalLength + 1);
        }

        if (schema == WorldEnvSchemaV4 && length >= WorldEnvV4TotalLength)
        {
            cloudDirectionFromHost = true;
            cloudDirX = BitConverter.ToSingle(data, WorldEnvV3TotalLength);
            cloudDirY = BitConverter.ToSingle(data, WorldEnvV3TotalLength + 4);
        }

        return true;
    }
}
