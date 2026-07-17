using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 研究员运行时控制台的事件提示等级。
/// </summary>
public enum RuntimeConsoleLevel
{
    /// <summary>正常流程信息。</summary>
    Info,
    /// <summary>可恢复风险、过期或安全干预。</summary>
    Warning,
    /// <summary>真正系统异常。</summary>
    Error
}

/// <summary>
/// 在 ResearcherCanvas 中只读展示 Session、System、Navigation、RDW、Guidance 和 Event Feed。
/// 本组件只观察正式系统入口，不修改 Risk、Task Progress、Guidance Target、Gain 或 Reset。
/// </summary>
public sealed class ResearcherRuntimeConsole : MonoBehaviour
{
    private const string ConsoleRootName = "ResearcherRuntimeConsole";
    private const string SessionTextName = "RuntimeConsole_Session";
    private const string SystemTextName = "RuntimeConsole_System";
    private const string NavigationTextName = "RuntimeConsole_Navigation";
    private const string RdwTextName = "RuntimeConsole_RDW";
    private const string GuidanceTextName = "RuntimeConsole_Guidance";
    private const string EventTextName = "RuntimeConsole_Events";

    [Header("状态来源")]
    [SerializeField] private ExperimentDashboard experimentDashboard;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [SerializeField] private XRTrackingAdapter xrTrackingAdapter;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private RotationGainController rotationGainController;
    [SerializeField] private PauseResetController pauseResetController;

    [Header("研究员 UI")]
    [Tooltip("研究员屏幕 Canvas；未配置时查找 ResearcherCanvas。")]
    [SerializeField] private Canvas researcherCanvas;
    [Tooltip("状态文本刷新间隔，单位：秒。")]
    [SerializeField] private float refreshIntervalSeconds = 0.25f;
    [Tooltip("Event Feed 保留的最近事件数量。")]
    [SerializeField, Range(8, 50)] private int maxEventCount = 24;
    [Tooltip("Event Feed 面板同时显示的最新事件行数。")]
    [SerializeField, Range(6, 20)] private int maxVisibleEventLines = 13;

    private readonly Queue<string> eventFeed = new Queue<string>();
    private readonly StringBuilder builder = new StringBuilder(1024);
    private GameObject consoleRoot;
    private TMP_Text sessionText;
    private TMP_Text systemText;
    private TMP_Text navigationText;
    private TMP_Text rdwText;
    private TMP_Text guidanceText;
    private TMP_Text eventText;
    private TMP_Text legacyStatusText;
    private GuidanceResponse lastResponse;
    private string lastRequestId = string.Empty;
    private string lastDecisionId = string.Empty;
    private string lastRuntimeValidation = "waiting";
    private string lastRiskSignature = string.Empty;
    private float nextRefreshTime;
    private bool subscribed;

    /// <summary>运行时创建的控制台根节点；视图尚未创建时为 null。</summary>
    public GameObject ConsoleRoot => consoleRoot;
    /// <summary>Event Feed 当前保留的事件数量。</summary>
    public int EventCount => eventFeed.Count;

    private void Awake()
    {
        ResolveReferences();
        EnsureView();
    }

    private void OnEnable()
    {
        ResolveReferences();
        EnsureView();
        if (consoleRoot != null) consoleRoot.SetActive(true);
        Subscribe();
        AppendEvent(RuntimeConsoleLevel.Info, "Runtime console ready");
        RefreshNow();
    }

    private void OnDisable()
    {
        Unsubscribe();
        if (consoleRoot != null) consoleRoot.SetActive(false);
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
        {
            return;
        }

        nextRefreshTime = Time.unscaledTime + Mathf.Max(0.05f, refreshIntervalSeconds);
        RefreshNow();
    }

