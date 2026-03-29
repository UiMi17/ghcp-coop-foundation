using GHPC;

namespace GHPC.CoopFoundation.Networking.Replication;

internal readonly struct CoopUnitStateSnapshot
{
    public const byte FlagDestroyed = 1 << 0;
    public const byte FlagIncapacitated = 1 << 1;
    public const byte FlagAbandoned = 1 << 2;
    public const byte FlagCannotMove = 1 << 3;
    public const byte FlagCannotShoot = 1 << 4;

    public readonly byte Flags;

    public CoopUnitStateSnapshot(byte flags)
    {
        Flags = flags;
    }

    public bool Destroyed => (Flags & FlagDestroyed) != 0;
    public bool Incapacitated => (Flags & FlagIncapacitated) != 0;
    public bool Abandoned => (Flags & FlagAbandoned) != 0;
    public bool CannotMove => (Flags & FlagCannotMove) != 0;
    public bool CannotShoot => (Flags & FlagCannotShoot) != 0;

    public static bool TryCapture(Unit unit, out CoopUnitStateSnapshot snapshot)
    {
        snapshot = default;
        if (unit == null)
            return false;
        byte flags = 0;
        if (unit.Destroyed)
            flags |= FlagDestroyed;
        if (unit.UnitIncapacitated)
            flags |= FlagIncapacitated;
        if (unit.Abandoned)
            flags |= FlagAbandoned;
        if (unit.CannotMove)
            flags |= FlagCannotMove;
        if (unit.CannotShoot)
            flags |= FlagCannotShoot;
        snapshot = new CoopUnitStateSnapshot(flags);
        return true;
    }

    public bool NearlyEquals(in CoopUnitStateSnapshot other) => Flags == other.Flags;

    public void ApplyTo(Unit unit)
    {
        if (unit == null)
            return;
        if (CannotMove && !unit.CannotMove)
            unit.NotifyCannotMove();
        if (CannotShoot && !unit.CannotShoot)
            unit.NotifyCannotShoot();
        if (Abandoned && !unit.Abandoned)
            unit.NotifyAbandoned();
        if (Incapacitated && !unit.UnitIncapacitated)
            unit.NotifyIncapacitated();
        if (Destroyed && !unit.Destroyed)
            unit.NotifyDestroyed();
    }
}
