using System.Collections.Generic;
using System.Net;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>
///     Host-authoritative peer id (Welcome), liveness (Heartbeat), and fallback for pre-0.3 hosts that ignore Hello.
/// </summary>
internal static class CoopNetSession
{
    private const float HelloIntervalSeconds = 2f;

    private const float HeartbeatIntervalSeconds = 1.25f;

    private const float LegacyHostFallbackSeconds = 6f;

    private const float ClientHeartbeatStaleSeconds = 30f;

    private static readonly Dictionary<string, byte> HostAssignedPeerByEndpoint = new();

    private static uint _clientHelloNonce;

    private static uint _clientLastHelloNonceSent;

    private static bool _clientWelcomed;

    private static float _clientSessionStartTime;

    private static float _clientLastHelloSent;

    private static float _clientLastHeartbeatSent;

    private static uint _clientHeartbeatSeq;

    private static bool _clientLegacyFallbackApplied;

    private static float _hostLastClientHeartbeatTime;

    private static bool _hostSawClientHeartbeat;

    private static bool _loggedStaleClientHeartbeat;
    private static string _lastDisconnectReason = string.Empty;
    private static ulong _currentSessionId;
    private static uint _currentRevision;
    private static uint _currentTransitionSeq;
    private static bool _isReadyLocal;
    private static bool _isReadyRemote;
    private static bool _canStartAsHost;
    private static CoopLobbyTransitionKind _transitionKind;

    public static void Reset()
    {
        HostAssignedPeerByEndpoint.Clear();
        _clientHelloNonce = 0;
        _clientLastHelloNonceSent = 0;
        _clientWelcomed = false;
        _clientSessionStartTime = 0f;
        _clientLastHelloSent = float.NegativeInfinity;
        _clientLastHeartbeatSent = float.NegativeInfinity;
        _clientHeartbeatSeq = 0;
        _clientLegacyFallbackApplied = false;
        _hostLastClientHeartbeatTime = 0f;
        _hostSawClientHeartbeat = false;
        _loggedStaleClientHeartbeat = false;
        _lastDisconnectReason = string.Empty;
        _currentSessionId = 0;
        _currentRevision = 0;
        _currentTransitionSeq = 0;
        _isReadyLocal = false;
        _isReadyRemote = false;
        _canStartAsHost = false;
        _transitionKind = CoopLobbyTransitionKind.None;
    }

    /// <summary>Menu / mission drop: client will Hello again next Playing; host clears endpoint→peer map.</summary>
    public static void OnPlayingSessionEnded()
    {
        HostAssignedPeerByEndpoint.Clear();
        _hostLastClientHeartbeatTime = 0f;
        _hostSawClientHeartbeat = false;
        _loggedStaleClientHeartbeat = false;
        _clientWelcomed = false;
        _clientLegacyFallbackApplied = false;
        _clientHeartbeatSeq = 0;
        _clientLastHeartbeatSent = float.NegativeInfinity;
        unchecked
        {
            _clientHelloNonce = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        }

        _clientLastHelloNonceSent = _clientHelloNonce;
        _clientSessionStartTime = Time.time;
        _clientLastHelloSent = Time.time - HelloIntervalSeconds;
    }

    public static void OnNetworkStarted(bool isHost)
    {
        Reset();
        if (isHost)
            return;
        unchecked
        {
            _clientHelloNonce = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        }

        _clientLastHelloNonceSent = _clientHelloNonce;
        _clientSessionStartTime = Time.time;
        _clientLastHelloSent = Time.time - HelloIntervalSeconds;
    }

    public static bool IsConnected
    {
        get
        {
            if (!CoopUdpTransport.IsNetworkActive)
                return false;
            if (CoopUdpTransport.IsHost)
                return HostAssignedPeerByEndpoint.Count > 0;
            return _clientWelcomed || _clientLegacyFallbackApplied;
        }
    }

    public static bool HandshakeOk => CoopUdpTransport.IsHost ? HostAssignedPeerByEndpoint.Count > 0 : _clientWelcomed;

    public static float LastHeartbeatAgeMs
    {
        get
        {
            if (!CoopUdpTransport.IsNetworkActive)
                return float.PositiveInfinity;
            if (!CoopUdpTransport.IsHost)
                return 0f;
            if (!_hostSawClientHeartbeat)
                return float.PositiveInfinity;
            return Mathf.Max(0f, (Time.time - _hostLastClientHeartbeatTime) * 1000f);
        }
    }

    public static string LastDisconnectReason => _lastDisconnectReason;

    public static void NotifyDisconnect(string reason)
    {
        _lastDisconnectReason = reason ?? string.Empty;
    }

    public static bool HasDisconnectReason => !string.IsNullOrWhiteSpace(_lastDisconnectReason);

    public static ulong CurrentSessionId => _currentSessionId;
    public static uint CurrentRevision => _currentRevision;
    public static uint CurrentTransitionSeq => _currentTransitionSeq;
    public static bool IsReadyLocal => _isReadyLocal;
    public static bool IsReadyRemote => _isReadyRemote;
    public static bool CanStartAsHost => _canStartAsHost;
    public static CoopLobbyTransitionKind CurrentTransitionKind => _transitionKind;

