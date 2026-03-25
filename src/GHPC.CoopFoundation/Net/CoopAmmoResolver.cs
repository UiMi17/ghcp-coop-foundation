using System.Collections.Generic;
using GHPC.Weapons;
using GHPC.Weaponry;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>
///     Resolve <see cref="AmmoType" /> from wire <c>ammoKey</c> via <see cref="AmmoCodexScriptable" /> assets plus
///     known runtime-only types (e.g. <see cref="LiveRound.SpallAmmoType" />, which is not in any codex).
/// </summary>
internal static class CoopAmmoResolver
{
    private static readonly Dictionary<uint, AmmoType> ByKey = new();

    private static bool _built;

    public static void InvalidateCache()
    {
        ByKey.Clear();
        _built = false;
    }

    public static bool TryResolve(uint ammoKey, out AmmoType? ammo)
    {
        ammo = null;
        if (ammoKey == 0)
            return false;
        EnsureBuilt();
        return ByKey.TryGetValue(ammoKey, out ammo) && ammo != null;
    }

    private static void EnsureBuilt()
    {
        if (_built)
            return;
        _built = true;
        AmmoCodexScriptable[] codices = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
        foreach (AmmoCodexScriptable? c in codices)
        {
            if (c == null || c.AmmoType == null)
                continue;
            uint k = CoopAmmoKey.FromAmmoType(c.AmmoType);
            if (k == 0)
                continue;
            if (!ByKey.ContainsKey(k))
                ByKey[k] = c.AmmoType;
        }

        RegisterRuntimeOnlyIfMissing(LiveRound.SpallAmmoType);
    }

    private static void RegisterRuntimeOnlyIfMissing(AmmoType? ammo)
    {
        uint k = CoopAmmoKey.FromAmmoType(ammo);
        if (k == 0 || ammo == null)
            return;
        if (!ByKey.ContainsKey(k))
            ByKey[k] = ammo;
    }
}
