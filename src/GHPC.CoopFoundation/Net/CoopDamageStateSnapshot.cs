using GHPC;
using GHPC.Vehicle;
using System;
using System.Reflection;

namespace GHPC.CoopFoundation.Net;

/// <summary>Compact host-authoritative damage state for one unit.</summary>
internal readonly struct CoopDamageStateSnapshot
{
    /// <summary>255 means "unknown / not available".</summary>
    public const byte UnknownPercent = 255;

    public readonly bool UnitDestroyed;

    public readonly byte EngineHpPct;

    public readonly byte TransmissionHpPct;

    public readonly byte RadiatorHpPct;

    public readonly byte LeftTrackHpPct;

    public readonly byte RightTrackHpPct;

    public CoopDamageStateSnapshot(
        bool unitDestroyed,
        byte engineHpPct,
        byte transmissionHpPct,
        byte radiatorHpPct,
        byte leftTrackHpPct,
        byte rightTrackHpPct)
    {
        UnitDestroyed = unitDestroyed;
        EngineHpPct = engineHpPct;
        TransmissionHpPct = transmissionHpPct;
        RadiatorHpPct = radiatorHpPct;
        LeftTrackHpPct = leftTrackHpPct;
        RightTrackHpPct = rightTrackHpPct;
    }

    public static bool TryCapture(Unit unit, out CoopDamageStateSnapshot snapshot)
    {
        snapshot = default;
        if (unit == null)
            return false;
        ChassisDamageManager? cdm = unit.InfoBroker?.ChassisDamageManager;
        snapshot = new CoopDamageStateSnapshot(
            unit.Destroyed,
            ToPct(cdm?.EngineHitZone),
            ToPct(cdm?.TransmissionHitZone),
            ToPct(cdm?.RadiatorHitZone),
            ToPct(cdm?.LeftTrackHitZone),
            ToPct(cdm?.RightTrackHitZone));
        return true;
    }

    public bool NearlyEquals(in CoopDamageStateSnapshot other)
    {
        return UnitDestroyed == other.UnitDestroyed
            && NearlySame(EngineHpPct, other.EngineHpPct)
            && NearlySame(TransmissionHpPct, other.TransmissionHpPct)
            && NearlySame(RadiatorHpPct, other.RadiatorHpPct)
            && NearlySame(LeftTrackHpPct, other.LeftTrackHpPct)
            && NearlySame(RightTrackHpPct, other.RightTrackHpPct);
    }

    public void ApplyTo(Unit victim)
    {
        if (victim == null)
            return;
        ChassisDamageManager? cdm = victim.InfoBroker?.ChassisDamageManager;
        if (cdm != null)
        {
            ApplyPct(cdm.EngineHitZone, EngineHpPct);
            ApplyPct(cdm.TransmissionHitZone, TransmissionHpPct);
            ApplyPct(cdm.RadiatorHitZone, RadiatorHpPct);
            ApplyPct(cdm.LeftTrackHitZone, LeftTrackHpPct);
            ApplyPct(cdm.RightTrackHitZone, RightTrackHpPct);
        }

        if (UnitDestroyed && !victim.Destroyed)
        {
            // Keep this one-way (host can only force destroyed=true).
            victim.NotifyDestroyed();
        }
    }

    private static byte ToPct(object? c)
    {
        if (c == null)
            return UnknownPercent;
        if (!TryGetHealthPercent(c, out float clamped))
            return UnknownPercent;
        if (clamped < 0f)
            clamped = 0f;
        if (clamped > 100f)
            clamped = 100f;
        return (byte)clamped;
    }

    private static void ApplyPct(object? c, byte pct)
    {
        if (c == null || pct == UnknownPercent)
            return;
        TrySetHealthPercent(c, pct);
    }

    private static bool NearlySame(byte a, byte b)
    {
        if (a == UnknownPercent || b == UnknownPercent)
            return a == b;
        int d = a - b;
        if (d < 0)
            d = -d;
        return d <= 1;
    }

    private static bool TryGetHealthPercent(object c, out float healthPercent)
    {
        healthPercent = 0f;
        PropertyInfo? p = c.GetType().GetProperty("HealthPercent", BindingFlags.Instance | BindingFlags.Public);
        if (p == null || !p.CanRead)
            return false;
        object? v = p.GetValue(c);
        if (v is float f)
        {
            healthPercent = f;
            return true;
        }

        try
        {
            healthPercent = Convert.ToSingle(v);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetHealthPercent(object c, byte pct)
    {
        MethodInfo? m = c.GetType().GetMethod("SetHealthPercent", BindingFlags.Instance | BindingFlags.Public);
        if (m == null)
            return;
        try
        {
            m.Invoke(c, new object[] { (float)pct });
        }
        catch
        {
            // Best-effort corrective path; leave local sim untouched if reflection fails.
        }
    }
}
