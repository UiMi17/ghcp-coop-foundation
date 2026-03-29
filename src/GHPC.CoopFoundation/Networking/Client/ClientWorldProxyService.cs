using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Client;

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

        /// <summary>Damped velocity term for <see cref="Vector3.SmoothDamp" /> on hull position.</summary>
        public Vector3 SmoothVel;

        /// <summary>Snapshot timing/velocity estimate for short-horizon extrapolation.</summary>
        public bool HasNetSample;

        public Vector3 LastNetPos;

        public float LastNetTime;

        public Vector3 NetVelEstimate;

        public float AvgSnapshotDt;

        public bool HadData;
    }

    private static readonly Dictionary<uint, ProxyEntry> Proxies = new();

    private static uint _pendingSeq;

    private static byte _pendingPartCount;

    private static WorldEntityWire[][]? _pendingParts;

    private static readonly HashSet<byte> ReceivedPartIndices = new();

    private static bool _havePending;
    private static float _pendingStartedAt;
    private static uint _staleMultipartDrops;
    private static bool _captureProxies = true;
    private const float MultipartMaxAgeSeconds = 0.6f;

    public static void ConfigureCapture(bool enabled)
    {
        _captureProxies = enabled;
        if (!enabled && Proxies.Count > 0)
            ClearAll();
    }

    public static void ClearAll()
    {
        foreach (KeyValuePair<uint, ProxyEntry> kv in Proxies)
            DestroyProxy(kv.Value);
        Proxies.Clear();
        _havePending = false;
        _pendingParts = null;
        _pendingStartedAt = 0f;
        ReceivedPartIndices.Clear();
        _staleMultipartDrops = 0;
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
            _pendingStartedAt = Time.time;
        }
        else if (_havePending && _pendingStartedAt > 0f && (Time.time - _pendingStartedAt) > MultipartMaxAgeSeconds)
        {
            _staleMultipartDrops++;
            _pendingSeq = packet.HostSeq;
            _pendingPartCount = packet.PartCount;
            ReceivedPartIndices.Clear();
            _pendingParts = new WorldEntityWire[packet.PartCount][];
            _pendingStartedAt = Time.time;
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
        _pendingStartedAt = 0f;
        ReceivedPartIndices.Clear();

        ApplyMergedSnapshot(merged, logReceive, packet.HostSeq);
    }

    private static void ApplyMergedSnapshot(List<WorldEntityWire> entities, bool logReceive, uint hostSeq)
    {
        ClientSimulationGovernor.OnMergedWorldSnapshot(entities);
        if (!_captureProxies)
        {
            if (Proxies.Count > 0)
                ClearAll();
            return;
        }

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
                $"[CoopNet] GHW recv seq={hostSeq} entities={entities.Count} proxies={Proxies.Count} staleMultipartDrops={_staleMultipartDrops} (after despawn)");
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

        float now = Time.time;
        if (!p.HasNetSample)
        {
            p.HasNetSample = true;
            p.LastNetPos = e.Position;
            p.LastNetTime = now;
            p.NetVelEstimate = Vector3.zero;
            p.AvgSnapshotDt = 0.2f;
        }
        else
        {
            float dt = now - p.LastNetTime;
            if (dt > 1e-4f && dt < 1.5f)
            {
                Vector3 rawVel = (e.Position - p.LastNetPos) / dt;
                p.NetVelEstimate = Vector3.Lerp(p.NetVelEstimate, rawVel, 0.5f);
                p.AvgSnapshotDt = p.AvgSnapshotDt <= 1e-4f
                    ? dt
                    : Mathf.Lerp(p.AvgSnapshotDt, dt, 0.15f);
            }

            p.LastNetPos = e.Position;
            p.LastNetTime = now;
        }

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

        // Convert "follow Hz-like" setting into damped smooth-time for stable motion at low/bursty update rates.
        float smoothTime = Mathf.Clamp(1f / Mathf.Max(1f, smoothing), 0.06f, 0.25f);
        foreach (KeyValuePair<uint, ProxyEntry> kv in Proxies)
        {
            ProxyEntry p = kv.Value;
            if (p.Root == null || !p.HadData || p.Hull == null || p.TurretPivot == null || p.Barrel == null)
                continue;

            p.Root.SetActive(true);
            float extrapolation = Mathf.Clamp(p.AvgSnapshotDt * 0.5f, 0f, 0.16f);
            Vector3 predicted = p.NetPosition + p.NetVelEstimate * extrapolation;
            Vector3 targetPos = predicted + new Vector3(0f, yOffsetWorld, 0f);
            float posErr = Vector3.Distance(p.SmoothPos, targetPos);
            if (posErr <= 0.03f)
            {
                // Deadband for sub-3cm corrections to prevent visible shimmer in dense formations.
            }
            else if (posErr > 8f)
            {
                // Teleport-sized divergence: converge immediately to avoid long catch-up drag.
                p.SmoothPos = targetPos;
                p.SmoothVel = Vector3.zero;
            }
            else
            {
                p.SmoothPos = Vector3.SmoothDamp(
                    p.SmoothPos,
                    targetPos,
                    ref p.SmoothVel,
                    smoothTime,
                    Mathf.Infinity,
                    Time.deltaTime);
            }

            float rotLerp = Mathf.Clamp01(Time.deltaTime * Mathf.Max(4f, smoothing));
            if (Quaternion.Angle(p.SmoothHull, p.NetHullRotation) > 0.05f)
                p.SmoothHull = Quaternion.Slerp(p.SmoothHull, p.NetHullRotation, rotLerp);
            if (Quaternion.Angle(p.SmoothTurretWorld, p.NetTurretWorldRotation) > 0.05f)
                p.SmoothTurretWorld = Quaternion.Slerp(p.SmoothTurretWorld, p.NetTurretWorldRotation, rotLerp);
            if (Quaternion.Angle(p.SmoothGunWorld, p.NetGunWorldRotation) > 0.05f)
                p.SmoothGunWorld = Quaternion.Slerp(p.SmoothGunWorld, p.NetGunWorldRotation, rotLerp);
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
        p.HasNetSample = false;
        p.SmoothVel = Vector3.zero;
    }
}
