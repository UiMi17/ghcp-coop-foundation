using System;
using System.Collections.Generic;
using GHPC.CoopFoundation;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>Client: reassemble multi-part GHW, spawn interpolated hull/turret/barrel proxies.</summary>
internal static class ClientWorldProxyService
{
    private const byte WirePlaying = 2;

    private sealed class ProxyEntry
    {
        public GameObject? Root;

        public Transform? Hull;

        public Transform? TurretPivot;

        public Transform? Barrel;

        /// <summary>Latest authoritative sample (from host).</summary>
        public Vector3 NetPosition;

        public Quaternion NetHullRotation;

        public Quaternion NetTurretWorldRotation;

        public Quaternion NetGunWorldRotation;

        /// <summary>Displayed state (interpolated toward net each LateUpdate).</summary>
        public Vector3 SmoothPos;

        public Quaternion SmoothHull;

        public Quaternion SmoothTurretWorld;

        public Quaternion SmoothGunWorld;

        public bool HadData;
    }

    private static readonly Dictionary<uint, ProxyEntry> Proxies = new();

    private static uint _pendingSeq;

    private static byte _pendingPartCount;

    private static WorldEntityWire[][]? _pendingParts;

    private static readonly HashSet<byte> ReceivedPartIndices = new();

    private static bool _havePending;

    public static void ClearAll()
    {
        foreach (KeyValuePair<uint, ProxyEntry> kv in Proxies)
            DestroyProxy(kv.Value);
        Proxies.Clear();
        _havePending = false;
        _pendingParts = null;
        ReceivedPartIndices.Clear();
    }

    public static void OnWorldDecoded(in CoopWorldPacketDecoded packet, bool logReceive)
    {
        if (!CoopSessionState.IsPlaying)
            return;
        if (packet.MissionPhase != WirePlaying)
            return;
        uint localToken = CoopSessionState.MissionCoherenceToken;
        if (localToken == 0 || packet.MissionToken != localToken)
            return;

        if (packet.PartCount == 1)
        {
            ApplyMergedSnapshot(new List<WorldEntityWire>(packet.Entities), logReceive, packet.HostSeq);
            return;
        }

        if (!_havePending || packet.HostSeq != _pendingSeq)
        {
            _pendingSeq = packet.HostSeq;
            _pendingPartCount = packet.PartCount;
            ReceivedPartIndices.Clear();
            _pendingParts = new WorldEntityWire[packet.PartCount][];
            _havePending = true;
        }

        if (packet.PartCount != _pendingPartCount || packet.HostSeq != _pendingSeq)
            return;
        if (packet.PartIndex >= packet.PartCount)
            return;
        if (!ReceivedPartIndices.Add(packet.PartIndex))
            return;

        _pendingParts![packet.PartIndex] = packet.Entities;

        for (int i = 0; i < packet.PartCount; i++)
        {
            if (_pendingParts[i] == null)
                return;
        }

        var merged = new List<WorldEntityWire>(packet.PartCount * CoopWorldPacket.MaxEntitiesPerPart);
        for (int i = 0; i < packet.PartCount; i++)
            merged.AddRange(_pendingParts[i]!);

        _havePending = false;
        _pendingParts = null;
        ReceivedPartIndices.Clear();

        ApplyMergedSnapshot(merged, logReceive, packet.HostSeq);
    }

    private static void ApplyMergedSnapshot(List<WorldEntityWire> entities, bool logReceive, uint hostSeq)
    {
        ClientSimulationGovernor.OnMergedWorldSnapshot(entities);

        uint skipNetId = 0;
        if (CoopRemoteState.HasData && CoopRemoteState.RemoteUnitNetId != 0)
            skipNetId = CoopRemoteState.RemoteUnitNetId;

        var alive = new HashSet<uint>();
        foreach (WorldEntityWire e in entities)
        {
            if (e.NetId == 0 || e.NetId == skipNetId)
                continue;
            alive.Add(e.NetId);
            UpdateOrCreateProxy(e);
        }

        var toRemove = new List<uint>();
        foreach (uint id in Proxies.Keys)
        {
            if (!alive.Contains(id))
                toRemove.Add(id);
        }

        foreach (uint id in toRemove)
        {
            if (Proxies.TryGetValue(id, out ProxyEntry? ent))
            {
                DestroyProxy(ent);
                Proxies.Remove(id);
            }
        }

        if (skipNetId != 0 && Proxies.TryGetValue(skipNetId, out ProxyEntry? peerDup))
        {
            DestroyProxy(peerDup);
            Proxies.Remove(skipNetId);
        }

        if (logReceive)
        {
            MelonLogger.Msg(
                $"[CoopNet] GHW recv seq={hostSeq} entities={entities.Count} proxies={Proxies.Count} (after despawn)");
        }
    }