    /// <summary>
    /// 注入控制台所有只读状态来源，并重新绑定事件。
    /// </summary>
    public void Configure(
        ExperimentDashboard dashboard,
        ExperimentLogger logger,
        PhysicalSpaceManager spaceManager,
        PhysicalSpaceCalibration calibration,
        XRTrackingAdapter trackingAdapter,
        GuidanceTaskController task,
        GuidanceCoordinator coordinator,
        AgentClient client,
        PhysicalRiskEvaluator evaluator,
        RotationGainController gainController,
        PauseResetController resetController,
        Canvas canvas = null)
    {
        Unsubscribe();
        if (dashboard != null) experimentDashboard = dashboard;
        if (logger != null) experimentLogger = logger;
        if (spaceManager != null) physicalSpaceManager = spaceManager;
        if (calibration != null) physicalSpaceCalibration = calibration;
        if (trackingAdapter != null) xrTrackingAdapter = trackingAdapter;
        if (task != null) taskController = task;
        if (coordinator != null) guidanceCoordinator = coordinator;
        if (client != null) agentClient = client;
        if (evaluator != null) riskEvaluator = evaluator;
        if (gainController != null) rotationGainController = gainController;
        if (resetController != null) pauseResetController = resetController;
        if (canvas != null) researcherCanvas = canvas;

        EnsureView();
        if (isActiveAndEnabled) Subscribe();
        RefreshNow();
    }

    /// <summary>立即刷新六个显示区域；供测试和低频外部刷新使用。</summary>
    public void RefreshNow()
    {
        if (!EnsureView())
        {
            return;
        }

        WriteSession();
        WriteSystem();
        WriteNavigation();
        WriteRdw();
        WriteGuidance();
        WriteEventFeed();
    }

    /// <summary>
    /// 向固定长度 Event Feed 追加一条研究事件；超出上限时移除最旧事件。
    /// </summary>
    /// <param name="level">INFO、WARNING 或 ERROR。</param>
    /// <param name="message">简洁事件内容。</param>
    public void AppendEvent(RuntimeConsoleLevel level, string message)
    {
        var normalized = string.IsNullOrWhiteSpace(message) ? "unspecified event" : message.Trim();
        eventFeed.Enqueue($"[{DateTime.Now:HH:mm:ss}] {LevelLabel(level)}  {normalized}");
        while (eventFeed.Count > Mathf.Max(1, maxEventCount))
        {
            eventFeed.Dequeue();
        }

        WriteEventFeed();
    }

    private void WriteSession()
    {
        var session = experimentLogger != null ? experimentLogger.Session : null;
        var spec = experimentDashboard != null ? experimentDashboard.CurrentSpec : null;
        var runtime = taskController != null ? taskController.RuntimeState : null;
        builder.Clear();
        builder.AppendLine("<b>SESSION</b>");
        builder.Append("Participant  ").AppendLine(First(spec?.participantId, session?.participantId, "--"));
        builder.Append("Condition    ").AppendLine(First(spec?.condition, session?.condition, "--"));
        builder.Append("Task         ").AppendLine(First(runtime?.taskId, spec?.taskId, session?.taskId, "--"));
        builder.Append("Physical     ").AppendLine(First(physicalSpaceManager?.CurrentSpaceId, session?.physicalSpaceId, "--"));
        builder.Append("Session      ").AppendLine(experimentDashboard != null ? experimentDashboard.RunState.ToString() : "Unavailable");
        builder.Append("Elapsed      ").Append(ElapsedText(session));
        sessionText.text = builder.ToString();
    }

    private void WriteSystem()
    {
        var runtime = taskController != null ? taskController.RuntimeState : null;
        builder.Clear();
        builder.AppendLine("<b>SYSTEM</b>");
        builder.Append("System       ").AppendLine(experimentDashboard != null ? experimentDashboard.RunState.ToString() : "Unavailable");
        builder.Append("Backend      ").AppendLine(agentClient == null ? "Unavailable" : agentClient.BackendOnline ? "Online" : "Offline");
        builder.Append("Tracking     ").AppendLine(TrackingText());
        builder.Append("Calibration  ").AppendLine(physicalSpaceCalibration != null ? physicalSpaceCalibration.State.ToString() : "Unavailable");
        builder.Append("Calibration ID  ").AppendLine(ShortId(physicalSpaceCalibration?.CalibrationId));
        builder.Append("State Version   ").Append(runtime != null ? runtime.stateVersion.ToString() : "--");
        systemText.text = builder.ToString();
    }

