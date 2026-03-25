using GHPC.CoopFoundation.Net;
using GHPC.Weaponry;
using GHPC.Weapons;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Host: after AT grenade <see cref="LiveRound" /> is initialized, send GHC <see cref="CoopCombatPacket.EventGrenadeJetVisual" /> so the client can show a ballistic ghost without local gameplay <see cref="LiveRound" />.
/// </summary>
[HarmonyPatch(typeof(AntiTankGrenadeExplosionBehaviour), "InitializeLiveRound", typeof(Grenade), typeof(LiveRound))]
internal static class PatchAtGrenadeJetVisualHostEmit
{
    [HarmonyPostfix]
    private static void Postfix(Grenade grenade, LiveRound liveRound)
    {
        if (!CoopUdpTransport.IsHost || !CoopSessionState.IsPlaying)
            return;
        HostCombatBroadcast.TrySendGrenadeJetVisual(
            grenade,
            liveRound,
            CoopUdpTransport.CombatReplicationLogImpactFx);
    }
}
