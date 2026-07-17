using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 长期使用的 guidance 场景装配器，负责补齐引用、连接事件和创建可选调试 UI。
/// </summary>
public sealed class GuidanceSceneInstaller : MonoBehaviour
{
    private const string GuidanceManagerName = "GuidanceManager";
    private const string LegacyAgentClientName = "AgentClient";
    private const string GuidanceCanvasName = "GuidanceDebugCanvas";

    [Header("安装策略")]
    [Tooltip("进入 Play 后自动执行安装。正式场景建议开启，便于保证引用有效。")]
    [SerializeField] private bool installOnAwake = true;
    [Tooltip("缺少 guidance 核心组件时自动补齐。正式接线稳定后可以关闭，用于发现漏挂组件。")]
    [SerializeField] private bool createMissingCoreComponents = true;
    [Tooltip("缺少调试文本时自动创建运行时 HUD。正式 ExperimentDashboard 接管后可以关闭。")]
    [SerializeField] private bool createDebugUiIfMissing;
    [Tooltip("创建临时 Request Guidance 按钮。后续由 ExperimentDashboard 驱动请求时应关闭。")]
    [SerializeField] private bool createRequestButton;
    [Tooltip("通过 GuidanceCoordinator 执行本地任务/RDW 校验；关闭后保持旧的 AgentClient -> Executor 直连。")]
    [SerializeField] private bool routeResponsesThroughCoordinator = true;
    [Tooltip("安装时打印缺失引用 warning，方便检查场景接线。")]
    [SerializeField] private bool validateSceneOnInstall = true;

    [Header("状态来源")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private MotionDetector motionDetector;
    [SerializeField] private XRTrackingAdapter xrTrackingAdapter;

    [Header("Guidance 核心组件")]
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private NavigationTargetManager navigationTargetManager;
    [SerializeField] private SemanticTargetSelector semanticTargetSelector;
    [SerializeField] private ArrivalDetector arrivalDetector;
    [SerializeField] private LocalGuidanceValidator localGuidanceValidator;
    [SerializeField] private PauseResetController pauseResetController;
    [SerializeField] private RDWController rdwController;
    [SerializeField] private RotationGainController rotationGainController;
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private ExperimentDashboard experimentDashboard;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private NarrationPlayer narrationPlayer;
    [SerializeField] private MovementHintController movementHintController;
    [SerializeField] private GazeCueController gazeCueController;
    [SerializeField] private GuideAvatarController guideAvatarController;
    [SerializeField] private ExhibitHighlightManager exhibitHighlightManager;
    [SerializeField] private RejectionPanel rejectionPanel;
    [SerializeField] private GuidanceDebugPanel guidanceDebugPanel;
    [SerializeField] private ResearcherRuntimeConsole researcherRuntimeConsole;
    [SerializeField] private ParticipantUiController participantUiController;
    [SerializeField] private BoundaryVisualizer boundaryVisualizer;

    [Header("表现对象")]
    [SerializeField] private Transform avatarRoot;
    [SerializeField] private Transform pointingArm;
    [SerializeField] private TMP_Text narrationText;
    [SerializeField] private TMP_Text movementHintText;
    [SerializeField] private TMP_Text gazeCueText;
    [SerializeField] private TMP_Text rejectionText;
    [SerializeField] private GameObject rejectionPanelRoot;
    [SerializeField] private TMP_Text debugText;

    private bool installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallFallbackAfterSceneLoad()
    {
        if (FindObjectOfType<GuidanceSceneInstaller>() != null)
        {
            return;
        }

        var hasGuidanceDependencies = FindObjectOfType<PhysicalSpaceManager>() != null || FindObjectOfType<UserPoseTracker>() != null;
        if (!hasGuidanceDependencies)
        {
            return;
        }

        var root = FindGuidanceRoot();
        if (root == null)
        {
            return;
        }

        root.AddComponent<GuidanceSceneInstaller>();
    }

    private void Awake()
    {
        if (installOnAwake)
        {
            Install();
        }
    }

    /// <summary>
    /// 安装 guidance 执行链；可由 Awake、Inspector ContextMenu 或测试脚本调用。
    /// </summary>
    [ContextMenu("Install Guidance Scene")]
    public void Install()
    {
        if (installed)
        {
            return;
        }

        ResolveSceneReferences();
        EnsureCoreComponents();
        EnsureOptionalUi();
        ConfigureComponents();
        WireEvents();

        installed = true;

        if (validateSceneOnInstall)
        {
            ValidateSceneContract();
        }
    }

    /// <summary>
    /// 重新执行安装，供修改 Inspector 引用或切换 UI 后手动刷新。
    /// </summary>
    [ContextMenu("Reinstall Guidance Scene")]
    public void Reinstall()
    {
        installed = false;
        Install();
    }

    private static GameObject FindGuidanceRoot()
    {
        var root = GameObject.Find(GuidanceManagerName);
        if (root != null)
        {
            return root;
        }

        // 兼容旧场景：保留 AgentClient 物体名称，不再运行时改名。
        return GameObject.Find(LegacyAgentClientName);
    }

    private void ResolveSceneReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (physicalSpaceCalibration == null)
        {
            physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }

        if (motionDetector == null)
        {
            motionDetector = FindObjectOfType<MotionDetector>();
        }

        if (xrTrackingAdapter == null)
        {
            xrTrackingAdapter = FindObjectOfType<XRTrackingAdapter>();
        }


        if (boundaryVisualizer == null)
        {
            boundaryVisualizer = FindObjectOfType<BoundaryVisualizer>();
        }

        if (avatarRoot == null)
        {
            avatarRoot = FindTransform("GuideAvatar");
        }

        if (pointingArm == null)
        {
            pointingArm = FindTransform("PointingArm_Debug");
        }

        if (narrationText == null)
        {
            narrationText = FindText("GuidanceTextPanel_Text");
        }

        if (movementHintText == null)
        {
            movementHintText = FindText("MovementHintPanel_Text");
        }

        if (gazeCueText == null)
        {
            gazeCueText = FindText("GazeCuePanel_Text");
        }

        if (rejectionText == null)
        {
            rejectionText = FindText("RejectionPanel_Text");
        }

        if (debugText == null)
        {
            debugText = FindText("GuidanceDebugPanel_Text");
        }

        if (researcherRuntimeConsole == null)
        {
            researcherRuntimeConsole = FindObjectOfType<ResearcherRuntimeConsole>();
        }
    }

