using GHPC.Weapons;

namespace GHPC.CoopFoundation.Net;

internal static class CoopWeaponKey
{
    public static uint FromWeaponSystem(WeaponSystem? ws)
    {
        if (ws == null)
            return 0;
        UnityEngine.GameObject go = ws.gameObject;
        string name = go != null ? go.name : string.Empty;
        return CoopMissionHash.Token(name);
    }
}