    public static void NotifyLobbySnapshotApplied(
        ulong sessionId,
        uint revision,
        uint transitionSeq,
        uint readyMask,
        byte transitionKind,
        bool isHost)
    {
        _currentSessionId = sessionId;
        _currentRevision = revision;
        _currentTransitionSeq = transitionSeq;
        bool hostReady = (readyMask & 1u) != 0;
        bool clientReady = (readyMask & 2u) != 0;
        if (isHost)
        {
            _isReadyLocal = hostReady;
            _isReadyRemote = clientReady;
            _canStartAsHost = hostReady && clientReady;
        }
        else
        {
            _isReadyLocal = clientReady;
            _isReadyRemote = hostReady;
            _canStartAsHost = false;
        }

        _transitionKind = (CoopLobbyTransitionKind)transitionKind;
    }

    public static void NotifyLobbyTransitionApplied(ulong sessionId, uint transitionSeq, byte transitionKind, bool isHost)
    {
        if (sessionId != 0)
            _currentSessionId = sessionId;
        _currentTransitionSeq = transitionSeq;
        _transitionKind = (CoopLobbyTransitionKind)transitionKind;
        if (!isHost)
            _canStartAsHost = false;
    }

    /// <summary>Host: first connecting client gets peer id 2 (room for future expansion).</summary>
    public static void HostRegisterPeer(IPEndPoint remote, out byte assignedPeerId)
    {
        string key = remote.ToString();
        if (!HostAssignedPeerByEndpoint.TryGetValue(key, out assignedPeerId))
        {
            assignedPeerId = 2;
            HostAssignedPeerByEndpoint[key] = assignedPeerId;
        }
    }

    public static byte HostGetExpectedPeerId(IPEndPoint remote)
    {
        return HostAssignedPeerByEndpoint.TryGetValue(remote.ToString(), out byte p) ? p : (byte)0;
    }

    public static void HostNotifyClientHeartbeat()
    {
        _hostSawClientHeartbeat = true;
        _hostLastClientHeartbeatTime = Time.time;
        _loggedStaleClientHeartbeat = false;
    }

    public static void Tick(float time)
    {
        if (!CoopUdpTransport.IsNetworkActive)
            return;
        if (CoopUdpTransport.IsHost)
            TickHost(time);
        else
            TickClient(time);
    }

    private static void TickHost(float time)
    {
        if (!CoopSessionState.IsPlaying)
            return;
        if (!_hostSawClientHeartbeat)
            return;
        if (time - _hostLastClientHeartbeatTime < ClientHeartbeatStaleSeconds)
            return;
        if (_loggedStaleClientHeartbeat)
            return;
        _loggedStaleClientHeartbeat = true;
        MelonLogger.Warning(
            "[CoopNet] No client heartbeat for " + (int)ClientHeartbeatStaleSeconds +
            "s (Playing). Remote ghost may be stale; check connection or pause.");
    }

    private static void TickClient(float time)
    {
        if (!_clientWelcomed && !_clientLegacyFallbackApplied)
        {
            if (time - _clientSessionStartTime >= LegacyHostFallbackSeconds)
            {
                ApplyLegacyHostFallback();
                return;
            }

            if (time - _clientLastHelloSent >= HelloIntervalSeconds)
            {
                _clientLastHelloSent = time;
                _clientLastHelloNonceSent = _clientHelloNonce;
                CoopUdpTransport.SendHello(_clientHelloNonce);
            }

            return;
        }

        if (!CoopSessionState.IsPlaying)
            return;

        if (time - _clientLastHeartbeatSent >= HeartbeatIntervalSeconds)
        {
            _clientLastHeartbeatSent = time;
            unchecked
            {
                _clientHeartbeatSeq++;
            }

            CoopUdpTransport.SendHeartbeat(CoopVehicleOwnership.LocalPeerId, _clientHeartbeatSeq);
        }
    }

    private static void ApplyLegacyHostFallback()
    {
        _clientLegacyFallbackApplied = true;
        _clientWelcomed = true;
        _clientLastHeartbeatSent = Time.time;
        MelonLogger.Msg(
            "[CoopNet] Host did not answer Hello (older mod?). Using legacy peerId=2 for vehicle ownership.");
        CoopVehicleOwnership.ApplyLegacyClientPeerAssignment();
    }

    public static bool ClientTryApplyWelcome(byte assignedPeerId, uint nonceEcho)
    {
        if (_clientWelcomed)
            return false;
        if (assignedPeerId == 0)
            return false;
        if (nonceEcho != _clientLastHelloNonceSent && nonceEcho != _clientHelloNonce)
            return false;
        _clientWelcomed = true;
        _clientLastHeartbeatSent = Time.time;
        CoopVehicleOwnership.ApplyHostAssignedPeerId(assignedPeerId);
        return true;
    }
}
