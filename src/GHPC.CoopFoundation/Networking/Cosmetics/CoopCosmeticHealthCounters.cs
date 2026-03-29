using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CoopCosmeticHealthCounters
{
    private static uint _particleDroppedInterest;

    private static uint _particleDroppedThrottle;

    private static uint _explosionDroppedInterest;

    private static uint _explosionDroppedThrottle;

    private static float _nextLogTime = float.NegativeInfinity;

    public static void RecordParticleDroppedInterest() => _particleDroppedInterest++;

    public static void RecordParticleDroppedThrottle() => _particleDroppedThrottle++;

    public static void RecordExplosionDroppedInterest() => _explosionDroppedInterest++;

    public static void RecordExplosionDroppedThrottle() => _explosionDroppedThrottle++;

    public static void ResetSession()
    {
        _particleDroppedInterest = 0;
        _particleDroppedThrottle = 0;
        _explosionDroppedInterest = 0;
        _explosionDroppedThrottle = 0;
        _nextLogTime = float.NegativeInfinity;
    }

    public static void TickLogIfDue()
    {
        if (!CoopUdpTransport.LogCosmeticHealth)
            return;
        float now = Time.time;
        if (now < _nextLogTime)
            return;
        if (_particleDroppedInterest + _particleDroppedThrottle + _explosionDroppedInterest + _explosionDroppedThrottle
            == 0)
            return;
        _nextLogTime = now + 4f;
        MelonLogger.Msg(
            $"[CoopNet][Cosmetic] drops: particle interest={_particleDroppedInterest} throttle={_particleDroppedThrottle} " +
            $"explosion interest={_explosionDroppedInterest} throttle={_explosionDroppedThrottle}");
    }
}
