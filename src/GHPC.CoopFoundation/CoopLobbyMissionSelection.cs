namespace GHPC.CoopFoundation;

/// <summary>
///     Latest <c>MissionBriefMenu.LoadMissionBriefing(string sceneMapKey)</c> argument from any UI (Instant Action / Multiplayer clone).
///     Wire token uses <see cref="Net.CoopMissionHash.Token" />(key); in-mission coherence uses
///     <see cref="CoopSessionState.MissionSceneKey" /> from <c>MissionInitializer.MissionSceneName</c> — same key string for GHPC instant missions.
/// </summary>
internal static class CoopLobbyMissionSelection
{
    public static string LastSceneMapKey { get; private set; } = "";

    public static void RecordSceneMapKey(string? sceneMapKey)
    {
        if (string.IsNullOrEmpty(sceneMapKey))
            return;
        LastSceneMapKey = sceneMapKey!;
    }

    public static void Clear()
    {
        LastSceneMapKey = "";
    }
}
