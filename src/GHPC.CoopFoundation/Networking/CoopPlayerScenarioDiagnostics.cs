using System.Text;
using GHPC;
using GHPC.CoopFoundation.GameSession;
using GHPC.CoopFoundation.Networking.Client;
using GHPC.CoopFoundation.Networking.Host;
using GHPC.CoopFoundation.Networking.Protocol;
using GHPC.CoopFoundation.Networking.Session;
using GHPC.CoopFoundation.Networking.Transport;
using GHPC.Player;
using MelonLoader;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Opt-in throttled log comparing <b>this machine’s</b> local player unit vs the <b>remote peer’s</b> unit:
///     on the host (GHP + HostPeer puppet + RB/VC), on the client (GHP peer hull via ClientPeerUnitPuppet + GHW buffer
///     for other entities + governor). Enable with Melon preference <c>LogPlayerReplicationScenario</c>.
/// </summary>
internal static class CoopPlayerScenarioDiagnostics
{
    private static bool _enabled;

    private static float _intervalSeconds = 2f;

    private static float _nextLogTime;

    private static bool _configuredOnce;

    private static bool _lastConfiguredEnabled;

    private static float _lastConfiguredInterval = -1f;

    /// <summary>
    ///     Melon prefs are applied every <c>OnUpdate</c>; do not reset the log schedule each call or we spam every frame.
    /// </summary>
    public static void Configure(bool enabled, float intervalSeconds)
    {
        float clamped = Mathf.Clamp(intervalSeconds, 0.25f, 30f);
        bool intervalChanged = _configuredOnce && Mathf.Abs(clamped - _lastConfiguredInterval) > 0.001f;
        bool enabledTurnedOn = !_lastConfiguredEnabled && enabled;
        _enabled = enabled;
        _intervalSeconds = clamped;
        _configuredOnce = true;
        _lastConfiguredEnabled = enabled;
        _lastConfiguredInterval = clamped;
        if (enabledTurnedOn || intervalChanged)
            _nextLogTime = 0f;
    }

    /// <summary>Call when coop session / governor buffers reset so the next snapshot logs immediately if enabled.</summary>
    public static void NotifySessionReset()
    {
        _nextLogTime = 0f;
    }

    public static void TickLateUpdate()
    {
        if (!_enabled || !CoopUdpTransport.IsNetworkActive || !CoopSessionState.IsPlaying)
            return;
        float now = Time.time;
        if (now < _nextLogTime)
            return;
        _nextLogTime = now + _intervalSeconds;

        var sb = new StringBuilder(1400);
        sb.Append("[CoopPlayerDiag] === scenario snapshot ===\n");
        sb.AppendFormat(
            CultureInv,
            "role={0} localPeer={1} governorEnabled={2} softSuppressCfg={3} suppressDegraded={4} corrDegraded={5}\n",
            CoopUdpTransport.IsHost ? "HOST" : CoopUdpTransport.IsClient ? "CLIENT" : "OFF",
            CoopVehicleOwnership.LocalPeerId,
            ClientSimulationGovernor.IsGovernorEnabled,
            ClientSimulationGovernor.IsSoftSuppressionConfigured,
            ClientSimulationGovernor.IsSuppressDegraded,
            ClientSimulationGovernor.IsCorrectionDegraded);

        AppendLocalPlayerBlock(sb, "LOCAL_PLAYER");

        if (CoopUdpTransport.IsHost)
            AppendHostPeerBlock(sb);

        if (CoopUdpTransport.IsClient)
        {
            AppendClientGhpPeerBlock(sb);
            AppendClientWorldBufferBlock(sb);
        }

        sb.Append("[CoopPlayerDiag] === end ===");
        MelonLogger.Msg(sb.ToString());
    }

    private static System.Globalization.CultureInfo CultureInv =>
        System.Globalization.CultureInfo.InvariantCulture;