    private void EnsureOptionalUi()
    {
        if (!createDebugUiIfMissing)
        {
            return;
        }

        if (narrationText == null)
        {
            narrationText = CreateHudText("GuidanceTextPanel_Text", new Vector2(24f, -24f), new Vector2(620f, 118f), 20);
        }

        if (movementHintText == null)
        {
            movementHintText = CreateHudText("MovementHintPanel_Text", new Vector2(24f, -156f), new Vector2(620f, 54f), 18);
        }

        if (rejectionText == null)
        {
            rejectionText = CreateHudText("RejectionPanel_Text", new Vector2(24f, -224f), new Vector2(620f, 86f), 18);
        }

        if (debugText == null)
        {
            debugText = CreateHudText("GuidanceDebugPanel_Text", new Vector2(-454f, -24f), new Vector2(430f, 230f), 14, true);
        }

        if (createRequestButton && agentClient != null)
        {
            EnsureRequestButton(agentClient);
        }
    }

    private void EnsureCoreComponents()
    {
        agentClient = GetOrAdd(agentClient, createMissingCoreComponents);
        guidanceCoordinator = GetOrAdd(guidanceCoordinator, createMissingCoreComponents);
        taskController = GetOrAdd(taskController, createMissingCoreComponents);
        navigationTargetManager = GetOrAdd(navigationTargetManager, createMissingCoreComponents);
        semanticTargetSelector = GetOrAdd(semanticTargetSelector, createMissingCoreComponents);
        arrivalDetector = GetOrAdd(arrivalDetector, createMissingCoreComponents);
        localGuidanceValidator = GetOrAdd(localGuidanceValidator, createMissingCoreComponents);
        pauseResetController = GetOrAdd(pauseResetController, createMissingCoreComponents);
        rdwController = GetOrAdd(rdwController, createMissingCoreComponents);
        rotationGainController = GetOrAdd(rotationGainController, createMissingCoreComponents);
        riskEvaluator = GetOrAdd(riskEvaluator, createMissingCoreComponents);
        physicalSpaceCalibration = GetOrAdd(physicalSpaceCalibration, createMissingCoreComponents);
        motionDetector = GetOrAdd(motionDetector, createMissingCoreComponents);
        xrTrackingAdapter = GetOrAdd(xrTrackingAdapter, createMissingCoreComponents);
        guidanceExecutor = GetOrAdd(guidanceExecutor, createMissingCoreComponents);
        experimentLogger = GetOrAdd(experimentLogger, createMissingCoreComponents);
        experimentDashboard = GetOrAdd(experimentDashboard, createMissingCoreComponents);
        narrationPlayer = GetOrAdd(narrationPlayer, createMissingCoreComponents);
        movementHintController = GetOrAdd(movementHintController, createMissingCoreComponents);
        gazeCueController = GetOrAdd(gazeCueController, createMissingCoreComponents);
        guideAvatarController = GetOrAdd(guideAvatarController, createMissingCoreComponents);
        exhibitHighlightManager = GetOrAdd(exhibitHighlightManager, createMissingCoreComponents);
        rejectionPanel = GetOrAdd(rejectionPanel, createMissingCoreComponents);
        guidanceDebugPanel = GetOrAdd(guidanceDebugPanel, createMissingCoreComponents);
        researcherRuntimeConsole = GetOrAdd(researcherRuntimeConsole, createMissingCoreComponents);
        participantUiController = GetOrAdd(participantUiController, createMissingCoreComponents);
        boundaryVisualizer = GetOrAdd(boundaryVisualizer, createMissingCoreComponents);
    }

