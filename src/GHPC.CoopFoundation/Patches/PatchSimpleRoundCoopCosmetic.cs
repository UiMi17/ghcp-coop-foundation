using System;
using GHPC.CoopFoundation.Net;
using GHPC.Effects;
using GHPC.Weaponry;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Not attribute-patched: <see cref="SimpleRound"/> static ctor runs when Harmony touches the type and uses
/// <c>ConstantsAndInfoManager.Instance</c> / <c>ImpactSFXManager.Instance</c> — both must exist first.
/// Retries from scene loads and <see cref="GHPC.CoopFoundation.CoopFoundationMod.OnUpdate"/> (throttled) until applied.
/// </summary>
internal static class PatchSimpleRoundCoopCosmetic
{
    private static bool _applied;
    private static float _nextAttemptTime;
    private static float _lastFailureLogTime;

    /// <summary>
    /// <see cref="SimpleRound"/> static fields read these singletons; patching before they exist forces .cctor and NRE.
    /// </summary>
    private static bool AreSimpleRoundStaticDependenciesReady()
    {
        try
        {
            var cType = AccessTools.TypeByName("GHPC.ConstantsAndInfoManager");
            if (cType == null)
                return false;
            var cInst = AccessTools.Property(cType, "Instance")?.GetValue(null, null);
            if (cInst == null)
                return false;

            var iType = AccessTools.TypeByName("GHPC.Audio.ImpactSFXManager");
            if (iType == null)
                return false;
            var iInst = AccessTools.Property(iType, "Instance")?.GetValue(null, null);
            return iInst != null;
        }
        catch
        {
            return false;
        }
    }

    internal static void TryApply(HarmonyLib.Harmony? harmony)
    {
        if (_applied || harmony == null)
            return;
        if (Time.time < _nextAttemptTime)
            return;
        _nextAttemptTime = Time.time + 0.2f;
        if (!AreSimpleRoundStaticDependenciesReady())
            return;
        try
        {
            var simpleRound = AccessTools.TypeByName("GHPC.Weaponry.SimpleRound");
            if (simpleRound == null)
                return;

            var target = AccessTools.Method(
                simpleRound,
                "HandleImpactEffect",
                new[]
                {
                    typeof(ParticleEffectsManager.FusedStatus),
                    typeof(ParticleEffectsManager.SurfaceMaterial),
                    typeof(RaycastHit)
                });
            if (target == null)
                return;

            var prefix = AccessTools.Method(
                typeof(PatchSimpleRoundCoopCosmetic),
                nameof(Prefix),
                new[]
                {
                    typeof(SimpleRound),
                    typeof(ParticleEffectsManager.FusedStatus),
                    typeof(ParticleEffectsManager.SurfaceMaterial),
                    typeof(RaycastHit)
                });
            if (prefix == null)
                return;

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _applied = true;
            MelonLogger.Msg("[GHPC_Coop_Foundation] Deferred Harmony: SimpleRound.HandleImpactEffect (cosmetic prefix).");
        }
        catch (Exception ex)
        {
            if (Time.time - _lastFailureLogTime < 3f)
                return;
            _lastFailureLogTime = Time.time;
            MelonLogger.Warning(
                $"[GHPC_Coop_Foundation] Deferred SimpleRound cosmetic patch (will retry): {ex}");
        }
    }

    /// <summary>Client suppresses local particle; host <see cref="ParticleEffectsManager"/> postfix sends <see cref="CoopCombatPacket.EventParticleImpact"/>.</summary>
    /// <remarks>
    /// Parameter names must match the game method exactly so Harmony can bind stack args
    /// (<c>fusedStatus</c>, <c>surfaceMaterial</c>, <c>hit</c>).
    /// </remarks>
    private static bool Prefix(
        SimpleRound __instance,
        ParticleEffectsManager.FusedStatus fusedStatus,
        ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
        RaycastHit hit)
    {
        _ = fusedStatus;
        _ = surfaceMaterial;
        _ = hit;
        return !CoopClientFxSuppression.ShouldSuppressSimpleRoundCosmetics(__instance);
    }
}
