using System;
using System.Collections;
using System.Collections.Generic;
using GHPC.CoopFoundation.UI;
using GHPC.UI;
using GHPC.UI.Menu;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GHPC.CoopFoundation.Patches;

[HarmonyPatch(typeof(BuildMenuController))]
internal static class PatchBuildMenuControllerMultiplayerButton
{
    private const string MultiplayerButtonObjectName = "CoopMultiplayerButton";
    private const string MultiplayerSubMenuObjectName = "CoopMultiplayerSubMenu";
    private const string MultiplayerLabel = "Multiplayer";
    private const float MinReasonableStep = 10f;
    private static readonly bool VerboseUiProbeLogs = false;
    private static readonly bool LogMissionUiState = true;
    private static readonly Dictionary<int, GameObject> SubMenuByController = new();
    private static readonly Dictionary<int, CoopLobbyMenuController> LobbyControllerByControllerId = new();

    internal static void TickLobbyControllers()
    {
        if (LobbyControllerByControllerId.Count == 0)
            return;
        List<int>? stale = null;
        foreach (KeyValuePair<int, CoopLobbyMenuController> kv in LobbyControllerByControllerId)
        {
            int id = kv.Key;
            CoopLobbyMenuController controller = kv.Value;
            if (GetLiveControllerById(id) == null)
            {
                stale ??= new List<int>();
                stale.Add(id);
                continue;
            }

            controller.Tick();
        }

        if (stale == null)
            return;
        for (int i = 0; i < stale.Count; i++)
        {
            int id = stale[i];
            LobbyControllerByControllerId.Remove(id);
            SubMenuByController.Remove(id);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    private static void StartPostfix(BuildMenuController __instance)
    {
        try
        {
            AddMultiplayerButtonIfNeeded(__instance);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopUI] Failed to add Multiplayer button: {ex.Message}");
        }
    }

    private static void AddMultiplayerButtonIfNeeded(BuildMenuController controller)
    {
        if (controller == null)
            return;
        int controllerId = controller.GetInstanceID();

        Button? templateButton = AccessTools.Field(typeof(BuildMenuController), "_settingsButton")
            ?.GetValue(controller) as Button;
        if (templateButton == null)
            return;

        Transform parent = templateButton.transform.parent;
        if (parent == null)
            return;

        Transform existing = parent.Find(MultiplayerButtonObjectName);
        if (existing != null)
            return;

        List<MenuButtonInfo> items = CollectMainMenuButtons(parent);
        if (!TryFindInsertionSlot(items, out int campaignIndex, out int settingsIndex))
            return;

        float step = Mathf.Abs(items[campaignIndex].Rect.anchoredPosition.y - items[settingsIndex].Rect.anchoredPosition.y);
        if (step < MinReasonableStep)
            step = EstimateStep(items, campaignIndex, settingsIndex);

        GameObject clone = UnityEngine.Object.Instantiate(templateButton.gameObject, parent);
        clone.name = MultiplayerButtonObjectName;
        clone.SetActive(true);

        Button? button = clone.GetComponent<Button>();
        if (button == null)
            return;

        // Important: cloned Button keeps persistent listeners from the prefab/template.
        // Replace event object to clear both persistent and runtime listeners.
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => OnMultiplayerClicked(controllerId));

        TMP_Text? templateTmpLabel = templateButton.GetComponentInChildren<TMP_Text>(true);
        TMP_Text? tmpLabel = clone.GetComponentInChildren<TMP_Text>(true);
        if (tmpLabel != null)
        {
            if (templateTmpLabel != null)
            {
                tmpLabel.fontSize = templateTmpLabel.fontSize;
                tmpLabel.fontSizeMin = templateTmpLabel.fontSizeMin;
                tmpLabel.fontSizeMax = templateTmpLabel.fontSizeMax;
                tmpLabel.enableAutoSizing = templateTmpLabel.enableAutoSizing;
                tmpLabel.characterSpacing = templateTmpLabel.characterSpacing;
                tmpLabel.wordSpacing = templateTmpLabel.wordSpacing;
                tmpLabel.fontStyle = templateTmpLabel.fontStyle;
            }
            tmpLabel.text = MultiplayerLabel;
        }

        Text? legacyLabel = clone.GetComponentInChildren<Text>(true);
        if (legacyLabel != null)
            legacyLabel.text = MultiplayerLabel;

        RectTransform? mpRect = clone.transform as RectTransform;
        if (mpRect != null)
        {
            Vector2 campaignPos = items[campaignIndex].Rect.anchoredPosition;
            mpRect.anchoredPosition = new Vector2(campaignPos.x, campaignPos.y - step);
        }

        ShiftButtonsBelow(items, settingsIndex, step);
        PlaceSiblingAfter(parent, clone.transform, items[campaignIndex].Transform);
        WireVerticalNavigation(items, button, campaignIndex, settingsIndex);
        ForceLayoutRefresh(parent);
        EnsureSubMenuCreated(controller, templateButton);

        MelonLogger.Msg("[CoopUI] Main menu Multiplayer button added.");
    }

