using System.Collections.Generic;
using FMOD.Studio;
using GHPC.Audio;
using GHPC.Effects;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

/// <summary>
/// Client: for units whose full <see cref="FlammablesManager" /> tick is suppressed, replay FMOD Burning + Combined Flame Height from GHC snapshots (host authority).
/// </summary>
internal static class CoopRemoteFlammablesBurningAudio
{
    private sealed class Holder
    {
        public EventInstance Instance;
    }

    private static readonly Dictionary<int, Holder> ByFlammablesId = new();

    private static string _paramCachedForPath = "";
    private static PARAMETER_ID _combinedFlameHeightParamId;
    private static bool _combinedFlameHeightParamReady;

    public static void TrySync(FlammablesManager fm, in CoopCompartmentStateSnapshot state)
    {
        if (fm == null || !fm.isActiveAndEnabled)
            return;

        int id = fm.GetInstanceID();

        if (!state.FirePresent)
        {
            StopIfTracked(id);
            return;
        }

        Transform follow = fm.FireAudioTransform != null ? fm.FireAudioTransform : fm.transform;
        string ev = string.IsNullOrEmpty(fm.BurningEvent)
            ? "event:/Vehicles/Shared/Fire/Burning"
            : fm.BurningEvent;

        float combinedFlameHeight = state.CombinedFlameHeightPct / 100f * 5f;
        if (combinedFlameHeight < 0.15f)
            combinedFlameHeight = Mathf.Max(combinedFlameHeight, 2f);
        EnsureCombinedFlameParam(ev);

        if (!ByFlammablesId.TryGetValue(id, out Holder? h))
        {
            h = new Holder();
            ByFlammablesId[id] = h;
            h.Instance = StartWithOptionalFlame(ev, follow, combinedFlameHeight);
            return;
        }

        if (!h.Instance.isValid())
        {
            h.Instance = StartWithOptionalFlame(ev, follow, combinedFlameHeight);
            return;
        }

        if (_combinedFlameHeightParamReady)
            h.Instance.setParameterByID(_combinedFlameHeightParamId, combinedFlameHeight);
    }

    private static EventInstance StartWithOptionalFlame(string ev, Transform follow, float combinedFlameHeight)
    {
        if (_combinedFlameHeightParamReady)
            return FmodGenericAudioManager.StartEvent(ev, follow, null, false, (_combinedFlameHeightParamId, combinedFlameHeight));
        return FmodGenericAudioManager.StartEvent(ev, follow, null, false);
    }

    private static void EnsureCombinedFlameParam(string eventPath)
    {
        if (_combinedFlameHeightParamReady && _paramCachedForPath == eventPath)
            return;
        _combinedFlameHeightParamId = default;
        _combinedFlameHeightParamReady = FmodGenericAudioManager.FindParameterID(
            eventPath,
            "Combined Flame Height",
            ref _combinedFlameHeightParamId);
        _paramCachedForPath = eventPath;
    }

    public static void StopIfTracked(int flammablesManagerInstanceId)
    {
        if (!ByFlammablesId.TryGetValue(flammablesManagerInstanceId, out Holder? h))
            return;
        FmodGenericAudioManager.StopEvent(ref h.Instance);
        ByFlammablesId.Remove(flammablesManagerInstanceId);
    }

    public static void ClearAll()
    {
        foreach (Holder h in ByFlammablesId.Values)
            FmodGenericAudioManager.StopEvent(ref h.Instance);
        ByFlammablesId.Clear();
        _paramCachedForPath = "";
        _combinedFlameHeightParamReady = false;
    }
}
