using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using GHPC.CoopFoundation;
using GHPC.CoopFoundation.Net;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.UI.Menu;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GHPC.CoopFoundation.UI;

/// <summary>
/// Runtime Co-op spawn slots in Customize: same column as friendly unit rows, cloned row visuals from <see cref="MissionConfigUnitSelectionEntry" />.
/// </summary>
internal static class CoopCustomizeSlotSection
{
    private const string LogTag = "[CoopCustomize]";

    private const string RootName = "CoopCustomizeSlotSection";

    private static GameObject? _root;

    /// <summary>Coalesce duplicate <see cref="RefreshPanelData" /> postfixes in the same frame (avoids destroy→rebuild→wrong template).</summary>
    private static int _coopAttachCompletedFrame = int.MinValue;

    private static TMP_Dropdown? _hostDropdown;

    private static TMP_Dropdown? _clientDropdown;

    /// <summary>Panel that currently owns <see cref="_root" />; only that instance may <see cref="DestroyIfPresent" /> from <c>OnDisable</c>.</summary>
    private static MissionConfigMenuPanel? _coopSectionOwner;

    private static bool _suppress;

    internal static bool IsCoopSectionOwner(MissionConfigMenuPanel? panel) =>
        panel != null && _coopSectionOwner == panel;

    internal static bool HasAttachedRoot() => _root != null;

    public static void AttachIfNeeded(MissionConfigMenuPanel panel, bool missionIsFlex)
    {
        bool net = CoopUdpTransport.IsNetworkActive;
        bool host = CoopUdpTransport.IsHost;
        bool client = CoopUdpTransport.IsClient;
        MelonLogger.Msg(
            $"{LogTag} AttachIfNeeded enter panel={panel.name} path={TransformPath(panel.transform)} "
            + $"missionIsFlex={missionIsFlex} netActive={net} isHost={host} isClient={client}");

        // Must not require IsConnected: host has no peer until client joins, but Customize should still show co-op spawn rows.
        if (!missionIsFlex
            || !net
            || (!host && !client))
        {
            MelonLogger.Warning(
                $"{LogTag} AttachIfNeeded SKIP: missionIsFlex={missionIsFlex} netActive={net} isHost={host} isClient={client}");
            DestroyIfPresent();
            return;
        }

        if (_root != null
            && ReferenceEquals(_coopSectionOwner, panel)
            && Time.frameCount == _coopAttachCompletedFrame)
        {
            MelonLogger.Msg($"{LogTag} AttachIfNeeded coalesce duplicate same-frame panel={panel.name}");
            return;
        }

        DestroyIfPresent();

        Transform? friendlyColumn = ResolveColumnParent(panel);
        if (friendlyColumn == null)
        {
            friendlyColumn = panel.transform;
        }

        MelonLogger.Msg(
            $"{LogTag} columnParent={friendlyColumn.name} path={TransformPath(friendlyColumn)} "
            + $"childCount={friendlyColumn.childCount}");

        _root = new GameObject(RootName);
        _root.layer = friendlyColumn.gameObject.layer;
        _root.transform.SetParent(friendlyColumn, false);

        var rootRect = _root.AddComponent<RectTransform>();
        rootRect.localScale = Vector3.one;
        rootRect.anchorMin = new Vector2(0f, 1f);
        rootRect.anchorMax = new Vector2(1f, 1f);
        rootRect.pivot = new Vector2(0.5f, 1f);
        rootRect.anchoredPosition = Vector2.zero;
        rootRect.sizeDelta = new Vector2(0f, 0f);

        var rootV = _root.AddComponent<VerticalLayoutGroup>();
        rootV.spacing = 4f;
        rootV.padding = new RectOffset(0, 0, 4, 8);
        rootV.childAlignment = TextAnchor.UpperLeft;
        // Second Customize open: VLG with childControlHeight=true can drive CoopSlotRowVanilla rects to 0 even when
        // LayoutElement min/preferred are 100 (Latest.log: _root 246px, rows stay 0×width). Keep row heights from StabilizeCoopVanillaRowClone.
        rootV.childControlHeight = false;
        rootV.childControlWidth = true;
        rootV.childForceExpandHeight = false;
        rootV.childForceExpandWidth = true;

        var rootLe = _root.AddComponent<LayoutElement>();
        rootLe.minWidth = 0f;
        rootLe.flexibleWidth = 1f;
        // Do NOT add ContentSizeFitter here: with VerticalLayoutGroup it re-enters layout and can read child preferred
        // height as 0 on the 2nd Customize open (scroll width known, template clone rect still 0) — parent collapses to ~header height and squishes rows.

        CopyVisualDefaultsFromPanel(panel, out TMP_FontAsset? font, out float titleSize, out float bodySize, out Color labelColor);

        AddSectionHeader(_root.transform, font, titleSize, labelColor);
        // Prefer a live friendly unit row: GetComponentInChildren order can hit a prefab/template node
        // under the panel that has no TMP_Dropdown, so BuildRowFromTemplate would return null (header only).
        MissionConfigUnitSelectionEntry? rowTemplate = ResolveUnitRowTemplate(panel);
        MelonLogger.Msg(
            $"{LogTag} rowTemplate={(rowTemplate == null ? "null" : TransformPath(rowTemplate.transform))}");

        // Picks which friendly Customize *unit* row (Tank / APC / …) this peer starts in; synced over lobby snapshot.
        _hostDropdown = BuildSlotRow(
            panel,
            _root.transform,
            rowTemplate,
            "Host",
            font,
            bodySize,
            labelColor,
            isHostRow: true);
        _clientDropdown = BuildSlotRow(
            panel,
            _root.transform,
            rowTemplate,
            "Client",
            font,
            bodySize,
            labelColor,
            isHostRow: false);

        if (_hostDropdown == null || _clientDropdown == null)
        {
            LogAttachDiagnostics(panel, friendlyColumn, rowTemplate, "PARTIAL_FAIL_HOST_OR_CLIENT_ROW");
            DestroyPartialCoopRoot();
            return;
        }

        Transform? friendlyUnitParent = Traverse.Create(panel).Field<Transform>("_friendlyUnitDataParent").Value;
        if (friendlyUnitParent != null && friendlyUnitParent.parent == friendlyColumn)
            _root.transform.SetSiblingIndex(friendlyUnitParent.GetSiblingIndex());

        CoopLobbyPlayerSlots.SlotsChanged += HandleSlotsChanged;
        RefreshInteractable();
        SyncDropdownsFromState();

        ApplyCoopRootExplicitMinPreferredHeight(rootV, rootLe);
        ForceCoopSectionLayoutRefresh(friendlyColumn);

        _coopSectionOwner = panel;

        LogRootLayoutSummary("AttachIfNeeded done");
        _coopAttachCompletedFrame = Time.frameCount;
        panel.StartCoroutine(CoRebuildCoopSectionLayoutEndOfFrame(panel, friendlyColumn));
    }