    private static void UpdateOrCreateProxy(in WorldEntityWire e)
    {
        if (!Proxies.TryGetValue(e.NetId, out ProxyEntry? p))
        {
            p = new ProxyEntry();
            Proxies[e.NetId] = p;
        }

        EnsureVisual(p, e.NetId);
        if (p.Hull == null)
            return;

        p.NetPosition = e.Position;
        p.NetHullRotation = e.HullRotation;
        p.NetTurretWorldRotation = e.TurretWorldRotation;
        p.NetGunWorldRotation = e.GunWorldRotation;

        if (!p.HadData)
        {
            p.SmoothPos = e.Position;
            p.SmoothHull = e.HullRotation;
            p.SmoothTurretWorld = e.TurretWorldRotation;
            p.SmoothGunWorld = e.GunWorldRotation;
            p.HadData = true;
            ApplyHierarchy(p);
        }
    }

    public static void TickLateUpdate(bool showProxies, float smoothing, float yOffsetWorld)
    {
        if (!showProxies || !CoopSessionState.IsPlaying)
        {
            SetAllActive(false);
            return;
        }

        float lerpT = Mathf.Clamp01(Time.deltaTime * smoothing);
        foreach (KeyValuePair<uint, ProxyEntry> kv in Proxies)
        {
            ProxyEntry p = kv.Value;
            if (p.Root == null || !p.HadData || p.Hull == null || p.TurretPivot == null || p.Barrel == null)
                continue;

            p.Root.SetActive(true);
            Vector3 targetPos = p.NetPosition + new Vector3(0f, yOffsetWorld, 0f);
            p.SmoothPos = Vector3.Lerp(p.SmoothPos, targetPos, lerpT);
            p.SmoothHull = Quaternion.Slerp(p.SmoothHull, p.NetHullRotation, lerpT);
            p.SmoothTurretWorld = Quaternion.Slerp(p.SmoothTurretWorld, p.NetTurretWorldRotation, lerpT);
            p.SmoothGunWorld = Quaternion.Slerp(p.SmoothGunWorld, p.NetGunWorldRotation, lerpT);
            ApplyHierarchy(p);
        }
    }

    private static void ApplyHierarchy(ProxyEntry p)
    {
        if (p.Hull == null || p.TurretPivot == null || p.Barrel == null)
            return;
        p.Hull.position = p.SmoothPos;
        p.Hull.rotation = p.SmoothHull;
        p.TurretPivot.localRotation = Quaternion.Inverse(p.SmoothHull) * p.SmoothTurretWorld;
        p.Barrel.localRotation = Quaternion.Inverse(p.SmoothTurretWorld) * p.SmoothGunWorld;
    }

    private static void SetAllActive(bool active)
    {
        foreach (KeyValuePair<uint, ProxyEntry> kv in Proxies)
        {
            if (kv.Value.Root != null && kv.Value.Root.activeSelf != active)
                kv.Value.Root.SetActive(active);
        }
    }

    private static void EnsureVisual(ProxyEntry p, uint netId)
    {
        if (p.Root != null)
            return;

        p.Root = new GameObject($"GHPC_Coop_WorldProxy_{netId}");
        UnityEngine.Object.DontDestroyOnLoad(p.Root);

        GameObject hullGo = new GameObject("HullPivot");
        hullGo.transform.SetParent(p.Root.transform, false);
        p.Hull = hullGo.transform;

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "HullCapsule";
        body.transform.SetParent(p.Hull, false);
        body.transform.localScale = new Vector3(2.2f, 1.3f, 4.8f);
        DestroyCollider(body);

        Renderer? bodyR = body.GetComponent<Renderer>();
        if (bodyR != null)
            bodyR.material.color = new Color(0.35f, 0.85f, 0.4f, 0.92f);

        GameObject turretGo = new GameObject("TurretPivot");
        turretGo.transform.SetParent(p.Hull, false);
        p.TurretPivot = turretGo.transform;

        GameObject barrelGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        barrelGo.name = "Barrel";
        barrelGo.transform.SetParent(p.TurretPivot, false);
        barrelGo.transform.localScale = new Vector3(0.3f, 0.3f, 2f);
        barrelGo.transform.localPosition = new Vector3(0f, 0f, 1f);
        DestroyCollider(barrelGo);

        Renderer? barrelR = barrelGo.GetComponent<Renderer>();
        if (barrelR != null)
            barrelR.material.color = new Color(0.5f, 0.9f, 0.45f, 0.9f);

        p.Barrel = barrelGo.transform;
        p.Root.SetActive(false);
    }

    private static void DestroyCollider(GameObject go)
    {
        Collider? col = go.GetComponent<Collider>();
        if (col != null)
            UnityEngine.Object.Destroy(col);
    }

    private static void DestroyProxy(ProxyEntry p)
    {
        if (p.Root != null)
        {
            UnityEngine.Object.Destroy(p.Root);
            p.Root = null;
            p.Hull = null;
            p.TurretPivot = null;
            p.Barrel = null;
        }

        p.HadData = false;
    }
}
