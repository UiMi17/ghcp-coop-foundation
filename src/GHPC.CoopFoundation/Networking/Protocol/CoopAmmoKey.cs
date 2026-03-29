namespace GHPC.CoopFoundation.Networking.Protocol;

/// <summary>FNV-1a key for <see cref="AmmoType.Name" /> (same algorithm as mission token).</summary>
internal static class CoopAmmoKey
{
    public static uint FromAmmoType(AmmoType? ammo)
    {
        if (ammo == null || string.IsNullOrEmpty(ammo.Name))
            return 0;
        return CoopMissionHash.Token(ammo.Name);
    }
}
