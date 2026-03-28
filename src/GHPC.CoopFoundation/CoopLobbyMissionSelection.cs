namespace GHPC.CoopFoundation;

/// <summary>
///     Latest <c>MissionBriefMenu.LoadMissionBriefing(string sceneMapKey)</c> argument from any UI (Instant Action / Multiplayer clone).
///     Wire token uses <see cref="Net.CoopMissionHash.Token" />(key); in-mission coherence uses
///     <see cref="CoopSessionState.MissionSceneKey" /> from <c>MissionInitializer.MissionSceneName</c> — same key string for GHPC instant missions.
/// </summary>
internal static class CoopLobbyMissionSelection
{
    public static string LastSceneMapKey { get; private set; } = "";

    /// <summary><see cref="GHPC.Mission.Data.SceneMissionKey.MissionKey" /> segment (flex / <c>AllFlexOverrides</c> dictionary key).</summary>
    public static string LastFlexMissionName { get; private set; } = "";

    public static void RecordSceneMapKey(string? sceneMapKey)
    {
        if (string.IsNullOrEmpty(sceneMapKey))
            return;
        LastSceneMapKey = sceneMapKey!;
        LastFlexMissionName = ExtractFlexMissionName(sceneMapKey!);
    }

    public static void RecordSceneMapKeyFromParts(string? theaterKey, string? missionKey)
    {
        if (string.IsNullOrEmpty(theaterKey) || string.IsNullOrEmpty(missionKey))
            return;
        LastSceneMapKey = $"{theaterKey},{missionKey}";
        LastFlexMissionName = missionKey!;
    }

    public static void Clear()
    {
        LastSceneMapKey = "";
        LastFlexMissionName = "";
    }

    private static string ExtractFlexMissionName(string sceneMapKey)
    {
        int i = sceneMapKey.IndexOf(',');
        return i >= 0 && i < sceneMapKey.Length - 1 ? sceneMapKey.Substring(i + 1) : "";
    }
}
