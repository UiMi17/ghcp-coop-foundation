using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using MelonLoader;

namespace GHPC.CoopFoundation.Diagnostics;

/// <summary>
///     Aggregated wall-time per named section (main thread). Intended for Steam builds where Unity Profiler is unavailable.
/// </summary>
internal static class CoopCpuDiag
{
    private struct Acc
    {
        public long SumTicks;
        public int Count;
        public long MaxTicks;
    }

    private static readonly Dictionary<string, Acc> AccMap = new();

    private static bool _enabled;
    private static float _intervalSec = 1f;
    private static float _nextLogAt = float.NaN;

    public static bool Enabled => _enabled;

    public static void Configure(bool enabled, float intervalSec)
    {
        if (!enabled && _enabled)
            AccMap.Clear();

        _enabled = enabled;
        _intervalSec = intervalSec < 0.25f ? 0.25f : intervalSec;
        if (!enabled)
            _nextLogAt = float.NaN;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string _key;
        private readonly long _startTicks;

        internal Scope(string key)
        {
            _key = key;
            _startTicks = _enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public void Dispose()
        {
            if (_startTicks == 0)
                return;

            Record(_key, Stopwatch.GetTimestamp() - _startTicks);
        }
    }

    public static Scope Start(string key) => new Scope(key);

    private static void Record(string key, long elapsedTicks)
    {
        AccMap.TryGetValue(key, out var a);
        a.SumTicks += elapsedTicks;
        a.Count++;
        if (elapsedTicks > a.MaxTicks)
            a.MaxTicks = elapsedTicks;
        AccMap[key] = a;
    }

    public static void FlushIfDue(float unscaledTime)
    {
        if (!_enabled)
            return;

        if (float.IsNaN(_nextLogAt))
            _nextLogAt = unscaledTime + _intervalSec;

        if (unscaledTime < _nextLogAt)
            return;

        _nextLogAt = unscaledTime + _intervalSec;

        if (AccMap.Count == 0)
            return;

        long freq = Stopwatch.Frequency;
        var keys = new string[AccMap.Count];
        var i = 0;
        foreach (var k in AccMap.Keys)
            keys[i++] = k;

        System.Array.Sort(keys, System.StringComparer.Ordinal);

        var sb = new StringBuilder(384);
        sb.Append("[CoopCpuDiag] ");
        foreach (var key in keys)
        {
            var a = AccMap[key];
            double avgUs = (double)a.SumTicks / a.Count * 1_000_000.0 / freq;
            double maxUs = (double)a.MaxTicks * 1_000_000.0 / freq;
            sb.Append(key)
                .Append('=')
                .Append((long)avgUs)
                .Append("avg/")
                .Append((long)maxUs)
                .Append("maxµs n=")
                .Append(a.Count)
                .Append("; ");
        }

        MelonLogger.Msg(sb.ToString());
        AccMap.Clear();
    }
}
