using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 统一整理观察者面板的位置、样式和显隐开关，避免研究员 UI 在不同分辨率下互相重叠。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-9000)]
public sealed class ObserverPanelLayoutController : MonoBehaviour
{
    [Header("面板引用")]
    [Tooltip("观察者 UI 所在的根画布；未配置时使用当前物体或自动查找 ResearcherCanvas。")]
    [SerializeField] private RectTransform canvasRoot;
    [Tooltip("左侧实验控制面板，会自动查找 ControlsPanel。")]
    [SerializeField] private RectTransform controlsPanel;
    [Tooltip("右侧状态面板根节点；未配置时会自动创建 ObserverStatusPanel。")]
    [SerializeField] private RectTransform statusPanel;
    [Tooltip("研究员状态文本，会自动查找 ResearcherStatusText。")]
    [SerializeField] private TMP_Text statusText;
    [Tooltip("观察者面板使用的 TMP 字体资产，需支持中文字符。")]
    [SerializeField] private TMP_FontAsset observerFont;
    [Tooltip("任务信息面板，会自动查找 TaskPanel。")]
    [SerializeField] private RectTransform taskPanel;
    [Tooltip("物理风险面板，会自动查找 RiskDebugPanel。")]
    [SerializeField] private RectTransform riskPanel;
    [Tooltip("导览调试面板，会自动查找 ResearcherDebugPanel。")]
    [SerializeField] private RectTransform debugPanel;

    [Header("布局尺寸")]
    [Tooltip("屏幕边距，单位：像素。")]
    [SerializeField, Min(0f)] private float sideMargin = 24f;
    [Tooltip("面板之间的间距，单位：像素。")]
    [SerializeField, Min(0f)] private float panelGap = 12f;
    [Tooltip("左侧控制面板宽度，单位：像素。")]
    [SerializeField, Min(280f)] private float controlsWidth = 520f;
    [Tooltip("右侧信息栏宽度，单位：像素。")]
    [SerializeField, Min(320f)] private float rightColumnWidth = 540f;
    [Tooltip("面板顶部标题栏高度，单位：像素。")]
    [SerializeField, Min(28f)] private float headerHeight = 38f;
    [Tooltip("顶部显隐按钮栏高度，单位：像素。")]
    [SerializeField, Min(36f)] private float toggleBarHeight = 46f;
    [Tooltip("状态面板高度，单位：像素。")]
    [SerializeField, Min(120f)] private float statusHeight = 300f;
    [Tooltip("任务面板高度，单位：像素。")]
    [SerializeField, Min(96f)] private float taskHeight = 132f;
    [Tooltip("风险面板高度，单位：像素。")]
    [SerializeField, Min(140f)] private float riskHeight = 270f;
    [Tooltip("调试面板高度，单位：像素。")]
    [SerializeField, Min(140f)] private float debugHeight = 260f;

    [Header("默认显隐")]
    [Tooltip("默认显示左侧实验控制面板。")]
    [SerializeField] private bool controlsVisible = true;
    [Tooltip("默认显示状态面板。")]
    [SerializeField] private bool statusVisible = true;
    [Tooltip("默认显示任务面板。")]
    [SerializeField] private bool taskVisible = true;
    [Tooltip("默认显示风险面板。")]
    [SerializeField] private bool riskVisible = true;
    [Tooltip("默认显示导览调试面板。")]
    [SerializeField] private bool debugVisible = true;

    private RectTransform rightColumn;
    private RectTransform toggleBar;

    private enum ObserverPanelId
    {
        Controls,
        Status,
        Task,
        Risk,
        Debug
    }

    private void Awake()
    {
        BuildLayout();
    }

    private void OnEnable()
    {
        BuildLayout();
    }

