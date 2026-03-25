using GHPC.Weaponry;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

/// <summary>
/// Host: maps <see cref="AntiPersonnelGrenade" /> serialized damage radius to GHC <see cref="CoopCombatPacket.EventExplosion" /> TNT kg
/// (client <see cref="GHPC.Effects.Explosions.RegisterExplosion" /> scale). Not gameplay damage — host already resolved AP overlap.
/// </summary>
internal static class CoopFragGrenadeCosmeticTnt
{
    /// <summary>Radius (m) at which legacy heuristic <c>0.22</c> kg was tuned.</summary>
    private const float ReferenceDamageRadiusMeters = 6f;

    /// <summary>Historical single-value cosmetic TNT when radius mapping is off.</summary>
    private const float LegacyHeuristicKg = 0.22f;

    private const float MinEmitKg = 0.02f;

    private const float MaxEmitKg = 4f;

    public static float ResolveTntKg(Grenade grenade, bool useAntiPersonnelRadius, float fallbackTntKg)
    {
        float fb = Mathf.Max(0f, fallbackTntKg);
        if (grenade is AntiPersonnelGrenade ap && useAntiPersonnelRadius)
        {
            float r = TryReadSerializedRadius(ap);
            if (r > 0f)
                return CosmeticTntFromDamageRadius(r, fb);
        }

        return fb;
    }

    private static float TryReadSerializedRadius(AntiPersonnelGrenade ap)
    {
        try
        {
            float r = Traverse.Create(ap).Field<float>("_radius").Value;
            return r > 0f ? r : -1f;
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>Linear scale vs reference radius; clamped for wire <see cref="CoopCombatPacket.WriteExplosion" /> centi-kg range.</summary>
    public static float CosmeticTntFromDamageRadius(float radiusMeters, float fallbackIfInvalid)
    {
        if (radiusMeters <= 0f)
            return fallbackIfInvalid;
        float t = LegacyHeuristicKg * (radiusMeters / ReferenceDamageRadiusMeters);
        return Mathf.Clamp(t, MinEmitKg, MaxEmitKg);
    }
}
