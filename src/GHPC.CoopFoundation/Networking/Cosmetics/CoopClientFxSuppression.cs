using GHPC;
using GHPC.Player;
using GHPC.Weaponry;
using GHPC.Weapons;
using UnityEngine;
#pragma warning disable CS0612 // VehicleInfo obsolete in game assembly; still used for WorldScript / OldLiveRound.

namespace GHPC.CoopFoundation.Networking.Cosmetics;

/// <summary>
/// Client-only: skip redundant impact/cosmetic paths for rounds not fired by the local player so host GHC cosmetics stay authoritative.
/// </summary>
internal static class CoopClientFxSuppression
{
    private static int _doUpdateDepth;

    public static bool SuppressRemoteShooterCosmeticsEnabled { get; set; } = true;

    /// <summary>Current <see cref="LiveRound"/> while <see cref="GHPC.Weapons.LiveRound.DoUpdate"/> runs (host + client).</summary>
    public static LiveRound? CurrentLiveRoundInDoUpdate { get; private set; }

    public static void EnterLiveRoundDoUpdate(LiveRound round)
    {
        _doUpdateDepth++;
        if (_doUpdateDepth == 1)
            CurrentLiveRoundInDoUpdate = round;
    }

    public static void ExitLiveRoundDoUpdate()
    {
        _doUpdateDepth--;
        if (_doUpdateDepth <= 0)
        {
            _doUpdateDepth = 0;
            CurrentLiveRoundInDoUpdate = null;
        }
    }

    public static bool ShouldSuppressLiveRoundCosmetics(LiveRound? round)
    {
        if (round == null)
            return false;
        if (CoopUdpTransport.IsHost)
            return false;
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.CombatReplicationEnabledPublic)
            return false;
        if (!SuppressRemoteShooterCosmeticsEnabled)
            return false;
        if (!CoopSessionState.IsPlaying)
            return false;
        Unit? shooter = round.Shooter;
        if (shooter == null)
            return false;
        PlayerInput? input = PlayerInput.Instance;
        Unit? local = input != null ? input.CurrentPlayerUnit : null;
        return local != null && shooter != local;
    }

    public static bool ShouldSuppressSimpleRoundCosmetics(SimpleRound? round)
    {
        if (round == null)
            return false;
        if (CoopUdpTransport.IsHost)
            return false;
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.CombatReplicationEnabledPublic)
            return false;
        if (!SuppressRemoteShooterCosmeticsEnabled)
            return false;
        if (!CoopSessionState.IsPlaying)
            return false;
        Unit? shooter = CoopSimpleRoundShooter.GetShooter(round);
        if (shooter == null)
            return false;
        PlayerInput? input = PlayerInput.Instance;
        Unit? local = input != null ? input.CurrentPlayerUnit : null;
        return local != null && shooter != local;
    }

    /// <summary>
    /// <see cref="OldLiveRound"/> detonation prefabs run in private <c>doImpactEffect</c> (not PEM / <see cref="LiveRound.DoUpdate"/> context).
    /// </summary>
    public static bool ShouldSuppressOldLiveRoundImpactFx(OldLiveRound? olr)
    {
        if (olr == null)
            return false;
        if (CoopUdpTransport.IsHost)
            return false;
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.CombatReplicationEnabledPublic)
            return false;
        if (!SuppressRemoteShooterCosmeticsEnabled)
            return false;
        if (!CoopSessionState.IsPlaying)
            return false;
        VehicleInfo? shooter = olr.Shooter;
        if (shooter == null)
            return false;
        VehicleInfo? local = WorldScript.PlayerVehicle;
        return local != null && shooter != local;
    }

    /// <summary>
    /// <see cref="BasicGrenadeExplosionBehaviour.Explode"/> spawns effect prefab + one-shot audio only.
    /// </summary>
    public static bool ShouldSuppressGrenadeExplosionFx(Grenade? grenade)
    {
        if (grenade == null)
            return false;
        if (CoopUdpTransport.IsHost)
            return false;
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.CombatReplicationEnabledPublic)
            return false;
        if (!SuppressRemoteShooterCosmeticsEnabled)
            return false;
        if (!CoopSessionState.IsPlaying)
            return false;
        Unit? owner = grenade.Owner;
        if (owner == null)
            return false;
        PlayerInput? input = PlayerInput.Instance;
        Unit? local = input != null ? input.CurrentPlayerUnit : null;
        return local != null && owner != local;
    }
}

#pragma warning restore CS0612
