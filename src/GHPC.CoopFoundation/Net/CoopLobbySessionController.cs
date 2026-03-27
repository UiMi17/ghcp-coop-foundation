using System;

namespace GHPC.CoopFoundation.Net;

internal enum CoopLobbyTransitionKind : byte
{
    None = 0,
    WaitingForHost = 1,
    Starting = 2
}

/// <summary>
/// Host-authoritative lobby control-plane state for M3.
/// Keeps session id, revision and transition sequence with idempotent apply helpers.
/// </summary>
internal sealed class CoopLobbySessionController
{
    private ulong _sessionId;
    private uint _revision;
    private uint _transitionSeq;
    private uint _lastAppliedRevision;
    private uint _lastAppliedTransitionSeq;
    private bool _hostReady;
    private bool _clientReady;
    private CoopLobbyTransitionKind _transitionKind;

    public ulong SessionId => _sessionId;
    public uint Revision => _revision;
    public uint TransitionSeq => _transitionSeq;
    public bool HostReady => _hostReady;
    public bool ClientReady => _clientReady;
    public CoopLobbyTransitionKind TransitionKind => _transitionKind;

    public void Reset()
    {
        _sessionId = 0;
        _revision = 0;
        _transitionSeq = 0;
        _lastAppliedRevision = 0;
        _lastAppliedTransitionSeq = 0;
        _hostReady = false;
        _clientReady = false;
        _transitionKind = CoopLobbyTransitionKind.None;
    }

    public ulong EnsureHostSession()
    {
        if (_sessionId != 0)
            return _sessionId;
        unchecked
        {
            ulong hi = (ulong)(uint)Environment.TickCount;
            ulong lo = (ulong)(uint)UnityEngine.Random.Range(1, int.MaxValue);
            _sessionId = (hi << 32) | lo;
        }

        _revision = 1;
        _transitionSeq = 0;
        _hostReady = true;
        _clientReady = false;
        _transitionKind = CoopLobbyTransitionKind.WaitingForHost;
        return _sessionId;
    }

    public bool HostApplyClientReady(bool ready)
    {
        EnsureHostSession();
        if (_clientReady == ready)
            return false;
        _clientReady = ready;
        _revision++;
        return true;
    }

    public bool HostCanStart()
    {
        EnsureHostSession();
        return _hostReady && _clientReady;
    }

    public bool HostStartTransition(out uint transitionSeq)
    {
        transitionSeq = 0;
        if (!HostCanStart())
            return false;
        if (_transitionKind == CoopLobbyTransitionKind.Starting)
            return false;
        unchecked
        {
            _transitionSeq++;
            _revision++;
        }

        _transitionKind = CoopLobbyTransitionKind.Starting;
        transitionSeq = _transitionSeq;
        return true;
    }

    public void BuildSnapshot(out ulong sessionId, out uint revision, out uint transitionSeq, out uint readyMask, out byte transitionKind)
    {
        EnsureHostSession();
        sessionId = _sessionId;
        revision = _revision;
        transitionSeq = _transitionSeq;
        readyMask = 0;
        if (_hostReady)
            readyMask |= 1u;
        if (_clientReady)
            readyMask |= 2u;
        transitionKind = (byte)_transitionKind;
    }

    public bool ClientApplySnapshot(ulong sessionId, uint revision, uint transitionSeq, uint readyMask, byte transitionKind)
    {
        if (sessionId == 0)
            return false;
        if (_sessionId != 0 && sessionId != _sessionId)
            return false;
        if (revision <= _lastAppliedRevision)
            return false;

        _sessionId = sessionId;
        _revision = revision;
        _transitionSeq = transitionSeq;
        _hostReady = (readyMask & 1u) != 0;
        _clientReady = (readyMask & 2u) != 0;
        _transitionKind = (CoopLobbyTransitionKind)transitionKind;
        _lastAppliedRevision = revision;
        if (transitionSeq > _lastAppliedTransitionSeq)
            _lastAppliedTransitionSeq = transitionSeq;
        return true;
    }

    public bool ClientApplyTransition(ulong sessionId, uint transitionSeq, byte transitionKind)
    {
        if (sessionId == 0 || _sessionId == 0 || sessionId != _sessionId)
            return false;
        if (transitionSeq <= _lastAppliedTransitionSeq)
            return false;
        _transitionSeq = transitionSeq;
        _transitionKind = (CoopLobbyTransitionKind)transitionKind;
        _lastAppliedTransitionSeq = transitionSeq;
        return true;
    }
}
