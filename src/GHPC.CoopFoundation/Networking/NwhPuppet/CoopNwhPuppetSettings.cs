namespace GHPC.CoopFoundation.Networking.NwhPuppet;

/// <summary>
///     Runtime prefs for NWH adaptation on client network puppets (configured from MelonPreferences).
///     Perf defaults: rigging off (no live suspension on puppets); wheel visuals on with optional wire-only at range.
/// </summary>
internal static class CoopNwhPuppetSettings
{
    /// <summary>Keep <see cref="NWH.WheelController3D.WheelController" /> enabled; inject wire velocity; skip chassis forces.</summary>
    public static bool WheelControllerVisualsEnabled { get; set; } = true;

    /// <summary>
    ///     Live suspension / rigging on puppets: expensive with many units; keep false unless testing presentation quality.
    /// </summary>
    public static bool RiggingEnabledOnPuppets { get; set; }

    /// <summary>
    ///     Beyond this distance (m) from camera / local unit, <see cref="CoopNwhPuppetContext" /> uses last GHW wire
    ///     velocities only (skips Hermite <see cref="ClientSimulationGovernor.TryGetDisplayVelocities" />). 0 = always full sample.
    /// </summary>
    public static float WheelWireOnlyBeyondMeters { get; set; }

    public static bool AnyNwhPuppetFeatureEnabled =>
        WheelControllerVisualsEnabled || RiggingEnabledOnPuppets;
}
