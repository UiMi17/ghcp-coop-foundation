using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.CoopFoundation;
using GHPC.CoopFoundation.Net;
using GHPC.UI;
using HarmonyLib;
using MelonLoader;

namespace GHPC.CoopFoundation.Patches;

/// <summary>Block client from picking a different briefing than the host after handshake (host authority).</summary>
[HarmonyPatch(typeof(MissionBriefMenu), nameof(MissionBriefMenu.LoadMissionBriefing), typeof(string))]
internal static class PatchMissionBriefMenuClientGateString
{
    [HarmonyPrefix]
    private static bool Prefix(string sceneMapKey)
    {
        if (CoopLobbyMissionUiSync.ShouldSkipClientBriefingGate)
            return true;
        if (!CoopUdpTransport.IsClient || !CoopNetSession.HandshakeOk)
            return true;
        string auth = CoopNetSession.AuthoritativeHostBriefingSceneKey;
        if (string.IsNullOrEmpty(auth))
            return true;
        if (string.Equals(sceneMapKey, auth, StringComparison.Ordinal))
            return true;
        MelonLogger.Warning("[CoopNet][Lobby] Client briefing change blocked (host authority).");
        return false;
    }
}

[HarmonyPatch]
internal static class PatchMissionBriefMenuClientGateSceneKey
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo m in typeof(MissionBriefMenu).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (m.Name != nameof(MissionBriefMenu.LoadMissionBriefing))
                continue;
            ParameterInfo[] p = m.GetParameters();
            if (p.Length != 1 || p[0].ParameterType.Name != "SceneMissionKey")
                continue;
            yield return m;
        }
    }

    [HarmonyPrefix]
    private static bool Prefix(object __0)
    {
        if (CoopLobbyMissionUiSync.ShouldSkipClientBriefingGate)
            return true;
        if (!CoopUdpTransport.IsClient || !CoopNetSession.HandshakeOk)
            return true;
        string auth = CoopNetSession.AuthoritativeHostBriefingSceneKey;
        if (string.IsNullOrEmpty(auth))
            return true;
        try
        {
            object sceneMissionKey = __0;
            Type kt = sceneMissionKey.GetType();
            string? tk = kt.GetProperty("TheaterKey")?.GetValue(sceneMissionKey) as string;
            string? mk = kt.GetProperty("MissionKey")?.GetValue(sceneMissionKey) as string;
            if (string.IsNullOrEmpty(tk) || string.IsNullOrEmpty(mk))
                return true;
            string wire = $"{tk},{mk}";
            if (string.Equals(wire, auth, StringComparison.Ordinal))
                return true;
        }
        catch
        {
            return true;
        }

        MelonLogger.Warning("[CoopNet][Lobby] Client briefing change blocked (host authority).");
        return false;
    }
}