    private void ConfigureComponents()
    {
        var context = new GuidanceRequestContext();
        agentClient?.Configure(
            physicalSpaceManager,
            userPoseTracker,
            context,
            riskEvaluator,
            physicalSpaceCalibration,
            experimentLogger);
        physicalSpaceCalibration?.Configure(physicalSpaceManager, userPoseTracker);
        riskEvaluator?.Configure(physicalSpaceManager, userPoseTracker, physicalSpaceCalibration);
        motionDetector?.Configure(userPoseTracker);
        xrTrackingAdapter?.Configure(userPoseTracker, physicalSpaceCalibration);
        boundaryVisualizer?.Configure(physicalSpaceManager, userPoseTracker, riskEvaluator, physicalSpaceCalibration);
        navigationTargetManager?.Configure(userPoseTracker);
        navigationTargetManager?.RebuildIndex();
        semanticTargetSelector?.Configure(navigationTargetManager);
        arrivalDetector?.Configure(userPoseTracker, navigationTargetManager);
        localGuidanceValidator?.Configure(navigationTargetManager);
        if (narrationText != null) narrationPlayer?.Configure(narrationText);
        if (movementHintText != null) movementHintController?.Configure(movementHintText);
        if (gazeCueText != null) gazeCueController?.Configure(gazeCueText);
        guideAvatarController?.Configure(avatarRoot, pointingArm);
        exhibitHighlightManager?.RebuildIndex();
        if (rejectionText != null || rejectionPanelRoot != null)
            rejectionPanel?.Configure(rejectionText, rejectionPanelRoot != null ? rejectionPanelRoot : rejectionText.gameObject);
        if (debugText != null) guidanceDebugPanel?.Configure(debugText);
        guidanceExecutor?.Configure(narrationPlayer, gazeCueController, movementHintController, guideAvatarController, exhibitHighlightManager, rejectionPanel, guidanceDebugPanel);
        pauseResetController?.Configure(
            guidanceExecutor,
            rejectionPanel,
            rotationGainController,
            userPoseTracker,
            riskEvaluator);
        rdwController?.Configure(riskEvaluator, pauseResetController);
        rotationGainController?.Configure(userPoseTracker, motionDetector, riskEvaluator, physicalSpaceCalibration);
        guidanceCoordinator?.Configure(agentClient, guidanceExecutor, taskController, navigationTargetManager, semanticTargetSelector, arrivalDetector, riskEvaluator, localGuidanceValidator, pauseResetController, rotationGainController, rejectionPanel);
        experimentDashboard?.Configure(
            physicalSpaceManager, userPoseTracker, riskEvaluator, xrTrackingAdapter, taskController,
            guidanceCoordinator, agentClient, experimentLogger, arrivalDetector, rotationGainController,
            guidanceExecutor, pauseResetController, participantUiController, null, physicalSpaceCalibration);
        researcherRuntimeConsole?.Configure(
            experimentDashboard,
            experimentLogger,
            physicalSpaceManager,
            physicalSpaceCalibration,
            xrTrackingAdapter,
            taskController,
            guidanceCoordinator,
            agentClient,
            riskEvaluator,
            rotationGainController,
            pauseResetController,
            FindCanvas("ResearcherCanvas"));
    }