    private void WriteNavigation()
    {
        var runtime = taskController != null ? taskController.RuntimeState : null;
        var target = guidanceCoordinator != null ? guidanceCoordinator.CurrentGuidanceTarget : runtime?.CurrentGuidanceTarget;
        builder.Clear();
        builder.AppendLine("<b>NAVIGATION</b>");
        builder.Append("Current      ").AppendLine(First(runtime?.currentExhibitId, "--"));
        builder.Append("Final        ").AppendLine(First(runtime?.finalTargetId, "--"));
        builder.Append("RouteSubgoal ").AppendLine(First(runtime?.nextTargetId, "--"));
        builder.Append("Guidance     ").AppendLine(target != null ? target.TargetId : "--");
        builder.Append("TargetRole   ").AppendLine(target != null ? target.TargetRole.ToString() : "--");
        builder.Append("ActiveDetour ").Append(First(runtime?.activeDetourId, "--"));
        navigationText.text = builder.ToString();
    }

    private void WriteRdw()
    {
        var risk = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        builder.Clear();
        builder.AppendLine("<b>RDW</b>");
        builder.Append("Risk         ").AppendLine(risk != null ? First(risk.boundary_risk, "unknown") : "Unavailable");
        builder.Append("Clearance F/L/R  ").AppendLine(risk == null
            ? "--"
            : $"{risk.clearance_forward:0.00} / {risk.clearance_left:0.00} / {risk.clearance_right:0.00} m");
        builder.Append("Safe         ").AppendLine(risk == null ? "--" : Join(risk.safe_directions));
        builder.Append("Preferred    ").AppendLine(PreferredDirection(risk));
        builder.Append("Reset Req.   ").AppendLine(risk != null && risk.reset_required ? "YES" : "no");
        builder.Append("Gain         ").AppendLine(rotationGainController != null ? rotationGainController.State.ToString() : "Unavailable");
        builder.Append("Reset        ").Append(pauseResetController != null ? pauseResetController.State.ToString() : "Unavailable");
        rdwText.text = builder.ToString();
    }

    private void WriteGuidance()
    {
        var requestId = First(guidanceCoordinator?.ActiveRequestId, lastRequestId, "--");
        var decisionId = First(guidanceCoordinator?.ActiveDecisionId, lastDecisionId, "--");
        builder.Clear();
        builder.AppendLine("<b>GUIDANCE</b>");
        builder.Append("request_id       ").AppendLine(ShortId(requestId));
        builder.Append("decision_id      ").AppendLine(ShortId(decisionId));
        builder.Append("Guidance State   ").AppendLine(guidanceCoordinator != null ? guidanceCoordinator.RequestPhase.ToString() : "Unavailable");
        builder.Append("Selected Target  ").AppendLine(First(lastResponse?.selected_target, guidanceCoordinator?.CurrentGuidanceTarget?.TargetId, "--"));
        builder.Append("Narration        ").AppendLine(Summary(lastResponse?.narration, 96));
        builder.Append("Verifier         ").AppendLine(VerifierText(lastResponse));
        builder.Append("Runtime Check    ").Append(lastRuntimeValidation);
        guidanceText.text = builder.ToString();
    }

    private void WriteEventFeed()
    {
        if (eventText == null)
        {
            return;
        }

        builder.Clear();
        builder.AppendLine("<b>EVENT FEED</b>");
        if (eventFeed.Count == 0)
        {
            builder.Append("No events");
        }
        else
        {
            var entries = eventFeed.ToArray();
            var firstVisible = Mathf.Max(0, entries.Length - Mathf.Max(1, maxVisibleEventLines));
            for (var index = firstVisible; index < entries.Length; index++)
            {
                builder.AppendLine(entries[index]);
            }
        }
        eventText.text = builder.ToString();
    }