    /// <summary>
    /// 重新整理观察者面板，可被编辑器按钮或其他脚本主动调用。
    /// </summary>
    public void BuildLayout()
    {
        ResolveReferences();
        if (canvasRoot == null)
        {
            return;
        }

        EnsureStatusPanel();
        EnsureRightColumn();
        EnsureToggleBar();
        ReparentPanels();
        ApplyPanelFrames();
        ApplyPanelVisibility();
        RebuildToggleBar();
        LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRoot);
    }

    /// <summary>
    /// 切换指定观察者面板的显示状态。
    /// </summary>
    public void TogglePanel(string panelName)
    {
        if (string.IsNullOrWhiteSpace(panelName))
        {
            return;
        }

        switch (panelName.Trim().ToLowerInvariant())
        {
            case "controls":
                controlsVisible = !controlsVisible;
                break;
            case "status":
                statusVisible = !statusVisible;
                break;
            case "task":
                taskVisible = !taskVisible;
                break;
            case "risk":
                riskVisible = !riskVisible;
                break;
            case "debug":
                debugVisible = !debugVisible;
                break;
        }

        ApplyPanelVisibility();
        UpdateToggleVisuals();
    }

    private void ResolveReferences()
    {
        if (canvasRoot == null)
        {
            var researcherCanvas = GameObject.Find("ResearcherCanvas");
            if (researcherCanvas != null)
            {
                canvasRoot = researcherCanvas.GetComponent<RectTransform>();
            }
        }

        if (canvasRoot == null)
        {
            canvasRoot = transform as RectTransform;
        }

        controlsPanel = controlsPanel != null ? controlsPanel : FindRect("ControlsPanel");
        statusText = statusText != null ? statusText : FindText("ResearcherStatusText");
        taskPanel = taskPanel != null ? taskPanel : FindRect("TaskPanel");
        riskPanel = riskPanel != null ? riskPanel : FindRect("RiskDebugPanel");
        debugPanel = debugPanel != null ? debugPanel : FindRect("ResearcherDebugPanel");
    }

    private void EnsureStatusPanel()
    {
        if (statusPanel == null)
        {
            statusPanel = FindRect("ObserverStatusPanel");
        }

        if (statusPanel == null)
        {
            var panelObject = new GameObject("ObserverStatusPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup), typeof(LayoutElement));
            panelObject.layer = canvasRoot.gameObject.layer;
            statusPanel = panelObject.GetComponent<RectTransform>();
            statusPanel.SetParent(canvasRoot, false);
        }

        if (statusText != null && statusText.transform.parent != statusPanel)
        {
            statusText.rectTransform.SetParent(statusPanel, false);
        }
    }

    private void EnsureRightColumn()
    {
        var effectiveRightWidth = GetEffectiveRightColumnWidth();
        rightColumn = FindDirectChild("ObserverRightColumn");
        if (rightColumn == null)
        {
            var columnObject = new GameObject("ObserverRightColumn", typeof(RectTransform), typeof(VerticalLayoutGroup));
            columnObject.layer = canvasRoot.gameObject.layer;
            rightColumn = columnObject.GetComponent<RectTransform>();
            rightColumn.SetParent(canvasRoot, false);
        }

        rightColumn.anchorMin = new Vector2(1f, 0f);
        rightColumn.anchorMax = new Vector2(1f, 1f);
        rightColumn.pivot = new Vector2(1f, 1f);
        rightColumn.anchoredPosition = new Vector2(-sideMargin, -sideMargin - toggleBarHeight - panelGap);
        rightColumn.sizeDelta = new Vector2(effectiveRightWidth, -(sideMargin * 2f + toggleBarHeight + panelGap));

        var layout = rightColumn.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = panelGap;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
    }

    private void EnsureToggleBar()
    {
        var effectiveRightWidth = GetEffectiveRightColumnWidth();
        toggleBar = FindDirectChild("ObserverPanelToggleBar");
        if (toggleBar == null)
        {
            var barObject = new GameObject("ObserverPanelToggleBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(HorizontalLayoutGroup));
            barObject.layer = canvasRoot.gameObject.layer;
            toggleBar = barObject.GetComponent<RectTransform>();
            toggleBar.SetParent(canvasRoot, false);
        }

        toggleBar.anchorMin = new Vector2(1f, 1f);
        toggleBar.anchorMax = new Vector2(1f, 1f);
        toggleBar.pivot = new Vector2(1f, 1f);
        toggleBar.anchoredPosition = new Vector2(-sideMargin, -sideMargin);
        toggleBar.sizeDelta = new Vector2(effectiveRightWidth, toggleBarHeight);

        var image = toggleBar.GetComponent<Image>();
        image.color = new Color(0.04f, 0.055f, 0.07f, 0.88f);

        var layout = toggleBar.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 6, 6);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
    }

    private void ReparentPanels()
    {
        SetupControlsPanel();
        SetParent(statusPanel, rightColumn);
        SetParent(taskPanel, rightColumn);
        SetParent(riskPanel, rightColumn);
        SetParent(debugPanel, rightColumn);

        if (statusPanel != null) statusPanel.SetSiblingIndex(0);
        if (taskPanel != null) taskPanel.SetSiblingIndex(1);
        if (riskPanel != null) riskPanel.SetSiblingIndex(2);
        if (debugPanel != null) debugPanel.SetSiblingIndex(3);
        toggleBar.SetAsLastSibling();
    }

    private void SetupControlsPanel()
    {
        if (controlsPanel == null)
        {
            return;
        }

        SetParent(controlsPanel, canvasRoot);
        controlsPanel.anchorMin = new Vector2(0f, 0f);
        controlsPanel.anchorMax = new Vector2(0f, 1f);
        controlsPanel.pivot = new Vector2(0f, 1f);
        controlsPanel.anchoredPosition = new Vector2(sideMargin, -sideMargin);
        controlsPanel.sizeDelta = new Vector2(GetEffectiveControlsWidth(), -sideMargin * 2f);
        StylePanel(controlsPanel, "实验控制", 0f, true);
    }

    private void ApplyPanelFrames()
    {
        StylePanel(statusPanel, "系统状态", statusHeight, false);
        StylePanel(taskPanel, "任务信息", taskHeight, false);
        StylePanel(riskPanel, "物理风险", riskHeight, false);
        StylePanel(debugPanel, "导览调试", debugHeight, false);
        StyleText(statusText != null ? statusText.rectTransform : null, 18f);
        StylePanelText(taskPanel, 20f);
        StylePanelText(riskPanel, 18f);
        StylePanelText(debugPanel, 18f);
    }

    private void StylePanel(RectTransform panel, string title, float preferredHeight, bool isControls)
    {
        if (panel == null)
        {
            return;
        }

        var image = panel.GetComponent<Image>();
        if (image == null)
        {
            image = panel.gameObject.AddComponent<Image>();
        }

        image.color = isControls
            ? new Color(0.035f, 0.05f, 0.065f, 0.94f)
            : new Color(0.055f, 0.065f, 0.075f, 0.9f);

        if (panel.GetComponent<CanvasGroup>() == null)
        {
            panel.gameObject.AddComponent<CanvasGroup>();
        }

        var layout = panel.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = panel.gameObject.AddComponent<LayoutElement>();
        }

        if (!isControls)
        {
            layout.preferredHeight = preferredHeight;
            layout.minHeight = Mathf.Min(96f, preferredHeight);
        }

        if (!isControls)
        {
            EnsureHeader(panel, title);
        }
    }

    private void EnsureHeader(RectTransform panel, string title)
    {
        var header = panel.Find("ObserverPanelHeader") as RectTransform;
        if (header == null)
        {
            var headerObject = new GameObject("ObserverPanelHeader", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            headerObject.layer = panel.gameObject.layer;
            header = headerObject.GetComponent<RectTransform>();
            header.SetParent(panel, false);
        }

        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = new Vector2(1f, 1f);
        header.pivot = new Vector2(0.5f, 1f);
        header.anchoredPosition = Vector2.zero;
        header.sizeDelta = new Vector2(0f, headerHeight);
        header.SetAsFirstSibling();

        var headerImage = header.GetComponent<Image>();
        headerImage.color = new Color(0.085f, 0.11f, 0.13f, 0.96f);

        var label = header.Find("Title") as RectTransform;
        if (label == null)
        {
            var labelObject = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.layer = panel.gameObject.layer;
            label = labelObject.GetComponent<RectTransform>();
            label.SetParent(header, false);
        }

        label.anchorMin = Vector2.zero;
        label.anchorMax = Vector2.one;
        label.offsetMin = new Vector2(16f, 0f);
        label.offsetMax = new Vector2(-16f, 0f);

        var text = label.GetComponent<TMP_Text>();
        text.text = title;
        text.font = ResolveObserverFont();
        text.fontSize = 18f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.color = new Color(0.93f, 0.96f, 1f, 1f);
        text.raycastTarget = false;
    }

    private void StylePanelText(RectTransform panel, float fontSize)
    {
        if (panel == null)
        {
            return;
        }

        for (var i = 0; i < panel.childCount; i++)
        {
            var child = panel.GetChild(i);
            if (child.name == "ObserverPanelHeader")
            {
                continue;
            }

            StyleText(child as RectTransform, fontSize);
        }
    }

    private void StyleText(RectTransform rect, float fontSize)
    {
        if (rect == null)
        {
            return;
        }

        var text = rect.GetComponent<TMP_Text>();
        if (text == null)
        {
            return;
        }

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(18f, 14f);
        rect.offsetMax = new Vector2(-18f, -headerHeight - 12f);
        text.fontSize = fontSize;
        text.font = ResolveObserverFont();
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.color = new Color(0.93f, 0.96f, 1f, 1f);
    }

    private void ApplyPanelVisibility()
    {
        SetVisible(controlsPanel, controlsVisible);
        SetVisible(statusPanel, statusVisible);
        SetVisible(taskPanel, taskVisible);
        SetVisible(riskPanel, riskVisible);
        SetVisible(debugPanel, debugVisible);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rightColumn);
    }

    private void RebuildToggleBar()
    {
        EnsureToggleButton(ObserverPanelId.Controls, "控制");
        EnsureToggleButton(ObserverPanelId.Status, "状态");
        EnsureToggleButton(ObserverPanelId.Task, "任务");
        EnsureToggleButton(ObserverPanelId.Risk, "风险");
        EnsureToggleButton(ObserverPanelId.Debug, "调试");
        UpdateToggleVisuals();
    }

    private void EnsureToggleButton(ObserverPanelId panelId, string label)
    {
        var buttonName = "Toggle_" + panelId;
        var buttonRect = toggleBar.Find(buttonName) as RectTransform;
        if (buttonRect == null)
        {
            var buttonObject = new GameObject(buttonName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonObject.layer = toggleBar.gameObject.layer;
            buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.SetParent(toggleBar, false);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.layer = buttonObject.layer;
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(buttonRect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);
        }

        var layout = buttonRect.GetComponent<LayoutElement>();
        layout.minWidth = 56f;
        layout.preferredWidth = 92f;

        var button = buttonRect.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => TogglePanel(panelId.ToString()));

        var text = buttonRect.GetComponentInChildren<TMP_Text>();
        text.text = label;
        text.font = ResolveObserverFont();
        text.fontSize = 17f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
    }

    private void UpdateToggleVisuals()
    {
        UpdateToggleVisual(ObserverPanelId.Controls, controlsVisible);
        UpdateToggleVisual(ObserverPanelId.Status, statusVisible);
        UpdateToggleVisual(ObserverPanelId.Task, taskVisible);
        UpdateToggleVisual(ObserverPanelId.Risk, riskVisible);
        UpdateToggleVisual(ObserverPanelId.Debug, debugVisible);
    }

    private void UpdateToggleVisual(ObserverPanelId panelId, bool visible)
    {
        if (toggleBar == null)
        {
            return;
        }

        var buttonRect = toggleBar.Find("Toggle_" + panelId) as RectTransform;
        if (buttonRect == null)
        {
            return;
        }

        var image = buttonRect.GetComponent<Image>();
        image.color = visible
            ? new Color(0.12f, 0.32f, 0.46f, 0.95f)
            : new Color(0.12f, 0.13f, 0.15f, 0.85f);

        var label = buttonRect.GetComponentInChildren<TMP_Text>();
        if (label != null)
        {
            label.color = visible ? Color.white : new Color(0.72f, 0.76f, 0.8f, 1f);
        }
    }

    private void SetVisible(RectTransform panel, bool visible)
    {
        if (panel == null)
        {
            return;
        }

        var group = panel.GetComponent<CanvasGroup>();
        if (group != null)
        {
            group.alpha = visible ? 1f : 0f;
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }

        panel.gameObject.SetActive(visible);
    }

    private RectTransform FindRect(string objectName)
    {
        var found = GameObject.Find(objectName);
        return found != null ? found.GetComponent<RectTransform>() : null;
    }

    private TMP_Text FindText(string objectName)
    {
        var found = GameObject.Find(objectName);
        return found != null ? found.GetComponent<TMP_Text>() : null;
    }

    private RectTransform FindDirectChild(string objectName)
    {
        return canvasRoot != null ? canvasRoot.Find(objectName) as RectTransform : null;
    }

    private float GetEffectiveControlsWidth()
    {
        var canvasWidth = GetCanvasWidth();
        var responsiveWidth = Mathf.Max(260f, canvasWidth * 0.34f);
        return Mathf.Min(controlsWidth, responsiveWidth);
    }

    private float GetEffectiveRightColumnWidth()
    {
        var canvasWidth = GetCanvasWidth();
        var responsiveWidth = Mathf.Max(320f, canvasWidth * 0.36f);
        return Mathf.Min(rightColumnWidth, responsiveWidth);
    }

    private float GetCanvasWidth()
    {
        if (canvasRoot != null && canvasRoot.rect.width > 1f)
        {
            return canvasRoot.rect.width;
        }

        var scaler = canvasRoot != null ? canvasRoot.GetComponent<CanvasScaler>() : null;
        return scaler != null && scaler.referenceResolution.x > 1f ? scaler.referenceResolution.x : 1920f;
    }

    private TMP_FontAsset ResolveObserverFont()
    {
        if (observerFont != null)
        {
            return observerFont;
        }

        observerFont = FindFontFrom(statusText) ??
                       FindFontInChildren(controlsPanel) ??
                       FindFontInChildren(taskPanel) ??
                       FindFontInChildren(riskPanel) ??
                       FindFontInChildren(debugPanel) ??
                       TMP_Settings.defaultFontAsset;
        return observerFont;
    }

    private static TMP_FontAsset FindFontFrom(TMP_Text text)
    {
        return text != null ? text.font : null;
    }

    private static TMP_FontAsset FindFontInChildren(RectTransform root)
    {
        if (root == null)
        {
            return null;
        }

        var texts = root.GetComponentsInChildren<TMP_Text>(true);
        for (var i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null && texts[i].font != null)
            {
                return texts[i].font;
            }
        }

        return null;
    }

    private static void SetParent(RectTransform child, Transform parent)
    {
        if (child != null && parent != null && child.parent != parent)
        {
            child.SetParent(parent, false);
        }
    }
}
