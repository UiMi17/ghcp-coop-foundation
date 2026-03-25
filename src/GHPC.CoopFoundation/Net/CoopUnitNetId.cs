using GHPC;

namespace GHPC.CoopFoundation.Net;

/// <summary>
///     Cross-client stable id for <see cref="Unit" />. Unity <see cref="UnityEngine.Object.GetInstanceID" /> is local-only.
/// </summary>
internal static class CoopUnitNetId
{
    /// <summary>FNV-1a of <see cref="Unit.UniqueName" /> or fallback to <c>gameObject.name</c>.</summary>
    public static uint FromUnit(Unit unit)
    {
        string key = !string.IsNullOrEmpty(unit.UniqueName) ? unit.UniqueName : unit.gameObject.name;
        return CoopMissionHash.Token(key);
    }
}
