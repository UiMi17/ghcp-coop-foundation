using GHPC;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Sampling;

/// <summary>
///     Reads <see cref="PlayerInput.CurrentPlayerUnit" /> at 10 Hz while <see cref="CoopSessionState.IsPlaying" />; optional periodic log.
/// </summary>
internal static class LocalPlayerSampler
{
    private static float _nextLogTime = float.NegativeInfinity;

    public static void Tick(float time, float deltaTime, bool logSummary, float logIntervalSeconds)
    {
        if (!CoopSessionState.IsPlaying)
        {
            _nextLogTime = float.NegativeInfinity;
            return;
        }

        PlayerInput? input = PlayerInput.Instance;
        if (input == null)
            return;

        Unit? unit = input.CurrentPlayerUnit;
        if (unit == null)
            return;

        if (!CoopSessionState.TryAdvanceSampling(deltaTime, unit))
            return;

        CoopUdpTransport.SendLocalSnapshot();

        if (!logSummary || logIntervalSeconds <= 0f)
            return;

        if (time < _nextLogTime)
            return;

        _nextLogTime = time + logIntervalSeconds;

        Vector3 p = CoopSessionState.LastSampledPosition;
        Vector3 euler = CoopSessionState.LastSampledRotation.eulerAngles;
        MelonLogger.Msg(
            $"[CoopSnapshot] id={CoopSessionState.LastSampledUnitInstanceId} " +
            $"go={CoopSessionState.LastSampledGoName} friendly={CoopSessionState.LastSampledFriendlyName} " +
            $"pos=({p.x:F1},{p.y:F1},{p.z:F1}) euler=({euler.x:F0},{euler.y:F0},{euler.z:F0})");
    }
}
