using System.Collections.Generic;
using GHPC;
using GHPC.Effects;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CoopCompartmentFxReplay
{
    private sealed class HeldSmokeColumn
    {
        public GameObject Root = null!;
        public IFireParticleSystem Particles = null!;
        public bool Playing;
    }

    private static readonly Dictionary<int, HeldSmokeColumn> RemoteSmokeColumns = new();

    /// <summary>
    /// Host runs <see cref="FlammablesManager.doTick" /> → overpressure opens <see cref="CompartmentExit" />; peers suppress
    /// <c>doTick</c>, so exits stay <see cref="CompartmentExit.Closed" />. Particle APIs no-op while closed (decompiled
    /// <c>SetFireRatio</c> / <c>SetSmokeRatio</c> / <c>MakeFireBurst</c>), hence smoke column from replication but weak flames.
    /// </summary>
    public static void TryOpenExitsWhenCompartmentHasNoVentilation(Unit unit, in CoopCompartmentStateSnapshot state)
    {
        if (unit == null || !state.FirePresent)
            return;
        FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
        if (fm?.Compartments == null)
            return;
        bool severeFire = state.CombinedFlameHeightPct >= 18;
        foreach (Compartment? c in fm.Compartments)
        {
            if (c?.Exits == null)
                continue;
            float openArea = 0f;
            foreach (CompartmentExit? e in c.Exits)
            {
                if (e != null && !e.Closed)
                    openArea += e.Area;
            }

            bool noVentilation = openArea <= 1e-4f;
            if (!noVentilation && !severeFire)
                continue;
            foreach (CompartmentExit? e in c.Exits)
            {
                if (e == null || !e.Closed)
                    continue;
                e.Open(0f, lockAnim: true, allowAnimation: false);
            }
        }
    }

    public static void TryFireBurstFx(Unit unit, float ratio = 1f)
    {
        if (unit == null)
            return;
        FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
        if (fm?.Compartments == null)
            return;
        foreach (Compartment? c in fm.Compartments)
        {
            if (c?.Exits == null)
                continue;
            foreach (CompartmentExit? exit in c.Exits)
            {
                exit?.MakeFireBurst(Mathf.Max(0.1f, ratio));
            }
        }
    }

    /// <summary>
    /// Drives exit fire/smoke ratios from a replicated snapshot (mirrors <see cref="Compartment"/> update logic).
    /// Tall smoke column + scorch are applied via <see cref="TryApplyScorchAndRemoteSmokeColumn"/> for non-local units.
    /// </summary>
    public static void TryApplySustainedFireAndSmoke(Unit unit, in CoopCompartmentStateSnapshot state)
    {
        if (unit == null)
            return;
        FlammablesManager? fm = unit.GetComponent<FlammablesManager>();
        if (fm?.Compartments == null)
            return;

        if (!state.FirePresent && state.CombinedFlameHeightPct == 0)
        {
            foreach (Compartment? c in fm.Compartments)
            {
                if (c?.Exits == null)
                    continue;
                foreach (CompartmentExit? exit in c.Exits)
                {
                    exit?.SetFireRatio(0f);
                    exit?.SetSmokeRatio(0f);
                }
            }

            return;
        }

        float combinedHeight = state.CombinedFlameHeightPct / 100f * 5f;
        if (state.FirePresent && combinedHeight < 0.15f)
            combinedHeight = Mathf.Max(combinedHeight, 2f);
        foreach (Compartment? c in fm.Compartments)
        {
            if (c?.Exits == null)
                continue;
            float openArea = 0f;
            foreach (CompartmentExit? exit2 in c.Exits)
            {
                if (exit2 != null && !exit2.Closed)
                    openArea += exit2.Area;
            }

            if (openArea <= 0f)
                continue;

            foreach (CompartmentExit? exit3 in c.Exits)
            {
                if (exit3 == null || exit3.Closed)
                    continue;
                float share = exit3.Area / openArea;
                if (combinedHeight < exit3.FlameHeightThreshold)
                    exit3.SetFireRatio(0f);
                else
                    exit3.SetFireRatioByFlameHeight(combinedHeight * share);
                exit3.SetSmokeRatio(0.1f + combinedHeight * share * 0.5f);
            }
        }
    }

    /// <summary>
    /// Host-authoritative scorch + synthetic smoke column for peers (local player vehicle still runs full <see cref="FlammablesManager" /> tick).
    /// </summary>
    public static void TryApplyScorchAndRemoteSmokeColumn(
        FlammablesManager fm,
        byte scorchPct,
        byte smokeColumnPct)
    {
        float maxScorch = fm.MaxScorchOverride > 0f ? fm.MaxScorchOverride : 0.6f;
        fm.ForceScorchAll((scorchPct / 100f) * maxScorch);

        int id = fm.GetInstanceID();
        float num2 = (smokeColumnPct / 100f) * 10f;
        if (num2 <= 0.001f)
        {
            TryStopRemoteSmokeColumn(id);
            return;
        }

        if (!RemoteSmokeColumns.TryGetValue(id, out HeldSmokeColumn? held) || held.Root == null || held.Particles == null)
        {
            if (ParticleEffectsManager.Instance == null)
                return;
            GameObject? prefab = ParticleEffectsManager.Instance.GetSmokePrefab(fm.SmokeColumnType);
            if (prefab == null)
                return;

            Transform origin = fm.SmokeColumnOrigin != null ? fm.SmokeColumnOrigin : fm.transform;
            GameObject root = Object.Instantiate(prefab, origin.position, Quaternion.identity, origin);
            IFireParticleSystem? particles = root.GetComponentInChildren<IFireParticleSystem>();
            if (particles == null)
            {
                Object.Destroy(root);
                return;
            }

            switch (particles.Orientation)
            {
                case FireParticleSystemOrientation.ZOut:
                    root.transform.forward = origin.forward;
                    break;
                case FireParticleSystemOrientation.YOut:
                    root.transform.up = origin.forward;
                    break;
            }

            held = new HeldSmokeColumn { Root = root, Particles = particles, Playing = false };
            RemoteSmokeColumns[id] = held;
        }

        held.Particles.SetAllRatios(num2);
        if (!held.Playing)
        {
            held.Particles.Play();
            held.Playing = true;
        }
    }

    public static void TryStopRemoteSmokeColumn(int flammablesManagerInstanceId)
    {
        if (!RemoteSmokeColumns.TryGetValue(flammablesManagerInstanceId, out HeldSmokeColumn? held))
            return;
        if (held.Particles != null)
        {
            held.Particles.Stop();
            held.Playing = false;
        }

        if (held.Root != null)
            Object.Destroy(held.Root);
        RemoteSmokeColumns.Remove(flammablesManagerInstanceId);
    }

    /// <summary>Clears synthetic columns when missions/session reset (optional hygiene).</summary>
    public static void ClearRemoteSmokeColumns()
    {
        foreach (KeyValuePair<int, HeldSmokeColumn> kv in RemoteSmokeColumns)
        {
            if (kv.Value.Root != null)
                Object.Destroy(kv.Value.Root);
        }

        RemoteSmokeColumns.Clear();
    }
}
