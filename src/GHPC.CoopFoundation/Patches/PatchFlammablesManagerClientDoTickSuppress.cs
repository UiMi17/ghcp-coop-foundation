using GHPC;
using GHPC.CoopFoundation.Net;
using GHPC.Effects;
using GHPC.Player;
using HarmonyLib;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Client + coop: skip <see cref="FlammablesManager.doTick"/> for every unit except <see cref="PlayerInput.CurrentPlayerUnit"/>.
/// Host remains full sim; peers drive scorch/column from GHC <see cref="CoopCompartmentFxReplay.TryApplyScorchAndRemoteSmokeColumn"/>.
/// </summary>
[HarmonyPatch(typeof(FlammablesManager), nameof(FlammablesManager.doTick))]
internal static class PatchFlammablesManagerClientDoTickSuppress
{
    [HarmonyPrefix]
    private static bool Prefix(FlammablesManager __instance)
    {
        if (CoopUdpTransport.IsHost)
            return true;
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.CombatReplicationEnabledPublic)
            return true;
        if (!CoopSessionState.IsPlaying)
            return true;
        Unit? u = __instance.GetComponent<Unit>();
        if (u == null)
            return true;
        PlayerInput? input = PlayerInput.Instance;
        Unit? local = input != null ? input.CurrentPlayerUnit : null;
        if (local == null)
            return true;
        return u == local;
    }
}
