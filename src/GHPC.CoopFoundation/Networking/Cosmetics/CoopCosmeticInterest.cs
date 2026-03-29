using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Cosmetics;

/// <summary>Host: skip cosmetic UDP when no peer is "near" the effect (saves bandwidth).</summary>
internal static class CoopCosmeticInterest
{
    public static bool ShouldEmitToPeer(Vector3 worldPosition)
    {
        float maxM = CoopUdpTransport.CosmeticInterestMaxDistanceMeters;
        if (maxM <= 0f)
            return true;
        float maxSq = maxM * maxM;
        UnityEngine.Camera? cam = UnityEngine.Camera.main;
        if (cam != null && (worldPosition - cam.transform.position).sqrMagnitude <= maxSq)
            return true;
        if (CoopRemoteState.HasData && (worldPosition - CoopRemoteState.RemotePosition).sqrMagnitude <= maxSq)
            return true;
        return false;
    }
}