    private static void AppendLocalPlayerBlock(StringBuilder sb, string tag)
    {
        Unit? ctrl = CoopSessionState.ControlledUnit;
        PlayerInput? pi = PlayerInput.Instance;
        Unit? cur = pi != null ? pi.CurrentPlayerUnit : null;
        uint wireCtrl = ctrl != null ? CoopUnitWireRegistry.GetWireId(ctrl) : 0;
        uint wireCur = cur != null ? CoopUnitWireRegistry.GetWireId(cur) : 0;
        sb.AppendFormat(
            CultureInv,
            "{0}: ControlledUnit wire={1} curPlayer wire={2} match={3}\n",
            tag,
            wireCtrl,
            wireCur,
            ReferenceEquals(ctrl, cur));

        if (ctrl == null)
        {
            sb.Append("  unit: <null>\n");
            return;
        }

        AppendUnitMotionLine(sb, "  ", ctrl, wireCtrl, forGovernorLocalCheck: true);
    }

    private static void AppendHostPeerBlock(StringBuilder sb)
    {
        sb.Append("HOST_PEER (GHP → CoopRemoteState + HostPeerUnitPuppet):\n");
        sb.AppendFormat(
            CultureInv,
            "  remoteState hasData={0} seq={1} remoteNetId={2}\n",
            CoopRemoteState.HasData,
            CoopRemoteState.LastSequence,
            CoopRemoteState.RemoteUnitNetId);
        sb.AppendFormat(
            CultureInv,
            "  wireVel=({0:F3},{1:F3},{2:F3}) |v|={3:F3} brake01={4:F2}\n",
            CoopRemoteState.RemoteWorldLinearVelocity.x,
            CoopRemoteState.RemoteWorldLinearVelocity.y,
            CoopRemoteState.RemoteWorldLinearVelocity.z,
            CoopRemoteState.RemoteWorldLinearVelocity.magnitude,
            CoopRemoteState.RemoteBrakePresentation01);

        if (!CoopRemoteState.HasData || CoopRemoteState.RemoteUnitNetId == 0)
        {
            sb.Append("  resolvedUnit: <no remote snapshot>\n");
            return;
        }

        Unit? ru = CoopUnitLookup.TryFindByNetId(CoopRemoteState.RemoteUnitNetId);
        if (ru == null)
        {
            sb.AppendFormat(CultureInv, "  resolvedUnit: NULL for netId={0}\n", CoopRemoteState.RemoteUnitNetId);
            return;
        }

        AppendUnitMotionLine(sb, "  ", ru, CoopRemoteState.RemoteUnitNetId, forGovernorLocalCheck: false);
        bool hp = HostPeerUnitPuppet.TryGetActivePuppet(out Unit? pu, out uint pNet);
        sb.AppendFormat(
            CultureInv,
            "  HostPeerUnitPuppet active={0} activeNetId={1} sameAsRemote={2}\n",
            hp,
            pNet,
            hp && pNet == CoopRemoteState.RemoteUnitNetId);
    }

    private static void AppendClientGhpPeerBlock(StringBuilder sb)
    {
        sb.Append("CLIENT_GHP (lobby peer hull/turret — GHP authoritative; excluded from governor GHW buffer):\n");
        sb.AppendFormat(
            CultureInv,
            "  remoteState hasData={0} seq={1} remoteNetId={2} inGhwBuffer={3}\n",
            CoopRemoteState.HasData,
            CoopRemoteState.LastSequence,
            CoopRemoteState.RemoteUnitNetId,
            CoopRemoteState.RemoteUnitNetId != 0 && IsBuffered(CoopRemoteState.RemoteUnitNetId));
        sb.AppendFormat(
            CultureInv,
            "  wireVel=({0:F3},{1:F3},{2:F3}) |v|={3:F3} brake01={4:F2}\n",
            CoopRemoteState.RemoteWorldLinearVelocity.x,
            CoopRemoteState.RemoteWorldLinearVelocity.y,
            CoopRemoteState.RemoteWorldLinearVelocity.z,
            CoopRemoteState.RemoteWorldLinearVelocity.magnitude,
            CoopRemoteState.RemoteBrakePresentation01);

        if (!CoopRemoteState.HasData || CoopRemoteState.RemoteUnitNetId == 0)
        {
            sb.Append("  resolvedUnit: <no GHP snapshot yet>\n");
            return;
        }

        Unit? ru = CoopUnitLookup.TryFindByNetId(CoopRemoteState.RemoteUnitNetId);
        if (ru == null)
        {
            sb.AppendFormat(CultureInv, "  resolvedUnit: NULL for netId={0}\n", CoopRemoteState.RemoteUnitNetId);
            return;
        }

        AppendUnitMotionLine(sb, "  ", ru, CoopRemoteState.RemoteUnitNetId, forGovernorLocalCheck: false);
        bool cp = ClientPeerUnitPuppet.TryGetActivePuppet(out _, out uint pNet);
        sb.AppendFormat(
            CultureInv,
            "  ClientPeerUnitPuppet active={0} activeNetId={1} sameAsRemote={2}\n",
            cp,
            pNet,
            cp && pNet == CoopRemoteState.RemoteUnitNetId);
    }

