using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CosmeticParticleThrottle
{
    private const float MinIntervalSeconds = 0.04f;

    private static float _nextEmitTime;

    public static bool TryConsume(ref float nextTime, float minIntervalSeconds = MinIntervalSeconds)
    {
        float t = Time.time;
        if (t < nextTime)
            return false;
        nextTime = t + minIntervalSeconds;
        return true;
    }

    public static bool TryConsumeGlobal()
    {
        return TryConsume(ref _nextEmitTime, MinIntervalSeconds);
    }
}
