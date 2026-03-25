using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using GHPC.CoopFoundation;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

internal enum CoopNetRole
{
    Off,
    Host,
    Client
}

/// <summary>UDP exchange of <see cref="CoopNetPacket" /> v3 (v2/v1 still accepted as legacy).</summary>
internal static class CoopUdpTransport
{
    private const byte WirePlaying = 2;

    private const float MissionMismatchLogCooldownSeconds = 4f;

    private static readonly object InboxLock = new();

    private static readonly Queue<(IPEndPoint Remote, byte[] Data)> Inbox = new();

    private static UdpClient? _udp;

    private static CoopNetRole _role;

    private static IPEndPoint? _serverEndPoint;

    /// <summary>Host only: peer to echo snapshots to (learned from inbound packets).</summary>
    private static IPEndPoint? _hostPeer;

    private static uint _sendSequence;

    private static byte[]? _sendBuffer;

    private static bool _logReceive;

    private static bool _logMissionMismatch = true;

    private static float _nextMissionMismatchLogTime = float.NegativeInfinity;

    private static bool _started;

    private static byte[]? _controlSendBuffer;

    /// <summary>Prefs: host sends GHW world snapshots when true.</summary>
    private static bool _worldReplicationEnabled = true;

    private static float _worldReplicationHz = 5f;

    private static bool _logWorldReplicationSend;

    private static bool _logWorldReplicationReceive;

    private static bool _combatReplicationEnabled = true;

    private static bool _logCombatReplication;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each GHC Struck send/recv (very noisy).</summary>
    private static bool _logCombatStruckPerHit;

    /// <summary>Client: max GHC events applied per frame (0 = unlimited).</summary>
    private static int _combatApplyMaxPerFrame = 64;

    /// <summary>Client: max wall milliseconds per frame for GHC apply (0 = unlimited).</summary>
    private static float _combatApplyMaxMsPerFrame = 16f;

    /// <summary>Phase 4: host emits GHC ImpactFx (terrain, ricochet, armor, penetration SFX); requires combat channel on.</summary>
    private static bool _impactFxReplicationEnabled = true;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each ImpactFx send/recv (can be noisy).</summary>
    private static bool _logImpactFx;

    /// <summary>Phase 4B: host emits compact damage correction snapshots.</summary>
    private static bool _damageStateReplicationEnabled = true;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each DamageState send/recv.</summary>
    private static bool _logDamageState;

    private static uint _hostCombatSeq;

    public static bool IsNetworkActive => _started && _role != CoopNetRole.Off;

    public static bool IsHost => _started && _role == CoopNetRole.Host;

    /// <summary>Host: GHC combat events to peer when network + combat replication are on.</summary>
    public static bool IsHostCombatReplicationActive =>
        IsHost
        && _combatReplicationEnabled
        && _hostPeer != null
        && _udp != null;

    /// <summary>Host: Phase 4 cosmetic/SFX packets (same UDP path as GHC).</summary>
    public static bool IsHostImpactFxReplicationActive =>
        IsHostCombatReplicationActive && _impactFxReplicationEnabled;

    /// <summary>Host: Phase 4B damage correction snapshots.</summary>
    public static bool IsHostDamageStateReplicationActive =>
        IsHostCombatReplicationActive && _damageStateReplicationEnabled;

    public static void SetCombatReplicationPrefs(
        bool enabled,
        bool logCombat,
        bool logStruckPerHit,
        int maxApplyPerFrame,
        float maxApplyMsPerFrame)
    {
        _combatReplicationEnabled = enabled;
        _logCombatReplication = logCombat;
        _logCombatStruckPerHit = logStruckPerHit;
        _combatApplyMaxPerFrame = maxApplyPerFrame < 0 ? 0 : maxApplyPerFrame;
        _combatApplyMaxMsPerFrame = maxApplyMsPerFrame < 0f ? 0f : maxApplyMsPerFrame;
    }

    public static void SetImpactFxReplicationPrefs(bool enabled, bool logImpactFx)
    {
        _impactFxReplicationEnabled = enabled;
        _logImpactFx = logImpactFx;
    }

    public static void SetDamageStateReplicationPrefs(bool enabled, bool logDamageState)
    {
        _damageStateReplicationEnabled = enabled;
        _logDamageState = logDamageState;
    }

    internal static bool CombatReplicationLogFired => _logCombatReplication;

    internal static bool CombatReplicationLogStruckPerHit => _logCombatReplication && _logCombatStruckPerHit;

