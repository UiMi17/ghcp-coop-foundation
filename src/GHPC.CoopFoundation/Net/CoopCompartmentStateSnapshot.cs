using GHPC;
using GHPC.Effects;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Net;

internal readonly struct CoopCompartmentStateSnapshot
{
    public readonly bool FirePresent;
    public readonly bool UnsecuredFirePresent;
    public readonly byte CombinedFlameHeightPct;
    public readonly byte InternalTemperaturePct;

    /// <summary>0–100: normalized scorch vs host <see cref="FlammablesManager" /> MaxScorch (shader drive).</summary>
    public readonly byte ScorchPct;

    /// <summary>0–100: host smoke plume intensity (<c>num2</c> in <c>doTick</c>, normalized by max 10).</summary>
    public readonly byte SmokeColumnPct;

    public CoopCompartmentStateSnapshot(
        bool firePresent,
        bool unsecuredFirePresent,
        byte combinedFlameHeightPct,
        byte internalTemperaturePct,
        byte scorchPct = 0,
        byte smokeColumnPct = 0)
    {
        FirePresent = firePresent;
        UnsecuredFirePresent = unsecuredFirePresent;
        CombinedFlameHeightPct = combinedFlameHeightPct;
        InternalTemperaturePct = internalTemperaturePct;
        ScorchPct = scorchPct;
        SmokeColumnPct = smokeColumnPct;
    }

    public static bool TryCapture(Unit unit, out CoopCompartmentStateSnapshot snapshot)
    {
        snapshot = default;
        if (unit == null)
            return false;
        FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
        if (fm == null)
            return false;
        float temp = TryGetMaxInternalTemperatureC(fm);

        float maxScorch = fm.MaxScorchOverride > 0f ? fm.MaxScorchOverride : 0.6f;
        float scorchRatio = fm.CurrentScorchRatio;
        byte scorchPct = 0;
        if (maxScorch > 1e-4f)
            scorchPct = (byte)Mathf.RoundToInt(Mathf.Clamp01(scorchRatio / maxScorch) * 100f);

        float effectiveFlame = GetEffectiveCombinedFlameHeight(fm);

        byte smokePct = 0;
        if (TryComputeHostSmokeColumnNum2(fm, effectiveFlame, out float num2) && num2 > 0f)
            smokePct = (byte)Mathf.RoundToInt(Mathf.Clamp01(num2 / 10f) * 100f);

        snapshot = new CoopCompartmentStateSnapshot(
            fm.FirePresent,
            fm.UnsecuredFirePresent,
            ToPct(effectiveFlame, 0f, 5f),
            ToPct(temp, -30f, 500f),
            scorchPct,
            smokePct);
        return true;
    }

    public bool NearlyEquals(in CoopCompartmentStateSnapshot other)
    {
        return FirePresent == other.FirePresent
            && UnsecuredFirePresent == other.UnsecuredFirePresent
            && CombinedFlameHeightPct == other.CombinedFlameHeightPct
            && InternalTemperaturePct == other.InternalTemperaturePct
            && ScorchPct == other.ScorchPct
            && SmokeColumnPct == other.SmokeColumnPct;
    }

    /// <summary>
    /// Manager <see cref="FlammablesManager.CombinedFlameHeight" /> can lag <see cref="Compartment" /> updates for a frame;
    /// secured brew-ups keep <c>_totalUnsecuredBurnTime</c> at 0 while exits still show strong fire/smoke.
    /// Ammo cook-offs often spread across compartments — <see cref="Compartment.CombinedFlameHeight" /> per compartment
    /// may stay modest (~1) while the spectacle reads hotter via max internal temperature.
    /// </summary>
    private static float GetEffectiveCombinedFlameHeight(FlammablesManager fm)
    {
        float m = fm.CombinedFlameHeight;
        float maxComp = 0f;
        float sumBurning = 0f;
        float maxTempC = TryGetMaxInternalTemperatureC(fm);

        if (fm.Compartments != null)
        {
            foreach (Compartment? c in fm.Compartments)
            {
                if (c == null)
                    continue;
                float h = c.CombinedFlameHeight;
                maxComp = Mathf.Max(maxComp, h);
                if (c.FirePresent && h > 1e-4f)
                    sumBurning += h;
            }
        }

        m = Mathf.Max(m, maxComp);
        float sumBlend = Mathf.Min(5f, sumBurning * 0.92f);
        if (sumBlend > m)
            m = sumBlend;

        if (fm.FirePresent)
        {
            float tNorm = Mathf.Clamp01(Mathf.InverseLerp(220f, 1300f, maxTempC));
            if (tNorm > 0f)
                m = Mathf.Max(m, Mathf.Lerp(2.2f, 5f, tNorm));

            if (fm.UnsecuredFirePresent)
                m = Mathf.Min(5f, m * 1.12f);
        }

        if (fm.FirePresent && m < 1e-4f)
            m = 1.5f;
        return Mathf.Clamp(m, 0f, 5f);
    }

    private static float TryGetMaxInternalTemperatureC(FlammablesManager fm)
    {
        if (fm.Compartments == null || fm.Compartments.Length == 0)
            return 0f;
        float maxT = float.NegativeInfinity;
        foreach (Compartment? c in fm.Compartments)
        {
            if (c == null)
                continue;
            maxT = Mathf.Max(maxT, c.InternalTemperature);
        }

        return float.IsNegativeInfinity(maxT) ? fm.Compartments[0].InternalTemperature : maxT;
    }

    /// <summary>Matches <see cref="FlammablesManager" />.<c>doTick</c> plume strength (before particle attach).</summary>
    internal static bool TryComputeHostSmokeColumnNum2(FlammablesManager fm, float effectiveCombinedFlame, out float num2)
    {
        num2 = 0f;
        var tr = Traverse.Create(fm);
        float totalUnsecured = tr.Field<float>("_totalUnsecuredBurnTime").Value;
        float min = fm.ResidualSmokePresent ? 0.5f : 0f;
        float combined = effectiveCombinedFlame;

        if (totalUnsecured <= 0f)
        {
            if (fm.FirePresent)
                num2 = Mathf.Clamp(combined, min, 5f);
            if (num2 < 0f)
                num2 = 0f;
            return true;
        }

        float grace = tr.Field<float>("SmokePlumeGracePeriod").Value;
        float timeToFull = tr.Field<float>("TimeToReachFullSmoke").Value;
        if (timeToFull <= 1e-4f)
            timeToFull = 50f;

        float totalScorchTime = fm.ScorchTimeOverride > 0f ? fm.ScorchTimeOverride : 150f;

        num2 = Mathf.Clamp(combined, min, 5f)
            * Mathf.Clamp((totalUnsecured - grace) / timeToFull, 0f, 2f)
            * Mathf.Max(0.1f, 1f - totalUnsecured / (totalScorchTime * 4f));
        if (num2 < 0f)
            num2 = 0f;
        return true;
    }

    private static byte ToPct(float value, float min, float max)
    {
        if (max <= min)
            return 0;
        float t = (value - min) / (max - min);
        if (t < 0f)
            t = 0f;
        if (t > 1f)
            t = 1f;
        return (byte)(t * 100f);
    }
}