    private bool EnsureView()
    {
        if (consoleRoot != null && sessionText != null && eventText != null)
        {
            return true;
        }

        if (researcherCanvas == null)
        {
            var canvasObject = GameObject.Find("ResearcherCanvas");
            researcherCanvas = canvasObject != null ? canvasObject.GetComponent<Canvas>() : null;
        }
        if (researcherCanvas == null)
        {
            return false;
        }

        var existing = researcherCanvas.transform.Find(ConsoleRootName);
        consoleRoot = existing != null ? existing.gameObject : CreateRoot(researcherCanvas.transform);
        sessionText = FindOrCreateText(SessionTextName, new Vector2(0f, 0.68f), new Vector2(0.32f, 1f));
        systemText = FindOrCreateText(SystemTextName, new Vector2(0.34f, 0.68f), new Vector2(0.66f, 1f));
        navigationText = FindOrCreateText(NavigationTextName, new Vector2(0.68f, 0.68f), Vector2.one);
        rdwText = FindOrCreateText(RdwTextName, new Vector2(0f, 0.31f), new Vector2(0.32f, 0.66f));
        guidanceText = FindOrCreateText(GuidanceTextName, new Vector2(0.34f, 0.31f), new Vector2(1f, 0.66f));
        eventText = FindOrCreateText(EventTextName, Vector2.zero, new Vector2(1f, 0.29f));

        if (legacyStatusText == null)
        {
            var statusTransform = researcherCanvas.transform.Find("ResearcherStatusText");
            var statusObject = statusTransform != null ? statusTransform.gameObject : GameObject.Find("ResearcherStatusText");
            legacyStatusText = statusObject != null ? statusObject.GetComponent<TMP_Text>() : null;
        }
        if (legacyStatusText != null) legacyStatusText.enabled = false;
        return true;
    }