    internal static bool CombatReplicationLogImpactFx => _logCombatReplication && _logImpactFx;

    internal static bool CombatReplicationLogDamageState => _logCombatReplication && _logDamageState;

    internal static bool CombatReplicationLogHealth => _logCombatReplication;

    public static uint TakeNextHostCombatSeq()
    {
        unchecked
        {
            _hostCombatSeq++;
        }

        return _hostCombatSeq;
    }

    public static bool TryHostSendCombat(byte[] buffer, int length)
    {
        if (!IsHost || _udp == null || _hostPeer == null || buffer == null || length <= 0)
            return false;
        try
        {
            _udp.Send(buffer, length, _hostPeer);
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] GHC send failed: {ex.Message}");
            return false;
        }
    }

    public static void SetWorldReplicationPrefs(bool enabled, float hz, bool logSend, bool logReceive)
    {
        _worldReplicationEnabled = enabled;
        _worldReplicationHz = hz <= 0f ? 5f : hz;
        _logWorldReplicationSend = logSend;
        _logWorldReplicationReceive = logReceive;
    }

    public static void ConfigureAndStart(
        bool enabled,
        string roleName,
        int bindPort,
        string remoteHost,
        int remotePort,
        bool logReceive,
        bool logMissionMismatch,
        bool enforceVehicleOwnership,
        bool logVehicleOwnershipBlocks)
    {
        Shutdown();
        _logReceive = logReceive;
        _logMissionMismatch = logMissionMismatch;
        if (!enabled)
        {
            _role = CoopNetRole.Off;
            return;
        }

        _role = ParseRole(roleName);
        if (_role == CoopNetRole.Off)
            return;

        try
        {
            if (_role == CoopNetRole.Host)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, bindPort));
                MelonLogger.Msg($"[CoopNet] Host listening UDP *:{bindPort}");
            }
            else
            {
                _udp = new UdpClient(0);
                if (!IPAddress.TryParse(remoteHost, out IPAddress? addr))
                {
                    MelonLogger.Warning($"[CoopNet] RemoteHost '{remoteHost}' is not a valid IPv4 address; network disabled.");
                    Shutdown();
                    return;
                }

                _serverEndPoint = new IPEndPoint(addr, remotePort);
                MelonLogger.Msg($"[CoopNet] Client will send to {_serverEndPoint}");
            }

            _sendBuffer = new byte[CoopNetPacket.LengthV3];
            _controlSendBuffer = new byte[CoopControlPacket.SyncHeaderLength + CoopControlPacket.MaxSyncEntries * 8];
            _started = true;
            CoopVehicleOwnership.Configure(_role == CoopNetRole.Host, enforceVehicleOwnership, logVehicleOwnershipBlocks);
            CoopNetSession.OnNetworkStarted(_role == CoopNetRole.Host);
            BeginReceive();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[CoopNet] Failed to start: {ex.Message}");
            Shutdown();
        }
    }

    public static void Shutdown()
    {
        _started = false;
        try
        {
            _udp?.Close();
        }
        catch
        {
            // ignored
        }

        _udp = null;
        _serverEndPoint = null;
        _hostPeer = null;
        _sendBuffer = null;
        _controlSendBuffer = null;
        _role = CoopNetRole.Off;
        lock (InboxLock)
            Inbox.Clear();

        CoopNetSession.Reset();
        CoopRemoteState.Clear();
        CoopVehicleOwnership.Clear();
        HostWorldReplication.Reset();
        CoopUnitWireRegistry.ResetSession();
        ClientWorldProxyService.ClearAll();
        ResetCombatSessionState();
    }

    /// <summary>Main-thread tick: Hello / Heartbeat (host-authoritative session).</summary>
    public static void NetworkSessionTick()
    {
        CoopNetSession.Tick(Time.time);
    }

    /// <summary>Drain async receives on the main thread.</summary>
    public static void ProcessInbound()
    {
        if (!_started || _udp == null)
            return;

        while (true)
        {
            (IPEndPoint Remote, byte[] Data) item;
            lock (InboxLock)
            {
                if (Inbox.Count == 0)
                    break;
                item = Inbox.Dequeue();
            }

            byte[] data = item.Data;
            if (CoopControlPacket.IsCoopControl(data, data.Length))
            {
                ProcessControlPacket(item.Remote, data, data.Length);
                continue;
            }

            if (CoopCombatPacket.IsCoopCombat(data, data.Length))
            {
                if (_role == CoopNetRole.Client && _combatReplicationEnabled)
                    ClientCombatApplier.EnqueuePendingCombat(data, data.Length);
                continue;
            }

            if (CoopWorldPacket.IsCoopWorld(data, data.Length))
            {
                if (_role == CoopNetRole.Client
                    && CoopWorldPacket.TryRead(data, data.Length, out CoopWorldPacketDecoded world))
                {
                    ClientWorldProxyService.OnWorldDecoded(in world, _logWorldReplicationReceive);
                }

                continue;
            }

            if (!CoopNetPacket.TryRead(data, data.Length, out CoopSnapshotWire snap))
                continue;

            if (!CoopSessionState.IsPlaying)
                continue;

            if (!AcceptRemoteSnapshot(snap.LegacyV1, snap.MissionToken, snap.MissionPhaseWire))
            {
                CoopRemoteState.Clear();
                LogMissionMismatchThrottled(snap.MissionToken, snap.MissionPhaseWire, snap.LegacyV1);
                continue;
            }

            EnsureHostPeerForOwnership(item.Remote);

            CoopRemoteState.Apply(
                snap.Sequence,
                snap.InstanceId,
                snap.Position,
                snap.HullRotation,
                snap.TurretWorldRotation,
                snap.GunWorldRotation,
                snap.UnitNetId);
            if (_logReceive)
            {
                Vector3 e = snap.HullRotation.eulerAngles;
                string extra = snap.LegacyV1
                    ? " legacy=v1"
                    : $" token={snap.MissionToken} phase={snap.MissionPhaseWire}";
                MelonLogger.Msg(
                    $"[CoopNet] recv seq={snap.Sequence} id={snap.InstanceId} netId={snap.UnitNetId} from={item.Remote} " +
                    $"pos=({snap.Position.x:F1},{snap.Position.y:F1},{snap.Position.z:F1}) hullEuler=({e.x:F0},{e.y:F0},{e.z:F0}){extra}");
            }
        }
    }

    private static bool AcceptRemoteSnapshot(bool legacyV1, uint remoteToken, byte remotePhase)
    {
        if (legacyV1)
            return true;
        uint localToken = CoopSessionState.MissionCoherenceToken;
        if (localToken == 0)
            return false;
        if (remotePhase != WirePlaying)
            return false;
        return remoteToken == localToken;
    }

    private static void LogMissionMismatchThrottled(uint remoteToken, byte remotePhase, bool legacyV1)
    {
        if (!_logMissionMismatch || legacyV1)
            return;
        float now = Time.time;
        if (now < _nextMissionMismatchLogTime)
            return;
        _nextMissionMismatchLogTime = now + MissionMismatchLogCooldownSeconds;
        uint local = CoopSessionState.MissionCoherenceToken;
        string key = CoopSessionState.MissionSceneKey;
        MelonLogger.Warning(
            "[CoopNet] Dropped remote snapshot (mission mismatch). " +
            $"localToken={local} localMission=\"{key}\" remoteToken={remoteToken} remotePhase={remotePhase}. " +
            "Both must be Playing with the same MissionSceneName (see MissionInitializer).");
    }

    /// <summary>Send local <see cref="CoopSessionState" /> snapshot (call right after a new 10 Hz sample).</summary>
    public static void SendLocalSnapshot()
    {
        if (!_started || _udp == null || _sendBuffer == null)
            return;
        if (!CoopSessionState.IsPlaying)
            return;

        IPEndPoint? target = _role switch
        {
            CoopNetRole.Client => _serverEndPoint,
            CoopNetRole.Host => _hostPeer,
            _ => null
        };

        if (target == null)
            return;

        try
        {
            unchecked
            {
                _sendSequence++;
            }

            uint token = CoopSessionState.MissionCoherenceToken;
            byte phase = CoopSessionState.MissionStateToWirePhase();
            CoopNetPacket.WriteV3(
                _sendBuffer,
                _sendSequence,
                CoopSessionState.LastSampledUnitInstanceId,
                CoopSessionState.LastSampledPosition,
                CoopSessionState.LastSampledRotation,
                token,
                phase,
                CoopSessionState.LastSampledTurretWorldRotation,
                CoopSessionState.LastSampledGunWorldRotation,
                CoopSessionState.LastSampledUnitNetId);
            _udp.Send(_sendBuffer, CoopNetPacket.LengthV3, target);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] Send failed: {ex.Message}");
        }
    }

    public static void OnSessionCleared()
    {
        _hostPeer = null;
        CoopNetSession.OnPlayingSessionEnded();
        CoopRemoteState.Clear();
        CoopVehicleOwnership.Clear();
        HostWorldReplication.Reset();
        CoopUnitWireRegistry.ResetSession();
        ClientWorldProxyService.ClearAll();
        ResetCombatSessionState();
    }

    /// <summary>Host: periodic GHW broadcast to connected peer (main thread).</summary>
    public static void HostTickWorldReplication(float deltaTime)
    {
        if (!_started || _udp == null || _role != CoopNetRole.Host || !_worldReplicationEnabled)
            return;
        if (_hostPeer == null)
            return;
        float interval = 1f / _worldReplicationHz;
        HostWorldReplication.TickSend(
            deltaTime,
            interval,
            _logWorldReplicationSend,
            _udp,
            _hostPeer);
    }

    public static void SendSwitchPacket(byte peerId, uint oldNetId, uint newNetId)
    {
        if (!_started || _udp == null || _controlSendBuffer == null)
            return;
        if (_role == CoopNetRole.Host)
            return;
        IPEndPoint? target = _serverEndPoint;
        if (target == null)
            return;
        try
        {
            CoopControlPacket.WriteSwitch(_controlSendBuffer, peerId, oldNetId, newNetId);
            _udp.Send(_controlSendBuffer, CoopControlPacket.FixedControlPayloadLength, target);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendSwitch failed: {ex.Message}");
        }
    }

    public static void SendOwnerSync()
    {
        if (!_started || _udp == null || _controlSendBuffer == null || _role != CoopNetRole.Host)
            return;
        IPEndPoint? target = _hostPeer;
        if (target == null)
            return;
        try
        {
            (uint netId, byte peerId)[] snap = CoopVehicleOwnership.SnapshotAuthoritativeForSync();
            int len = CoopControlPacket.WriteOwnerSync(_controlSendBuffer, snap);
            _udp.Send(_controlSendBuffer, len, target);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendOwnerSync failed: {ex.Message}");
        }
    }

    private static void EnsureHostPeerForOwnership(IPEndPoint remote)
    {
        if (_role != CoopNetRole.Host)
            return;
        if (_hostPeer == null)
        {
            _hostPeer = remote;
            SendOwnerSync();
            return;
        }

        _hostPeer = remote;
    }

    internal static void SendHello(uint nonce)
    {
        if (!_started || _udp == null || _controlSendBuffer == null || _role != CoopNetRole.Client)
            return;
        IPEndPoint? target = _serverEndPoint;
        if (target == null)
            return;
        try
        {
            CoopControlPacket.WriteHello(_controlSendBuffer, nonce);
            _udp.Send(_controlSendBuffer, CoopControlPacket.FixedControlPayloadLength, target);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendHello failed: {ex.Message}");
        }
    }

    internal static void SendHeartbeat(byte senderPeerId, uint seq)
    {
        if (!_started || _udp == null || _controlSendBuffer == null || _role != CoopNetRole.Client)
            return;
        IPEndPoint? target = _serverEndPoint;
        if (target == null)
            return;
        try
        {
            CoopControlPacket.WriteHeartbeat(_controlSendBuffer, senderPeerId, seq);
            _udp.Send(_controlSendBuffer, CoopControlPacket.FixedControlPayloadLength, target);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendHeartbeat failed: {ex.Message}");
        }
    }

    private static void SendWelcome(IPEndPoint clientRemote, byte assignedPeerId, uint nonceEcho)
    {
        if (!_started || _udp == null || _controlSendBuffer == null || _role != CoopNetRole.Host)
            return;
        try
        {
            CoopControlPacket.WriteWelcome(_controlSendBuffer, assignedPeerId, nonceEcho);
            _udp.Send(_controlSendBuffer, CoopControlPacket.FixedControlPayloadLength, clientRemote);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendWelcome failed: {ex.Message}");
        }
    }

    private static void ProcessControlPacket(IPEndPoint remote, byte[] data, int length)
    {
        if (length < 5)
            return;
        byte op = data[4];
        if (op == CoopControlPacket.OpHello && _role == CoopNetRole.Host)
        {
            if (length < CoopControlPacket.FixedControlPayloadLength
                || !CoopControlPacket.TryReadHello(data, length, out uint nonce, out _))
                return;
            EnsureHostPeerForOwnership(remote);
            CoopNetSession.HostRegisterPeer(remote, out byte assigned);
            SendWelcome(remote, assigned, nonce);
            if (CoopSessionState.IsPlaying)
                SendOwnerSync();
            return;
        }

        if (op == CoopControlPacket.OpWelcome && _role == CoopNetRole.Client)
        {
            if (length < CoopControlPacket.FixedControlPayloadLength
                || !CoopControlPacket.TryReadWelcome(data, length, out byte peer, out uint nonceEcho))
                return;
            if (CoopNetSession.ClientTryApplyWelcome(peer, nonceEcho))
                MelonLogger.Msg($"[CoopNet] Session: host assigned peerId={peer}");
            return;
        }

        if (op == CoopControlPacket.OpHeartbeat && _role == CoopNetRole.Host)
        {
            if (length < CoopControlPacket.FixedControlPayloadLength
                || !CoopControlPacket.TryReadHeartbeat(data, length, out _, out _))
                return;
            EnsureHostPeerForOwnership(remote);
            CoopNetSession.HostNotifyClientHeartbeat();
            return;
        }

        if (!CoopSessionState.IsPlaying)
            return;

        if (op == CoopControlPacket.OpSwitch && _role == CoopNetRole.Host)
        {
            if (!CoopControlPacket.TryReadSwitch(data, length, out byte peerId, out uint oldId, out uint newId))
                return;
            EnsureHostPeerForOwnership(remote);
            byte expected = CoopNetSession.HostGetExpectedPeerId(remote);
            if (expected != 0 && peerId != expected)
            {
                MelonLogger.Warning(
                    $"[CoopNet] Switch from {remote} claimed peerId={peerId}; host table expects {expected} — applying host id.");
                peerId = expected;
            }

            CoopVehicleOwnership.HostApplySwitch(peerId, oldId, newId);
            SendOwnerSync();
        }
        else if (op == CoopControlPacket.OpSync && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadOwnerSync(data, length, out (uint netId, byte peerId)[] entries))
                return;
            CoopVehicleOwnership.ClientApplyOwnerSync(entries);
        }
    }

    private static void ResetCombatSessionState()
    {
        _hostCombatSeq = 0;
        HostCombatBroadcast.ResetSession();
        ClientCombatApplier.ResetSession();
        CoopAmmoResolver.InvalidateCache();
    }

    /// <summary>Client: flush a slice of queued GHC applies (call after <see cref="ProcessInbound" />).</summary>
    public static void DrainClientCombatApply()
    {
        if (!_started || _role != CoopNetRole.Client)
            return;
        if (!_combatReplicationEnabled)
        {
            ClientCombatApplier.ClearPendingQueueOnly();
            return;
        }

        ClientCombatApplier.DrainPendingCombat(
            _combatApplyMaxPerFrame,
            _combatApplyMaxMsPerFrame,
            CombatReplicationLogFired,
            CombatReplicationLogStruckPerHit,
            CombatReplicationLogImpactFx,
            CombatReplicationLogDamageState,
            CombatReplicationLogHealth);
    }

    private static CoopNetRole ParseRole(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CoopNetRole.Off;
        if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            return CoopNetRole.Host;
        if (string.Equals(name, "Client", StringComparison.OrdinalIgnoreCase))
            return CoopNetRole.Client;
        return CoopNetRole.Off;
    }

    private static void BeginReceive()
    {
        if (!_started || _udp == null)
            return;

        try
        {
            _udp.BeginReceive(ReceiveCallback, null);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] BeginReceive: {ex.Message}");
        }
    }

    private static void ReceiveCallback(IAsyncResult ar)
    {
        try
        {
            if (_udp == null)
                return;
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] data = _udp.EndReceive(ar, ref remote);
            if (data != null && (CoopWorldPacket.IsCoopWorld(data, data.Length)
                                 || CoopCombatPacket.IsCoopCombat(data, data.Length)
                                 || data.Length >= CoopNetPacket.MinIncomingLength
                                 || CoopControlPacket.IsCoopControl(data, data.Length)))
            {
                lock (InboxLock)
                    Inbox.Enqueue((remote, data));
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.ConnectionReset
            || ex.SocketErrorCode == SocketError.NetworkReset)
        {
            // Windows UDP: ICMP "port unreachable" / peer vanished — not a fatal error; avoid log spam.
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] Receive: {ex.Message}");
        }

        BeginReceive();
    }
}
