using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CosmeticExplosionThrottle
{
    private const float MinIntervalSeconds = 0.06f;

    private static float _nextEmitTime;

    public static bool TryConsumeGlobal()
    {
        float t = Time.time;
        if (t < _nextEmitTime)
            return false;
        _nextEmitTime = t + MinIntervalSeconds;
        return true;
    }
}
