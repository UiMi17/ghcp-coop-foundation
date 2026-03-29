using GHPC;
using GHPC.Crew;
using GHPC.Humans;

namespace GHPC.CoopFoundation.Networking.Replication;

internal readonly struct CoopCrewStateSnapshot
{
    public readonly byte PresentMask;
    public readonly byte DeadMask;
    public readonly byte IncapacitatedMask;
    public readonly byte EvacuatedMask;
    public readonly byte SuspendedMask;

    public CoopCrewStateSnapshot(byte presentMask, byte deadMask, byte incapacitatedMask, byte evacuatedMask, byte suspendedMask)
    {
        PresentMask = presentMask;
        DeadMask = deadMask;
        IncapacitatedMask = incapacitatedMask;
        EvacuatedMask = evacuatedMask;
        SuspendedMask = suspendedMask;
    }

    public static bool TryCapture(Unit unit, out CoopCrewStateSnapshot snapshot)
    {
        snapshot = default;
        if (unit == null)
            return false;
        CrewManager? cm = unit.CrewManager;
        if (cm == null)
            return false;

        byte present = 0;
        byte dead = 0;
        byte incapacitated = 0;
        byte evacuated = 0;
        byte suspended = 0;

        CaptureSeat(cm, CrewPosition.Driver, 0, ref present, ref dead, ref incapacitated, ref evacuated, ref suspended);
        CaptureSeat(cm, CrewPosition.Gunner, 1, ref present, ref dead, ref incapacitated, ref evacuated, ref suspended);
        CaptureSeat(cm, CrewPosition.Loader, 2, ref present, ref dead, ref incapacitated, ref evacuated, ref suspended);
        CaptureSeat(cm, CrewPosition.Commander, 3, ref present, ref dead, ref incapacitated, ref evacuated, ref suspended);

        snapshot = new CoopCrewStateSnapshot(present, dead, incapacitated, evacuated, suspended);
        return true;
    }

    public bool NearlyEquals(in CoopCrewStateSnapshot other)
    {
        return PresentMask == other.PresentMask
            && DeadMask == other.DeadMask
            && IncapacitatedMask == other.IncapacitatedMask
            && EvacuatedMask == other.EvacuatedMask
            && SuspendedMask == other.SuspendedMask;
    }

    public void ApplyTo(Unit unit)
    {
        if (unit == null)
            return;
        CrewManager? cm = unit.CrewManager;
        if (cm == null)
            return;

        ApplySeat(cm, CrewPosition.Driver, 0);
        ApplySeat(cm, CrewPosition.Gunner, 1);
        ApplySeat(cm, CrewPosition.Loader, 2);
        ApplySeat(cm, CrewPosition.Commander, 3);
    }

    private static void CaptureSeat(
        CrewManager cm,
        CrewPosition seat,
        int bit,
        ref byte present,
        ref byte dead,
        ref byte incapacitated,
        ref byte evacuated,
        ref byte suspended)
    {
        CrewManager.CrewMember? member = cm.GetCrewMember(seat);
        if (member == null)
            return;
        byte m = (byte)(1 << bit);
        present |= m;
        if (member.Human != null && member.Human.IsDead)
            dead |= m;
        if (member.Brain != null)
        {
            if (member.Brain.Incapacitated)
                incapacitated |= m;
            if (member.Brain.Suspended)
                suspended |= m;
        }
        if (member.Evacuated)
            evacuated |= m;
    }

    private void ApplySeat(CrewManager cm, CrewPosition seat, int bit)
    {
        byte m = (byte)(1 << bit);
        if ((PresentMask & m) == 0)
            return;
        CrewManager.CrewMember? member = cm.GetCrewMember(seat);
        if (member == null)
            return;

        if ((DeadMask & m) != 0 && member.Human is IHuman h && !h.IsDead)
            h.Kill();
        if ((IncapacitatedMask & m) != 0 && member.Brain != null)
            member.Brain.Incapacitated = true;
        if ((EvacuatedMask & m) != 0)
            member.Evacuated = true;
        if (member.Brain != null)
            member.Brain.Suspended = (SuspendedMask & m) != 0;
    }
}
