using System;
using System.Collections;
using System.Text;
using GHPC.UI;
using MelonLoader;
using UnityEngine;

namespace GHPC.CoopFoundation.Networking.Session;

/// <summary>
///     Deferred local mission load using vanilla <see cref="MissionBriefMenu.LoadMap" /> after M4 COO gate.
/// </summary>
internal static class CoopMissionLaunchBridge
{
    private const StringComparison PathCmp = StringComparison.OrdinalIgnoreCase;

    private static ulong _completedSessionId;
    private static uint _completedTransitionSeq;
    private static ulong _inFlightSessionId;
    private static uint _inFlightTransitionSeq;

    public static void Reset()
    {
        _completedSessionId = 0;
        _completedTransitionSeq = 0;
        _inFlightSessionId = 0;
        _inFlightTransitionSeq = 0;
    }

    public static void TryScheduleCoopMissionLoad(ulong sessionId, uint transitionSeq, string sceneMapKey, bool isHost)
    {
        if (sessionId == 0 || transitionSeq == 0 || string.IsNullOrEmpty(sceneMapKey))
            return;
        if (_completedSessionId == sessionId && _completedTransitionSeq == transitionSeq)
            return;
        if (_inFlightSessionId == sessionId && _inFlightTransitionSeq == transitionSeq)
            return;
        _inFlightSessionId = sessionId;
        _inFlightTransitionSeq = transitionSeq;
        MelonCoroutines.Start(RunDeferredLaunch(sessionId, transitionSeq, sceneMapKey, isHost));
    }

    private static IEnumerator RunDeferredLaunch(ulong sessionId, uint transitionSeq, string sceneMapKey, bool isHost)
    {
        yield return null;
        if (_completedSessionId == sessionId && _completedTransitionSeq == transitionSeq)
        {
            ClearInFlightIfMatches(sessionId, transitionSeq);
            yield break;
        }

        try
        {
            MissionBriefMenu? menu = FindMissionBriefMenuForLaunch();
            if (menu == null)
            {
                MelonLogger.Warning("[CoopNet][M4] mission-launch: no MissionBriefMenu found");
                yield break;
            }

            menu.LoadMissionBriefing(sceneMapKey);
            menu.LoadMap();
            _completedSessionId = sessionId;
            _completedTransitionSeq = transitionSeq;
            MelonLogger.Msg(
                $"[CoopNet][M4] mission-launch: Load host={isHost} sid={sessionId} seq={transitionSeq} keyLen={Encoding.UTF8.GetByteCount(sceneMapKey)}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopNet][M4] mission-launch failed: {ex.Message}");
        }
        finally
        {
            ClearInFlightIfMatches(sessionId, transitionSeq);
        }
    }

    private static void ClearInFlightIfMatches(ulong sessionId, uint transitionSeq)
    {
        if (_inFlightSessionId == sessionId && _inFlightTransitionSeq == transitionSeq)
        {
            _inFlightSessionId = 0;
            _inFlightTransitionSeq = 0;
        }
    }

    private static MissionBriefMenu? FindMissionBriefMenuForLaunch()
    {
        MissionBriefMenu[] all = UnityEngine.Object.FindObjectsOfType<MissionBriefMenu>(true);
        MissionBriefMenu? preferred = null;
        for (int i = 0; i < all.Length; i++)
        {
            MissionBriefMenu m = all[i];
            Transform? t = m.transform;
            while (t != null)
            {
                if (t.name.IndexOf(CoopMultiplayerSubMenuName, PathCmp) >= 0)
                {
                    preferred = m;
                    break;
                }

                t = t.parent;
            }

            if (preferred != null)
                break;
        }

        if (preferred != null)
            return preferred;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.activeInHierarchy)
                return all[i];
        }

        return all.Length > 0 ? all[0] : null;
    }

    private const string CoopMultiplayerSubMenuName = "CoopMultiplayerSubMenu";
}
