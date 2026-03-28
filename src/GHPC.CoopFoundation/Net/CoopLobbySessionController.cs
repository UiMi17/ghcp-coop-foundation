using System;
using GHPC.CoopFoundation;

namespace GHPC.CoopFoundation.Net;

internal enum CoopLobbyTransitionKind : byte
{
    None = 0,
    WaitingForHost = 1,
    Starting = 2,
    LoadRequested = 3,
    WaitingClientLoaded = 4,
    StartApproved = 5
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
    private bool _hostLoaded;
    private bool _clientLoadedAck;
    private uint _selectedMissionToken;
    private CoopLobbyTransitionKind _transitionKind;
    private byte _hostFriendlyUnitRowIndex;
    private byte _clientFriendlyUnitRowIndex;

    /// <summary>Host: last briefing key broadcast to client (Instant Action <c>SceneMissionKey</c> wire form).</summary>
    private string _hostLobbyBriefingSceneKey = "";

    public ulong SessionId => _sessionId;
    public uint Revision => _revision;

    /// <summary>Client: last applied lobby snapshot revision (for ordering briefing/flex vs snapshot).</summary>
    public uint ClientLastAppliedLobbyRevision => _lastAppliedRevision;
    public uint TransitionSeq => _transitionSeq;
    public bool HostReady => _hostReady;
    public bool ClientReady => _clientReady;
    public bool HostLoaded => _hostLoaded;
    public bool ClientLoadedAck => _clientLoadedAck;
    public uint SelectedMissionToken => _selectedMissionToken;
    public CoopLobbyTransitionKind TransitionKind => _transitionKind;

    public byte HostFriendlyUnitRowIndex => _hostFriendlyUnitRowIndex;

    public byte ClientFriendlyUnitRowIndex => _clientFriendlyUnitRowIndex;

    public void Reset()
    {
        _sessionId = 0;
        _revision = 0;
        _transitionSeq = 0;
        _lastAppliedRevision = 0;
        _lastAppliedTransitionSeq = 0;
        _hostReady = false;
        _clientReady = false;
        _hostLoaded = false;
        _clientLoadedAck = false;
        _selectedMissionToken = 0;
        _transitionKind = CoopLobbyTransitionKind.None;
        _hostFriendlyUnitRowIndex = 0;
        _clientFriendlyUnitRowIndex = 1;
        _hostLobbyBriefingSceneKey = "";
        CoopLobbyPlayerSlots.Reset();
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
        _hostLoaded = false;
        _clientLoadedAck = false;
        _selectedMissionToken = 0;
        _transitionKind = CoopLobbyTransitionKind.WaitingForHost;
        _hostFriendlyUnitRowIndex = 0;
        _clientFriendlyUnitRowIndex = 1;
        _hostLobbyBriefingSceneKey = "";
        return _sessionId;
    }

    /// <summary>Host: set canonical briefing key from menu without bumping revision (first session or post-reset).</summary>
    public void HostSeedBriefingKeyIfUnset(string? sceneMapKey)
    {
        if (_sessionId == 0)
            return;
        if (!string.IsNullOrEmpty(_hostLobbyBriefingSceneKey))
            return;
        if (string.IsNullOrEmpty(sceneMapKey))
            return;
        _hostLobbyBriefingSceneKey = sceneMapKey!;
    }

    /// <summary>Host: user changed briefing; bumps revision when key changes.</summary>
    public bool HostTrySetLobbyBriefingKey(string? sceneMapKey)
    {
        EnsureHostSession();
        string next = sceneMapKey ?? "";
        if (next == _hostLobbyBriefingSceneKey)
            return false;
        _hostLobbyBriefingSceneKey = next;
        _revision++;
        return true;
    }

    /// <summary>Host: Customize Apply changed <c>AllFlexOverrides</c>; bumps revision.</summary>
    public void HostBumpRevisionForFlexOverrideSync()
    {
        EnsureHostSession();
        _revision++;
    }

    public string HostLobbyBriefingSceneKey => _hostLobbyBriefingSceneKey;

    /// <summary>Host wins: if client row equals host row, move client to the smallest byte value not equal to host.</summary>
    private void NormalizeClientVersusHost()
    {
        if (_clientFriendlyUnitRowIndex != _hostFriendlyUnitRowIndex)
            return;
        for (int i = 0; i < 256; i++)
        {
            if (i != _hostFriendlyUnitRowIndex)
            {
                _clientFriendlyUnitRowIndex = (byte)i;
                return;
            }
        }
    }

    /// <summary>Host: apply client’s requested friendly Customize row index; bumps revision if net state changed.</summary>
    public bool HostApplyClientPlayerSlot(byte rowIndex)
    {
        EnsureHostSession();
        byte prevHost = _hostFriendlyUnitRowIndex;
        byte prevClient = _clientFriendlyUnitRowIndex;
        _clientFriendlyUnitRowIndex = rowIndex;
        NormalizeClientVersusHost();
        if (_hostFriendlyUnitRowIndex == prevHost && _clientFriendlyUnitRowIndex == prevClient)
            return false;
        _revision++;
        return true;
    }