    private GameObject CreateRoot(Transform parent)
    {
        var root = new GameObject(ConsoleRootName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        root.layer = parent.gameObject.layer;
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(568f, 24f);
        rect.offsetMax = new Vector2(-24f, -24f);
        var image = root.GetComponent<Image>();
        image.color = new Color(0.025f, 0.035f, 0.045f, 0.96f);
        image.raycastTarget = false;
        return root;
    }

    private TMP_Text FindOrCreateText(string objectName, Vector2 anchorMin, Vector2 anchorMax)
    {
        var existing = consoleRoot.transform.Find(objectName);
        var textObject = existing != null ? existing.gameObject : new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        textObject.layer = consoleRoot.layer;
        if (existing == null) textObject.transform.SetParent(consoleRoot.transform, false);

        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = new Vector2(16f, 12f);
        rect.offsetMax = new Vector2(-12f, -12f);
        var text = textObject.GetComponent<TMP_Text>();
        text.fontSize = 14f;
        text.color = new Color(0.9f, 0.93f, 0.95f, 1f);
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.richText = true;
        text.raycastTarget = false;
        text.text = string.Empty;
        return text;
    }

    private void ResolveReferences()
    {
        if (experimentDashboard == null) experimentDashboard = FindObjectOfType<ExperimentDashboard>();
        if (experimentLogger == null) experimentLogger = FindObjectOfType<ExperimentLogger>();
        if (physicalSpaceManager == null) physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        if (physicalSpaceCalibration == null) physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
        if (xrTrackingAdapter == null) xrTrackingAdapter = FindObjectOfType<XRTrackingAdapter>();
        if (taskController == null) taskController = FindObjectOfType<GuidanceTaskController>();
        if (guidanceCoordinator == null) guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        if (agentClient == null) agentClient = FindObjectOfType<AgentClient>();
        if (riskEvaluator == null) riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        if (rotationGainController == null) rotationGainController = FindObjectOfType<RotationGainController>();
        if (pauseResetController == null) pauseResetController = FindObjectOfType<PauseResetController>();
    }

    private void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        if (experimentDashboard != null) experimentDashboard.SessionStateChanged += HandleSessionStateChanged;
        if (taskController != null) taskController.StateChanged += HandleTaskStateChanged;
        if (riskEvaluator != null) riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        if (physicalSpaceCalibration != null) physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RequestPhaseChanged += HandleRequestPhaseChanged;
            guidanceCoordinator.Replanning += HandleReplanning;
            guidanceCoordinator.TaskCompleted += HandleTaskCompleted;
            guidanceCoordinator.RuntimeIntervention += HandleRuntimeIntervention;
            guidanceCoordinator.GuidanceExecuted += HandleGuidanceExecuted;
            guidanceCoordinator.GuidanceExpired += HandleGuidanceExpired;
            guidanceCoordinator.GuidanceRejected += HandleGuidanceRejected;
        }
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceReceived.AddListener(HandleResponseReceived);
            agentClient.BackendOnlineChanged.AddListener(HandleBackendOnlineChanged);
        }
        if (rotationGainController != null) rotationGainController.LifecycleEventOccurred += HandleGainEvent;
        if (pauseResetController != null)
        {
            pauseResetController.ResetStarted += HandleResetStarted;
            pauseResetController.ResetCompleted += HandleResetCompleted;
        }
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        subscribed = false;
        if (experimentDashboard != null) experimentDashboard.SessionStateChanged -= HandleSessionStateChanged;
        if (taskController != null) taskController.StateChanged -= HandleTaskStateChanged;
        if (riskEvaluator != null) riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        if (physicalSpaceCalibration != null) physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RequestPhaseChanged -= HandleRequestPhaseChanged;
            guidanceCoordinator.Replanning -= HandleReplanning;
            guidanceCoordinator.TaskCompleted -= HandleTaskCompleted;
            guidanceCoordinator.RuntimeIntervention -= HandleRuntimeIntervention;
            guidanceCoordinator.GuidanceExecuted -= HandleGuidanceExecuted;
            guidanceCoordinator.GuidanceExpired -= HandleGuidanceExpired;
            guidanceCoordinator.GuidanceRejected -= HandleGuidanceRejected;
        }
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.BackendOnlineChanged.RemoveListener(HandleBackendOnlineChanged);
        }
        if (rotationGainController != null) rotationGainController.LifecycleEventOccurred -= HandleGainEvent;
        if (pauseResetController != null)
        {
            pauseResetController.ResetStarted -= HandleResetStarted;
            pauseResetController.ResetCompleted -= HandleResetCompleted;
        }
    }

    private void HandleSessionStateChanged(ExperimentRunState nextState) => AppendEvent(RuntimeConsoleLevel.Info, $"Session: {nextState}");
    private void HandleTaskStateChanged(GuidanceRuntimeState state) => AppendEvent(RuntimeConsoleLevel.Info, $"Task: {state?.phase}");

    private void HandleRiskStateChanged(PhysicalRiskState risk)
    {
        var signature = risk == null ? "none" : $"{risk.boundary_risk}|{risk.reset_required}|{Join(risk.safe_directions)}";
        if (signature == lastRiskSignature) return;
        lastRiskSignature = signature;
        var level = risk != null && (risk.reset_required || risk.boundary_risk == "critical")
            ? RuntimeConsoleLevel.Warning
            : RuntimeConsoleLevel.Info;
        AppendEvent(level, $"Risk: {risk?.boundary_risk ?? "unknown"}");
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState nextState) =>
        AppendEvent(nextState == PhysicalCalibrationState.Calibrated ? RuntimeConsoleLevel.Info : RuntimeConsoleLevel.Warning, $"Calibration: {nextState}");

    private void HandleRequestPhaseChanged(GuidanceRequestPhase phase) => AppendEvent(
        phase == GuidanceRequestPhase.Failed || phase == GuidanceRequestPhase.Expired ? RuntimeConsoleLevel.Warning : RuntimeConsoleLevel.Info,
        $"Guidance: {phase}");

    private void HandleReplanning(GuidanceRuntimeState _) => AppendEvent(RuntimeConsoleLevel.Info, "Replanning");
    private void HandleTaskCompleted(GuidanceRuntimeState _) => AppendEvent(RuntimeConsoleLevel.Info, "Task completed");

    private void HandleRuntimeIntervention(RuntimeInterventionInfo info)
    {
        lastResponse = info?.Response;
        lastRuntimeValidation = First(info?.Reason, "runtime intervention");
        AppendEvent(RuntimeConsoleLevel.Warning, $"Runtime intervention: {lastRuntimeValidation}");
    }

    private void HandleGuidanceExecuted(GuidanceResponse response)
    {
        lastResponse = response;
        lastRuntimeValidation = "approved";
        AppendEvent(RuntimeConsoleLevel.Info, $"Guidance executed: {First(response?.selected_target, "--")}");
    }

    private void HandleGuidanceExpired(string reason)
    {
        lastRuntimeValidation = First(reason, "expired");
        AppendEvent(RuntimeConsoleLevel.Warning, $"Guidance expired: {lastRuntimeValidation}");
    }

    private void HandleGuidanceRejected(string reason)
    {
        lastRuntimeValidation = First(reason, "rejected");
        AppendEvent(RuntimeConsoleLevel.Warning, $"Guidance rejected: {lastRuntimeValidation}");
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        lastRequestId = request != null ? request.request_id : string.Empty;
        lastDecisionId = request != null ? request.decision_id : string.Empty;
        AppendEvent(RuntimeConsoleLevel.Info, $"Guidance requested: {ShortId(lastRequestId)}");
    }

    private void HandleResponseReceived(GuidanceResponse response)
    {
        lastResponse = response;
        AppendEvent(RuntimeConsoleLevel.Info, $"Guidance received: {ShortId(response?.request_id)}");
    }

    private void HandleBackendOnlineChanged(bool online) => AppendEvent(
        online ? RuntimeConsoleLevel.Info : RuntimeConsoleLevel.Error,
        online ? "Backend online" : "Backend unavailable");

    private void HandleGainEvent(RotationGainEventInfo info)
    {
        if (info == null) return;
        AppendEvent(info.EventType == RotationGainEventType.GainCancelled ? RuntimeConsoleLevel.Warning : RuntimeConsoleLevel.Info,
            info.EventType == RotationGainEventType.GainCancelled
                ? $"Gain cancelled: {info.Reason}"
                : info.EventType.ToString());
    }

    private void HandleResetStarted(ResetEventInfo info) => AppendEvent(RuntimeConsoleLevel.Warning, $"Reset started: {info?.SafeDirection ?? "--"}");
    private void HandleResetCompleted(ResetEventInfo _) => AppendEvent(RuntimeConsoleLevel.Info, "Reset completed");

    private string TrackingText()
    {
        if (xrTrackingAdapter == null) return "Unavailable";
        if (!xrTrackingAdapter.IsHeadsetTrackingEnabled) return "Disabled";
        return xrTrackingAdapter.IsHeadsetTrackingActive ? "Active" : "Lost / waiting";
    }

    private static string PreferredDirection(PhysicalRiskState risk)
    {
        if (risk == null) return "--";
        if (risk.clearance_left > risk.clearance_forward && risk.clearance_left >= risk.clearance_right) return "left";
        if (risk.clearance_right > risk.clearance_forward && risk.clearance_right > risk.clearance_left) return "right";
        return "forward";
    }

    private static string VerifierText(GuidanceResponse response)
    {
        if (response?.verifier == null) return "--";
        return response.verifier.approved ? "approved" : $"rejected: {First(response.verifier.reason, "unspecified")}";
    }

    private static string ElapsedText(ExperimentSession session)
    {
        if (session == null || !DateTime.TryParse(session.startedAtUtc, out var started)) return "00:00:00";
        var elapsed = (session.IsActive ? DateTime.UtcNow : DateTime.TryParse(session.endedAtUtc, out var ended) ? ended : DateTime.UtcNow) - started.ToUniversalTime();
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string Summary(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "--";
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized.Substring(0, maxLength - 3) + "...";
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "--") return "--";
        return value.Length <= 16 ? value : value.Substring(0, 16);
    }

    private static string Join(string[] values) => values == null || values.Length == 0 ? "none" : string.Join(", ", values);

    private static string First(params string[] values)
    {
        if (values == null) return string.Empty;
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string LevelLabel(RuntimeConsoleLevel level)
    {
        if (level == RuntimeConsoleLevel.Warning) return "<color=#F2C46D>WARNING</color>";
        if (level == RuntimeConsoleLevel.Error) return "<color=#FF7B72>ERROR</color>";
        return "<color=#72D4A4>INFO</color>";
    }
}
