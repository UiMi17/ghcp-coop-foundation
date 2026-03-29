namespace GHPC.CoopFoundation.Networking.Protocol;

/// <summary>Wire <c>effectKind</c> for GHC <see cref="CoopCombatPacket.EventImpactFx" /> (Phase 4).</summary>
internal static class CoopImpactFxKind
{
    public const ushort Terrain = 1;

    public const ushort Ricochet = 2;

    /// <summary>Wire: armor thickness in ImpactFx payload <c>normal.x</c>.</summary>
    public const ushort ArmorSmallCal = 3;

    /// <summary>Wire: armor thickness in ImpactFx payload <c>normal.x</c>.</summary>
    public const ushort ArmorLargeCal = 4;

    /// <summary>No <c>ammoKey</c> on wire; thickness in <c>normal.x</c> (penetration int/ext perspective SFX).</summary>
    public const ushort ArmorPenPerspective = 5;
}