    private void WireEvents()
    {
        if (agentClient == null || guidanceExecutor == null)
        {
            return;
        }

        agentClient.GuidanceReceived.RemoveListener(guidanceExecutor.Apply);
        if (guidanceCoordinator != null)
        {
            agentClient.GuidanceReceived.RemoveListener(guidanceCoordinator.HandleGuidanceResponse);
        }

        if (routeResponsesThroughCoordinator && guidanceCoordinator != null)
        {
            agentClient.GuidanceReceived.AddListener(guidanceCoordinator.HandleGuidanceResponse);
        }
        else
        {
            agentClient.GuidanceReceived.AddListener(guidanceExecutor.Apply);
        }

        if (rejectionPanel != null)
        {
            agentClient.GuidanceRequestFailed.RemoveListener(rejectionPanel.Show);
            agentClient.GuidanceRequestFailed.AddListener(rejectionPanel.Show);
        }

        if (guidanceDebugPanel != null)
        {
            agentClient.GuidanceRequestFailed.RemoveListener(guidanceDebugPanel.ShowStatus);
            agentClient.GuidanceRequestFailed.AddListener(guidanceDebugPanel.ShowStatus);
            agentClient.GuidanceRequestStarted.RemoveListener(guidanceDebugPanel.ShowStatus);
            agentClient.GuidanceRequestStarted.AddListener(guidanceDebugPanel.ShowStatus);
        }

        if (createRequestButton)
        {
            EnsureRequestButton(agentClient);
        }
    }

    private T GetOrAdd<T>(T current, bool allowCreate) where T : Component
    {
        if (current != null)
        {
            return current;
        }

        var found = GetComponent<T>();
        if (found != null || !allowCreate)
        {
            return found;
        }

        return gameObject.AddComponent<T>();
    }

    private void ValidateSceneContract()
    {
        WarnIfMissing(physicalSpaceManager, "PhysicalSpaceManager");
        WarnIfMissing(userPoseTracker, "UserPoseTracker");
        WarnIfMissing(physicalSpaceCalibration, "PhysicalSpaceCalibration");
        WarnIfMissing(riskEvaluator, "PhysicalRiskEvaluator");
        WarnIfMissing(motionDetector, "MotionDetector");
        WarnIfMissing(xrTrackingAdapter, "XRTrackingAdapter");
        WarnIfMissing(agentClient, "AgentClient");
        if (routeResponsesThroughCoordinator)
        {
            WarnIfMissing(guidanceCoordinator, "GuidanceCoordinator");
            WarnIfMissing(taskController, "GuidanceTaskController");
            WarnIfMissing(navigationTargetManager, "NavigationTargetManager");
            WarnIfMissing(semanticTargetSelector, "SemanticTargetSelector");
            WarnIfMissing(arrivalDetector, "ArrivalDetector");
            WarnIfMissing(localGuidanceValidator, "LocalGuidanceValidator");
            WarnIfMissing(pauseResetController, "PauseResetController");
            WarnIfMissing(rdwController, "RDWController");
            WarnIfMissing(rotationGainController, "RotationGainController");
        }
        WarnIfMissing(guidanceExecutor, "GuidanceExecutor");
        WarnIfMissing(experimentLogger, "ExperimentLogger");
        WarnIfMissing(experimentDashboard, "ExperimentDashboard");
        WarnIfMissing(narrationPlayer, "NarrationPlayer");
        WarnIfMissing(movementHintController, "MovementHintController");
        WarnIfMissing(guideAvatarController, "GuideAvatarController");
        WarnIfMissing(exhibitHighlightManager, "ExhibitHighlightManager");
        WarnIfMissing(rejectionPanel, "RejectionPanel");
        WarnIfMissing(guidanceDebugPanel, "GuidanceDebugPanel");
        WarnIfMissing(researcherRuntimeConsole, "ResearcherRuntimeConsole");
        WarnIfMissing(participantUiController, "ParticipantUiController");
        WarnIfMissing(boundaryVisualizer, "BoundaryVisualizer");
        WarnIfMissing(narrationText, "GuidanceTextPanel_Text");
        WarnIfMissing(avatarRoot, "GuideAvatar");
        WarnIfMissing(pointingArm, "PointingArm_Debug");
    }

