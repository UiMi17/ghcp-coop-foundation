using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    // Balanced profile: cap per-frame network burst without over-throttling steady sync.
    private const int MaxInboundDatagramsPerTick = 192;

    private static UdpClient? _udp;

    private static CoopNetRole _role;

    private static IPEndPoint? _serverEndPoint;

    /// <summary>Host only: peer to echo snapshots to (learned from inbound packets).</summary>
    private static IPEndPoint? _hostPeer;

    private static uint _sendSequence;

    private static byte[]? _sendBuffer;

    private static bool _logReceive;

    private static bool _logMissionMismatch = true;
    private static bool _enforceVehicleOwnership = true;
    private static bool _logVehicleOwnershipBlocks;

    private static float _nextMissionMismatchLogTime = float.NegativeInfinity;

    private static float _nextGhcNoPeerLogTime = float.NegativeInfinity;

    private const float GhcNoPeerLogCooldownSeconds = 5f;

    private static bool _started;

    private static byte[]? _controlSendBuffer;

    /// <summary>Prefs: host sends GHW world snapshots when true.</summary>
    private static bool _worldReplicationEnabled = true;

    private static float _worldReplicationHz = 5f;

    private static bool _logWorldReplicationSend;

    private static bool _logWorldReplicationReceive;
    private static float _nextWorldBurstLogTime = float.NegativeInfinity;
    private static int _worldDatagramsThisTick;
    private static int _worldDatagramsMaxTick;
    private static uint _worldDatagramsTotal;
    private static uint _worldProcessTicks;

    private static bool _combatReplicationEnabled = true;

    private static bool _logCombatReplication;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each GHC Struck send/recv (very noisy).</summary>
    private static bool _logCombatStruckPerHit;

    /// <summary>Client: max GHC events applied per frame (0 = unlimited).</summary>
    private static int _combatApplyMaxPerFrame = 64;

    /// <summary>Client: max wall milliseconds per frame for GHC apply (0 = unlimited).</summary>
    private static float _combatApplyMaxMsPerFrame = 16f;

    /// <summary>Host: replicate HitResolved events when true.</summary>
    private static bool _hitResolvedReplicationEnabled = true;

    /// <summary>Host: max HitResolved sends per second (0 = unlimited).</summary>
    private static int _hitResolvedMaxPerSecond = 60;

    /// <summary>Host: max HitResolved UDP sends per LateUpdate after coalescing (0 = unlimited).</summary>
    private static int _hitResolvedHostMaxPerFrame = 8;

    /// <summary>Client: max low-priority HitResolved applies per frame.</summary>
    private static int _hitResolvedApplyMaxPerFrame = 8;

    /// <summary>Phase 4: host emits GHC ImpactFx (terrain, ricochet, armor, penetration SFX); requires combat channel on.</summary>
    private static bool _impactFxReplicationEnabled = true;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each ImpactFx send/recv (can be noisy).</summary>
    private static bool _logImpactFx;

    /// <summary>Phase 4B: host emits compact damage correction snapshots.</summary>
    private static bool _damageStateReplicationEnabled = true;

    /// <summary>When <see cref="_logCombatReplication" /> is true, log each DamageState send/recv.</summary>
    private static bool _logDamageState;

    private static bool _particleImpactReplicationEnabled = true;

    private static bool _explosionReplicationEnabled = true;

    private static bool _muzzleCosmeticReplayEnabled = true;

    private static float _cosmeticInterestMaxDistanceMeters;

    private static float _explosionCameraMinTntKg = 0.01f;

    private static float _explosionCameraMaxDistanceMeters = 400f;

    private static bool _logCosmeticHealth;

    private static bool _fragGrenadeCosmeticTntUseApRadius = true;

    private static float _fragGrenadeCosmeticTntFallbackKg = 0.22f;

    private static bool _atGrenadeJetVisualReplicationEnabled = true;

    /// <summary>Host→client COO <see cref="CoopControlPacket.OpWorldEnv" /> (mission atmosphere / ballistics globals).</summary>
    private static bool _worldEnvironmentReplicationEnabled = true;

    private static float _worldEnvironmentReplicationHz = 1f;

    private static float _hostWorldEnvSendAccumulator;

    private static bool _logWorldEnvironmentSync;

    private static uint _hostCombatSeq;
    private static readonly CoopLobbySessionController LobbySession = new();
    private static float _nextLobbyStaleLogTime = float.NegativeInfinity;
    private const float LobbyStaleLogCooldownSeconds = 2f;
    private static ulong _clientAckSentSessionId;
    private static uint _clientAckSentTransitionSeq;

    /// <summary>M4: merge <see cref="CoopControlPacket.OpLobbyMissionLaunchInfo" /> with <see cref="CoopControlPacket.OpLobbyLoadMission" /> (UDP reorder).</summary>
    private static ulong _clientMergeLoadSid;

    private static uint _clientMergeLoadSeq;

    private static uint _clientMergeLoadToken;

    private static bool _clientHasMergeLoad;

    private static ulong _clientMergeKeySid;

    private static uint _clientMergeKeySeq;

    private static string? _clientMergeKey;

    private static bool _clientHasMergeKey;

    public static bool IsNetworkActive => _started && _role != CoopNetRole.Off;

    public static bool IsHost => _started && _role == CoopNetRole.Host;

    public static bool IsClient => _started && _role == CoopNetRole.Client;

    public static bool CombatReplicationEnabledPublic => _combatReplicationEnabled;

    /// <summary>User prefs: replicate WorldEnvironmentManager + Weather (COO WorldEnv).</summary>
    public static bool WorldEnvironmentReplicationEnabledPublic => _worldEnvironmentReplicationEnabled;

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

    /// <summary>Host: Phase 5 hit outcome event stream.</summary>
    public static bool IsHostHitResolvedReplicationActive =>
        IsHostCombatReplicationActive && _hitResolvedReplicationEnabled;

    public static bool IsHostParticleImpactReplicationActive =>
        IsHostCombatReplicationActive && _particleImpactReplicationEnabled;

    public static bool IsHostExplosionReplicationActive =>
        IsHostCombatReplicationActive && _explosionReplicationEnabled;

    /// <summary>Host: emit GHC <see cref="CoopCombatPacket.EventGrenadeJetVisual" /> for AT grenade <see cref="GHPC.Weapons.LiveRound" /> spawns.</summary>
    public static bool IsHostAtGrenadeJetVisualActive =>
        IsHostCombatReplicationActive && _atGrenadeJetVisualReplicationEnabled;

    internal static bool MuzzleCosmeticReplayEnabled => _muzzleCosmeticReplayEnabled;

    internal static float CosmeticInterestMaxDistanceMeters => _cosmeticInterestMaxDistanceMeters;

    internal static float ExplosionCameraMinTntKg => _explosionCameraMinTntKg;

    internal static float ExplosionCameraMaxDistanceMeters => _explosionCameraMaxDistanceMeters;

    internal static bool LogCosmeticHealth => _logCombatReplication && _logCosmeticHealth;

    internal static bool FragGrenadeCosmeticTntUseApRadius => _fragGrenadeCosmeticTntUseApRadius;

    internal static float FragGrenadeCosmeticTntFallbackKg => _fragGrenadeCosmeticTntFallbackKg;

    internal static int HitResolvedMaxPerSecond => _hitResolvedMaxPerSecond;

    internal static int HitResolvedHostMaxPerFrame => _hitResolvedHostMaxPerFrame;

    public static void SetCombatReplicationPrefs(
        bool enabled,
        bool logCombat,
        bool logStruckPerHit,
        int maxApplyPerFrame,
        float maxApplyMsPerFrame,
        bool hitResolvedEnabled,
        int hitResolvedMaxPerSecond,
        int hitResolvedHostMaxPerFrame,
        int hitResolvedApplyMaxPerFrame)
    {
        _combatReplicationEnabled = enabled;
        _logCombatReplication = logCombat;
        _logCombatStruckPerHit = logStruckPerHit;
        _combatApplyMaxPerFrame = maxApplyPerFrame < 0 ? 0 : maxApplyPerFrame;
        _combatApplyMaxMsPerFrame = maxApplyMsPerFrame < 0f ? 0f : maxApplyMsPerFrame;
        _hitResolvedReplicationEnabled = hitResolvedEnabled;
        _hitResolvedMaxPerSecond = hitResolvedMaxPerSecond < 0 ? 0 : hitResolvedMaxPerSecond;
        _hitResolvedHostMaxPerFrame = hitResolvedHostMaxPerFrame < 0 ? 0 : hitResolvedHostMaxPerFrame;
        _hitResolvedApplyMaxPerFrame = hitResolvedApplyMaxPerFrame < 0 ? 0 : hitResolvedApplyMaxPerFrame;
    }

    public static void SetWorldEnvironmentReplicationPrefs(bool enabled, float hz, bool logSync)
    {
        _worldEnvironmentReplicationEnabled = enabled;
        _worldEnvironmentReplicationHz = hz <= 0f ? 0f : hz;
        _logWorldEnvironmentSync = logSync;
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

    public static void SetCosmeticReplicationPrefs(
        bool suppressRemoteShooterRoundCosmetics,
        bool particleImpactEnabled,
        bool explosionEnabled,
        bool muzzleReplayEnabled,
        float cosmeticInterestMaxDistanceMeters,
        float explosionCameraMinTntKg,
        float explosionCameraMaxDistanceMeters,
        bool logCosmeticHealth,
        bool fragGrenadeCosmeticTntUseApRadius,
        float fragGrenadeCosmeticTntFallbackKg,
        bool atGrenadeJetVisualReplicationEnabled)
    {
        CoopClientFxSuppression.SuppressRemoteShooterCosmeticsEnabled = suppressRemoteShooterRoundCosmetics;
        _particleImpactReplicationEnabled = particleImpactEnabled;
        _explosionReplicationEnabled = explosionEnabled;
        _muzzleCosmeticReplayEnabled = muzzleReplayEnabled;
        _cosmeticInterestMaxDistanceMeters = cosmeticInterestMaxDistanceMeters < 0f ? 0f : cosmeticInterestMaxDistanceMeters;
        _explosionCameraMinTntKg = explosionCameraMinTntKg < 0f ? 0f : explosionCameraMinTntKg;
        _explosionCameraMaxDistanceMeters = explosionCameraMaxDistanceMeters <= 0f ? 400f : explosionCameraMaxDistanceMeters;
        _logCosmeticHealth = logCosmeticHealth;
        _fragGrenadeCosmeticTntUseApRadius = fragGrenadeCosmeticTntUseApRadius;
        _fragGrenadeCosmeticTntFallbackKg = fragGrenadeCosmeticTntFallbackKg < 0f ? 0.22f : fragGrenadeCosmeticTntFallbackKg;
        _atGrenadeJetVisualReplicationEnabled = atGrenadeJetVisualReplicationEnabled;
    }

    internal static bool CombatReplicationLogFired => _logCombatReplication;

    internal static bool CombatReplicationLogStruckPerHit => _logCombatReplication && _logCombatStruckPerHit;

    internal static bool CombatReplicationLogImpactFx => _logCombatReplication && _logImpactFx;

    internal static bool CombatReplicationLogDamageState => _logDamageState;

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
        if (!IsHost || _udp == null || buffer == null || length <= 0)
            return false;
        if (_hostPeer == null)
        {
            if (_combatReplicationEnabled && Time.time >= _nextGhcNoPeerLogTime)
            {
                _nextGhcNoPeerLogTime = Time.time + GhcNoPeerLogCooldownSeconds;
                MelonLogger.Warning(
                    "[CoopNet] GHC send skipped: host has no UDP peer yet (no client endpoint). " +
                    "Combat/cosmetic packets are dropped until the client sends Hello/GHP/Heartbeat — " +
                    "states on the client will diverge for events that happen in this window.");
            }

            return false;
        }

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
        _enforceVehicleOwnership = enforceVehicleOwnership;
        _logVehicleOwnershipBlocks = logVehicleOwnershipBlocks;
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
            int controlCap = CoopControlPacket.SyncHeaderLength + CoopControlPacket.MaxSyncEntries * 8;
            if (CoopControlPacket.MissionLaunchInfoMaxTotalLength > controlCap)
                controlCap = CoopControlPacket.MissionLaunchInfoMaxTotalLength;
            _controlSendBuffer = new byte[controlCap];
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
        ClientSimulationGovernor.ResetSession();
        CoopWorldEnvironmentReplication.ClearSession();
        _hostWorldEnvSendAccumulator = 0f;
        ResetCombatSessionState();
        LobbySession.Reset();
        _clientAckSentSessionId = 0;
        _clientAckSentTransitionSeq = 0;
        ClientClearMissionLaunchMergeState();
        CoopMissionLaunchBridge.Reset();
        if (!CoopNetSession.HasDisconnectReason)
            CoopNetSession.NotifyDisconnect("transport stopped");
    }

    /// <summary>Menu entrypoint: start host transport if not already active.</summary>
    public static bool TryStartHostFromMenu(int bindPort)
    {
        if (CoopSessionState.IsPlaying)
            return false;
        if (IsHost && _started)
            return true;
        if (IsClient && _started)
            Shutdown();

        ConfigureAndStart(
            enabled: true,
            roleName: "Host",
            bindPort: bindPort <= 0 ? 27015 : bindPort,
            remoteHost: "127.0.0.1",
            remotePort: 27015,
            logReceive: _logReceive,
            logMissionMismatch: _logMissionMismatch,
            enforceVehicleOwnership: _enforceVehicleOwnership,
            logVehicleOwnershipBlocks: _logVehicleOwnershipBlocks);
        if (IsHost)
        {
            ulong sessionId = LobbySession.EnsureHostSession();
            LobbySession.BuildSnapshot(out _, out uint rev, out uint seq, out uint readyMask, out byte kind, out uint missionToken, out uint loadingFlags);
            CoopNetSession.NotifyLobbySnapshotApplied(
                sessionId,
                rev,
                seq,
                readyMask,
                kind,
                missionToken,
                loadingFlags,
                isHost: true);
        }

        return IsHost;
    }

    /// <summary>Menu entrypoint: start client transport if not already active.</summary>
    public static bool TryStartClientFromMenu(string remoteHost, int remotePort)
    {
        if (CoopSessionState.IsPlaying)
            return false;
        if (IsClient && _started && _serverEndPoint != null
            && string.Equals(_serverEndPoint.Address.ToString(), remoteHost, StringComparison.OrdinalIgnoreCase)
            && _serverEndPoint.Port == remotePort)
            return true;
        if (IsHost && _started)
            Shutdown();

        ConfigureAndStart(
            enabled: true,
            roleName: "Client",
            bindPort: 0,
            remoteHost: string.IsNullOrWhiteSpace(remoteHost) ? "127.0.0.1" : remoteHost,
            remotePort: remotePort <= 0 ? 27015 : remotePort,
            logReceive: _logReceive,
            logMissionMismatch: _logMissionMismatch,
            enforceVehicleOwnership: _enforceVehicleOwnership,
            logVehicleOwnershipBlocks: _logVehicleOwnershipBlocks);

        return IsClient;
    }

    public static bool TrySendClientReadyFromMenu(bool ready)
    {
        if (!IsClient || _udp == null || _controlSendBuffer == null || _serverEndPoint == null)
            return false;
        ulong sessionId = CoopNetSession.CurrentSessionId;
        uint revision = CoopNetSession.CurrentRevision;
        if (sessionId == 0)
            return false;
        try
        {
            CoopControlPacket.WriteLobbySetReadyRequest(_controlSendBuffer, sessionId, revision, ready);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, _serverEndPoint);
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbyReady failed: {ex.Message}");
            return false;
        }
    }

    public static bool TryHostStartRequestFromMenu()
    {
        if (!IsHost || _udp == null || _controlSendBuffer == null)
            return false;
        if (_hostPeer == null)
            return false;
        string sceneKey = CoopLobbyMissionSelection.LastSceneMapKey;
        if (string.IsNullOrEmpty(sceneKey))
        {
            MelonLogger.Warning("[CoopNet][M4] start blocked: no mission selected (choose a mission in the briefing panel first)");
            return false;
        }

        if (!LobbySession.HostStartTransition(out uint transitionSeq))
            return false;
        uint missionToken = CoopMissionHash.Token(sceneKey);
        LobbySession.HostRequestLoad(missionToken);

        LobbySession.BuildSnapshot(
            out ulong sessionId,
            out uint revision,
            out _,
            out uint readyMask,
            out byte transitionKind,
            out uint selectedMissionToken,
            out uint loadingFlags);
        SendLobbyTransitionToPeer(_hostPeer, sessionId, revision, transitionSeq, transitionKind);
        SendLobbyMissionLaunchInfoToPeer(_hostPeer, sessionId, revision, transitionSeq, sceneKey);
        SendLobbyLoadMissionToPeer(_hostPeer, sessionId, revision, transitionSeq, selectedMissionToken);
        SendLobbySnapshotToPeer(_hostPeer, sessionId, revision, transitionSeq, readyMask, transitionKind);
        CoopNetSession.NotifyLobbyTransitionApplied(sessionId, transitionSeq, transitionKind, isHost: true);
        CoopNetSession.NotifyLoadMissionReceived(sessionId, revision, transitionSeq, selectedMissionToken, isHost: true);
        CoopNetSession.NotifyLobbySnapshotApplied(
            sessionId,
            revision,
            transitionSeq,
            readyMask,
            transitionKind,
            selectedMissionToken,
            loadingFlags,
            isHost: true);
        MelonLogger.Msg($"[CoopNet][M3] transition-applied sid={sessionId} seq={transitionSeq} kind={transitionKind}");
        MelonLogger.Msg($"[CoopNet][M4] load-mission sid={sessionId} seq={transitionSeq} token={selectedMissionToken}");
        CoopMissionLaunchBridge.TryScheduleCoopMissionLoad(sessionId, transitionSeq, sceneKey, true);
        return true;
    }

    public static void HostNotifyLocalMissionLoaded()
    {
        if (!IsHost)
            return;
        if (!LobbySession.HostMarkLoaded())
            return;
        LobbySession.BuildSnapshot(
            out ulong sessionId,
            out uint revision,
            out uint transitionSeq,
            out uint readyMask,
            out byte transitionKind,
            out uint missionToken,
            out uint loadingFlags);
        CoopNetSession.NotifyLobbySnapshotApplied(
            sessionId,
            revision,
            transitionSeq,
            readyMask,
            transitionKind,
            missionToken,
            loadingFlags,
            isHost: true);
        TryHostApproveStartIfReady();
    }

    public static bool TrySendClientLoadedAckForCurrentTransition()
    {
        if (!IsClient || _udp == null || _controlSendBuffer == null || _serverEndPoint == null)
            return false;
        ulong sessionId = CoopNetSession.CurrentSessionId;
        uint revision = CoopNetSession.CurrentRevision;
        uint transitionSeq = CoopNetSession.CurrentTransitionSeq;
        if (sessionId == 0 || transitionSeq == 0)
            return false;
        if (_clientAckSentSessionId == sessionId && _clientAckSentTransitionSeq == transitionSeq)
            return true;
        try
        {
            CoopControlPacket.WriteLobbyClientLoadedAck(_controlSendBuffer, sessionId, revision, transitionSeq);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, _serverEndPoint);
            _clientAckSentSessionId = sessionId;
            _clientAckSentTransitionSeq = transitionSeq;
            MelonLogger.Msg($"[CoopNet][M4] client-loaded-ack sid={sessionId} seq={transitionSeq}");
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendClientLoadedAck failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Menu entrypoint: stop menu session safely.</summary>
    public static void StopMenuSession(string reason)
    {
        if (CoopSessionState.IsPlaying)
            return;
        if (!_started)
            return;
        CoopNetSession.NotifyDisconnect(reason);
        MelonLogger.Msg($"[CoopNet] Menu session stopped: {reason}");
        LobbySession.Reset();
        Shutdown();
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

        int processed = 0;
        _worldDatagramsThisTick = 0;
        while (true)
        {
            if (processed >= MaxInboundDatagramsPerTick)
                break;
            (IPEndPoint Remote, byte[] Data) item;
            lock (InboxLock)
            {
                if (Inbox.Count == 0)
                    break;
                item = Inbox.Dequeue();
            }
            processed++;

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
                    _worldDatagramsThisTick++;
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
        if (_role == CoopNetRole.Client && _worldDatagramsThisTick > 0)
        {
            _worldProcessTicks++;
            _worldDatagramsTotal += (uint)_worldDatagramsThisTick;
            if (_worldDatagramsThisTick > _worldDatagramsMaxTick)
                _worldDatagramsMaxTick = _worldDatagramsThisTick;
            if (_logWorldReplicationReceive && Time.time >= _nextWorldBurstLogTime)
            {
                float avgPerTick = _worldProcessTicks > 0 ? (float)_worldDatagramsTotal / _worldProcessTicks : 0f;
                MelonLogger.Msg(
                    $"[CoopNet] GHW burst stats tick={_worldDatagramsThisTick} avg={avgPerTick:0.00} max={_worldDatagramsMaxTick}");
                _nextWorldBurstLogTime = Time.time + 2f;
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
        CoopWorldEnvironmentReplication.ClearSession();
        _hostWorldEnvSendAccumulator = 0f;
        ResetCombatSessionState();
        LobbySession.Reset();
        _clientAckSentSessionId = 0;
        _clientAckSentTransitionSeq = 0;
        ClientClearMissionLaunchMergeState();
        CoopMissionLaunchBridge.Reset();
        CoopLobbyMissionSelection.Clear();
    }

    private static void ClientClearMissionLaunchMergeState()
    {
        _clientHasMergeLoad = false;
        _clientHasMergeKey = false;
        _clientMergeLoadSid = 0;
        _clientMergeLoadSeq = 0;
        _clientMergeLoadToken = 0;
        _clientMergeKeySid = 0;
        _clientMergeKeySeq = 0;
        _clientMergeKey = null;
    }

    private static void ClientTryFlushMissionLaunchIfReady()
    {
        if (!_clientHasMergeLoad || !_clientHasMergeKey)
            return;
        if (_clientMergeLoadSid != _clientMergeKeySid || _clientMergeLoadSeq != _clientMergeKeySeq)
            return;
        if (_clientMergeLoadSid == 0 || _clientMergeLoadSeq == 0 || string.IsNullOrEmpty(_clientMergeKey))
            return;
        uint tok = CoopMissionHash.Token(_clientMergeKey);
        if (tok != _clientMergeLoadToken)
        {
            MelonLogger.Warning(
                $"[CoopNet][M4] mission token mismatch: keyHash={tok} loadPacket={_clientMergeLoadToken} (using host key)");
        }

        ulong sid = _clientMergeLoadSid;
        uint seq = _clientMergeLoadSeq;
        string key = _clientMergeKey!;
        ClientClearMissionLaunchMergeState();
        CoopMissionLaunchBridge.TryScheduleCoopMissionLoad(sid, seq, key, false);
    }

    /// <summary>Host: periodic GHW + low-rate COO WorldEnv refresh to keep weather/sky authoritative on clients.</summary>
    public static void HostTickWorldReplication(float deltaTime)
    {
        if (!_started || _udp == null || _role != CoopNetRole.Host)
            return;
        if (_hostPeer == null)
            return;

        if (_worldReplicationEnabled)
        {
            float interval = 1f / _worldReplicationHz;
            HostWorldReplication.TickSend(
                deltaTime,
                interval,
                _logWorldReplicationSend,
                _udp,
                _hostPeer);
        }

        if (!_worldEnvironmentReplicationEnabled || _worldEnvironmentReplicationHz <= 0f)
            return;

        _hostWorldEnvSendAccumulator += Mathf.Max(0f, deltaTime);
        float worldEnvInterval = 1f / _worldEnvironmentReplicationHz;
        if (_hostWorldEnvSendAccumulator < worldEnvInterval)
            return;

        _hostWorldEnvSendAccumulator = 0f;
        TrySendWorldEnvironmentToPeer(_hostPeer);
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
            TrySendWorldEnvironmentToPeer(clientRemote);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendWelcome failed: {ex.Message}");
        }
    }

    /// <summary>Host: push current <see cref="GHPC.World.WorldEnvironmentManager" /> snapshot (COO, not GHC).</summary>
    internal static void TrySendWorldEnvironmentToPeer(IPEndPoint peerRemote)
    {
        if (!IsHost || !_worldEnvironmentReplicationEnabled || _udp == null || _controlSendBuffer == null || peerRemote == null)
            return;
        try
        {
            if (!CoopWorldEnvironmentReplication.TryCaptureHost(
                    out float tc,
                    out float ad,
                    out float af,
                    out float ac,
                    out bool night,
                    out bool wv,
                    out bool wDyn,
                    out float wr,
                    out float wc,
                    out float ww,
                    out float wcb,
                    out byte wCloudCond,
                    out float wCloudSpeed,
                    out float wCloudDirX,
                    out float wCloudDirY))
                return;
            int len = CoopControlPacket.WriteWorldEnvV4(
                _controlSendBuffer,
                tc,
                ad,
                af,
                ac,
                night,
                wv,
                wDyn,
                wr,
                wc,
                ww,
                wcb,
                wCloudCond,
                wCloudSpeed,
                wCloudDirX,
                wCloudDirY);
            _udp.Send(_controlSendBuffer, len, peerRemote);
            if (_logWorldEnvironmentSync)
            {
                MelonLogger.Msg(
                    wv
                        ? $"[CoopNet] COO send WorldEnv v4 to={peerRemote} T={tc:F1}°C night={night} rain={wr:F2} cloud={wc:F2} wind={ww:F2} cloudIdx={wCloudCond} dyn={wDyn}"
                        : $"[CoopNet] COO send WorldEnv v4 to={peerRemote} T={tc:F1}°C night={night} (no Weather)");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] WorldEnv send failed: {ex.Message}");
        }
    }

    /// <summary>Host: resend world env to connected peer (e.g. mission entered <c>Playing</c>).</summary>
    public static void HostBroadcastWorldEnvironmentToPeer()
    {
        if (_hostPeer == null)
            return;
        TrySendWorldEnvironmentToPeer(_hostPeer);
    }

    private static void ProcessControlPacket(IPEndPoint remote, byte[] data, int length)
    {
        if (length < 5)
            return;
        byte op = data[4];
        if (op == CoopControlPacket.OpLobbySetReadyRequest && _role == CoopNetRole.Host)
        {
            if (!CoopControlPacket.TryReadLobbySetReadyRequest(data, length, out ulong sessionId, out _, out bool ready))
                return;
            if (LobbySession.SessionId != 0 && sessionId != LobbySession.SessionId)
            {
                LogLobbyStale("set-ready", sessionId, LobbySession.SessionId);
                return;
            }

            EnsureHostPeerForOwnership(remote);
            if (!LobbySession.HostApplyClientReady(ready))
                return;
            LobbySession.BuildSnapshot(
                out ulong sid,
                out uint rev,
                out uint seq,
                out uint readyMask,
                out byte kind,
                out uint missionToken,
                out uint loadingFlags);
            SendLobbySnapshotToPeer(remote, sid, rev, seq, readyMask, kind);
            CoopNetSession.NotifyLobbySnapshotApplied(
                sid,
                rev,
                seq,
                readyMask,
                kind,
                missionToken,
                loadingFlags,
                isHost: true);
            MelonLogger.Msg($"[CoopNet][M3] snapshot-applied sid={sid} rev={rev} readyMask={readyMask}");
            return;
        }

        if (op == CoopControlPacket.OpLobbyStartRequest && _role == CoopNetRole.Host)
        {
            if (!CoopControlPacket.TryReadLobbyStartRequest(data, length, out ulong sessionId, out _))
                return;
            if (LobbySession.SessionId != 0 && sessionId != LobbySession.SessionId)
            {
                LogLobbyStale("start-request", sessionId, LobbySession.SessionId);
                return;
            }

            // In M3 host remains the only authority; remote start requests are ignored.
            return;
        }

        if (op == CoopControlPacket.OpLobbySnapshot && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadLobbySnapshot(
                    data,
                    length,
                    out ulong sid,
                    out uint rev,
                    out uint seq,
                    out uint readyMask,
                    out byte kind))
                return;
            if (!LobbySession.ClientApplySnapshot(sid, rev, seq, readyMask, kind, CoopNetSession.CurrentMissionToken, 0u))
            {
                LogLobbyStale("snapshot", sid, LobbySession.SessionId);
                return;
            }

            CoopNetSession.NotifyLobbySnapshotApplied(
                sid,
                rev,
                seq,
                readyMask,
                kind,
                CoopNetSession.CurrentMissionToken,
                0u,
                isHost: false);
            MelonLogger.Msg($"[CoopNet][M3] snapshot-applied sid={sid} rev={rev} readyMask={readyMask}");
            return;
        }

        if (op == CoopControlPacket.OpLobbyTransition && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadLobbyTransition(data, length, out ulong sid, out _, out uint seq, out byte kind))
                return;
            if (!LobbySession.ClientApplyTransition(sid, seq, kind))
            {
                LogLobbyStale("transition", sid, LobbySession.SessionId);
                return;
            }

            CoopNetSession.NotifyLobbyTransitionApplied(sid, seq, kind, isHost: false);
            MelonLogger.Msg($"[CoopNet][M3] transition-applied sid={sid} seq={seq} kind={kind}");
            return;
        }

        if (op == CoopControlPacket.OpLobbyMissionLaunchInfo && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadLobbyMissionLaunchInfo(
                    data,
                    length,
                    out ulong sid,
                    out uint rev,
                    out uint seq,
                    out string launchKey))
                return;
            if (LobbySession.SessionId != 0 && sid != LobbySession.SessionId)
            {
                LogLobbyStale("mission-launch-info", sid, LobbySession.SessionId);
                return;
            }

            _clientMergeKeySid = sid;
            _clientMergeKeySeq = seq;
            _clientMergeKey = launchKey;
            _clientHasMergeKey = true;
            MelonLogger.Msg(
                $"[CoopNet][M4] mission-launch-info sid={sid} rev={rev} seq={seq} keyLen={Encoding.UTF8.GetByteCount(launchKey)}");
            ClientTryFlushMissionLaunchIfReady();
            return;
        }

        if (op == CoopControlPacket.OpLobbyLoadMission && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadLobbyLoadMission(data, length, out ulong sid, out uint rev, out uint seq, out uint missionToken))
                return;
            if (!LobbySession.ClientApplyLoadRequested(sid, rev, seq, missionToken))
            {
                LogLobbyStale("load-mission", sid, LobbySession.SessionId);
                return;
            }

            _clientAckSentSessionId = 0;
            _clientAckSentTransitionSeq = 0;
            CoopNetSession.NotifyLoadMissionReceived(sid, rev, seq, missionToken, isHost: false);
            MelonLogger.Msg($"[CoopNet][M4] load-mission sid={sid} seq={seq} token={missionToken}");
            _clientMergeLoadSid = sid;
            _clientMergeLoadSeq = seq;
            _clientMergeLoadToken = missionToken;
            _clientHasMergeLoad = true;
            ClientTryFlushMissionLaunchIfReady();
            return;
        }

        if (op == CoopControlPacket.OpLobbyClientLoadedAck && _role == CoopNetRole.Host)
        {
            if (!CoopControlPacket.TryReadLobbyClientLoadedAck(data, length, out ulong sid, out uint rev, out uint seq))
                return;
            if (!LobbySession.HostApplyClientLoadedAck(sid, seq))
            {
                LogLobbyStale("client-loaded-ack", sid, LobbySession.SessionId);
                return;
            }

            CoopNetSession.NotifyClientLoadedAckApplied(sid, rev, seq);
            TryHostApproveStartIfReady();
            return;
        }

        if (op == CoopControlPacket.OpLobbyStartApproved && _role == CoopNetRole.Client)
        {
            if (!CoopControlPacket.TryReadLobbyStartApproved(data, length, out ulong sid, out uint rev, out uint seq, out uint missionToken))
                return;
            if (!LobbySession.ClientApplyStartApproved(sid, rev, seq))
            {
                LogLobbyStale("start-approved", sid, LobbySession.SessionId);
                return;
            }

            CoopNetSession.NotifyStartApprovedApplied(sid, rev, seq, missionToken);
            MelonLogger.Msg($"[CoopNet][M4] start-approved sid={sid} seq={seq}");
            return;
        }

        if (op == CoopControlPacket.OpHello && _role == CoopNetRole.Host)
        {
            if (length < CoopControlPacket.FixedControlPayloadLength
                || !CoopControlPacket.TryReadHello(data, length, out uint nonce, out _))
                return;
            EnsureHostPeerForOwnership(remote);
            CoopNetSession.HostRegisterPeer(remote, out byte assigned);
            SendWelcome(remote, assigned, nonce);
            ulong sid = LobbySession.EnsureHostSession();
            LobbySession.BuildSnapshot(
                out _,
                out uint rev,
                out uint seq,
                out uint readyMask,
                out byte kind,
                out uint missionToken,
                out uint loadingFlags);
            SendLobbySnapshotToPeer(remote, sid, rev, seq, readyMask, kind);
            CoopNetSession.NotifyLobbySnapshotApplied(
                sid,
                rev,
                seq,
                readyMask,
                kind,
                missionToken,
                loadingFlags,
                isHost: true);
            MelonLogger.Msg($"[CoopNet][M3] session-created sid={sid}");
            if (CoopSessionState.IsPlaying)
                SendOwnerSync();
            return;
        }

        if (op == CoopControlPacket.OpWorldEnv && _role == CoopNetRole.Client)
        {
            if (_worldEnvironmentReplicationEnabled
                && CoopControlPacket.TryParseWorldEnv(
                    data,
                    length,
                    out float tc,
                    out float ad,
                    out float af,
                    out float ac,
                    out bool night,
                    out bool wv,
                    out bool wDyn,
                    out float wr,
                    out float wc,
                    out float ww,
                    out float wcb,
                    out bool cloudLayerOk,
                    out byte wCloudCond,
                    out float wCloudSpeed,
                    out bool cloudDirOk,
                    out float wCloudDirX,
                    out float wCloudDirY))
            {
                CoopWorldEnvironmentReplication.ApplyFromHost(
                    tc,
                    ad,
                    af,
                    ac,
                    night,
                    wv,
                    wDyn,
                    wr,
                    wc,
                    ww,
                    wcb,
                    cloudLayerOk,
                    wCloudCond,
                    wCloudSpeed,
                    cloudDirOk,
                    wCloudDirX,
                    wCloudDirY,
                    _logWorldEnvironmentSync);
            }

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

    private static void SendLobbySnapshotToPeer(IPEndPoint peer, ulong sessionId, uint revision, uint transitionSeq, uint readyMask, byte transitionKind)
    {
        if (_udp == null || _controlSendBuffer == null || peer == null)
            return;
        try
        {
            CoopControlPacket.WriteLobbySnapshot(_controlSendBuffer, sessionId, revision, transitionSeq, readyMask, transitionKind);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, peer);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbySnapshot failed: {ex.Message}");
        }
    }

    private static void SendLobbyTransitionToPeer(IPEndPoint peer, ulong sessionId, uint revision, uint transitionSeq, byte transitionKind)
    {
        if (_udp == null || _controlSendBuffer == null || peer == null)
            return;
        try
        {
            CoopControlPacket.WriteLobbyTransition(_controlSendBuffer, sessionId, revision, transitionSeq, transitionKind);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, peer);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbyTransition failed: {ex.Message}");
        }
    }

    private static void SendLobbyLoadMissionToPeer(IPEndPoint peer, ulong sessionId, uint revision, uint transitionSeq, uint missionToken)
    {
        if (_udp == null || _controlSendBuffer == null || peer == null)
            return;
        try
        {
            CoopControlPacket.WriteLobbyLoadMission(_controlSendBuffer, sessionId, revision, transitionSeq, missionToken);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, peer);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbyLoadMission failed: {ex.Message}");
        }
    }

    private static void SendLobbyMissionLaunchInfoToPeer(
        IPEndPoint peer,
        ulong sessionId,
        uint revision,
        uint transitionSeq,
        string sceneMapKey)
    {
        if (_udp == null || _controlSendBuffer == null || peer == null)
            return;
        try
        {
            int len = CoopControlPacket.WriteLobbyMissionLaunchInfo(
                _controlSendBuffer,
                sessionId,
                revision,
                transitionSeq,
                sceneMapKey);
            if (len <= 0)
            {
                MelonLogger.Warning("[CoopNet][M4] SendLobbyMissionLaunchInfo: key too long or buffer underrun");
                return;
            }

            _udp.Send(_controlSendBuffer, len, peer);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbyMissionLaunchInfo failed: {ex.Message}");
        }
    }

    private static void SendLobbyStartApprovedToPeer(IPEndPoint peer, ulong sessionId, uint revision, uint transitionSeq, uint missionToken)
    {
        if (_udp == null || _controlSendBuffer == null || peer == null)
            return;
        try
        {
            CoopControlPacket.WriteLobbyStartApproved(_controlSendBuffer, sessionId, revision, transitionSeq, missionToken);
            _udp.Send(_controlSendBuffer, CoopControlPacket.LobbyControlPayloadLength, peer);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet] SendLobbyStartApproved failed: {ex.Message}");
        }
    }

    private static void TryHostApproveStartIfReady()
    {
        if (!IsHost || _hostPeer == null)
            return;
        if (!LobbySession.HostCanApproveStart())
            return;
        if (!LobbySession.HostApproveStart(out uint seq))
            return;

        LobbySession.BuildSnapshot(
            out ulong sessionId,
            out uint revision,
            out _,
            out uint readyMask,
            out byte transitionKind,
            out uint missionToken,
            out uint loadingFlags);
        SendLobbyTransitionToPeer(_hostPeer, sessionId, revision, seq, transitionKind);
        SendLobbyStartApprovedToPeer(_hostPeer, sessionId, revision, seq, missionToken);
        SendLobbySnapshotToPeer(_hostPeer, sessionId, revision, seq, readyMask, transitionKind);
        CoopNetSession.NotifyStartApprovedApplied(sessionId, revision, seq, missionToken);
        CoopNetSession.NotifyLobbySnapshotApplied(
            sessionId,
            revision,
            seq,
            readyMask,
            transitionKind,
            missionToken,
            loadingFlags,
            isHost: true);
        MelonLogger.Msg($"[CoopNet][M4] start-approved sid={sessionId} seq={seq}");
    }

    private static void LogLobbyStale(string type, ulong incomingSessionId, ulong localSessionId)
    {
        if (Time.time < _nextLobbyStaleLogTime)
            return;
        _nextLobbyStaleLogTime = Time.time + LobbyStaleLogCooldownSeconds;
        MelonLogger.Msg($"[CoopNet][M3] stale-dropped type={type} incomingSid={incomingSessionId} localSid={localSessionId}");
    }

    private static void ResetCombatSessionState()
    {
        _hostCombatSeq = 0;
        _nextWorldBurstLogTime = float.NegativeInfinity;
        _worldDatagramsThisTick = 0;
        _worldDatagramsMaxTick = 0;
        _worldDatagramsTotal = 0;
        _worldProcessTicks = 0;
        CoopCosmeticHealthCounters.ResetSession();
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
            _hitResolvedApplyMaxPerFrame,
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