    private static void AppendClientWorldBufferBlock(StringBuilder sb)
    {
        sb.Append("CLIENT_GHW (world buffer — governor/puppet; excludes local + often peer GHP netId):\n");
        uint localN = CoopSessionState.ControlledUnit != null
            ? CoopUnitWireRegistry.GetWireId(CoopSessionState.ControlledUnit)
            : 0;
        sb.AppendFormat(CultureInv, "  localControlledNetId={0} inGhwBuffer={1}\n", localN, IsBuffered(localN));

        byte lp = CoopVehicleOwnership.LocalPeerId;
        byte otherPeer = lp == 1 ? (byte)2 : lp == 2 ? (byte)1 : (byte)0;
        sb.AppendFormat(CultureInv, "  otherPeerId(assumed 1↔2)={0}\n", otherPeer);

        int buffered = 0;
        uint firstPeerNet = 0;
        foreach (uint id in ClientSimulationGovernor.EnumerateBufferedNetIds())
        {
            buffered++;
            if (otherPeer != 0 && CoopVehicleOwnership.GetOwnerPeer(id) == otherPeer && firstPeerNet == 0)
                firstPeerNet = id;
        }

        sb.AppendFormat(CultureInv, "  bufferedEntityCount={0}\n", buffered);

        if (firstPeerNet != 0)
            AppendClientBufferedPeerDetail(sb, firstPeerNet, "FIRST_PEER_OWNED_BUFFERED");
        else if (otherPeer != 0)
            sb.AppendFormat(
                CultureInv,
                "  FIRST_PEER_OWNED_BUFFERED: <none> (no netId with ownerPeer={0} in GHW buffer — peer hull may be GHP-only)\n",
                otherPeer);

        AppendFirstAiLikeBuffered(sb, localN, otherPeer, firstPeerNet);
    }

    private static void AppendFirstAiLikeBuffered(StringBuilder sb, uint localN, byte otherPeer, uint skipPeerNet)
    {
        foreach (uint id in ClientSimulationGovernor.EnumerateBufferedNetIds())
        {
            if (id == 0 || id == localN)
                continue;
            if (id == skipPeerNet)
                continue;
            byte o = CoopVehicleOwnership.GetOwnerPeer(id);
            if (o != 0 && o == otherPeer)
                continue;
            sb.Append("CLIENT_COMPARE (first buffered non-peer-non-local, typical AI/neutral):\n");
            AppendClientBufferedPeerDetail(sb, id, "SAMPLE_NON_PEER");
            return;
        }

        sb.Append("CLIENT_COMPARE: <no extra buffered id for sample>\n");
    }

    private static bool IsBuffered(uint netId)
    {
        if (netId == 0)
            return false;
        foreach (uint id in ClientSimulationGovernor.EnumerateBufferedNetIds())
        {
            if (id == netId)
                return true;
        }

        return false;
    }

