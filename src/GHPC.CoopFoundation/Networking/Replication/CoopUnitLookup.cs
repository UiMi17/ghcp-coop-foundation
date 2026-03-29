using GHPC;

namespace GHPC.CoopFoundation.Networking.Replication;

internal static class CoopUnitLookup
{
    public static Unit? TryFindByNetId(uint netId) => CoopUnitWireRegistry.TryResolveUnit(netId);
}