    /// <summary>Host: local host row index change (Customize UI); bumps revision if net state changed.</summary>
    public bool HostSetHostPlayerSlot(byte rowIndex)
    {
        EnsureHostSession();
        byte prevHost = _hostFriendlyUnitRowIndex;
        byte prevClient = _clientFriendlyUnitRowIndex;
        _hostFriendlyUnitRowIndex = rowIndex;
        NormalizeClientVersusHost();
        if (_hostFriendlyUnitRowIndex == prevHost && _clientFriendlyUnitRowIndex == prevClient)
            return false;
        _revision++;
        return true;
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
        if (_transitionKind == CoopLobbyTransitionKind.Starting
            || _transitionKind == CoopLobbyTransitionKind.LoadRequested
            || _transitionKind == CoopLobbyTransitionKind.WaitingClientLoaded
            || _transitionKind == CoopLobbyTransitionKind.StartApproved)
            return false;
        unchecked
        {
            _transitionSeq++;
            _revision++;
        }

        _transitionKind = CoopLobbyTransitionKind.Starting;
        _hostLoaded = false;
        _clientLoadedAck = false;
        transitionSeq = _transitionSeq;
        return true;
    }

    public bool HostRequestLoad(uint missionToken)
    {
        EnsureHostSession();
        if (_transitionKind != CoopLobbyTransitionKind.Starting
            && _transitionKind != CoopLobbyTransitionKind.LoadRequested
            && _transitionKind != CoopLobbyTransitionKind.WaitingClientLoaded)
            return false;
        if (_transitionKind == CoopLobbyTransitionKind.WaitingClientLoaded
            && _selectedMissionToken == missionToken)
            return false;

        _selectedMissionToken = missionToken;
        _transitionKind = CoopLobbyTransitionKind.LoadRequested;
        _hostLoaded = false;
        _clientLoadedAck = false;
        _revision++;
        return true;
    }

    public bool HostMarkLoaded()
    {
        EnsureHostSession();
        if (_hostLoaded)
            return false;
        _hostLoaded = true;
        if (_transitionKind == CoopLobbyTransitionKind.LoadRequested)
            _transitionKind = CoopLobbyTransitionKind.WaitingClientLoaded;
        _revision++;
        return true;
    }

    public bool HostApplyClientLoadedAck(ulong sessionId, uint transitionSeq)
    {
        EnsureHostSession();
        if (sessionId != _sessionId)
            return false;
        if (transitionSeq != _transitionSeq)
            return false;
        if (_clientLoadedAck)
            return false;
        _clientLoadedAck = true;
        if (_transitionKind == CoopLobbyTransitionKind.LoadRequested)
            _transitionKind = CoopLobbyTransitionKind.WaitingClientLoaded;
        _revision++;
        return true;
    }

    public bool HostCanApproveStart()
    {
        EnsureHostSession();
        if (_transitionKind == CoopLobbyTransitionKind.StartApproved)
            return false;
        return _hostLoaded && _clientLoadedAck;
    }

    public bool HostApproveStart(out uint transitionSeq)
    {
        transitionSeq = 0;
        if (!HostCanApproveStart())
            return false;
        _transitionKind = CoopLobbyTransitionKind.StartApproved;
        _revision++;
        transitionSeq = _transitionSeq;
        return true;
    }

    public void BuildSnapshot(
        out ulong sessionId,
        out uint revision,
        out uint transitionSeq,
        out uint readyMask,
        out byte transitionKind,
        out uint missionToken,
        out uint loadingFlags)
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
        readyMask |= (uint)_hostFriendlyUnitRowIndex << CoopLobbyPlayerSlots.ReadyMaskHostRowShift;
        readyMask |= (uint)_clientFriendlyUnitRowIndex << CoopLobbyPlayerSlots.ReadyMaskClientRowShift;
        transitionKind = (byte)_transitionKind;
        missionToken = _selectedMissionToken;
        loadingFlags = 0u;
        if (_hostLoaded)
            loadingFlags |= 1u;
        if (_clientLoadedAck)
            loadingFlags |= 2u;
    }

    public bool ClientApplySnapshot(
        ulong sessionId,
        uint revision,
        uint transitionSeq,
        uint readyMask,
        byte transitionKind,
        uint missionToken,
        uint loadingFlags)
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
        _hostFriendlyUnitRowIndex = (byte)((readyMask >> CoopLobbyPlayerSlots.ReadyMaskHostRowShift) & 0xFF);
        _clientFriendlyUnitRowIndex = (byte)((readyMask >> CoopLobbyPlayerSlots.ReadyMaskClientRowShift) & 0xFF);
        _transitionKind = (CoopLobbyTransitionKind)transitionKind;
        _selectedMissionToken = missionToken;
        _hostLoaded = (loadingFlags & 1u) != 0;
        _clientLoadedAck = (loadingFlags & 2u) != 0;
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

    public bool ClientApplyLoadRequested(ulong sessionId, uint revision, uint transitionSeq, uint missionToken)
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
        _selectedMissionToken = missionToken;
        _hostLoaded = false;
        _clientLoadedAck = false;
        _transitionKind = CoopLobbyTransitionKind.LoadRequested;
        _lastAppliedRevision = revision;
        if (transitionSeq > _lastAppliedTransitionSeq)
            _lastAppliedTransitionSeq = transitionSeq;
        return true;
    }

    public bool ClientApplyStartApproved(ulong sessionId, uint revision, uint transitionSeq)
    {
        if (sessionId == 0 || _sessionId == 0 || sessionId != _sessionId)
            return false;
        if (revision <= _lastAppliedRevision)
            return false;
        if (transitionSeq != _transitionSeq)
            return false;
        _revision = revision;
        _transitionKind = CoopLobbyTransitionKind.StartApproved;
        _hostLoaded = true;
        _clientLoadedAck = true;
        _lastAppliedRevision = revision;
        return true;
    }
}