    private static void AppendClientBufferedPeerDetail(StringBuilder sb, uint netId, string tag)
    {
        sb.AppendFormat(CultureInv, "  {0} netId={1}\n", tag, netId);
        bool skipLocal = ClientSimulationGovernor.IsGovernorSkippedAsLocalOwned(netId);
        bool puppet = ClientSimulationGovernor.IsClientSuppressedPuppet(netId);
        sb.AppendFormat(CultureInv, "    governorSkipLocalOwned={0} clientSuppressedPuppet={1}\n", skipLocal, puppet);

        bool dispV = ClientSimulationGovernor.TryGetDisplayLinearVelocity(netId, out Vector3 dv);
        bool dispW = ClientSimulationGovernor.TryGetDisplayAngularVelocity(netId, out Vector3 dw);
        bool wireV = ClientSimulationGovernor.TryGetLatestWireLinearVelocity(netId, out Vector3 wv);
        bool wireW = ClientSimulationGovernor.TryGetLatestWireAngularVelocity(netId, out Vector3 ww);
        sb.AppendFormat(
            CultureInv,
            "    displayLinVel |v|={0:F4} (ok={1})  wireLinVel |v|={2:F4} (ok={3})\n",
            dispV ? dv.magnitude : 0f,
            dispV,
            wireV ? wv.magnitude : 0f,
            wireV);
        sb.AppendFormat(
            CultureInv,
            "    displayAngVel |ω|={0:F4} (ok={1})  wireAngVel |ω|={2:F4} (ok={3})\n",
            dispW ? dw.magnitude : 0f,
            dispW,
            wireW ? ww.magnitude : 0f,
            wireW);

        bool b = ClientSimulationGovernor.TryGetDisplayBrakePresentation01(netId, out float brake);
        bool m = ClientSimulationGovernor.TryGetDisplayMotorInputVertical(netId, out float motor);
        sb.AppendFormat(
            CultureInv,
            "    displayBrake01={0:F2} (ok={1})  displayMotorV={2:F2} (ok={3})\n",
            b ? brake : 0f,
            b,
            m ? motor : 0f,
            m);

        Unit? u = CoopUnitLookup.TryFindByNetId(netId);
        if (u == null)
        {
            sb.Append("    unit: <CoopUnitLookup null>\n");
            return;
        }

        AppendUnitMotionLine(sb, "    ", u, netId, forGovernorLocalCheck: false);
    }

    private static void AppendUnitMotionLine(StringBuilder sb, string indent, Unit u, uint netId, bool forGovernorLocalCheck)
    {
        IChassis? ch = u.Chassis;
        Rigidbody? rb = ch?.Rigidbody;
        rb ??= u.GetComponentInParent<Rigidbody>();
        rb ??= u.GetComponentInChildren<Rigidbody>();
        VehicleController? vc = u.GetComponentInChildren<VehicleController>(true);
        NwhChassis? nwh = u.GetComponentInChildren<NwhChassis>(true);
        Behaviour? aiBeh = u.InfoBroker?.AI as Behaviour;
        bool haveRb = rb != null;
        sb.AppendFormat(
            CultureInv,
            "{0}unit=\"{1}\" rb={2} kinematic={3} |v|={4:F3} |ω|={5:F3}\n",
            indent,
            u.FriendlyName,
            haveRb,
            haveRb && rb!.isKinematic,
            haveRb ? rb!.velocity.magnitude : 0f,
            haveRb ? rb!.angularVelocity.magnitude : 0f);
        sb.AppendFormat(
            CultureInv,
            "{0}  VC.enabled={1} tracks={2} NwhChassis.enabled={3} AI_beh.enabled={4}\n",
            indent,
            vc != null && vc.enabled,
            vc != null && vc.tracks != null && vc.tracks.trackedVehicle,
            nwh != null && nwh.enabled,
            aiBeh != null && aiBeh.enabled);

        if (forGovernorLocalCheck && netId != 0)
        {
            bool skip = ClientSimulationGovernor.IsGovernorSkippedAsLocalOwned(netId);
            sb.AppendFormat(CultureInv, "{0}  governorWouldSkipAsLocalOwned={1}\n", indent, skip);
        }
    }
}
