using GHPC.UI;
using HarmonyLib;
using UnityEngine;

namespace GHPC.CoopFoundation.Patches;

/// <summary>
/// Cloned Multiplayer submenu is appended last under the menu canvas, so it draws above modal menus
/// (e.g. mission Customize / MissionConfig). When a modal opens, bring it to the front of its parent.
/// </summary>
[HarmonyPatch(typeof(BuildMenuController), nameof(BuildMenuController.ToggleModalMenuObject))]
internal static class PatchBuildMenuControllerModalZOrder
{
    [HarmonyPostfix]
    private static void Postfix(GameObject menuObject)
    {
        if (menuObject == null || !menuObject.activeInHierarchy)
            return;

        menuObject.transform.SetAsLastSibling();
    }
}
