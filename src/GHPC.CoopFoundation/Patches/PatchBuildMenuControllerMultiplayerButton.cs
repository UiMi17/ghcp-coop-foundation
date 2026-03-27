using System;
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

        Transform panelParent = templateSubMenu.transform.parent;
        if (panelParent == null)
            return;

        // Clone native submenu object to keep animator/canvas-group and built-in submenu behavior.
        GameObject panel = UnityEngine.Object.Instantiate(templateSubMenu, panelParent);
        panel.name = MultiplayerSubMenuObjectName;
        panel.SetActive(true);
        RectTransform panelRect = (RectTransform)panel.transform;
        panelRect.localScale = Vector3.one;

        ConfigureInstantActionTemplateForMultiplayer(panel, controller, id);

        panel.SetActive(false);
        SubMenuByController[id] = panel;

        GameObject[] expanded = new GameObject[subMenus.Length + 1];
        Array.Copy(subMenus, expanded, subMenus.Length);
        expanded[expanded.Length - 1] = panel;
        AccessTools.Field(typeof(BuildMenuController), "_subMenus")?.SetValue(controller, expanded);
    }

    private static GameObject? FindInstantActionTemplate(GameObject[] subMenus)
    {
        for (int i = 0; i < subMenus.Length; i++)
        {
            GameObject g = subMenus[i];
            if (g == null)
                continue;
            if (g.GetComponent("MissionBriefMenu") != null)
                return g;
        }
        return null;
    }

    private static void ConfigureInstantActionTemplateForMultiplayer(GameObject panel, BuildMenuController controller, int controllerId)
    {
        if (VerboseUiProbeLogs)
            LogMultiplayerPanelDiagnostics(panel, "before-config");

        // Keep native Instant Action layout and only remap controls/content for Multiplayer shell.
        Component? missionBrief = panel.GetComponent("MissionBriefMenu");
        if (missionBrief is Behaviour missionBehavior)
            missionBehavior.enabled = false;

        // Hide theater/mission selector at top for Multiplayer shell.
        TMP_Dropdown? topDropdown = panel.GetComponentInChildren<TMP_Dropdown>(true);
        if (topDropdown != null)
        {
            topDropdown.gameObject.SetActive(false);
            DisableNearestPanel(topDropdown.gameObject, panel.transform);
        }

        ScrollRect? missionList = panel.GetComponentInChildren<ScrollRect>(true);
        if (missionList != null)
            DisableNearestPanel(missionList.gameObject, panel.transform);

        Button? startButton = FindButtonByLabel(panel, "START");
        if (startButton != null)
        {
            startButton.onClick = new Button.ButtonClickedEvent();
            startButton.onClick.AddListener(OnHostSessionClicked);
            startButton.enabled = true;
            startButton.interactable = true;
            SetButtonLabel(startButton.gameObject, "HOST SESSION");
        }

        Button? customizeButton = FindButtonByLabel(panel, "CUSTOMIZE");
        if (customizeButton != null)
        {
            customizeButton.onClick = new Button.ButtonClickedEvent();
            customizeButton.onClick.AddListener(OnJoinSessionClicked);
            customizeButton.enabled = true;
            customizeButton.interactable = true;
            SetButtonLabel(customizeButton.gameObject, "JOIN SESSION");
        }

        // Disable mission-specific controls that should not drive gameplay in MultiplayerMenu shell.
        Selectable[] selectables = panel.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < selectables.Length; i++)
        {
            Selectable s = selectables[i];
            if (s == null)
                continue;
            if ((startButton != null && s == startButton) || (customizeButton != null && s == customizeButton))
                continue;
            s.interactable = false;
        }

        SetFirstTextEqual(panel, "Scenario", "MULTIPLAYER");
        SetFirstTextEqual(panel, "No briefing available.", "Create or join a co-op session.\nBack: ESC");
        DisableNamedChild(panel.transform, "Map");
        DisablePanelContainingText(panel, "MAP DATA");
        DisablePanelContainingText(panel, "UNAVAILABLE");
        HideTextContaining(panel, "MAP DATA");
        HideTextContaining(panel, "UNAVAILABLE");
        HideTextContaining(panel, "FACTION:");
        HideTopQuadrantPanels(panel, startButton, customizeButton);
        SuppressTopRightBoxVisuals(panel);

        TMP_Text? briefingText = FindBriefingText(panel);
        CoopLobbyMenuController lobbyController = new(controllerId, panel, startButton, customizeButton, briefingText);
        lobbyController.Bind();
        LobbyControllerByControllerId[controllerId] = lobbyController;

        if (VerboseUiProbeLogs)
            LogMultiplayerPanelDiagnostics(panel, "after-config");
    }

    private static TMP_Text? FindBriefingText(GameObject root)
    {
        TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            TMP_Text t = tmps[i];
            if (t == null)
                continue;
            string text = t.text ?? string.Empty;
            if (text.IndexOf("co-op session", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("briefing", StringComparison.OrdinalIgnoreCase) >= 0)
                return t;
        }

        return null;
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

    private static void OnHostSessionClicked() { }

    private static void OnJoinSessionClicked() { }

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