    /// <summary>
    /// After first layout pass, Content/scroll may still squash co-op rows; one more rebuild next frame matches vanilla timing.
    /// </summary>
    private static IEnumerator CoRebuildCoopSectionLayoutEndOfFrame(MissionConfigMenuPanel panel, Transform scrollContent)
    {
        yield return null;
        if (_root == null || _coopSectionOwner != panel)
            yield break;
        VerticalLayoutGroup? rootV = _root.GetComponent<VerticalLayoutGroup>();
        LayoutElement? rootLe = _root.GetComponent<LayoutElement>();
        if (rootV != null && rootLe != null)
            ApplyCoopRootExplicitMinPreferredHeight(rootV, rootLe);
        ForceCoopSectionLayoutRefresh(scrollContent);
        LogRootLayoutSummary("AttachIfNeeded end-of-frame relayout");
        ApplyClientVanillaSelectLockIfNeeded(panel);
    }

    /// <summary>
    /// Fixed height for co-op block so parent scroll layout cannot collapse rows to 0 (see Latest.log 2nd open: root 46px, rows 0).
    /// </summary>
    private static bool IsCoopUnitSlotRow(Transform ch) =>
        ch.name == "CoopSlotRowVanilla"
        || ch.name == "CoopSlotRowManual"
        || ch.name == "CoopSlotRowOfficial";

    private static void ApplyCoopRootExplicitMinPreferredHeight(VerticalLayoutGroup rootV, LayoutElement rootLe)
    {
        float rowH = 100f;
        for (int i = 0; i < _root!.transform.childCount; i++)
        {
            Transform ch = _root.transform.GetChild(i);
            if (!IsCoopUnitSlotRow(ch))
                continue;
            LayoutElement? rle = ch.GetComponent<LayoutElement>();
            if (rle != null)
                rowH = Mathf.Max(rowH, rle.preferredHeight, rle.minHeight);
            if (ch.TryGetComponent<RectTransform>(out RectTransform? crt) && crt.rect.height > 4f)
                rowH = Mathf.Max(rowH, crt.rect.height);
        }

        float headerH = 26f;
        Transform? header = _root.transform.Find("CoopSectionHeader");
        if (header != null && header.TryGetComponent<LayoutElement>(out LayoutElement? hle))
            headerH = Mathf.Max(headerH, hle.preferredHeight, hle.minHeight);

        int rowCount = 0;
        for (int i = 0; i < _root.transform.childCount; i++)
        {
            if (IsCoopUnitSlotRow(_root.transform.GetChild(i)))
                rowCount++;
        }

        int gaps = Mathf.Max(0, _root.transform.childCount - 1);
        float total = rootV.padding.top + rootV.padding.bottom + headerH + rowCount * rowH + gaps * rootV.spacing;
        rootLe.minHeight = total;
        rootLe.preferredHeight = total;
        rootLe.flexibleHeight = 0f;
    }

    /// <summary>
    /// Removes failed attach (header only / one row) without touching <see cref="CoopLobbyPlayerSlots" /> subscription.
    /// </summary>
    private static void DestroyPartialCoopRoot()
    {
        if (_root != null)
        {
            MelonLogger.Warning($"{LogTag} DestroyPartialCoopRoot: removing incomplete CoopCustomizeSlotSection");
            UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        _hostDropdown = _clientDropdown = null;
        _coopSectionOwner = null;
    }

    /// <summary>
    /// One-shot snapshot when Host/Client rows fail to build (e.g. Cancel → second Customize, deferred Destroy, or early Refresh exit).
    /// </summary>
    internal static void LogAttachDiagnostics(
        MissionConfigMenuPanel panel,
        Transform? friendlyColumn,
        MissionConfigUnitSelectionEntry? rowTemplate,
        string phase)
    {
        try
        {
            MissionMetaData? mission = Traverse.Create(panel).Property<MissionMetaData>("SelectedMission").Value;
            MissionTheaterScriptable? theater = Traverse.Create(panel).Property<MissionTheaterScriptable>("SelectedTheater").Value;
            Transform? friendlyUnitParent = Traverse.Create(panel).Field<Transform>("_friendlyUnitDataParent").Value;
            int friendlyChildCount = friendlyUnitParent != null ? friendlyUnitParent.childCount : -1;
            TMP_Dropdown[] panelDds = panel.GetComponentsInChildren<TMP_Dropdown>(true);
            MissionConfigUnitSelectionEntry[] friendlyEntries =
                friendlyUnitParent != null
                    ? friendlyUnitParent.GetComponentsInChildren<MissionConfigUnitSelectionEntry>(true)
                    : Array.Empty<MissionConfigUnitSelectionEntry>();
            int friendlyEntryAlive = 0;
            for (int i = 0; i < friendlyEntries.Length; i++)
            {
                if (friendlyEntries[i] != null)
                    friendlyEntryAlive++;
            }

            MelonLogger.Error(
                $"{LogTag} DIAG [{phase}] frame={Time.frameCount} panel={panel.name} id={panel.GetInstanceID()}\n"
                + $"  SelectedMission={(mission == null ? "null" : mission.MissionName)} IsFlex={(mission != null && mission.IsFlexMission)}\n"
                + $"  SelectedTheater={(theater == null ? "null" : theater.Key)}\n"
                + $"  friendlyUnitParent children={friendlyChildCount} friendlyUnitEntries(alive)={friendlyEntryAlive} TMP_DropdownsInPanel={panelDds.Length}\n"
                + $"  columnParent={(friendlyColumn == null ? "null" : friendlyColumn.name)} columnChildren={(friendlyColumn == null ? -1 : friendlyColumn.childCount)}\n"
                + $"  rowTemplate={(rowTemplate == null ? "null" : TransformPath(rowTemplate.transform))}\n"
                + $"  hostDd={(_hostDropdown == null ? "null" : "ok")} clientDd={(_clientDropdown == null ? "null" : "ok")}\n"
                + "  Hint: if TMP_DropdownsInPanel==0 after Refresh, vanilla likely returned early (mission/theater null or !IsFlex) after ClearAllContents; "
                + "or deferred Destroy left no live unit rows when template was resolved.");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"{LogTag} DIAG [{phase}] logging failed: {ex.Message}");
        }
    }

