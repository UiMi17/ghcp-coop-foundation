using GHPC.Effects;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

internal static class CoopGrenadeSmokeReplay
{
    /// <summary>Approximate world-space smoke for replicated smoke grenades (prefab choice matches vehicle exit smoke where possible).</summary>
    public static void TrySpawn(Vector3 worldPos)
    {
        ParticleEffectsManager? pem = ParticleEffectsManager.Instance;
        if (pem == null)
            return;
        GameObject? prefab = pem.GetSmokePrefab(FlammableSourceType.Surface)
            ?? pem.GetSmokePrefab(FlammableSourceType.SmokeColumnTank);
        if (prefab == null)
            return;
        GameObject go = Object.Instantiate(prefab, worldPos, Quaternion.identity);
        IFireParticleSystem? sys = go.GetComponentInChildren<IFireParticleSystem>();
        if (sys != null)
        {
            sys.SetAllRatios(1f);
            sys.Play();
        }
    }
}
