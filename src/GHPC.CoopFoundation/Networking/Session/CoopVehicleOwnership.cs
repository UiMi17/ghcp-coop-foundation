using System;
using System.Collections.Generic;
using GHPC;
using MelonLoader;

namespace GHPC.CoopFoundation.Networking.Session;

/// <summary>Host-authoritative vehicle claims; clients keep a mirror updated via OwnerSync.</summary>
internal static class CoopVehicleOwnership
{
    private const float BlockLogCooldownSeconds = 2.5f;

    private static readonly Dictionary<uint, byte> Authoritative = new();

    private static readonly Dictionary<uint, byte> Mirror = new();

    private static bool _enforce = true;

    private static bool _logBlocks = true;

    private static float _nextBlockLogTime = float.NegativeInfinity;

    public static byte LocalPeerId { get; private set; }

    public static bool IsHost { get; private set; }

    public static void Configure(bool isHost, bool enforceOwnership, bool logBlocks)
    {
        IsHost = isHost;
        LocalPeerId = isHost ? (byte)1 : (byte)0;
        _enforce = enforceOwnership;
        _logBlocks = logBlocks;
        Clear();
    }

    /// <summary>Host-assigned id after <see cref="CoopNetSession" /> Welcome (or legacy fallback).</summary>
    internal static void ApplyHostAssignedPeerId(byte peerId)
    {
        if (peerId == 0)
            return;
        LocalPeerId = peerId;
        ResyncCurrentVehicleAfterPeerReady();
    }

    internal static void ApplyLegacyClientPeerAssignment()
    {
        LocalPeerId = 2;
        ResyncCurrentVehicleAfterPeerReady();
    }

    private static void ResyncCurrentVehicleAfterPeerReady()
    {
        if (!CoopUdpTransport.IsNetworkActive || !CoopSessionState.IsPlaying)
            return;
        Unit? u = CoopSessionState.ControlledUnit;
        if (u == null)
            return;
        uint newN = CoopUnitWireRegistry.GetWireId(u);
        if (IsHost)
        {
            HostApplySwitch(LocalPeerId, 0, newN);
            CoopUdpTransport.SendOwnerSync();
        }
        else
        {
            ClientOptimisticSwitch(0, newN);
            CoopUdpTransport.SendSwitchPacket(LocalPeerId, 0, newN);
        }
    }

    public static void Clear()
    {
        Authoritative.Clear();
        Mirror.Clear();
        if (!IsHost)
            LocalPeerId = 0;
    }

    public static bool ShouldEnforce() => _enforce && CoopUdpTransport.IsNetworkActive && CoopSessionState.IsPlaying;

    public static bool CanLocalEnter(uint unitNetId)
    {
        if (!Mirror.TryGetValue(unitNetId, out byte owner) || owner == 0)
            return true;
        return owner == LocalPeerId;
    }

    /// <summary>Client mirror lookup: 0 means unknown/unowned.</summary>
    public static byte GetOwnerPeer(uint unitNetId)
    {
        if (unitNetId == 0)
            return 0;
        return Mirror.TryGetValue(unitNetId, out byte owner) ? owner : (byte)0;
    }

    /// <summary>True when mirror says this unit is owned by local peer.</summary>
    public static bool IsLocalOwner(uint unitNetId)
    {
        if (unitNetId == 0)
            return false;
        byte owner = GetOwnerPeer(unitNetId);
        return owner != 0 && owner == LocalPeerId;
    }

    public static void LogEnterBlocked(uint netId, Unit unit)
    {
        if (!_logBlocks)
            return;
        float now = UnityEngine.Time.time;
        if (now < _nextBlockLogTime)
            return;
        _nextBlockLogTime = now + BlockLogCooldownSeconds;
        Mirror.TryGetValue(netId, out byte o);
        MelonLogger.Warning(
            "[CoopOwn] Blocked entering unit held by another player. " +
            $"netId={netId} unique=\"{unit.UniqueName}\" ownerPeer={o} (you={LocalPeerId}).");
    }

    /// <summary>After a successful local unit change (both roles).</summary>
    public static void NotifyLocalUnitChanged(Unit? oldUnit, Unit newUnit)
    {
        if (!CoopUdpTransport.IsNetworkActive || !CoopSessionState.IsPlaying)
            return;
        uint oldN = oldUnit != null ? CoopUnitWireRegistry.GetWireId(oldUnit) : 0;
        uint newN = CoopUnitWireRegistry.GetWireId(newUnit);
        if (oldN == newN)
            return;
        if (IsHost)
        {
            HostApplySwitch(LocalPeerId, oldN, newN);
            CoopUdpTransport.SendOwnerSync();
        }
        else
        {
            if (LocalPeerId == 0)
                return;
            ClientOptimisticSwitch(oldN, newN);
            CoopUdpTransport.SendSwitchPacket(LocalPeerId, oldN, newN);
        }
    }

    public static void HostApplySwitch(byte peerId, uint oldNetId, uint newNetId)
    {
        if (oldNetId != 0 && Authoritative.TryGetValue(oldNetId, out byte o) && o == peerId)
            Authoritative.Remove(oldNetId);
        if (newNetId != 0)
        {
            if (Authoritative.TryGetValue(newNetId, out byte cur) && cur != 0 && cur != peerId)
            {
                MelonLogger.Warning(
                    $"[CoopOwn] Host rejected claim: netId={newNetId} already held by peer {cur} (claimant={peerId}).");
            }
            else
            {
                Authoritative[newNetId] = peerId;
            }
        }

        RebuildHostMirror();
    }

    public static void ClientApplyOwnerSync((uint netId, byte peerId)[] entries)
    {
        Mirror.Clear();
        foreach ((uint id, byte p) in entries)
        {
            if (id != 0 && p != 0)
                Mirror[id] = p;
        }
    }

    public static void ClientOptimisticSwitch(uint oldNetId, uint newNetId)
    {
        if (oldNetId != 0 && Mirror.TryGetValue(oldNetId, out byte o) && o == LocalPeerId)
            Mirror.Remove(oldNetId);
        if (newNetId != 0)
            Mirror[newNetId] = LocalPeerId;
    }

    public static (uint netId, byte peerId)[] SnapshotAuthoritativeForSync()
    {
        var list = new List<(uint, byte)>(Authoritative.Count);
        foreach (KeyValuePair<uint, byte> kv in Authoritative)
        {
            if (kv.Key != 0 && kv.Value != 0)
                list.Add((kv.Key, kv.Value));
        }

        return list.ToArray();
    }

    private static void RebuildHostMirror()
    {
        Mirror.Clear();
        foreach (KeyValuePair<uint, byte> kv in Authoritative)
            Mirror[kv.Key] = kv.Value;
    }
}