    private static List<MenuButtonInfo> CollectMainMenuButtons(Transform parent)
    {
        List<MenuButtonInfo> items = new();
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform t = parent.GetChild(i);
            if (t.name == MultiplayerButtonObjectName)
                continue;

            Button? b = t.GetComponent<Button>();
            RectTransform? r = t as RectTransform;
            if (b == null || r == null)
                continue;

            string? label = TryGetButtonLabel(t.gameObject);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            items.Add(new MenuButtonInfo(t, b, r, label!.Trim().ToLowerInvariant()));
        }

        items.Sort((a, b) => b.Rect.anchoredPosition.y.CompareTo(a.Rect.anchoredPosition.y));
        return items;
    }

    private static bool TryFindInsertionSlot(List<MenuButtonInfo> items, out int campaignIndex, out int settingsIndex)
    {
        campaignIndex = -1;
        settingsIndex = -1;
        for (int i = 0; i < items.Count; i++)
        {
            string normalized = items[i].NormalizedLabel;
            if (normalized == "campaign")
                campaignIndex = i;
            else if (normalized == "settings" || normalized == "options")
                settingsIndex = i;
        }

        if (campaignIndex >= 0 && settingsIndex >= 0 && campaignIndex < settingsIndex)
            return true;
        return false;
    }

    private static float EstimateStep(List<MenuButtonInfo> items, int settingsIndex, int helpIndex)
    {
        if (settingsIndex >= 0 && helpIndex >= 0 && settingsIndex < helpIndex)
        {
            float raw = Mathf.Abs(items[settingsIndex].Rect.anchoredPosition.y - items[helpIndex].Rect.anchoredPosition.y);
            if (raw >= MinReasonableStep)
                return raw;
        }

        float sum = 0f;
        int count = 0;
        for (int i = 0; i < items.Count - 1; i++)
        {
            float d = Mathf.Abs(items[i].Rect.anchoredPosition.y - items[i + 1].Rect.anchoredPosition.y);
            if (d >= MinReasonableStep)
            {
                sum += d;
                count++;
            }
        }

        return count > 0 ? sum / count : 36f;
    }

    private static void ShiftButtonsBelow(List<MenuButtonInfo> items, int fromIndexInclusive, float step)
    {
        for (int i = fromIndexInclusive; i < items.Count; i++)
        {
            RectTransform rect = items[i].Rect;
            rect.anchoredPosition -= new Vector2(0f, step);
        }
    }

    private static void PlaceSiblingAfter(Transform parent, Transform inserted, Transform after)
    {
        int idx = after.GetSiblingIndex();
        inserted.SetSiblingIndex(Mathf.Clamp(idx + 1, 0, parent.childCount - 1));
    }

    private static void WireVerticalNavigation(List<MenuButtonInfo> items, Button multiplayer, int aboveIndex, int belowIndex)
    {
        Button above = items[aboveIndex].Button;
        Button below = items[belowIndex].Button;

        Navigation aboveNav = above.navigation;
        aboveNav.mode = Navigation.Mode.Explicit;
        aboveNav.selectOnDown = multiplayer;
        above.navigation = aboveNav;

        Navigation mpNav = multiplayer.navigation;
        mpNav.mode = Navigation.Mode.Explicit;
        mpNav.selectOnUp = above;
        mpNav.selectOnDown = below;
        multiplayer.navigation = mpNav;

        Navigation belowNav = below.navigation;
        belowNav.mode = Navigation.Mode.Explicit;
        belowNav.selectOnUp = multiplayer;
        below.navigation = belowNav;
    }

    private static string? TryGetButtonLabel(GameObject go)
    {
        TMP_Text? tmp = go.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
            return tmp.text;

        Text? legacy = go.GetComponentInChildren<Text>(true);
        if (legacy != null && !string.IsNullOrWhiteSpace(legacy.text))
            return legacy.text;

        return null;
    }

    private static void ForceLayoutRefresh(Transform parent)
    {
        RectTransform? rect = parent as RectTransform;
        if (rect == null)
            return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
    }

    private static void EnsureSubMenuCreated(BuildMenuController controller, Button templateButton)
    {
        int id = controller.GetInstanceID();
        if (SubMenuByController.TryGetValue(id, out GameObject? existing) && existing != null)
            return;

        GameObject[]? subMenus = AccessTools.Field(typeof(BuildMenuController), "_subMenus")
            ?.GetValue(controller) as GameObject[];
        if (subMenus == null || subMenus.Length == 0 || subMenus[0] == null)
            return;

        GameObject templateSubMenu = FindInstantActionTemplate(subMenus) ?? subMenus[0];
        MissionMenuSetup? templateProbe = templateSubMenu.GetComponentInChildren<MissionMenuSetup>(true);
        MelonLogger.Msg(
            $"[CoopUI][MissionUI] Template for multiplayer clone: name={templateSubMenu.name} MissionMenuSetup={(templateProbe != null)}");
        if (templateProbe == null)
        {
            MelonLogger.Warning(
                "[CoopUI][MissionUI] Template has no MissionMenuSetup (some scenes only wire MissionBriefMenu). Will try injecting from a donor MissionMenuSetup at runtime.");
        }

        Transform panelParent = templateSubMenu.transform.parent;
        if (panelParent == null)
            return;

        // Clone native submenu object to keep animator/canvas-group and built-in submenu behavior.
        GameObject panel = UnityEngine.Object.Instantiate(templateSubMenu, panelParent);
        panel.name = MultiplayerSubMenuObjectName;
        RectTransform panelRect = (RectTransform)panel.transform;
        panelRect.localScale = Vector3.one;

        // Deactivate before injecting MissionMenuSetup so its Awake runs after serialized fields are wired.
        panel.SetActive(false);
        TryInjectMissionMenuSetupIfMissing(panel);
        panel.SetActive(true);

        ConfigureInstantActionTemplateForMultiplayer(panel, controller, id);

        MissionMenuSetup? cloneSetup = panel.GetComponentInChildren<MissionMenuSetup>(true);
        MissionMenuSetup? templateSetup = templateSubMenu.GetComponentInChildren<MissionMenuSetup>(true);
        if (templateSetup != null && cloneSetup == null)
        {
            MelonLogger.Warning(
                $"[CoopUI][MissionUI] Instantiate lost MissionMenuSetup (template has component). template={templateSubMenu.name} clone={panel.name}");
        }

        panel.SetActive(false);
        SubMenuByController[id] = panel;

        GameObject[] expanded = new GameObject[subMenus.Length + 1];
        Array.Copy(subMenus, expanded, subMenus.Length);
        expanded[expanded.Length - 1] = panel;
        AccessTools.Field(typeof(BuildMenuController), "_subMenus")?.SetValue(controller, expanded);
    }

    /// <summary>
    /// Prefer the submenu that includes <see cref="MissionMenuSetup" /> (theater dropdown + mission list).
    /// Some scenes expose a panel that only has <see cref="MissionBriefMenu" />; cloning that omits list population.
    /// </summary>
    private static GameObject? FindInstantActionTemplate(GameObject[] subMenus)
    {
        for (int i = 0; i < subMenus.Length; i++)
        {
            GameObject g = subMenus[i];
            if (g == null)
                continue;
            if (g.GetComponentInChildren<MissionMenuSetup>(true) != null)
                return g;
        }

        for (int i = 0; i < subMenus.Length; i++)
        {
            GameObject g = subMenus[i];
            if (g == null)
                continue;
            if (g.GetComponentInChildren<MissionBriefMenu>(true) != null)
                return g;
        }

        return null;
    }

    /// <summary>
    /// Some menu scenes (e.g. MainMenu2_Scene) use <c>InstantActionMenuPanel</c> with <see cref="MissionBriefMenu" /> but without
    /// <see cref="MissionMenuSetup" /> on the prefab — theater options may be baked into the TMP_Dropdown, while the mission list
    /// is only filled by <see cref="MissionMenuSetup.Awake" />. Copy serialized references from any other MissionMenuSetup in memory.
    /// </summary>
    private static void TryInjectMissionMenuSetupIfMissing(GameObject panel)
    {
        if (panel.GetComponentInChildren<MissionMenuSetup>(true) != null)
            return;

        MissionMenuSetup? donor = PickDonorMissionMenuSetup();
        if (donor == null)
        {
            MelonLogger.Warning(
                "[CoopUI][MissionUI] MissionMenuSetup inject skipped: no donor (scene has no MissionMenuSetup, Resources.FindObjectsOfTypeAll empty).");
            return;
        }

        TMP_Dropdown? theaterDd = FindTheaterDropdown(panel);
        GameObject? missionContent = FindMissionSelectContent(panel);
        MissionBriefMenu? brief = panel.GetComponentInChildren<MissionBriefMenu>(true);
        if (theaterDd == null || missionContent == null || brief == null)
        {
            MelonLogger.Warning(
                $"[CoopUI][MissionUI] MissionMenuSetup inject skipped: missing UI refs (theaterDd={theaterDd != null} missionContent={missionContent != null} brief={brief != null}).");
            return;
        }

        theaterDd.ClearOptions();

        MissionMenuSetup setup = panel.AddComponent<MissionMenuSetup>();
        AccessTools.Field(typeof(MissionMenuSetup), "_missionButtonPrefab")
            .SetValue(setup, AccessTools.Field(typeof(MissionMenuSetup), "_missionButtonPrefab").GetValue(donor));
        AccessTools.Field(typeof(MissionMenuSetup), "_missionButtonSpacerPrefab")
            .SetValue(setup, AccessTools.Field(typeof(MissionMenuSetup), "_missionButtonSpacerPrefab").GetValue(donor));
        AccessTools.Field(typeof(MissionMenuSetup), "_allMissionsScriptable")
            .SetValue(setup, AccessTools.Field(typeof(MissionMenuSetup), "_allMissionsScriptable").GetValue(donor));
        AccessTools.Field(typeof(MissionMenuSetup), "_lockedMissionScenes")
            .SetValue(setup, AccessTools.Field(typeof(MissionMenuSetup), "_lockedMissionScenes").GetValue(donor));

        AccessTools.Field(typeof(MissionMenuSetup), "_theaterDropdown").SetValue(setup, theaterDd);
        AccessTools.Field(typeof(MissionMenuSetup), "_missionSelectContent").SetValue(setup, missionContent);
        AccessTools.Field(typeof(MissionMenuSetup), "_missionBrief").SetValue(setup, brief);

        MelonLogger.Msg(
            $"[CoopUI][MissionUI] Injected MissionMenuSetup from donor '{donor.gameObject.name}' (template lacked component).");
    }

    private static MissionMenuSetup? PickDonorMissionMenuSetup()
    {
        MissionMenuSetup[] scene = UnityEngine.Object.FindObjectsOfType<MissionMenuSetup>(true);
        for (int i = 0; i < scene.Length; i++)
        {
            MissionMenuSetup? m = scene[i];
            if (m != null && IsPlausibleMissionMenuDonor(m))
                return m;
        }

        MissionMenuSetup[] all = Resources.FindObjectsOfTypeAll<MissionMenuSetup>();
        for (int i = 0; i < all.Length; i++)
        {
            MissionMenuSetup? m = all[i];
            if (m != null && IsPlausibleMissionMenuDonor(m))
                return m;
        }

        return null;
    }

    private static bool IsPlausibleMissionMenuDonor(MissionMenuSetup m)
    {
        if (m.gameObject.name.IndexOf(MultiplayerSubMenuObjectName, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        object? prefab = AccessTools.Field(typeof(MissionMenuSetup), "_missionButtonPrefab").GetValue(m);
        return prefab != null;
    }

    private static TMP_Dropdown? FindTheaterDropdown(GameObject panel)
    {
        Transform? t = panel.transform.Find("Theatre/Dropdown");
        if (t != null)
        {
            TMP_Dropdown? dd = t.GetComponent<TMP_Dropdown>();
            if (dd != null)
                return dd;
        }

        TMP_Dropdown[] dropdowns = panel.GetComponentsInChildren<TMP_Dropdown>(true);
        for (int i = 0; i < dropdowns.Length; i++)
        {
            TMP_Dropdown d = dropdowns[i];
            string path = GetTransformPath(d.transform);
            if (path.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            return d;
        }

        return null;
    }

    private static GameObject? FindMissionSelectContent(GameObject panel)
    {
        Transform? t = panel.transform.Find("Mission Scroller/Viewport/Content");
        return t != null ? t.gameObject : null;
    }

    private static void ConfigureInstantActionTemplateForMultiplayer(GameObject panel, BuildMenuController controller, int controllerId)
    {
        if (VerboseUiProbeLogs)
            LogMultiplayerPanelDiagnostics(panel, "before-config");

        // Keep native Instant Action layout active so mission list/preview can populate.
        if (LogMissionUiState)
            LogMissionSubmenuState(panel, "configure");
        EnsureMissionSelectorVisible(panel);

        Button? startButton = FindButtonByLabel(panel, "START");
        if (startButton != null)
        {
            startButton.enabled = true;
            startButton.interactable = true;
            SetButtonLabel(startButton.gameObject, "HOST SESSION");
        }

        Button? customizeButton = FindButtonByLabel(panel, "CUSTOMIZE");
        Button? joinButton = null;
        if (customizeButton != null)
        {
            customizeButton.enabled = true;
            customizeButton.interactable = true;
            SetButtonLabel(customizeButton.gameObject, "CUSTOMIZE");
            // Keep vanilla Customize listeners (mission config). Add a sibling for co-op join.
            GameObject joinGo = UnityEngine.Object.Instantiate(customizeButton.gameObject, customizeButton.transform.parent);
            joinGo.name = "CoopJoinSessionButton";
            joinGo.transform.SetSiblingIndex(customizeButton.transform.GetSiblingIndex() + 1);
            SetButtonLabel(joinGo, "JOIN SESSION");
            joinButton = joinGo.GetComponent<Button>();
            if (joinButton != null)
            {
                joinButton.onClick = new Button.ButtonClickedEvent();
                joinButton.enabled = true;
                joinButton.interactable = true;
            }

            RectTransform? optionsParent = customizeButton.transform.parent as RectTransform;
            if (optionsParent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(optionsParent);
        }

        SetFirstTextEqual(panel, "Scenario", "MULTIPLAYER");

        TMP_Text? mapLobbyText = FindMapLobbyText(panel);
        CoopLobbyMenuController lobbyController = new(controllerId, panel, startButton, joinButton, mapLobbyText);
        lobbyController.Bind();
        LobbyControllerByControllerId[controllerId] = lobbyController;

        if (VerboseUiProbeLogs)
            LogMultiplayerPanelDiagnostics(panel, "after-config");
    }

    /// <summary>Large map preview text (normally "MAP DATA UNAVAILABLE") — repurposed for co-op lobby status.</summary>
    private static TMP_Text? FindMapLobbyText(GameObject root)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            if (t == null)
                continue;
            string text = t.text ?? string.Empty;
            if (text.IndexOf("MAP DATA", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                return t;
        }

        Transform? mapRoot = root.transform.Find("Map");
        if (mapRoot == null)
            return null;
        TMP_Text[] mapTmps = mapRoot.GetComponentsInChildren<TMP_Text>(true);
        TMP_Text? best = null;
        for (int i = 0; i < mapTmps.Length; i++)
        {
            TMP_Text t = mapTmps[i];
            if (t == null)
                continue;
            string path = GetTransformPath(t.transform);
            if (path.IndexOf("Mission Title", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (best == null || t.fontSize > best.fontSize)
                best = t;
        }

        return best;
    }

    private static Button? FindButtonByLabel(GameObject root, string expectedLabelUpper)
    {
        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            string? label = TryGetButtonLabel(b.gameObject);
            if (string.IsNullOrWhiteSpace(label))
                continue;
            if (string.Equals(label!.Trim(), expectedLabelUpper, StringComparison.OrdinalIgnoreCase))
                return b;
        }
        return null;
    }

    private static void SetButtonLabel(GameObject buttonGo, string newLabel)
    {
        TMP_Text? tmp = buttonGo.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = newLabel;
        }
        Text? legacy = buttonGo.GetComponentInChildren<Text>(true);
        if (legacy != null)
            legacy.text = newLabel;
    }

    private static void SetFirstTextEqual(GameObject root, string oldText, string newText)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            if (string.Equals(t.text?.Trim(), oldText, StringComparison.OrdinalIgnoreCase))
            {
                t.text = newText;
                return;
            }
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text t = texts[i];
            if (string.Equals(t.text?.Trim(), oldText, StringComparison.OrdinalIgnoreCase))
            {
                t.text = newText;
                return;
            }
        }
    }

    private static void HideTextContaining(GameObject root, string token)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            string value = t.text ?? string.Empty;
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                t.gameObject.SetActive(false);
            }
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text t = texts[i];
            string value = t.text ?? string.Empty;
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                t.gameObject.SetActive(false);
            }
        }
    }

    private static void DisablePanelContainingText(GameObject root, string token)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            string value = t.text ?? string.Empty;
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                DisableNearestPanel(t.gameObject, root.transform);
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text t = texts[i];
            string value = t.text ?? string.Empty;
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                DisableNearestPanel(t.gameObject, root.transform);
        }
    }

    private static void DisableNearestPanel(GameObject source, Transform root)
    {
        Transform? best = null;
        float bestArea = -1f;
        Transform? current = source.transform;
        while (current != null && current != root)
        {
            if (current.GetComponent<Image>() != null && current != source.transform)
            {
                RectTransform? rt = current as RectTransform;
                float area = 0f;
                if (rt != null)
                    area = Mathf.Abs(rt.rect.width * rt.rect.height);

                // Prefer big right/top visual panels (MAP DATA box), not tiny label backgrounds.
                bool rightTopBias = false;
                if (rt != null)
                    rightTopBias = rt.anchoredPosition.x > 0f && rt.anchoredPosition.y > 0f;
                if (rightTopBias)
                    area += 1000000f;

                if (area > bestArea)
                {
                    bestArea = area;
                    best = current;
                }
            }
            current = current.parent;
        }

        if (best != null)
        {
            best.gameObject.SetActive(false);
        }
    }

    private static string GetTransformPath(Transform t)
    {
        List<string> parts = new();
        Transform? cur = t;
        while (cur != null)
        {
            parts.Add(cur.name);
            cur = cur.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void HideTopQuadrantPanels(GameObject root, Button? hostButton, Button? joinButton)
    {
        Image[] images = root.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < images.Length; i++)
        {
            Image img = images[i];
            if (img == null)
                continue;

            RectTransform? rect = img.transform as RectTransform;
            if (rect == null)
                continue;

            // Top big IA boxes are large and positioned in upper half.
            bool isLarge = rect.rect.width >= 300f && rect.rect.height >= 140f;
            bool isTop = rect.anchoredPosition.y > 0f;
            if (!isLarge || !isTop)
                continue;

            if (hostButton != null && img.transform.IsChildOf(hostButton.transform))
                continue;
            if (joinButton != null && img.transform.IsChildOf(joinButton.transform))
                continue;

            img.gameObject.SetActive(false);
        }
    }

    private static void SuppressTopRightBoxVisuals(GameObject root)
    {
        Graphic[] graphics = root.GetComponentsInChildren<Graphic>(true);
        Graphic? best = null;
        float bestArea = -1f;
        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic g = graphics[i];
            if (g == null)
                continue;

            RectTransform? rect = g.transform as RectTransform;
            if (rect == null)
                continue;

            // Target the remaining large right-side visual box.
            bool isLarge = rect.rect.width >= 300f && rect.rect.height >= 140f;
            bool isRightSide = rect.anchoredPosition.x > 0f;
            bool isText = g is MaskableGraphic mg && (mg is TMP_Text || mg is Text);
            if (!isLarge || !isRightSide || isText)
                continue;

            // Keep controls/headers containers alive; only remove the dead map-data background panel.
            if (ContainsAnyText(g.transform,
                    "MULTIPLAYER",
                    "JOIN SESSION",
                    "HOST SESSION",
                    "OPTIONS",
                    "Create or join a co-op session"))
            {
                continue;
            }

            float area = Mathf.Abs(rect.rect.width * rect.rect.height);
            if (area > bestArea)
            {
                bestArea = area;
                best = g;
            }
        }

        if (best != null)
        {
            best.enabled = false;
            best.raycastTarget = false;
            MelonLogger.Msg($"[CoopUI] Suppressed top-right box visual: {GetTransformPath(best.transform)}");
        }
    }

    private static bool ContainsAnyText(Transform root, params string[] tokens)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            string value = tmps[i].text ?? string.Empty;
            for (int t = 0; t < tokens.Length; t++)
            {
                if (value.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        Text[] texts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            string value = texts[i].text ?? string.Empty;
            for (int t = 0; t < tokens.Length; t++)
            {
                if (value.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        return false;
    }

    private static void DisableNamedChild(Transform root, string childName)
    {
        Transform? child = root.Find(childName);
        if (child == null)
            return;
        child.gameObject.SetActive(false);
    }

    private static void LogMultiplayerPanelDiagnostics(GameObject panel, string stage)
    {
        MelonLogger.Msg($"[CoopUI][Probe] ==== Multiplayer panel probe {stage} ====");
        MelonLogger.Msg($"[CoopUI][Probe] panelPath={GetTransformPath(panel.transform)} active={panel.activeInHierarchy}");

        Graphic[] graphics = panel.GetComponentsInChildren<Graphic>(true);
        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic g = graphics[i];
            RectTransform? rt = g.transform as RectTransform;
            string rect = rt == null
                ? "rect=n/a"
                : $"rect=({rt.rect.width:0.#}x{rt.rect.height:0.#}) pos=({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})";
            MelonLogger.Msg(
                $"[CoopUI][Probe][Graphic] type={g.GetType().Name} enabled={g.enabled} active={g.gameObject.activeInHierarchy} raycast={g.raycastTarget} {rect} path={GetTransformPath(g.transform)}");
        }

        TMP_Text[] tmps = panel.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            RectTransform? rt = t.transform as RectTransform;
            string rect = rt == null
                ? "rect=n/a"
                : $"rect=({rt.rect.width:0.#}x{rt.rect.height:0.#}) pos=({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})";
            string text = (t.text ?? string.Empty).Replace("\n", "\\n");
            MelonLogger.Msg(
                $"[CoopUI][Probe][TMP] enabled={t.enabled} active={t.gameObject.activeInHierarchy} text='{text}' {rect} path={GetTransformPath(t.transform)}");
        }

        Text[] texts = panel.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            Text t = texts[i];
            RectTransform? rt = t.transform as RectTransform;
            string rect = rt == null
                ? "rect=n/a"
                : $"rect=({rt.rect.width:0.#}x{rt.rect.height:0.#}) pos=({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#})";
            string text = (t.text ?? string.Empty).Replace("\n", "\\n");
            MelonLogger.Msg(
                $"[CoopUI][Probe][Text] enabled={t.enabled} active={t.gameObject.activeInHierarchy} text='{text}' {rect} path={GetTransformPath(t.transform)}");
        }

        Selectable[] selectables = panel.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < selectables.Length; i++)
        {
            Selectable s = selectables[i];
            MelonLogger.Msg(
                $"[CoopUI][Probe][Selectable] type={s.GetType().Name} enabled={s.enabled} interactable={s.interactable} active={s.gameObject.activeInHierarchy} path={GetTransformPath(s.transform)}");
        }
        MelonLogger.Msg($"[CoopUI][Probe] ==== End probe {stage} ====");
    }

    private static void OnMultiplayerClicked(int controllerId)
    {
        BuildMenuController? controller = GetLiveControllerById(controllerId);
        if (controller == null)
            return;

        if (!SubMenuByController.TryGetValue(controllerId, out GameObject? panel) || panel == null)
        {
            Button? template = AccessTools.Field(typeof(BuildMenuController), "_settingsButton")
                ?.GetValue(controller) as Button;
            if (template == null)
                return;
            EnsureSubMenuCreated(controller, template);
            panel = SubMenuByController.TryGetValue(controllerId, out GameObject? created) ? created : null;
            if (panel == null)
                return;
        }

        SubMenuPanel? subMenu = panel.GetComponent<SubMenuPanel>();
        if (subMenu != null)
        {
            if (LobbyControllerByControllerId.TryGetValue(controllerId, out CoopLobbyMenuController? lobby))
                lobby.Tick();

            // Match native submenu open flow (e.g., Instant Action): trigger title squash animation first.
            controller.TriggerMainMenuAnimation();
            controller.CollapseSubMenus(subMenu);
            EnsureMissionSelectorVisible(panel);
            TryKickMissionMenuSetup(panel);
            if (LogMissionUiState)
                LogMissionSubmenuState(panel, "after-open");
        }
    }

    /// <summary>
    /// Vanilla fills the mission list in <c>MissionMenuSetup.LoadTheater</c>, normally triggered from the Instant Action
    /// submenu animation via <c>DelayedLoadTheater</c>. Our cloned submenu may never receive that path reliably.
    /// </summary>
    private static void TryKickMissionMenuSetup(GameObject panel)
    {
        MissionMenuSetup? setup = ResolveMissionMenuSetup(panel);
        if (setup == null)
        {
            MelonLogger.Warning("[CoopUI][MissionUI] No MissionMenuSetup on multiplayer panel — mission list will stay empty.");
            if (LogMissionUiState)
                LogMonoBehavioursUnderPanelForDiagnostics(panel);
            return;
        }

        try
        {
            setup.DelayedLoadTheater();
            MelonLogger.Msg("[CoopUI][MissionUI] MissionMenuSetup.DelayedLoadTheater() — populate theater/mission list.");
            if (LogMissionUiState)
                setup.StartCoroutine(LogMissionUiAfterKick(setup, panel));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopUI][MissionUI] DelayedLoadTheater failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Prefer the same component instance the game uses; avoid <see cref="TMP_Dropdown" /> template ScrollRects.
    /// </summary>
    private static MissionMenuSetup? ResolveMissionMenuSetup(GameObject panel)
    {
        MissionMenuSetup? m = panel.GetComponent<MissionMenuSetup>();
        if (m != null)
            return m;
        m = panel.GetComponentInChildren<MissionMenuSetup>(true);
        return m;
    }

    private static void LogMonoBehavioursUnderPanelForDiagnostics(GameObject panel)
    {
        const int maxLines = 40;
        int n = 0;
        foreach (MonoBehaviour mb in panel.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null)
                continue;
            MelonLogger.Msg($"[CoopUI][MissionUI][diag] MB={mb.GetType().FullName} path={GetTransformPath(mb.transform)}");
            if (++n >= maxLines)
            {
                MelonLogger.Msg("[CoopUI][MissionUI][diag] … truncated");
                break;
            }
        }
    }

    /// <summary>Runs after vanilla MissionMenuSetup deferred theater load (3 end-frames + work).</summary>
    private static IEnumerator LogMissionUiAfterKick(MonoBehaviour host, GameObject panel)
    {
        for (int i = 0; i < 8; i++)
            yield return null;
        LogMissionSubmenuState(panel, "after-kick-delayed");
    }

    private static void EnsureMissionSelectorVisible(GameObject panel)
    {
        ScrollRect? scroll = FindMissionListScrollRect(panel);
        if (scroll == null)
            return;

        Transform? t = scroll.transform;
        while (t != null && t != panel.transform)
        {
            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);
            t = t.parent;
        }

        if (!scroll.gameObject.activeSelf)
            scroll.gameObject.SetActive(true);
    }

    /// <summary>
    /// First ScrollRect in hierarchy is often the TMP dropdown item template, not the mission list.
    /// </summary>
    private static ScrollRect? FindMissionListScrollRect(GameObject panel)
    {
        ScrollRect[] scrolls = panel.GetComponentsInChildren<ScrollRect>(true);
        for (int i = 0; i < scrolls.Length; i++)
        {
            ScrollRect sr = scrolls[i];
            string name = sr.gameObject.name;
            if (name.IndexOf("Dropdown", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            return sr;
        }

        return scrolls.Length > 0 ? scrolls[0] : null;
    }

    private static void LogMissionSubmenuState(GameObject panel, string stage)
    {
        try
        {
            MissionBriefMenu? missionBrief = panel.GetComponentInChildren<MissionBriefMenu>(true);
            string missionBriefType = missionBrief?.GetType().FullName ?? "null";
            bool missionBriefEnabled = missionBrief != null && missionBrief.enabled;
            MissionMenuSetup? missionSetup = ResolveMissionMenuSetup(panel);
            TMP_Dropdown? dropdown = panel.GetComponentInChildren<TMP_Dropdown>(true);
            ScrollRect[] scrolls = panel.GetComponentsInChildren<ScrollRect>(true);
            int optionCount = dropdown?.options?.Count ?? -1;
            ScrollRect? missionScroll = FindMissionListScrollRect(panel);
            int contentChildren = missionScroll?.content?.childCount ?? -1;
            MelonLogger.Msg(
                $"[CoopUI][MissionUI] stage={stage} panelActive={panel.activeInHierarchy} missionBrief={missionBriefType} missionBriefEnabled={missionBriefEnabled} missionMenuSetup={(missionSetup != null)} dropdownActive={(dropdown != null && dropdown.gameObject.activeInHierarchy)} optionCount={optionCount} scrollRectCount={scrolls.Length} missionScrollActive={(missionScroll != null && missionScroll.gameObject.activeInHierarchy)} missionScrollContentChildren={contentChildren}");
            for (int i = 0; i < scrolls.Length && i < 4; i++)
            {
                ScrollRect sr = scrolls[i];
                int cc = sr.content != null ? sr.content.childCount : -1;
                MelonLogger.Msg(
                    $"[CoopUI][MissionUI]   scroll[{i}] active={sr.gameObject.activeInHierarchy} contentChildren={cc} path={GetTransformPath(sr.transform)}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CoopUI][MissionUI] log-failed stage={stage} err={ex.Message}");
        }
    }

    private static BuildMenuController? GetLiveControllerById(int id)
    {
        BuildMenuController[] controllers = UnityEngine.Object.FindObjectsOfType<BuildMenuController>(true);
        for (int i = 0; i < controllers.Length; i++)
        {
            BuildMenuController c = controllers[i];
            if (c != null && c.GetInstanceID() == id)
                return c;
        }
        return null;
    }

    private sealed class MenuButtonInfo
    {
        public MenuButtonInfo(Transform transform, Button button, RectTransform rect, string normalizedLabel)
        {
            Transform = transform;
            Button = button;
            Rect = rect;
            NormalizedLabel = normalizedLabel;
        }

        public Transform Transform { get; }
        public Button Button { get; }
        public RectTransform Rect { get; }
        public string NormalizedLabel { get; }
    }
}
