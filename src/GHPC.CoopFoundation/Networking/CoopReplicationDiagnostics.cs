using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GHPC;
using GHPC.CoopFoundation.GameSession;
using GHPC.CoopFoundation.Networking.Protocol;
using GHPC.CoopFoundation.Networking.Transport;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Opt-in verbose tracing for wire-id ↔ unit mapping and snapshot acceptance (Melon preference
///     <c>LogReplicationDiagnostics</c>). Use when peers desync or vehicles “disappear” at mission start.
/// </summary>
internal static class CoopReplicationDiagnostics
{
    public static bool Enabled { get; private set; }

    private static int _lastWireUnitCount = -1;

    private static int _lastWireDumpFrame = -1;

    private static readonly Dictionary<string, float> NextWorldDropLog = new();

    private static readonly Dictionary<uint, float> NextGovernorMissLog = new();

    private static float _nextHostPeerMissLog;

    private static float _nextClientPeerMissLog;

    private static float _nextGhpApplyLog;

    private static float _nextGhwHostLog;

    private static float _nextGhwClientMergeLog;

    public static void Configure(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
            NotifySessionReset();
    }

    public static void NotifySessionReset()
    {
        _lastWireUnitCount = -1;
        _lastWireDumpFrame = -1;
        NextWorldDropLog.Clear();
        NextGovernorMissLog.Clear();
        _nextHostPeerMissLog = 0f;
        _nextClientPeerMissLog = 0f;
        _nextGhpApplyLog = 0f;
        _nextGhwHostLog = 0f;
        _nextGhwClientMergeLog = 0f;
    }

    private static string RoleLabel()
    {
        if (CoopUdpTransport.IsHost)
            return "HOST";
        if (CoopUdpTransport.IsClient)
            return "CLIENT";
        return "OFF";
    }

    public static void LogSyntheticWire(uint assignedWire, uint blockedNatural, Unit u)
    {
        if (!Enabled)
            return;
        MelonLogger.Warning(
            $"[CoopRepDiag][{RoleLabel()}] synthetic wire={assignedWire} blocked_natural={blockedNatural} " +
            $"unique=\"{u.UniqueName}\" go=\"{u.gameObject.name}\" inst={u.GetInstanceID()}");
    }

    public static void LogDuplicateWireMaps(int wireToUnitCount, int unitToWireCount)
    {
        if (!Enabled)
            return;
        MelonLogger.Error(
            $"[CoopRepDiag][{RoleLabel()}] DUPLICATE wire ids: WireToUnit.Count={wireToUnitCount} != UnitToWire.Count={unitToWireCount} — netId→unit map corrupted.");
    }

    public static void MaybeLogFullWireMap(IReadOnlyDictionary<Unit, uint> unitToWire)
    {
        if (!Enabled || !CoopSessionState.IsPlaying || unitToWire.Count == 0)
            return;
        int fc = Time.frameCount;
        int n = unitToWire.Count;
        bool jump = n != _lastWireUnitCount;
        bool periodic = fc - _lastWireDumpFrame >= 300;
        if (!jump && !periodic)
            return;
        _lastWireUnitCount = n;
        _lastWireDumpFrame = fc;

        var sb = new StringBuilder(Math.Min(8192, 512 + n * 112));
        foreach (KeyValuePair<Unit, uint> kv in unitToWire.OrderBy(k => k.Value))
        {
            Unit u = kv.Key;
            if (u == null)
                continue;
            uint nat = CoopUnitNetId.FromUnit(u);
            Vector3 p = u.transform.position;
            sb.AppendLine(
                $"    wire={kv.Value} natural_fnv={nat} {(kv.Value == nat ? "nat" : "SYN")} " +
                $"unique=\"{u.UniqueName}\" go=\"{u.gameObject.name}\" inst={u.GetInstanceID()} " +
                $"pos=({p.x:F1},{p.y:F1},{p.z:F1}) friendly=\"{u.FriendlyName}\"");
        }

        MelonLogger.Msg(
            $"[CoopRepDiag][{RoleLabel()}] ---- wire map dump units={n} missionTok={CoopSessionState.MissionCoherenceToken} sceneKey=\"{CoopSessionState.MissionSceneKey}\" ----\n{sb}");
    }

    public static void LogWorldPacketIgnored(string reason, byte pktPhase, uint pktTok, uint localTok, bool isPlaying)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        string key = reason + pktPhase;
        if (NextWorldDropLog.TryGetValue(key, out float next) && now < next)
            return;
        NextWorldDropLog[key] = now + 2f;
        MelonLogger.Warning(
            $"[CoopRepDiag][CLIENT] GHW dropped: {reason} playing={isPlaying} pktPhase={pktPhase} pktTok={pktTok} localTok={localTok} sceneKey=\"{CoopSessionState.MissionSceneKey}\"");
    }

    public static void LogGhwApplyClient(uint hostSeq, int entityCount, uint skipNetId, int unresolved, List<uint>? sampleUnresolved)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (now < _nextGhwClientMergeLog)
            return;
        _nextGhwClientMergeLog = now + 2f;
        string samp = sampleUnresolved == null || sampleUnresolved.Count == 0
            ? ""
            : string.Join(",", sampleUnresolved.Take(12));
        MelonLogger.Msg(
            $"[CoopRepDiag][CLIENT] GHW merged seq={hostSeq} entities={entityCount} skipNetId={skipNetId} " +
            $"TryResolve_missing={unresolved} sample_missing=[{samp}] remoteNetId={CoopRemoteState.RemoteUnitNetId} remoteHasData={CoopRemoteState.HasData}");
    }

    public static void LogGhwSendHost(uint seq, int sentCount, uint excludeNetId, bool remoteHasData, uint remoteNetId, uint[] sampleNetIds)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (now < _nextGhwHostLog)
            return;
        _nextGhwHostLog = now + 2f;
        string samp = sampleNetIds.Length == 0 ? "" : string.Join(",", sampleNetIds.Take(12));
        MelonLogger.Msg(
            $"[CoopRepDiag][HOST] GHW send seq={seq} entities={sentCount} excludeNetId={excludeNetId} " +
            $"remoteHasData={remoteHasData} remoteReportedNetId={remoteNetId} sample=[{samp}] missionTok={CoopSessionState.MissionCoherenceToken}");
    }

    public static void LogGhpApplied(uint seq, uint netId, uint token, byte phase, Vector3 pos)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (now < _nextGhpApplyLog)
            return;
        _nextGhpApplyLog = now + 2f;
        MelonLogger.Msg(
            $"[CoopRepDiag][{RoleLabel()}] GHP apply seq={seq} unitNetId={netId} token={token} phase={phase} pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})");
    }

    public static void LogGhpRejected(uint remoteToken, byte remotePhase, uint localToken, bool legacy)
    {
        if (!Enabled)
            return;
        MelonLogger.Warning(
            $"[CoopRepDiag][{RoleLabel()}] GHP rejected legacy={legacy} remoteTok={remoteToken} remotePhase={remotePhase} localTok={localToken} playing={CoopSessionState.IsPlaying} sceneKey=\"{CoopSessionState.MissionSceneKey}\"");
    }

    public static void LogGovernorUnitNotFound(uint netId)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (NextGovernorMissLog.TryGetValue(netId, out float t) && now < t)
            return;
        NextGovernorMissLog[netId] = now + 3f;
        MelonLogger.Warning(
            $"[CoopRepDiag][CLIENT] Governor: buffered netId={netId} → TryResolveUnit NULL (host GHW id unknown locally).");
    }

    public static void LogHostPeerUnitNotFound(uint remoteNetId)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (now < _nextHostPeerMissLog)
            return;
        _nextHostPeerMissLog = now + 2f;
        MelonLogger.Warning(
            $"[CoopRepDiag][HOST] HostPeerPuppet: RemoteUnitNetId={remoteNetId} → TryResolveUnit NULL — host cannot puppet client vehicle (wire id mismatch?).");
    }

    public static void LogClientPeerUnitNotFound(uint remoteNetId)
    {
        if (!Enabled)
            return;
        float now = Time.time;
        if (now < _nextClientPeerMissLog)
            return;
        _nextClientPeerMissLog = now + 2f;
        MelonLogger.Warning(
            $"[CoopRepDiag][CLIENT] ClientPeerPuppet: RemoteUnitNetId={remoteNetId} → TryResolveUnit NULL — client cannot puppet host vehicle (wire id mismatch?).");
    }
}