    private void WarnIfMissing(Object target, string label)
    {
        if (target == null)
        {
            Debug.LogWarning($"GuidanceSceneInstaller missing reference: {label}", this);
        }
    }

    private static Transform FindTransform(string objectName)
    {
        var found = GameObject.Find(objectName);
        return found != null ? found.transform : null;
    }

    private static TMP_Text FindText(string objectName)
    {
        var found = GameObject.Find(objectName);
        return found != null ? found.GetComponent<TMP_Text>() : null;
    }

    private static Canvas FindCanvas(string objectName)
    {
        var found = GameObject.Find(objectName);
        return found != null ? found.GetComponent<Canvas>() : null;
    }

    private static TMP_Text CreateHudText(string objectName, Vector2 anchoredPosition, Vector2 size, int fontSize, bool anchorRight = false)
    {
        var canvas = EnsureCanvas();
        var textObject = new GameObject(objectName);
        textObject.transform.SetParent(canvas.transform, false);

        var rect = textObject.AddComponent<RectTransform>();
        rect.anchorMin = anchorRight ? new Vector2(1f, 1f) : new Vector2(0f, 1f);
        rect.anchorMax = anchorRight ? new Vector2(1f, 1f) : new Vector2(0f, 1f);
        rect.pivot = anchorRight ? new Vector2(1f, 1f) : new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.color = Color.white;
        text.enableWordWrapping = true;
        text.text = string.Empty;

        return text;
    }

    private static Canvas EnsureCanvas()
    {
        var canvasObject = GameObject.Find(GuidanceCanvasName);
        if (canvasObject != null)
        {
            var existing = canvasObject.GetComponent<Canvas>();
            if (existing != null)
            {
                EnsureEventSystem();
                return existing;
            }
        }

        canvasObject = new GameObject(GuidanceCanvasName);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.sortingOrder = 30;

        var scaler = canvasObject.AddComponent<CanvasScaler>();
        if (ShouldUseWorldSpaceDebugCanvas())
        {
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = canvasObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1920f, 1080f);
            rect.position = new Vector3(0f, 1.5f, 2.5f);
            rect.rotation = Quaternion.identity;
            rect.localScale = Vector3.one * 0.001f;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.dynamicPixelsPerUnit = 10f;
        }
        else
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        canvasObject.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return canvas;
    }

    private static bool ShouldUseWorldSpaceDebugCanvas()
    {
        return UnityEngine.XR.XRSettings.enabled
            || UnityEngine.XR.XRSettings.isDeviceActive
            || !string.IsNullOrWhiteSpace(UnityEngine.XR.XRSettings.loadedDeviceName);
    }

    private static void EnsureEventSystem()
    {
        if (FindObjectOfType<EventSystem>() != null)
        {
            return;
        }

        var eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
        eventSystem.AddComponent<StandaloneInputModule>();
    }

    private static void EnsureRequestButton(AgentClient client)
    {
        if (client == null || GameObject.Find("RequestGuidanceButton") != null)
        {
            return;
        }

        var canvas = EnsureCanvas();
        var buttonObject = new GameObject("RequestGuidanceButton");
        buttonObject.transform.SetParent(canvas.transform, false);

        var rect = buttonObject.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(24f, -324f);
        rect.sizeDelta = new Vector2(220f, 42f);

        var image = buttonObject.AddComponent<Image>();
        image.color = new Color(0.08f, 0.18f, 0.28f, 0.92f);

        var button = buttonObject.AddComponent<Button>();
        button.onClick.AddListener(client.RequestGuidance);

        var labelObject = new GameObject("RequestGuidanceButton_Text");
        labelObject.transform.SetParent(buttonObject.transform, false);

        var labelRect = labelObject.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelObject.AddComponent<TextMeshProUGUI>();
        label.text = "Request Guidance";
        label.fontSize = 18;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
    }
}
