using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// MainScene 的唯一组合根，负责校验显式引用、配置系统并连接跨模块事件。
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class SystemBootstrapper : MonoBehaviour
{
    [Header("状态来源")]
    [Tooltip("物理空间配置与当前空间状态。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("虚拟与物理用户位姿状态。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("当前物理空间校准状态。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [Tooltip("物理边界与障碍物风险评估器。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [Tooltip("物理转向检测器。")]
    [SerializeField] private MotionDetector motionDetector;
    [Tooltip("XR 头显 Tracking 适配器。")]
    [SerializeField] private XRTrackingAdapter xrTrackingAdapter;

    [Header("静态场景语义")]
    [Tooltip("Build-safe 的 schema_version=4 场景语义配置；缺失时禁止启动正式会话。")]
    [SerializeField] private TextAsset sceneSemanticsJson;
    [Tooltip("JSON 场景位置与 MainScene Anchor 的校验容差，单位：米。")]
    [SerializeField, Min(0f)] private float semanticBindingToleranceMeters = 0.05f;

    [Header("任务与导览")]
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private NavigationTargetManager navigationTargetManager;
    [SerializeField] private SemanticTargetSelector semanticTargetSelector;
    [SerializeField] private ArrivalDetector arrivalDetector;
    [SerializeField] private LocalGuidanceValidator localGuidanceValidator;

    [Header("RDW 与安全")]
    [SerializeField] private PauseResetController pauseResetController;
    [Tooltip("可选的 OURS Reset 语义旁路；为空时在组合根上运行时创建。")]
    [SerializeField] private ResetSemanticInterventionManager resetSemanticInterventionManager;
    [Header("OURS Reset 语义旁路")]
    [Tooltip("是否在 OURS 条件下请求 Reset 期间的上下文叙事。")]
    [SerializeField] private bool resetSemanticInterventionEnabled = true;
    [Tooltip("Medium 语义干预的 Reset 转角下限，单位：度。")]
    [SerializeField, Range(0f, 180f)] private float resetSemanticMediumThresholdDegrees = 60f;
    [Tooltip("High 语义干预的 Reset 转角下限，单位：度。")]
    [SerializeField, Range(0f, 360f)] private float resetSemanticHighThresholdDegrees = 120f;
    [SerializeField] private RotationGainController rotationGainController;
    [Header("Continuous RDW - 基础设置")]
    [Tooltip("是否启用 Continuous RDW；关闭后保留 None 对照模式。")]
    [InspectorName("启用 Continuous RDW")]
    [SerializeField] private bool continuousRdwEnabled = false;
    [Tooltip("Continuous Redirector 模式；None、S2C、S2O 或 ThomasAPF 可用于同一实验配置切换。")]
    [InspectorName("重定向算法")]
    [SerializeField] private ContinuousRedirectorMode continuousRedirectorMode = ContinuousRedirectorMode.S2C;

    [Header("Continuous RDW - 通用增益参数")]
    [Tooltip("触发曲率增益的最小物理移动速度，单位：米/秒。")]
    [InspectorName("移动触发阈值 (m/s)")]
    [SerializeField] private float s2cMovementThresholdMetersPerSecond = 0.2f;
    [Tooltip("触发旋转增益的最小物理转速，单位：度/秒。")]
    [InspectorName("旋转触发阈值 (°/s)")]
    [SerializeField] private float s2cRotationThresholdDegreesPerSecond = 1.5f;
    [Tooltip("三种 Continuous steering 算法共享的曲率半径，单位：米。")]
    [InspectorName("曲率半径 (m)")]
    [SerializeField] private float s2cCurvatureRadiusMeters = 10f;
    [Tooltip("统一 GainLimiter 对三种 Continuous steering 算法执行的最大曲率转速，单位：度/秒。")]
    [InspectorName("最大曲率转速 (°/s)")]
    [SerializeField] private float s2cMaxCurvatureDegreesPerSecond = 15f;
    [Tooltip("统一 GainLimiter 对三种 Continuous steering 算法执行的最大旋转增益转速，单位：度/秒。")]
    [InspectorName("最大旋转增益转速 (°/s)")]
    [SerializeField] private float s2cMaxRotationDegreesPerSecond = 30f;
    [Tooltip("物理转动映射为额外虚拟 yaw 的比例。")]
    [InspectorName("旋转增益倍率")]
    [SerializeField] private float s2cRotationGainMultiplier = 0.05f;
    [Tooltip("steering angle 对增益的衰减权重，范围 0-1。")]
    [InspectorName("导向衰减")]
    [Range(0f, 1f)] [SerializeField] private float s2cSteeringDampening = 0.75f;
    [Tooltip("三种 Continuous steering 算法共享的跨帧平滑系数，范围 0-1。")]
    [InspectorName("跨帧平滑系数")]
    [Range(0f, 1f)] [SerializeField] private float s2cSmoothingFactor = 0.125f;

    [Header("Steer-to-Center (S2C)")]
    [Tooltip("距 Steering Center 小于该值时停止导向，单位：米。")]
    [InspectorName("中心停止容差 (m)")]
    [SerializeField] private float s2cCenterToleranceMeters = 0.15f;
    [Tooltip("进入 near-180 临时目标处理的角度阈值，单位：度。")]
    [InspectorName("近反向角度阈值 (°)")]
    [Range(90f, 179f)] [SerializeField] private float s2cNearOppositeAngleDegrees = 160f;
    [Tooltip("near-180 时临时 steering bearing，单位：度。")]
    [InspectorName("近反向临时导向角 (°)")]
    [Range(1f, 90f)] [SerializeField] private float s2cOppositeTemporaryBearingDegrees = 90f;

    [Header("Steer-to-Orbit (S2O)")]
    [Tooltip("S2O 轨道半径，单位：米；会受 boundary safety margin 限制。")]
    [InspectorName("轨道半径 (m)")]
    [SerializeField] private float s2oOrbitRadiusMeters = 1.5f;
    [Tooltip("S2O 轨道距 Boundary 的最小安全余量，单位：米。")]
    [InspectorName("边界安全余量 (m)")]
    [SerializeField] private float s2oOrbitBoundarySafetyMarginMeters = 0.25f;
    [Tooltip("S2O 切换 CW/CCW 的最小优势角，单位：度。")]
    [InspectorName("方向切换滞回角 (°)")]
    [SerializeField] private float s2oSideSwitchHysteresisDegrees = 12f;

    [Header("Thomas Artificial Potential Field (APF)")]
    [Tooltip("Thomas APF Boundary 产生斥力的影响半径，单位：米。")]
    [InspectorName("边界影响半径 (m)")]
    [SerializeField] private float thomasApfBoundaryInfluenceRadiusMeters = 1.5f;
    [Tooltip("Thomas APF Obstacle 产生斥力的影响半径，单位：米。")]
    [InspectorName("障碍物影响半径 (m)")]
    [SerializeField] private float thomasApfObstacleInfluenceRadiusMeters = 1.5f;
    [Tooltip("Thomas APF 的额外影响余量，单位：米；这是 APF 斥力 tuning，不是 Physical Safety 阈值。")]
    [InspectorName("APF 影响余量 (m)")]
    [FormerlySerializedAs("thomasApfSafetyMarginMeters")]
    [SerializeField] private float thomasApfInfluenceMarginMeters = 0.15f;
    [Tooltip("Thomas APF Boundary 斥力权重。")]
    [InspectorName("边界斥力权重")]
    [SerializeField] private float thomasApfBoundaryStrength = 1f;
    [Tooltip("Thomas APF Obstacle 斥力权重。")]
    [InspectorName("障碍物斥力权重")]
    [SerializeField] private float thomasApfObstacleStrength = 1f;
    [Tooltip("Thomas APF 单帧最大合力，防止近边界数值爆炸。")]
    [InspectorName("单帧最大合力")]
    [SerializeField] private float thomasApfMaxForceMagnitude = 100f;

    [Header("RDW 约束规划")]
    [Tooltip("独立 RDW 路线规划配置；第一轮必须保持 Shadow。")]
    [SerializeField] private RdwPlanningSettings rdwPlanningSettings = new RdwPlanningSettings();

    [Header("实验与表现")]
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private ExperimentDashboard experimentDashboard;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private NarrationPlayer narrationPlayer;
    [SerializeField] private MovementHintController movementHintController;
    [SerializeField] private GazeCueController gazeCueController;
    [SerializeField] private ExhibitHighlightManager exhibitHighlightManager;
    [SerializeField] private RejectionPanel rejectionPanel;
    [SerializeField] private GuidanceDebugPanel guidanceDebugPanel;
    [SerializeField] private ParticipantUiController participantUiController;
    [SerializeField] private BoundaryVisualizer boundaryVisualizer;

    [Header("UI 显式引用")]
    [SerializeField] private TMP_Text narrationText;
    [SerializeField] private TMP_Text movementHintText;
    [SerializeField] private TMP_Text gazeCueText;
    [SerializeField] private TMP_Text rejectionText;
    [SerializeField] private GameObject rejectionPanelRoot;
    [SerializeField] private TMP_Text debugText;
    [SerializeField] private TMP_Text researcherStatusText;

    private bool initialized;
    private RdwGainApplier rdwGainApplier;
    private RdwFrameCoordinator rdwFrameCoordinator;
    private StaticSceneSemanticGraph semanticGraph;

    /// <summary>
    /// 当前组合根是否已完成一次成功初始化。
    /// </summary>
    public bool IsInitialized => initialized;

    /// <summary>
    /// 最近一次场景引用校验结果。
    /// </summary>
    public SceneReadinessReport Readiness { get; private set; } = SceneReadinessReport.NotEvaluated;
    /// <summary>Step 2A 当前使用的统一 RDW 帧事务入口。</summary>
    public RdwFrameCoordinator RdwFrameCoordinator => rdwFrameCoordinator;

    /// <summary>
    /// 在正式 Session 启动前应用三条件 Profile，统一切换 Continuous RDW 与 Top-K 模式。
    /// </summary>
    public void ApplyExperimentProfile(ExperimentProfile profile)
    {
        if (profile == null)
        {
            return;
        }

        continuousRdwEnabled = profile.continuousRdwEnabled;
        rdwPlanningSettings?.SetMode(profile.rdwPlanningMode);
        rdwFrameCoordinator?.SetContinuousEnabled(profile.continuousRdwEnabled);
        guidanceCoordinator?.ApplyExperimentProfile(profile);
    }
    /// <summary>启动时加载的唯一静态场景语义图。</summary>
    public StaticSceneSemanticGraph SemanticGraph => semanticGraph;

    private void Awake()
    {
        Initialize();
    }

    /// <summary>
    /// 按固定顺序完成引用校验、系统配置、事件接线与初始就绪状态评估。
    /// </summary>
    /// <returns>场景满足运行前置条件并完成初始化时返回 true。</returns>
    public bool Initialize()
    {
        if (initialized)
        {
            return true;
        }

        Readiness = ValidateReferences();
        experimentDashboard?.SetSceneReadiness(Readiness);
        if (!Readiness.IsReady)
        {
            Debug.LogError($"[SystemBootstrapper] MainScene 未就绪 | {Readiness.ErrorSummary}", this);
            return false;
        }

        Debug.Log(
            $"scene_semantics_loaded schema_version={semanticGraph.SchemaVersion} " +
            $"scene_id={semanticGraph.SceneId} exhibits={semanticGraph.Exhibits.Count} " +
            $"themes={semanticGraph.Themes.Count} semantic_edges={semanticGraph.SemanticEdges.Count} " +
            $"scene_bindings={semanticGraph.Exhibits.Count} " +
            $"semantic_ready={(Readiness.SemanticReady ? "true" : "false")}",
            this);
        ConfigureSystems();
        WireEvents();
        EvaluateInitialState();
        initialized = true;
        return true;
    }

    /// <summary>
    /// 只读检查 MainScene 运行所需的显式引用，不查找、创建或修复对象。
    /// </summary>
    public SceneReadinessReport ValidateReferences()
    {
        var missing = new List<string>();
        var semanticConfigLoaded = false;
        var semanticSchemaValid = false;
        var semanticGraphValid = false;
        var sceneBindingValid = false;
        var taskReferencesValid = false;
        semanticGraph = null;
        if (sceneSemanticsJson == null)
        {
            missing.Add("semantic_config_text_asset");
        }
        else if (!SceneSemanticsLoader.TryLoadJson(sceneSemanticsJson.text, out semanticGraph, out var semanticError))
        {
            missing.Add("semantic_config_invalid:" + semanticError);
        }
        else
        {
            semanticConfigLoaded = true;
            semanticSchemaValid = semanticGraph.SchemaVersion == 4;
            semanticGraphValid = semanticSchemaValid && semanticGraph.Exhibits.Count > 0;
            if (!semanticSchemaValid) missing.Add("semantic_schema_invalid");

            var binding = SceneSemanticBindingValidator.ValidateLoadedScene(
                semanticGraph,
                semanticBindingToleranceMeters,
                semanticBindingToleranceMeters,
                semanticBindingToleranceMeters);
            sceneBindingValid = binding.IsValid;
            if (!sceneBindingValid)
            {
                foreach (var error in binding.Errors) missing.Add(error);
            }

            if (taskController != null)
            {
                var taskErrors = new List<string>();
                taskController.ValidateSemanticReferences(semanticGraph, taskErrors);
                ExperimentConditionMatrix.ValidateSemanticReferences(semanticGraph, taskErrors);
                taskReferencesValid = taskErrors.Count == 0;
                missing.AddRange(taskErrors);
            }
        }
        AddIfMissing(missing, physicalSpaceManager, nameof(physicalSpaceManager));
        AddIfMissing(missing, userPoseTracker, nameof(userPoseTracker));
        AddIfMissing(missing, physicalSpaceCalibration, nameof(physicalSpaceCalibration));
        AddIfMissing(missing, riskEvaluator, nameof(riskEvaluator));
        AddIfMissing(missing, motionDetector, nameof(motionDetector));
        AddIfMissing(missing, xrTrackingAdapter, nameof(xrTrackingAdapter));
        AddIfMissing(missing, agentClient, nameof(agentClient));
        AddIfMissing(missing, guidanceCoordinator, nameof(guidanceCoordinator));
        AddIfMissing(missing, taskController, nameof(taskController));
        AddIfMissing(missing, navigationTargetManager, nameof(navigationTargetManager));
        AddIfMissing(missing, semanticTargetSelector, nameof(semanticTargetSelector));
        AddIfMissing(missing, arrivalDetector, nameof(arrivalDetector));
        AddIfMissing(missing, localGuidanceValidator, nameof(localGuidanceValidator));
        AddIfMissing(missing, pauseResetController, nameof(pauseResetController));
        AddIfMissing(missing, rotationGainController, nameof(rotationGainController));
        AddIfMissing(missing, guidanceExecutor, nameof(guidanceExecutor));
        AddIfMissing(missing, experimentDashboard, nameof(experimentDashboard));
        AddIfMissing(missing, experimentLogger, nameof(experimentLogger));
        AddIfMissing(missing, narrationPlayer, nameof(narrationPlayer));
        AddIfMissing(missing, movementHintController, nameof(movementHintController));
        AddIfMissing(missing, gazeCueController, nameof(gazeCueController));
        AddIfMissing(missing, exhibitHighlightManager, nameof(exhibitHighlightManager));
        AddIfMissing(missing, rejectionPanel, nameof(rejectionPanel));
        AddIfMissing(missing, guidanceDebugPanel, nameof(guidanceDebugPanel));
        AddIfMissing(missing, participantUiController, nameof(participantUiController));
        AddIfMissing(missing, boundaryVisualizer, nameof(boundaryVisualizer));
        AddIfMissing(missing, narrationText, nameof(narrationText));
        AddIfMissing(missing, movementHintText, nameof(movementHintText));
        AddIfMissing(missing, gazeCueText, nameof(gazeCueText));
        AddIfMissing(missing, researcherStatusText, nameof(researcherStatusText));
        if (userPoseTracker != null)
        {
            AddIfMissing(missing, userPoseTracker.VirtualPoseSource, "userPoseTracker.virtualUserRoot");
            if (!userPoseTracker.HasValidVirtualPoseContract)
            {
                missing.Add("userPoseTracker.virtualPoseHierarchy");
            }
        }
        return new SceneReadinessReport(
            missing,
            semanticConfigLoaded,
            semanticSchemaValid,
            semanticGraphValid,
            sceneBindingValid,
            taskReferencesValid);
    }

    private void ConfigureSystems()
    {
        agentClient.Configure(
            physicalSpaceManager,
            userPoseTracker,
            new GuidanceRequestContext(),
            riskEvaluator,
            physicalSpaceCalibration,
            experimentLogger);
        physicalSpaceCalibration.Configure(physicalSpaceManager, userPoseTracker);
        riskEvaluator.Configure(physicalSpaceManager, userPoseTracker, physicalSpaceCalibration);
        motionDetector.Configure(userPoseTracker);
        xrTrackingAdapter.Configure(userPoseTracker, physicalSpaceCalibration);
        boundaryVisualizer.Configure(physicalSpaceManager, userPoseTracker, riskEvaluator, physicalSpaceCalibration);
        navigationTargetManager.Configure(userPoseTracker, semanticGraph);
        taskController.ConfigureSemanticGraph(semanticGraph);
        navigationTargetManager.RebuildIndex();
        semanticTargetSelector.Configure(navigationTargetManager, riskEvaluator);
        arrivalDetector.Configure(userPoseTracker, navigationTargetManager);
        localGuidanceValidator.Configure(navigationTargetManager, riskEvaluator);
        narrationPlayer.Configure(narrationText);
        movementHintController.Configure(movementHintText);
        gazeCueController.Configure(gazeCueText);
        exhibitHighlightManager.RebuildIndex();

        if (rejectionText != null || rejectionPanelRoot != null)
        {
            rejectionPanel.Configure(
                rejectionText,
                rejectionPanelRoot != null ? rejectionPanelRoot : rejectionText.gameObject);
        }

        if (debugText != null)
        {
            guidanceDebugPanel.Configure(debugText);
        }

        guidanceExecutor.Configure(
            narrationPlayer,
            gazeCueController,
            movementHintController,
            exhibitHighlightManager,
            rejectionPanel,
            guidanceDebugPanel);
        pauseResetController.Configure(
            guidanceExecutor,
            rejectionPanel,
            rotationGainController,
            userPoseTracker,
            riskEvaluator);
        pauseResetController.ConfigureMappingReset();
        rotationGainController.Configure(userPoseTracker, motionDetector, riskEvaluator, physicalSpaceCalibration);
        guidanceCoordinator.Configure(
            agentClient,
            guidanceExecutor,
            taskController,
            navigationTargetManager,
            semanticTargetSelector,
            arrivalDetector,
            riskEvaluator,
            localGuidanceValidator,
            pauseResetController,
            rotationGainController,
            rejectionPanel,
            motionDetector);
        if (resetSemanticInterventionManager == null)
        {
            resetSemanticInterventionManager = GetComponent<ResetSemanticInterventionManager>();
            if (resetSemanticInterventionManager == null)
            {
                resetSemanticInterventionManager = gameObject.AddComponent<ResetSemanticInterventionManager>();
            }
        }
        resetSemanticInterventionManager.Configure(
            pauseResetController,
            guidanceCoordinator,
            agentClient,
            narrationPlayer,
            experimentLogger,
            resetSemanticInterventionEnabled,
            resetSemanticMediumThresholdDegrees,
            resetSemanticHighThresholdDegrees);
        rdwGainApplier = new RdwGainApplier(
            userPoseTracker.VirtualRotationRoot,
            userPoseTracker.VirtualPoseSource);
        var rdwGainLimiter = new RdwGainLimiter(
            maxContinuousRotationDegreesPerSecond: s2cMaxRotationDegreesPerSecond,
            maxContinuousCurvatureDegreesPerSecond: s2cMaxCurvatureDegreesPerSecond);
        rdwFrameCoordinator = new RdwFrameCoordinator(
            userPoseTracker,
            physicalSpaceCalibration,
            physicalSpaceManager,
            riskEvaluator,
            motionDetector,
            pauseResetController,
            rotationGainController,
            guidanceCoordinator,
            navigationTargetManager,
            rdwGainApplier,
            CreateContinuousRedirector(),
            limiter: rdwGainLimiter,
            dashboard: experimentDashboard,
            continuousEnabled: continuousRdwEnabled);
        // Estimator 描述的是当前选择的 RDW 算法能力，不随启动时的运行开关冻结。
        // Ours Profile 可能在 Bootstrap 完成后才打开 Continuous RDW；此时仍必须使用真实 S2C/S2O/APF 代价模型。
        var continuousPlanningAvailable = continuousRedirectorMode != ContinuousRedirectorMode.None;
        var planningEstimator = RdwPlanningCostEstimatorFactory.Create(
            continuousRedirectorMode,
            continuousPlanningAvailable,
            new PhysicalRiskPlanningDirectionEvaluator(riskEvaluator),
            s2cCurvatureRadiusMeters,
            s2oOrbitRadiusMeters,
            s2oOrbitBoundarySafetyMarginMeters,
            s2oSideSwitchHysteresisDegrees,
            CreateThomasApfSettings());
        var planningCapability = new RdwCapabilityProfile(
            continuousRedirectorType: RdwPlanningCostEstimatorFactory.RedirectorType(
                continuousRedirectorMode,
                continuousPlanningAvailable),
            resetterType: "SafeHeadingMapping",
            rotationGainAllowed: continuousPlanningAvailable,
            curvatureGainAllowed: continuousPlanningAvailable,
            translationGainAllowed: false,
            gainLimitsVersion: "rdw-gain-limiter-v2",
            estimatorId: planningEstimator.EstimatorId,
            estimatorVersion: planningEstimator.EstimatorVersion);
        var rdwPlanningPipeline = new RdwPlanningPipeline(
            planningEstimator,
            planningCapability,
            rdwPlanningSettings);
        guidanceCoordinator.ConfigureRdwPlanning(
            rdwPlanningSettings,
            rdwPlanningPipeline,
            rdwFrameCoordinator,
            agentClient,
            experimentLogger);
        guidanceCoordinator.ConfigureSemanticPlanning(
            semanticGraph,
            agentClient);
        motionDetector.SetExternallyDriven(true);
        rotationGainController.ConfigureRdwCommandSink(rdwFrameCoordinator);
        BindRdwPipeline();
        experimentDashboard.Configure(
            physicalSpaceManager,
            userPoseTracker,
            riskEvaluator,
            xrTrackingAdapter,
            taskController,
            guidanceCoordinator,
            agentClient,
            experimentLogger,
            arrivalDetector,
            rotationGainController,
            guidanceExecutor,
            pauseResetController,
            participantUiController,
            researcherStatusText,
            physicalSpaceCalibration);
    }

    private IRdwRedirector CreateContinuousRedirector()
    {
        return new CompositeContinuousRedirector(
            new ControllerSimulationLocomotionRedirector(xrTrackingAdapter),
            CreateSteeringRedirector());
    }

    private IRdwRedirector CreateSteeringRedirector()
    {
        switch (continuousRedirectorMode)
        {
            case ContinuousRedirectorMode.S2C:
                return new SteerToCenterRedirector(
                    s2cMovementThresholdMetersPerSecond,
                    s2cRotationThresholdDegreesPerSecond,
                    s2cCurvatureRadiusMeters,
                    s2cRotationGainMultiplier,
                    s2cSteeringDampening,
                    s2cSmoothingFactor,
                    s2cCenterToleranceMeters,
                    s2cNearOppositeAngleDegrees,
                    s2cOppositeTemporaryBearingDegrees);
            case ContinuousRedirectorMode.S2O:
                return new SteerToOrbitRedirector(
                    s2cMovementThresholdMetersPerSecond,
                    s2cRotationThresholdDegreesPerSecond,
                    s2cCurvatureRadiusMeters,
                    s2cRotationGainMultiplier,
                    s2cSteeringDampening,
                    s2cSmoothingFactor,
                    s2oOrbitRadiusMeters,
                    s2oOrbitBoundarySafetyMarginMeters,
                    s2oSideSwitchHysteresisDegrees);
            case ContinuousRedirectorMode.ThomasAPF:
                return new ThomasApfRedirector(
                    s2cMovementThresholdMetersPerSecond,
                    s2cRotationThresholdDegreesPerSecond,
                    s2cCurvatureRadiusMeters,
                    s2cRotationGainMultiplier,
                    s2cSteeringDampening,
                    s2cSmoothingFactor,
                    CreateThomasApfSettings());
            default:
                return new NullRedirector();
        }
    }

    private ThomasApfForceSettings CreateThomasApfSettings()
    {
        return new ThomasApfForceSettings(
            thomasApfBoundaryInfluenceRadiusMeters,
            thomasApfObstacleInfluenceRadiusMeters,
            riskEvaluator != null ? riskEvaluator.UserRadiusMeters : 0f,
            thomasApfInfluenceMarginMeters,
            thomasApfBoundaryStrength,
            thomasApfObstacleStrength,
            thomasApfMaxForceMagnitude);
    }

    private void WireEvents()
    {
        agentClient.GuidanceReceived.RemoveListener(guidanceExecutor.Apply);
        agentClient.GuidanceReceived.RemoveListener(guidanceCoordinator.HandleGuidanceResponse);
        agentClient.GuidanceReceived.AddListener(guidanceCoordinator.HandleGuidanceResponse);

        agentClient.GuidanceRequestFailed.RemoveListener(rejectionPanel.Show);
        agentClient.GuidanceRequestFailed.AddListener(rejectionPanel.Show);
        agentClient.GuidanceRequestFailed.RemoveListener(guidanceDebugPanel.ShowStatus);
        agentClient.GuidanceRequestFailed.AddListener(guidanceDebugPanel.ShowStatus);
        agentClient.GuidanceRequestStarted.RemoveListener(guidanceDebugPanel.ShowStatus);
        agentClient.GuidanceRequestStarted.AddListener(guidanceDebugPanel.ShowStatus);
    }

    private void OnDestroy()
    {
        UnbindRdwPipeline();
        rotationGainController?.ConfigureRdwCommandSink(null);
        motionDetector?.SetExternallyDriven(false);
    }

    private void BindRdwPipeline()
    {
        UnbindRdwPipeline();
        if (xrTrackingAdapter != null)
        {
            xrTrackingAdapter.TrackingSampleApplied += HandleTrackingSampleApplied;
            xrTrackingAdapter.TrackingStateChanged += HandleTrackingStateChanged;
        }
        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
        }
        if (rdwFrameCoordinator != null)
        {
            rdwFrameCoordinator.FrameProcessed += HandleRdwFrameProcessed;
        }
    }

    private void UnbindRdwPipeline()
    {
        if (xrTrackingAdapter != null)
        {
            xrTrackingAdapter.TrackingSampleApplied -= HandleTrackingSampleApplied;
            xrTrackingAdapter.TrackingStateChanged -= HandleTrackingStateChanged;
        }
        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        }
        if (rdwFrameCoordinator != null)
        {
            rdwFrameCoordinator.FrameProcessed -= HandleRdwFrameProcessed;
        }
    }

    private void HandleTrackingSampleApplied(float deltaTimeSeconds)
    {
        rdwFrameCoordinator?.ProcessTrackingSample(deltaTimeSeconds);
    }

    private void HandleTrackingStateChanged(bool trackingActive)
    {
        if (!trackingActive)
        {
            rdwFrameCoordinator?.InvalidatePoseChain("tracking_lost");
        }
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState state)
    {
        if (state != PhysicalCalibrationState.Calibrated)
        {
            rdwFrameCoordinator?.InvalidatePoseChain("calibration_invalid");
        }
    }

    private void HandleRdwFrameProcessed(RdwFrameEventInfo frameInfo)
    {
        experimentLogger?.LogRdwFrame(
            frameInfo,
            LogTraceContext.Empty,
            taskController != null ? taskController.RuntimeState : null,
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void EvaluateInitialState()
    {
        experimentDashboard.SetSceneReadiness(Readiness);
    }

    private static void AddIfMissing(List<string> missing, Object reference, string label)
    {
        if (reference == null)
        {
            missing.Add(label);
        }
    }
}
