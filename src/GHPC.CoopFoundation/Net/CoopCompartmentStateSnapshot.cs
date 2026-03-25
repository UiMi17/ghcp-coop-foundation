using GHPC;
using GHPC.Effects;

namespace GHPC.CoopFoundation.Net;

internal readonly struct CoopCompartmentStateSnapshot
{
    public readonly bool FirePresent;
    public readonly bool UnsecuredFirePresent;
    public readonly byte CombinedFlameHeightPct;
    public readonly byte InternalTemperaturePct;

    public CoopCompartmentStateSnapshot(
        bool firePresent,
        bool unsecuredFirePresent,
        byte combinedFlameHeightPct,
        byte internalTemperaturePct)
    {
        FirePresent = firePresent;
        UnsecuredFirePresent = unsecuredFirePresent;
        CombinedFlameHeightPct = combinedFlameHeightPct;
        InternalTemperaturePct = internalTemperaturePct;
    }

    public static bool TryCapture(Unit unit, out CoopCompartmentStateSnapshot snapshot)
    {
        snapshot = default;
        if (unit == null)
            return false;
        FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
        if (fm == null)
            return false;
        float temp = 0f;
        if (fm.Compartments != null && fm.Compartments.Length > 0)
            temp = fm.Compartments[0].InternalTemperature;
        snapshot = new CoopCompartmentStateSnapshot(
            fm.FirePresent,
            fm.UnsecuredFirePresent,
            ToPct(fm.CombinedFlameHeight, 0f, 5f),
            ToPct(temp, -30f, 500f));
        return true;
    }

    public bool NearlyEquals(in CoopCompartmentStateSnapshot other)
    {
        return FirePresent == other.FirePresent
            && UnsecuredFirePresent == other.UnsecuredFirePresent
            && CombinedFlameHeightPct == other.CombinedFlameHeightPct
            && InternalTemperaturePct == other.InternalTemperaturePct;
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
