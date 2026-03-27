using System;
using GHPC.CoopFoundation.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GHPC.CoopFoundation.UI;

internal sealed class CoopLobbyMenuController
{
    private readonly int _controllerId;
    private readonly GameObject _panel;
    private readonly Button? _hostButton;
    private readonly Button? _joinButton;
    private readonly TMP_Text? _mapLobbyText;
    private readonly CoopLobbyMenuState _state = new();
    private bool _readyRequested;
    private float _nextAllowedClickTime;
    private const float ClickDebounceSeconds = 0.25f;

    public CoopLobbyMenuController(int controllerId, GameObject panel, Button? hostButton, Button? joinButton, TMP_Text? mapLobbyText)
    {
        _controllerId = controllerId;
        _panel = panel;
        _hostButton = hostButton;
        _joinButton = joinButton;
        _mapLobbyText = mapLobbyText;
    }

    public void Bind()
    {
        if (_hostButton != null)
        {
            _hostButton.onClick = new Button.ButtonClickedEvent();
            _hostButton.onClick.AddListener(OnHostClicked);
        }

        if (_joinButton != null)
        {
            _joinButton.onClick = new Button.ButtonClickedEvent();
            _joinButton.onClick.AddListener(OnJoinClicked);
        }

        Render();
    }

    public void Tick()
    {
        if (!_panel.activeInHierarchy)
            return;

        if (_state.Status is CoopLobbyStatus.Hosting or CoopLobbyStatus.Joining or CoopLobbyStatus.Connected)
        {
            if (!CoopUdpTransport.IsNetworkActive)
            {
                _state.MarkDisconnected(CoopNetSession.LastDisconnectReason);
            }
            else
            {
                bool connected = CoopNetSession.IsConnected;
                if (connected)
                {
                    string mode = _state.Role == CoopLobbyRole.Host ? "Host" : "Client";
                    string hs = CoopNetSession.HandshakeOk ? "ok" : "pending";
                    string sid = CoopNetSession.CurrentSessionId.ToString();
                    string transition = CoopNetSession.CurrentTransitionKind.ToString();
                    string flowStatus = transition switch
                    {
                        nameof(CoopLobbyTransitionKind.LoadRequested) => "Loading mission...",
                        nameof(CoopLobbyTransitionKind.WaitingClientLoaded) => "Waiting for client loaded ack...",
                        nameof(CoopLobbyTransitionKind.StartApproved) => "Start approved.",
                        _ => $"state={transition}"
                    };
                    _state.MarkConnected(
                        $"{mode} connected ({hs}) sid={sid} rev={CoopNetSession.CurrentRevision} " +
                        $"seq={CoopNetSession.CurrentTransitionSeq} {flowStatus}\nBack: ESC");
                }
            }
        }

        if (_state.Role == CoopLobbyRole.Client
            && CoopUdpTransport.IsClient
            && CoopNetSession.CurrentSessionId != 0
            && !_readyRequested
            && !CoopNetSession.IsReadyLocal)
        {
            _readyRequested = CoopUdpTransport.TrySendClientReadyFromMenu(true);
        }

        Render();
    }

    public void OnPanelClosed()
    {
        // Keep UDP running for now; full lifecycle policy comes in M3/M4.
    }

    private void OnHostClicked()
    {
        if (!CanClick())
            return;

        if (_state.Role == CoopLobbyRole.Host && CoopUdpTransport.IsHost && CoopNetSession.IsConnected)
        {
            if (!CoopUdpTransport.TryHostStartRequestFromMenu())
            {
                _state.MarkError("Start request rejected (ready preconditions)");
            }
            else
            {
                _state.MarkConnected("Start requested by host.\nWaiting for transition...\nBack: ESC");
            }

            Render();
            return;
        }

        if (CoopUdpTransport.IsNetworkActive && _state.Role != CoopLobbyRole.Host)
            CoopUdpTransport.StopMenuSession("switch role");

        _state.BeginHost(27015);
        _readyRequested = false;
        if (!CoopUdpTransport.TryStartHostFromMenu(27015))
            _state.MarkError("Failed to start host transport");
        Render();
    }

    private void OnJoinClicked()
    {
        if (!CanClick())
            return;

        if (CoopUdpTransport.IsNetworkActive)
        {
            CoopUdpTransport.StopMenuSession("user disconnected");
            _state.MarkDisconnected("user disconnected");
            _readyRequested = false;
            Render();
            return;
        }

        _state.BeginJoin("127.0.0.1", 27015);
        _readyRequested = false;
        if (!CoopUdpTransport.TryStartClientFromMenu("127.0.0.1", 27015))
            _state.MarkError("Failed to start client transport");
        Render();
    }

    private void Render()
    {
        if (_mapLobbyText != null)
            _mapLobbyText.text = _state.StatusText;

        if (_hostButton != null)
        {
            bool isStarting = CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.Starting;
            bool isLoadingGate = CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.LoadRequested
                || CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.WaitingClientLoaded
                || CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.StartApproved;
            bool hostCanStart = _state.Role == CoopLobbyRole.Host
                && _state.Status == CoopLobbyStatus.Connected
                && CoopNetSession.CanStartAsHost
                && !isStarting
                && !isLoadingGate;
            _hostButton.interactable = (_state.Status is CoopLobbyStatus.Idle or CoopLobbyStatus.Error) || hostCanStart;
            SetButtonLabel(
                _hostButton.gameObject,
                _state.Status == CoopLobbyStatus.Connected && _state.Role == CoopLobbyRole.Host
                    ? (isStarting || isLoadingGate ? "STARTING..." : "START SESSION")
                    : "HOST SESSION");
        }

        if (_joinButton != null)
        {
            bool loadingGateActive = CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.LoadRequested
                || CoopNetSession.CurrentTransitionKind == CoopLobbyTransitionKind.WaitingClientLoaded;
            _joinButton.interactable = !loadingGateActive;
            SetButtonLabel(_joinButton.gameObject, CoopUdpTransport.IsNetworkActive ? "DISCONNECT" : "JOIN SESSION");
        }
    }

    private bool CanClick()
    {
        float now = Time.unscaledTime;
        if (now < _nextAllowedClickTime)
            return false;
        _nextAllowedClickTime = now + ClickDebounceSeconds;
        return true;
    }

    private static void SetButtonLabel(GameObject buttonGo, string newLabel)
    {
        TMP_Text? tmp = buttonGo.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
            tmp.text = newLabel;
        Text? legacy = buttonGo.GetComponentInChildren<Text>(true);
        if (legacy != null)
            legacy.text = newLabel;
    }
}
