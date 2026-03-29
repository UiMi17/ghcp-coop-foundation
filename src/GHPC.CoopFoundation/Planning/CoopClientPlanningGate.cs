using GHPC.State;

namespace GHPC.CoopFoundation.Planning;

/// <summary>Coop client: host-only tactical map during UMC <see cref="MissionState.Planning" />.</summary>
internal static class CoopClientPlanningGate
{
    /// <summary>True while client waits for <see cref="Net.CoopUdpTransport"/> mission-planning-complete from host.</summary>
    public static bool IsWaitingForHostPlanning { get; private set; }

    public static void EnterWaitingForHost() => IsWaitingForHostPlanning = true;

    public static void Clear() => IsWaitingForHostPlanning = false;

    /// <summary>Apply host signal: leave Planning if still there (idempotent with vanilla guards).</summary>
    public static void ApplyHostPlanningCompleteFromNetwork()
    {
        IsWaitingForHostPlanning = false;
        MissionStateController? msc = MissionStateController.Instance;
        if (msc == null)
            return;
        if (MissionStateController.CurrentState != MissionState.Planning)
            return;
        msc.EndPlanningPhase();
    }
}
