using System.Collections.Generic;
using GHPC;
using GHPC.CoopFoundation.Networking.NwhPuppet;
using MelonLoader;
using NWH.VehiclePhysics;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking;

/// <summary>
///     Disables NWH vehicle driver components and makes the chassis rigidbody kinematic so network pose apply
///     does not fight wheel/suspension integration. Track visuals are driven by
///     <see cref="CoopChassisTrackVisualPresenter" /> using wire velocity, not NWH.
/// </summary>
internal sealed class CoopVanillaVehicleDriverMute
{
    private readonly List<(Behaviour Behaviour, bool WasEnabled)> _behaviours = new();

    private readonly List<(VehicleController Vc, bool WasRiggingEnabled)> _riggingRestore = new();

    private Rigidbody? _body;

    private bool _capturedBody;

    private bool _wasKinematic;

    private RigidbodyInterpolation _wasInterpolation;

    /// <summary>Returns false if nothing was changed (caller may still puppet transform-only units).</summary>
    public static bool TryBegin(Unit unit, out CoopVanillaVehicleDriverMute? mute)
    {
        var m = new CoopVanillaVehicleDriverMute();
        if (!m.Populate(unit))
        {
            m.Restore();
            mute = null;
            return false;
        }

        mute = m;
        return true;
    }

    public void Restore()
    {
        for (int i = 0; i < _behaviours.Count; i++)
        {
            (Behaviour b, bool was) = _behaviours[i];
            if (b != null && was)
                b.enabled = true;
        }

        _behaviours.Clear();
        CoopNwhRiggingSuppress.Restore(_riggingRestore);

        if (_capturedBody && _body != null)
        {
            _body.isKinematic = _wasKinematic;
            _body.interpolation = _wasInterpolation;
        }

        _capturedBody = false;
        _body = null;
    }

    private bool Populate(Unit unit)
    {
        try
        {
            if (!CoopNwhPuppetSettings.RiggingEnabledOnPuppets)
                CoopNwhRiggingSuppress.DisableOnUnit(unit, _riggingRestore);

            Behaviour[] drivers = unit.GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < drivers.Length; i++)
            {
                Behaviour b = drivers[i];
                if (b == null || !b.enabled)
                    continue;
                string? n = b.GetType().FullName;
                if (string.IsNullOrEmpty(n))
                    continue;
                if (!n.Contains("VehicleController")
                    && !n.Contains("DriverAI")
                    && !n.Contains("DriverBrain")
                    && !n.Contains("Navigator")
                    && !n.Contains("PathDelayHandler"))
                    continue;
                _behaviours.Add((b, true));
                b.enabled = false;
            }

            NwhChassis? nwh = unit.GetComponentInChildren<NwhChassis>(true);
            if (nwh != null && nwh.enabled)
            {
                _behaviours.Add((nwh, true));
                nwh.enabled = false;
            }

            if (!CoopNwhPuppetSettings.WheelControllerVisualsEnabled)
                CoopNwhWheelControllerSuppress.DisableAllOnUnit(unit, _behaviours);

            Rigidbody? rb = unit.Chassis?.Rigidbody;
            if (rb == null)
            {
                rb = unit.GetComponentInParent<Rigidbody>();
                rb ??= unit.GetComponentInChildren<Rigidbody>();
            }

            if (rb != null)
            {
                _body = rb;
                _wasKinematic = rb.isKinematic;
                _wasInterpolation = rb.interpolation;
                _capturedBody = true;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }

            return _behaviours.Count > 0 || _capturedBody;
        }
        catch (System.Exception ex)
        {
            MelonLogger.Warning($"[CoopHostPuppet] vehicle driver mute failed: {ex.Message}");
            Restore();
            return false;
        }
    }
}
