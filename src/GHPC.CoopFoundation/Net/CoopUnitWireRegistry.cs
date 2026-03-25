using System;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>
///     Host and client: one wire <c>netId</c> per live <see cref="Unit" /> (FNV of UniqueName when unique;
///     monotonic synthetic ids on collision). Stable for the unit’s lifetime so GHW/GHC/ownership/snapshots agree.
/// </summary>
internal static class CoopUnitWireRegistry
{
    public const uint SyntheticIdBase = 0xA000_0000u;

    private static readonly Dictionary<Unit, uint> UnitToWire = new();

    private static readonly Dictionary<uint, Unit> WireToUnit = new();

    private static uint _nextSynthetic = SyntheticIdBase;

    private static int _lastRefreshFrame = -1;

    private static bool _loggedCollision;

    public static void ResetSession()
    {
        UnitToWire.Clear();
        WireToUnit.Clear();
        _nextSynthetic = SyntheticIdBase;
        _lastRefreshFrame = -1;
        _loggedCollision = false;
    }

    /// <summary>Rebuild map at most once per Unity frame (main thread).</summary>
    public static void EnsureRefreshedThisFrame()
    {
        int f = Time.frameCount;
        if (_lastRefreshFrame == f)
            return;
        _lastRefreshFrame = f;
        RefreshLiveWireMap();
    }

    public static uint GetWireId(Unit unit)
    {
        if (unit == null)
            return 0;
        EnsureRefreshedThisFrame();
        if (UnitToWire.TryGetValue(unit, out uint w))
            return w;
        return CoopUnitNetId.FromUnit(unit);
    }

    public static Unit? TryResolveUnit(uint wireId)
    {
        if (wireId == 0)
            return null;
        EnsureRefreshedThisFrame();
        return WireToUnit.TryGetValue(wireId, out Unit? u) ? u : null;
    }

    private static void RefreshLiveWireMap()
    {
        Unit[] units = UnityEngine.Object.FindObjectsOfType<Unit>();
        var live = new HashSet<Unit>();
        foreach (Unit? u in units)
        {
            if (u != null && u.gameObject != null)
                live.Add(u);
        }

        var stale = new List<Unit>();
        foreach (Unit k in UnitToWire.Keys)
        {
            if (!live.Contains(k))
                stale.Add(k);
        }

        foreach (Unit k in stale)
            UnitToWire.Remove(k);

        var occupied = new HashSet<uint>(UnitToWire.Values);

        List<Unit> sorted = live
            .OrderBy(u => u.UniqueName ?? "", StringComparer.Ordinal)
            .ThenBy(u => u.transform.position.x)
            .ThenBy(u => u.transform.position.y)
            .ThenBy(u => u.transform.position.z)
            .ThenBy(u => u.gameObject.name, StringComparer.Ordinal)
            .ToList();

        foreach (Unit u in sorted)
        {
            if (UnitToWire.ContainsKey(u))
                continue;

            uint nat = CoopUnitNetId.FromUnit(u);
            uint wire;
            if (!occupied.Contains(nat))
            {
                wire = nat;
            }
            else
            {
                wire = AllocateSynthetic(occupied);
                if (!_loggedCollision)
                {
                    _loggedCollision = true;
                    MelonLogger.Warning(
                        "[CoopNet] Unit wire id: FNV collision — assigning synthetic id (once per session log).");
                }
            }

            UnitToWire[u] = wire;
            occupied.Add(wire);
        }

        WireToUnit.Clear();
        foreach (KeyValuePair<Unit, uint> kv in UnitToWire)
            WireToUnit[kv.Value] = kv.Key;
    }

    private static uint AllocateSynthetic(HashSet<uint> occupied)
    {
        uint id;
        do
        {
            id = _nextSynthetic++;
            if (_nextSynthetic < SyntheticIdBase)
                _nextSynthetic = SyntheticIdBase;
        }
        while (occupied.Contains(id));

        return id;
    }
}