    /// <summary>
    /// First layout pass can leave new clones at 0×0; rebuild scroll Content so VerticalLayoutGroup assigns heights.
    /// </summary>
    private static void ForceCoopSectionLayoutRefresh(Transform scrollContent)
    {
        Canvas.ForceUpdateCanvases();
        if (scrollContent is RectTransform contentRt)
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);
        if (_root != null && _root.TryGetComponent<RectTransform>(out RectTransform? rootRt))
            LayoutRebuilder.ForceRebuildLayoutImmediate(rootRt);
    }

    private static string TransformPath(Transform? t, int maxDepth = 16)
    {
        if (t == null)
            return "(null)";

        var sb = new StringBuilder();
        int n = 0;
        for (Transform? x = t; x != null && n < maxDepth; x = x.parent, n++)
        {
            if (sb.Length > 0)
                sb.Insert(0, '/');
            sb.Insert(0, x.name);
        }

        Transform? p = t;
        for (int i = 0; i < maxDepth && p != null; i++)
            p = p.parent;
        if (p != null)
            sb.Insert(0, "…/");

        return sb.ToString();
    }

    private static void LogRootLayoutSummary(string phase)
    {
        if (_root == null)
        {
            MelonLogger.Warning($"{LogTag} {phase}: _root is null");
            return;
        }

        var rt = _root.GetComponent<RectTransform>();
        MelonLogger.Msg(
            $"{LogTag} {phase}: _root activeSelf={_root.activeSelf} activeInHierarchy={_root.activeInHierarchy} "
            + $"path={TransformPath(_root.transform)} childCount={_root.transform.childCount} "
            + $"rectSize={(rt == null ? "n/a" : rt.rect.size.ToString())}");

        for (int i = 0; i < _root.transform.childCount; i++)
        {
            Transform ch = _root.transform.GetChild(i);
            var crt = ch as RectTransform;
            LayoutElement? le = ch.GetComponent<LayoutElement>();
            MelonLogger.Msg(
                $"{LogTag}   child[{i}] name={ch.name} active={ch.gameObject.activeSelf} inHierarchy={ch.gameObject.activeInHierarchy} "
                + $"rectSize={(crt == null ? "n/a" : crt.rect.size.ToString())} "
                + $"layout minH={(le == null ? "n/a" : le.minHeight.ToString())} prefH={(le == null ? "n/a" : le.preferredHeight.ToString())} "
                + $"dd={(ch.GetComponentInChildren<TMP_Dropdown>(true) != null)}");
        }

        MelonLogger.Msg(
            $"{LogTag} {phase}: hostDropdown={(_hostDropdown == null ? "NULL" : "ok")} clientDropdown={(_clientDropdown == null ? "NULL" : "ok")}");
    }

    /// <summary>
    /// Parent next to vanilla friendly rows: <c>_friendlyUnitDataParent</c> is cleared each refresh — use its sibling container.
    /// </summary>
    private static Transform? ResolveColumnParent(MissionConfigMenuPanel panel)
    {
        Transform? friendlyUnitParent = Traverse.Create(panel).Field<Transform>("_friendlyUnitDataParent").Value;
        return friendlyUnitParent != null ? friendlyUnitParent.parent : null;
    }

    /// <summary>
    /// Vanilla adds rows under <c>_friendlyUnitDataParent</c>. Pick a <b>live</b> friendly unit row (active, non-zero row rect) so
    /// <see cref="BuildRowManual" /> / fallback clone get the same chrome as "Friendly Tank". Deepest nested entries are often 0×0 templates.
    /// </summary>
    private static MissionConfigUnitSelectionEntry? ResolveUnitRowTemplate(MissionConfigMenuPanel panel)
    {
        Transform? friendlyUnitParent = Traverse.Create(panel).Field<Transform>("_friendlyUnitDataParent").Value;
        MelonLogger.Msg(
            $"{LogTag} ResolveUnitRowTemplate: _friendlyUnitParent={(friendlyUnitParent == null ? "null" : TransformPath(friendlyUnitParent))} "
            + $"childCount={(friendlyUnitParent == null ? -1 : friendlyUnitParent.childCount)}");

        if (friendlyUnitParent != null)
        {
            for (int i = 0; i < friendlyUnitParent.childCount; i++)
            {
                Transform ch = friendlyUnitParent.GetChild(i);
                MissionConfigUnitSelectionEntry? e = ch.GetComponent<MissionConfigUnitSelectionEntry>();
                TMP_Dropdown? dd = e == null ? null : e.GetComponentInChildren<TMP_Dropdown>(true);
                MelonLogger.Msg(
                    $"{LogTag}   friendlyChild[{i}] name={ch.name} hasUnitEntry={(e != null)} hasDropdown={(dd != null)}");
            }
        }

        MissionConfigUnitSelectionEntry? underFriendly = PickBestVisibleUnitEntryUnder(friendlyUnitParent, friendlyUnitParent);
        if (underFriendly == null && friendlyUnitParent != null)
            underFriendly = PickDeepestUnitEntryWithDropdown(friendlyUnitParent);
        if (underFriendly == null && friendlyUnitParent != null)
        {
            for (int i = 0; i < friendlyUnitParent.childCount; i++)
            {
                Transform ch = friendlyUnitParent.GetChild(i);
                if (ch.name.IndexOf("(Clone)", System.StringComparison.Ordinal) < 0)
                    continue;
                MissionConfigUnitSelectionEntry? e = ch.GetComponent<MissionConfigUnitSelectionEntry>();
                if (e != null && e.GetComponentInChildren<TMP_Dropdown>(true) != null)
                {
                    underFriendly = e;
                    MelonLogger.Msg($"{LogTag} ResolveUnitRowTemplate: fallback direct Clone child name={ch.name}");
                    break;
                }
            }
        }

        if (underFriendly != null && friendlyUnitParent != null)
        {
            int d = TransformDepthBelow(underFriendly.transform, friendlyUnitParent);
            MelonLogger.Msg(
                $"{LogTag} ResolveUnitRowTemplate: pick under friendly depth={d} name={underFriendly.gameObject.name} "
                + $"path={TransformPath(underFriendly.transform)}");
            return underFriendly;
        }

        if (friendlyUnitParent == null)
        {
            MissionConfigUnitSelectionEntry? underPanel = PickBestVisibleUnitEntryUnder(panel.transform, null);
            if (underPanel == null)
                underPanel = PickDeepestUnitEntryWithDropdown(panel.transform);
            if (underPanel != null)
            {
                MelonLogger.Msg(
                    $"{LogTag} ResolveUnitRowTemplate: no _friendlyUnitDataParent; panel fallback path={TransformPath(underPanel.transform)}");
                return underPanel;
            }
        }

        MelonLogger.Warning($"{LogTag} ResolveUnitRowTemplate: NO entry with TMP_Dropdown under friendly tree");
        return null;
    }

    private static int TransformDepthBelow(Transform t, Transform ancestor)
    {
        int d = 0;
        for (Transform? x = t; x != null; x = x.parent)
        {
            if (x == ancestor)
                return d;
            d++;
        }

        return -1;
    }

    /// <summary>
    /// Scene ships a static <c>Unit Settings Panel</c> under Team Config; real rows are <c>Unit Settings Panel(Clone)</c>.
    /// All can sit at the same transform depth, so we exclude the static shell and prefer clones, then shallower sibling order.
    /// </summary>
    private static bool IsStaticTeamConfigUnitShell(MissionConfigUnitSelectionEntry e, Transform teamConfigRoot)
    {
        return e.transform.parent == teamConfigRoot
            && string.Equals(e.gameObject.name, "Unit Settings Panel", System.StringComparison.Ordinal);
    }

    private static int TopLevelSiblingUnderSubtreeRoot(MissionConfigUnitSelectionEntry e, Transform subtreeRoot)
    {
        Transform t = e.transform;
        while (t.parent != null && t.parent != subtreeRoot)
            t = t.parent;
        return t.GetSiblingIndex();
    }

    private const float MinVanillaUnitRowBarHeight = 12f;

    /// <summary>
    /// Prefer a shallow, laid-out friendly row (same bar as on-screen "Friendly Tank"), not a deep 0×0 template node.
    /// </summary>
    private static MissionConfigUnitSelectionEntry? PickBestVisibleUnitEntryUnder(
        Transform? subtreeRoot,
        Transform? friendlyDataParentForShellCheck)
    {
        if (subtreeRoot == null)
            return null;

        MissionConfigUnitSelectionEntry? best = null;
        int bestDepth = int.MaxValue;
        float bestRowH = -1f;
        int bestCloneRank = -1;
        int bestSibling = int.MaxValue;

        foreach (MissionConfigUnitSelectionEntry e in subtreeRoot.GetComponentsInChildren<MissionConfigUnitSelectionEntry>(true))
        {
            if (e == null || !e.gameObject.activeInHierarchy)
                continue;
            if (e.GetComponentInChildren<TMP_Dropdown>(true) == null)
                continue;
            if (friendlyDataParentForShellCheck != null && IsStaticTeamConfigUnitShell(e, friendlyDataParentForShellCheck))
                continue;
            if (!TryGetVanillaUnitRowChrome(e, out Transform vanillaRow, out _, out _))
                continue;
            if (!vanillaRow.TryGetComponent<RectTransform>(out RectTransform? vrt) || vrt.rect.height < MinVanillaUnitRowBarHeight)
                continue;

            int d = TransformDepthBelow(e.transform, subtreeRoot);
            if (d < 0)
                continue;

            float h = vrt.rect.height;
            int cloneRank = e.gameObject.name.IndexOf("(Clone)", System.StringComparison.Ordinal) >= 0 ? 1 : 0;
            int sib = TopLevelSiblingUnderSubtreeRoot(e, subtreeRoot);

            bool better = best == null
                || d < bestDepth
                || (d == bestDepth && h > bestRowH + 0.5f)
                || (d == bestDepth && Mathf.Abs(h - bestRowH) <= 0.5f && cloneRank > bestCloneRank)
                || (d == bestDepth && Mathf.Abs(h - bestRowH) <= 0.5f && cloneRank == bestCloneRank && sib < bestSibling);

            if (better)
            {
                best = e;
                bestDepth = d;
                bestRowH = h;
                bestCloneRank = cloneRank;
                bestSibling = sib;
            }
        }

        if (best != null)
        {
            MelonLogger.Msg(
                $"{LogTag} PickBestVisibleUnitEntry: depth={bestDepth} rowBarH={bestRowH:F1} "
                + $"name={best.gameObject.name} path={TransformPath(best.transform)}");
        }

        return best;
    }

    /// <summary>Last resort when <see cref="PickBestVisibleUnitEntryUnder" /> finds nothing (e.g. layout not run yet).</summary>
    private static MissionConfigUnitSelectionEntry? PickDeepestUnitEntryWithDropdown(Transform? subtreeRoot)
    {
        if (subtreeRoot == null)
            return null;

        MissionConfigUnitSelectionEntry? best = null;
        int bestDepth = -1;
        int bestCloneRank = -1;
        int bestSibling = int.MaxValue;

        foreach (MissionConfigUnitSelectionEntry e in subtreeRoot.GetComponentsInChildren<MissionConfigUnitSelectionEntry>(true))
        {
            if (e == null)
                continue;
            if (e.GetComponentInChildren<TMP_Dropdown>(true) == null)
                continue;
            if (IsStaticTeamConfigUnitShell(e, subtreeRoot))
                continue;

            int d = TransformDepthBelow(e.transform, subtreeRoot);
            if (d < 0)
                continue;

            int cloneRank = e.gameObject.name.IndexOf("(Clone)", System.StringComparison.Ordinal) >= 0 ? 1 : 0;
            int sib = TopLevelSiblingUnderSubtreeRoot(e, subtreeRoot);

            bool better = false;
            if (best == null)
            {
                better = true;
            }
            else if (d > bestDepth)
            {
                better = true;
            }
            else if (d == bestDepth)
            {
                if (cloneRank > bestCloneRank)
                    better = true;
                else if (cloneRank == bestCloneRank && sib < bestSibling)
                    better = true;
            }

            if (better)
            {
                best = e;
                bestDepth = d;
                bestCloneRank = cloneRank;
                bestSibling = sib;
            }
        }

        return best;
    }

    private static void CopyVisualDefaultsFromPanel(
        MissionConfigMenuPanel panel,
        out TMP_FontAsset? font,
        out float titleSize,
        out float bodySize,
        out Color labelColor)
    {
        font = null;
        titleSize = 20f;
        bodySize = 16f;
        labelColor = new Color(0.95f, 0.95f, 0.95f);

        TextMeshProUGUI[] tmps = panel.GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (TextMeshProUGUI t in tmps)
        {
            if (t.font != null)
            {
                font = t.font;
                bodySize = t.fontSize > 0 ? t.fontSize : bodySize;
                labelColor = t.color;
                break;
            }
        }

        TMP_Dropdown? dd = panel.GetComponentInChildren<TMP_Dropdown>(true);
        if (dd != null && dd.captionText != null && dd.captionText.font != null)
            font ??= dd.captionText.font;
    }

    private static void AddSectionHeader(Transform parent, TMP_FontAsset? font, float fontSize, Color color)
    {
        var go = new GameObject("CoopSectionHeader");
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = "Co-op starting unit (Customize row)";
        tmp.font = font;
        tmp.fontSize = fontSize + 2f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.color = color;
        tmp.margin = new Vector4(0f, 0f, 0f, 4f);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 24f;
        le.preferredHeight = 26f;
        le.flexibleWidth = 1f;
        var headerRt = go.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(0f, le.preferredHeight);
    }

    private static void FillCoopDropdownFromFriendlyRows(MissionConfigMenuPanel panel, TMP_Dropdown dd)
    {
        dd.ClearOptions();
        UnitPrefabLookupScriptable? lookup = Traverse.Create(panel).Field<UnitPrefabLookupScriptable>("_allUnitsScriptable").Value;
        List<MissionConfigUnitSelectionEntry> rows = CoopFriendlyCustomizeRows.GetOrderedFriendlyUnitEntries(panel);
        var opts = new List<TMP_Dropdown.OptionData>();
        foreach (MissionConfigUnitSelectionEntry e in rows)
        {
            string? key = e.GetSelectedUnitNameKey();
            TextMeshProUGUI? lab = Traverse.Create(e).Field<TextMeshProUGUI>("_label").Value;
            string rowLabel = lab != null ? lab.text : "Unit";
            string detail = key ?? "?";
            if (lookup != null && key != null)
            {
                detail = lookup.GetFriendlyName(key) ?? key;
                string? fac = lookup.GetFactionAbbreviation(key);
                if (fac != null)
                    detail += " (" + fac + ")";
            }

            opts.Add(new TMP_Dropdown.OptionData($"{rowLabel}: {detail}"));
        }

        if (opts.Count == 0)
            opts.Add(new TMP_Dropdown.OptionData("(No friendly unit rows)"));
        dd.AddOptions(opts);
    }

    private static void WireCoopRowDropdown(MissionConfigMenuPanel panel, TMP_Dropdown dd, bool isHostRow)
    {
        dd.onValueChanged.AddListener(_ =>
        {
            if (_suppress)
                return;
            int n = CoopFriendlyCustomizeRows.GetOrderedFriendlyUnitEntries(panel).Count;
            if (n == 0)
                return;
            int idx = Mathf.Clamp(dd.value, 0, n - 1);
            if (dd.value != idx)
                dd.SetValueWithoutNotify(idx);
            if (isHostRow)
            {
                if (!CoopUdpTransport.IsHost)
                    return;
                CoopUdpTransport.TryApplyLocalHostPlayerSlot((byte)idx);
            }
            else
            {
                if (CoopUdpTransport.IsClient)
                    CoopUdpTransport.TrySendClientPlayerSlot((byte)idx);
                else if (CoopUdpTransport.IsHost && !CoopUdpTransport.HostHasLobbyPeer)
                    CoopUdpTransport.TryApplyLocalHostClientRowSlot((byte)idx);
            }
        });
    }

    private static TMP_Dropdown? BuildSlotRow(
        MissionConfigMenuPanel panel,
        Transform parent,
        MissionConfigUnitSelectionEntry? template,
        string rowLabel,
        TMP_FontAsset? font,
        float labelFontSize,
        Color labelColor,
        bool isHostRow)
    {
        string role = isHostRow ? "host" : "client";

        TMP_Dropdown? official = BuildRowFromOfficialUnitDisplayPrefab(
            panel,
            parent,
            rowLabel,
            font,
            labelFontSize,
            isHostRow);
        if (official != null)
        {
            MelonLogger.Msg($"{LogTag} BuildSlotRow {role}: official unit prefab OK path={TransformPath(official.transform)}");
            return official;
        }

        if (template != null
            && TryGetVanillaUnitRowChrome(template, out Transform vanillaRow, out TextMeshProUGUI? refLabel, out TMP_Dropdown? refVanillaDd)
            && refLabel != null
            && refVanillaDd != null)
        {
            // Same structure as vanilla: row Image + HLG + label LE + dropdown instance (white caption box), not a full-row Instantiate.
            TMP_Dropdown? manualFromChrome = BuildRowManual(panel, parent, rowLabel, font, labelFontSize, labelColor, isHostRow, template);
            if (manualFromChrome != null)
            {
                MelonLogger.Msg($"{LogTag} BuildSlotRow {role}: vanilla-chrome manual OK path={TransformPath(manualFromChrome.transform)}");
                return manualFromChrome;
            }

            MelonLogger.Warning($"{LogTag} BuildSlotRow {role}: manual-from-chrome failed → full-row clone fallback");
            TMP_Dropdown? clonedRow = BuildRowMatchingVanillaUnitRow(
                panel,
                parent,
                vanillaRow,
                refLabel,
                refVanillaDd,
                rowLabel,
                font,
                labelFontSize,
                isHostRow);
            if (clonedRow != null)
            {
                MelonLogger.Msg($"{LogTag} BuildSlotRow {role}: full-row clone OK path={TransformPath(clonedRow.transform)}");
                return clonedRow;
            }
        }
        else if (template != null)
        {
            MelonLogger.Warning($"{LogTag} BuildSlotRow {role}: no vanilla _label/_dropdown parent row → manual");
        }
        else
        {
            MelonLogger.Warning($"{LogTag} BuildSlotRow {role}: template is null → manual");
        }

        TMP_Dropdown? manual = BuildRowManual(panel, parent, rowLabel, font, labelFontSize, labelColor, isHostRow, template);
        if (manual == null)
            MelonLogger.Error($"{LogTag} BuildSlotRow {role}: manual FAILED (no ref TMP_Dropdown on panel?)");
        else
            MelonLogger.Msg($"{LogTag} BuildSlotRow {role}: manual OK path={TransformPath(manual.transform)}");

        return manual;
    }

    /// <summary>
    /// Vanilla unit row: one horizontal bar (Image + <see cref="HorizontalLayoutGroup" />) with <c>_label</c> and <c>_dropdown</c> as siblings.
    /// </summary>
    private static bool TryGetVanillaUnitRowChrome(
        MissionConfigUnitSelectionEntry template,
        out Transform vanillaRow,
        out TextMeshProUGUI? refLabel,
        out TMP_Dropdown? refDropdown)
    {
        vanillaRow = null!;
        refLabel = Traverse.Create(template).Field<TextMeshProUGUI>("_label").Value;
        refDropdown = Traverse.Create(template).Field<TMP_Dropdown>("_dropdown").Value;
        if (refLabel == null || refDropdown == null)
            return false;
        if (refLabel.transform.parent == null || refLabel.transform.parent != refDropdown.transform.parent)
            return false;

        vanillaRow = refLabel.transform.parent;
        return true;
    }

    private static void CopyImageForRow(Image? src, Image dst)
    {
        if (src != null)
        {
            dst.sprite = src.sprite;
            dst.type = src.type;
            dst.preserveAspect = src.preserveAspect;
            dst.color = src.color;
            dst.raycastTarget = src.raycastTarget;
            dst.maskable = src.maskable;
        }
        else
        {
            dst.color = new Color(0.22f, 0.22f, 0.24f, 1f);
            dst.raycastTarget = true;
        }
    }

    private static void CopyHorizontalLayout(HorizontalLayoutGroup? src, HorizontalLayoutGroup dst)
    {
        if (src == null)
        {
            dst.spacing = 12f;
            dst.padding = new RectOffset(10, 10, 6, 6);
            dst.childAlignment = TextAnchor.MiddleLeft;
            dst.childControlHeight = true;
            dst.childControlWidth = true;
            dst.childForceExpandHeight = false;
            dst.childForceExpandWidth = true;
            return;
        }

        dst.padding = src.padding;
        dst.spacing = src.spacing;
        dst.childAlignment = src.childAlignment;
        dst.childControlHeight = src.childControlHeight;
        dst.childControlWidth = src.childControlWidth;
        dst.childForceExpandHeight = src.childForceExpandHeight;
        dst.childForceExpandWidth = src.childForceExpandWidth;
        dst.reverseArrangement = src.reverseArrangement;
    }

    /// <summary>Match vanilla unit-row dropdown: white caption, same ColorBlock / transition as source row.</summary>
    private static void CopyTmpDropdownChromeFrom(TMP_Dropdown source, TMP_Dropdown dest)
    {
        if (source == null || dest == null)
            return;
        dest.colors = source.colors;
        dest.transition = source.transition;
        dest.spriteState = source.spriteState;
        dest.navigation = source.navigation;
        if (dest.captionText != null && source.captionText != null)
        {
            TMP_Text s = source.captionText;
            TMP_Text d = dest.captionText;
            d.font = s.font;
            d.fontSize = s.fontSize;
            d.fontStyle = s.fontStyle;
            d.color = s.color;
            d.raycastTarget = s.raycastTarget;
        }

        if (dest.itemText != null && source.itemText != null)
        {
            TMP_Text s = source.itemText;
            TMP_Text d = dest.itemText;
            d.font = s.font;
            d.fontSize = s.fontSize;
            d.fontStyle = s.fontStyle;
            d.color = s.color;
        }
    }

    private static void CopyLayoutElement(LayoutElement? src, LayoutElement dst)
    {
        if (src == null)
            return;
        dst.ignoreLayout = src.ignoreLayout;
        dst.minWidth = src.minWidth;
        dst.minHeight = src.minHeight;
        dst.preferredWidth = src.preferredWidth;
        dst.preferredHeight = src.preferredHeight;
        dst.flexibleWidth = src.flexibleWidth;
        dst.flexibleHeight = src.flexibleHeight;
        dst.layoutPriority = src.layoutPriority;
    }

    private static TMP_Dropdown? BuildRowMatchingVanillaUnitRow(
        MissionConfigMenuPanel panel,
        Transform parent,
        Transform vanillaRow,
        TextMeshProUGUI refLabel,
        TMP_Dropdown refVanillaDd,
        string rowLabel,
        TMP_FontAsset? fontOverride,
        float labelFontSizeFallback,
        bool isHostRow)
    {
        // Full row clone keeps vanilla RectTransforms + LayoutElements (label vs dropdown width split). Rebuilding
        // a new Label + dropdown instance was too narrow/tall vs real unit rows.
        if (refLabel.transform.parent != vanillaRow || refVanillaDd.transform.parent != vanillaRow)
        {
            MelonLogger.Warning($"{LogTag} BuildRowMatchingVanillaUnitRow: label/dropdown not direct children of vanilla row");
            return null;
        }

        int labelSibling = refLabel.transform.GetSiblingIndex();
        int ddSibling = refVanillaDd.transform.GetSiblingIndex();
        GameObject row = UnityEngine.Object.Instantiate(vanillaRow.gameObject, parent, worldPositionStays: false);
        row.name = "CoopSlotRowVanilla";
        row.layer = parent.gameObject.layer;
        ApplyLayerRecursively(row.transform, row.layer);

        MissionConfigUnitSelectionEntry? entry = row.GetComponent<MissionConfigUnitSelectionEntry>();
        if (entry != null)
            UnityEngine.Object.Destroy(entry);

        Transform root = row.transform;
        if (labelSibling >= root.childCount || ddSibling >= root.childCount)
        {
            MelonLogger.Error($"{LogTag} BuildRowMatchingVanillaUnitRow: sibling index out of range");
            UnityEngine.Object.Destroy(row);
            return null;
        }

        TextMeshProUGUI? lab = root.GetChild(labelSibling).GetComponent<TextMeshProUGUI>();
        TMP_Dropdown? dd = root.GetChild(ddSibling).GetComponent<TMP_Dropdown>();
        if (lab == null || dd == null)
        {
            MelonLogger.Error($"{LogTag} BuildRowMatchingVanillaUnitRow: missing TMP on clone");
            UnityEngine.Object.Destroy(row);
            return null;
        }

        lab.text = rowLabel;
        if (fontOverride != null)
            lab.font = fontOverride;

        dd.onValueChanged.RemoveAllListeners();
        FillCoopDropdownFromFriendlyRows(panel, dd);
        int rowCount = CoopFriendlyCustomizeRows.GetOrderedFriendlyUnitEntries(panel).Count;
        if (rowCount > 0)
        {
            int want = isHostRow
                ? CoopLobbyPlayerSlots.HostFriendlyUnitRowIndex
                : CoopLobbyPlayerSlots.ClientFriendlyUnitRowIndex;
            dd.SetValueWithoutNotify(Mathf.Clamp(want, 0, rowCount - 1));
        }

        dd.RefreshShownValue();
        CopyTmpDropdownChromeFrom(refVanillaDd, dd);

        WireCoopRowDropdown(panel, dd, isHostRow);
        StabilizeCoopVanillaRowClone(row, vanillaRow);
        return dd;
    }

    /// <summary>
    /// Under our <see cref="VerticalLayoutGroup" />, cloned vanilla rows can end up with rect height 0 (logs: minH=100 but rect.y=0).
    /// Manual path used <see cref="ApplyCoopRowRectDefaults" />; vanilla clone path must do the same + sync <see cref="LayoutElement" />.
    /// </summary>
    private static void StabilizeCoopVanillaRowClone(GameObject row, Transform vanillaRowTemplate)
    {
        ContentSizeFitter? rootFitter = row.GetComponent<ContentSizeFitter>();
        if (rootFitter != null)
            UnityEngine.Object.Destroy(rootFitter);

        float targetH = 44f;
        if (vanillaRowTemplate.TryGetComponent<RectTransform>(out RectTransform? tplRt))
        {
            float th = tplRt.rect.height;
            if (th > 4f)
                targetH = th;
        }

        LayoutElement? tplLe = vanillaRowTemplate.GetComponent<LayoutElement>();
        if (targetH < 10f && tplLe != null)
        {
            if (tplLe.preferredHeight > 10f)
                targetH = tplLe.preferredHeight;
            else if (tplLe.minHeight > 10f)
                targetH = tplLe.minHeight;
        }

        if (targetH < 10f)
            targetH = 100f;

        if (!row.TryGetComponent<RectTransform>(out RectTransform rowRt))
            return;
        rowRt.localScale = Vector3.one;
        rowRt.anchorMin = new Vector2(0f, 1f);
        rowRt.anchorMax = new Vector2(1f, 1f);
        rowRt.pivot = new Vector2(0.5f, 1f);
        rowRt.sizeDelta = new Vector2(0f, targetH);

        LayoutElement? le = row.GetComponent<LayoutElement>();
        if (le != null)
        {
            le.minHeight = Mathf.Max(le.minHeight, targetH);
            le.preferredHeight = Mathf.Max(le.preferredHeight, targetH);
            le.flexibleHeight = 0f;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(rowRt);
    }

    private static void ApplyLayerRecursively(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            ApplyLayerRecursively(t.GetChild(i), layer);
    }

    /// <summary>
    /// Cloned vanilla rows often report rect height 0 under our VerticalLayoutGroup; force a concrete row height.
    /// </summary>
    private static void ApplyCoopRowRectDefaults(GameObject row)
    {
        ContentSizeFitter? rootFitter = row.GetComponent<ContentSizeFitter>();
        if (rootFitter != null)
            UnityEngine.Object.Destroy(rootFitter);

        if (!row.TryGetComponent<RectTransform>(out RectTransform rowRt))
            return;
        rowRt.localScale = Vector3.one;
        rowRt.anchorMin = new Vector2(0f, 1f);
        rowRt.anchorMax = new Vector2(1f, 1f);
        rowRt.pivot = new Vector2(0.5f, 1f);
        rowRt.anchoredPosition = Vector2.zero;
        rowRt.sizeDelta = new Vector2(0f, 44f);
    }

    /// <summary>
    /// Match <see cref="MissionConfigMenuPanel.AddUnitEntry" />: instantiate serialized <c>_unitDataDisplayPrefab</c> (same hierarchy as Friendly Tank), not an isolated dropdown clone.
    /// </summary>
    private static TMP_Dropdown? BuildRowFromOfficialUnitDisplayPrefab(
        MissionConfigMenuPanel panel,
        Transform parent,
        string rowLabel,
        TMP_FontAsset? fontOverride,
        float labelFontSizeFallback,
        bool isHostRow)
    {
        GameObject? prefab = Traverse.Create(panel).Field<GameObject>("_unitDataDisplayPrefab").Value;
        if (prefab == null)
        {
            MelonLogger.Warning($"{LogTag} BuildRowFromOfficialPrefab: _unitDataDisplayPrefab is null");
            return null;
        }

        GameObject row = UnityEngine.Object.Instantiate(prefab, parent, false);
        row.name = "CoopSlotRowOfficial";
        row.layer = parent.gameObject.layer;
        ApplyLayerRecursively(row.transform, row.layer);

        MissionConfigUnitSelectionEntry? entry = row.GetComponent<MissionConfigUnitSelectionEntry>();
        if (entry == null)
        {
            MelonLogger.Error($"{LogTag} BuildRowFromOfficialPrefab: prefab missing MissionConfigUnitSelectionEntry");
            UnityEngine.Object.Destroy(row);
            return null;
        }

        TextMeshProUGUI? lab = Traverse.Create(entry).Field<TextMeshProUGUI>("_label").Value;
        TMP_Dropdown? dd = Traverse.Create(entry).Field<TMP_Dropdown>("_dropdown").Value;
        UnityEngine.Object.Destroy(entry);
        if (lab == null || dd == null)
        {
            MelonLogger.Error($"{LogTag} BuildRowFromOfficialPrefab: missing _label or _dropdown on prefab");
            UnityEngine.Object.Destroy(row);
            return null;
        }

        lab.text = rowLabel;
        if (fontOverride != null)
            lab.font = fontOverride;
        if (labelFontSizeFallback > 0.1f && lab.fontSize < 0.1f)
            lab.fontSize = labelFontSizeFallback;

        dd.onValueChanged.RemoveAllListeners();
        FillCoopDropdownFromFriendlyRows(panel, dd);
        int rowCount = CoopFriendlyCustomizeRows.GetOrderedFriendlyUnitEntries(panel).Count;
        if (rowCount > 0)
        {
            int want = isHostRow
                ? CoopLobbyPlayerSlots.HostFriendlyUnitRowIndex
                : CoopLobbyPlayerSlots.ClientFriendlyUnitRowIndex;
            dd.SetValueWithoutNotify(Mathf.Clamp(want, 0, rowCount - 1));
        }

        dd.RefreshShownValue();
        WireCoopRowDropdown(panel, dd, isHostRow);
        NormalizeCoopRowRectForScrollChild(row);
        if (row.TryGetComponent<RectTransform>(out RectTransform rowRt))
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRt);
        return dd;
    }

    /// <summary>
    /// Same top-stretch convention as other co-op rows; drop root ContentSizeFitter if present (layout re-entry issues).
    /// </summary>
    private static void NormalizeCoopRowRectForScrollChild(GameObject row)
    {
        ContentSizeFitter? rootFitter = row.GetComponent<ContentSizeFitter>();
        if (rootFitter != null)
            UnityEngine.Object.Destroy(rootFitter);

        if (!row.TryGetComponent<RectTransform>(out RectTransform rowRt))
            return;
        rowRt.localScale = Vector3.one;
        rowRt.anchorMin = new Vector2(0f, 1f);
        rowRt.anchorMax = new Vector2(1f, 1f);
        rowRt.pivot = new Vector2(0.5f, 1f);
        rowRt.anchoredPosition = Vector2.zero;

        float h = 44f;
        if (row.TryGetComponent<LayoutElement>(out LayoutElement? le))
        {
            if (le.preferredHeight > 8f)
                h = le.preferredHeight;
            else if (le.minHeight > 8f)
                h = le.minHeight;
        }

        rowRt.sizeDelta = new Vector2(0f, h);
    }

    private static void SyncManualRowHeightFromVanillaBar(GameObject row, Transform? vanillaRowBar)
    {
        if (!row.TryGetComponent<RectTransform>(out RectTransform rowRt))
            return;
        float h = -1f;
        if (vanillaRowBar != null
            && vanillaRowBar.TryGetComponent<RectTransform>(out RectTransform? vrt)
            && vrt.rect.height > 8f)
            h = vrt.rect.height;
        if (h < 8f && row.TryGetComponent<LayoutElement>(out LayoutElement? le))
        {
            if (le.preferredHeight > 8f)
                h = le.preferredHeight;
            else if (le.minHeight > 8f)
                h = le.minHeight;
        }

        if (h > 8f)
            rowRt.sizeDelta = new Vector2(rowRt.sizeDelta.x, h);
    }

    private static TMP_Dropdown? BuildRowManual(
        MissionConfigMenuPanel panel,
        Transform parent,
        string rowLabel,
        TMP_FontAsset? font,
        float labelFontSize,
        Color labelColor,
        bool isHostRow,
        MissionConfigUnitSelectionEntry? styleFromTemplate = null)
    {
        var row = new GameObject("CoopSlotRowManual");
        row.transform.SetParent(parent, false);
        row.layer = parent.gameObject.layer;

        var rowRt = row.AddComponent<RectTransform>();
        rowRt.localScale = Vector3.one;
        rowRt.anchorMin = new Vector2(0f, 1f);
        rowRt.anchorMax = new Vector2(1f, 1f);
        rowRt.pivot = new Vector2(0.5f, 1f);
        rowRt.sizeDelta = new Vector2(0f, 0f);

        Image rowBg = row.AddComponent<Image>();
        HorizontalLayoutGroup h = row.AddComponent<HorizontalLayoutGroup>();
        TextMeshProUGUI? refLab = null;
        TMP_Dropdown? vanillaDdForLayout = null;
        Transform? styledVanillaRow = null;
        if (styleFromTemplate != null
            && TryGetVanillaUnitRowChrome(styleFromTemplate, out Transform vanillaRow, out refLab, out vanillaDdForLayout))
        {
            styledVanillaRow = vanillaRow;
            CopyImageForRow(vanillaRow.GetComponent<Image>(), rowBg);
            CopyHorizontalLayout(vanillaRow.GetComponent<HorizontalLayoutGroup>(), h);
        }
        else
        {
            CopyImageForRow(null, rowBg);
            CopyHorizontalLayout(null, h);
        }

        var labGo = new GameObject("Label");
        labGo.transform.SetParent(row.transform, false);
        labGo.layer = row.layer;
        var lab = labGo.AddComponent<TextMeshProUGUI>();
        lab.text = rowLabel;
        lab.alignment = TextAlignmentOptions.Left;
        lab.raycastTarget = false;
        var labLe = labGo.AddComponent<LayoutElement>();
        if (refLab != null)
        {
            lab.font = font ?? refLab.font;
            lab.fontSize = refLab.fontSize > 0.1f ? refLab.fontSize : labelFontSize;
            lab.fontStyle = refLab.fontStyle;
            lab.color = refLab.color;
            lab.alignment = refLab.alignment;
            lab.margin = refLab.margin;
            CopyLayoutElement(refLab.GetComponent<LayoutElement>(), labLe);
        }
        else
        {
            lab.font = font;
            lab.fontSize = labelFontSize;
            lab.fontStyle = FontStyles.Normal;
            lab.color = labelColor;
            labLe.minWidth = 140f;
            labLe.preferredWidth = 160f;
            labLe.flexibleWidth = 0.35f;
        }

        TMP_Dropdown? refDdPanel = panel.GetComponentInChildren<TMP_Dropdown>(true);
        if (refDdPanel == null && vanillaDdForLayout == null)
        {
            MelonLogger.Error($"{LogTag} BuildRowManual: no TMP_Dropdown template panel={panel.name}");
            return null;
        }

        TMP_Dropdown styleSource = vanillaDdForLayout ?? refDdPanel!;
        GameObject ddGo = UnityEngine.Object.Instantiate(styleSource.gameObject, row.transform, worldPositionStays: false);
        ddGo.layer = row.layer;
        var dd = ddGo.GetComponent<TMP_Dropdown>();
        if (dd == null)
        {
            MelonLogger.Error($"{LogTag} BuildRowManual: instantiated object missing TMP_Dropdown");
            return null;
        }

        dd.onValueChanged.RemoveAllListeners();
        FillCoopDropdownFromFriendlyRows(panel, dd);
        int rowCountM = CoopFriendlyCustomizeRows.GetOrderedFriendlyUnitEntries(panel).Count;
        if (rowCountM > 0)
        {
            int wantM = isHostRow
                ? CoopLobbyPlayerSlots.HostFriendlyUnitRowIndex
                : CoopLobbyPlayerSlots.ClientFriendlyUnitRowIndex;
            dd.SetValueWithoutNotify(Mathf.Clamp(wantM, 0, rowCountM - 1));
        }

        dd.RefreshShownValue();
        CopyTmpDropdownChromeFrom(styleSource, dd);

        var ddLe = ddGo.GetComponent<LayoutElement>() ?? ddGo.AddComponent<LayoutElement>();
        if (vanillaDdForLayout != null)
            CopyLayoutElement(vanillaDdForLayout.GetComponent<LayoutElement>(), ddLe);
        if (ddLe.flexibleWidth < 0.01f)
            ddLe.flexibleWidth = 1f;
        if (ddLe.minWidth < 1f)
            ddLe.minWidth = 120f;

        WireCoopRowDropdown(panel, dd, isHostRow);

        var rowLe = row.AddComponent<LayoutElement>();
        if (styledVanillaRow != null)
            CopyLayoutElement(styledVanillaRow.GetComponent<LayoutElement>(), rowLe);

        if (rowLe.minHeight < 1f)
            rowLe.minHeight = 40f;
        if (rowLe.preferredHeight < 1f)
            rowLe.preferredHeight = 44f;
        rowLe.flexibleWidth = 1f;

        ApplyCoopRowRectDefaults(row);
        SyncManualRowHeightFromVanillaBar(row, styledVanillaRow);

        return dd;
    }

    public static void DestroyIfPresent()
    {
        if (_root != null)
            MelonLogger.Msg($"{LogTag} DestroyIfPresent: destroying _root path={TransformPath(_root.transform)}");

        CoopLobbyPlayerSlots.SlotsChanged -= HandleSlotsChanged;
        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        _hostDropdown = _clientDropdown = null;
        _coopSectionOwner = null;
    }

    private static void HandleSlotsChanged()
    {
        if (_root == null)
            return;
        SyncDropdownsFromState();
        RefreshInteractable();
    }

    private static void SyncDropdownsFromState()
    {
        if (_hostDropdown == null || _clientDropdown == null)
            return;
        _suppress = true;
        try
        {
            int hn = Mathf.Clamp(
                CoopLobbyPlayerSlots.HostFriendlyUnitRowIndex,
                0,
                Mathf.Max(0, _hostDropdown.options.Count - 1));
            int cn = Mathf.Clamp(
                CoopLobbyPlayerSlots.ClientFriendlyUnitRowIndex,
                0,
                Mathf.Max(0, _clientDropdown.options.Count - 1));
            _hostDropdown.SetValueWithoutNotify(hn);
            _hostDropdown.RefreshShownValue();
            _clientDropdown.SetValueWithoutNotify(cn);
            _clientDropdown.RefreshShownValue();
        }
        finally
        {
            _suppress = false;
        }
    }

    private static void RefreshInteractable()
    {
        bool host = CoopUdpTransport.IsHost;
        bool client = CoopUdpTransport.IsClient;
        bool hostPreClient = host && !CoopUdpTransport.HostHasLobbyPeer;

        if (_hostDropdown != null)
            _hostDropdown.interactable = host;
        if (_clientDropdown != null)
            _clientDropdown.interactable = client || hostPreClient;
    }

    /// <summary>
    /// Co-op client: cannot change vanilla Customize dropdowns (units, ammo, infantry, …); only Host/Client co-op row dropdowns stay usable.
    /// </summary>
    internal static void ApplyClientVanillaSelectLockIfNeeded(MissionConfigMenuPanel panel)
    {
        if (!CoopUdpTransport.IsNetworkActive || !CoopUdpTransport.IsClient)
            return;

        Transform? coopRoot = _root != null && ReferenceEquals(_coopSectionOwner, panel) ? _root.transform : null;

        TMP_Dropdown[] dropdowns = panel.GetComponentsInChildren<TMP_Dropdown>(true);
        for (int i = 0; i < dropdowns.Length; i++)
        {
            TMP_Dropdown? dd = dropdowns[i];
            if (dd == null)
                continue;
            if (coopRoot != null && dd.transform.IsChildOf(coopRoot))
                continue;
            dd.interactable = false;
        }

        RefreshInteractable();
    }
}
