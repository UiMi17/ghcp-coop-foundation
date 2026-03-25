using System;

namespace GHPC.CoopFoundation.Net;

/// <summary>FNV-1a 32-bit over mission key (ASCII, invariant lowercased). Stable across .NET / Python test tools.</summary>
internal static class CoopMissionHash
{
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>0 = unknown / empty (reject remote snapshots that require mission coherence).</summary>
    public static uint Token(string? missionSceneName)
    {
        if (string.IsNullOrEmpty(missionSceneName))
            return 0;
        uint h = FnvOffset;
        foreach (char c in missionSceneName!.ToLowerInvariant())
        {
            h ^= c;
            unchecked
            {
                h *= FnvPrime;
            }
        }

        return h == 0 ? 1u : h;
    }
}
