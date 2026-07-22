using System.Collections.Generic;
using TMPro;
using UnityEngine;

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
    [SerializeField] private RotationGainController rotationGainController;

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

    [Header("可选 Avatar 表现")]
    [Tooltip("现有 Guide Avatar 控制器；不参与正式安全闭环时可留空。")]
    [SerializeField] private GuideAvatarController guideAvatarController;
    [SerializeField] private Transform avatarRoot;
    [SerializeField] private Transform pointingArm;

    [Header("UI 显式引用")]
    [SerializeField] private TMP_Text narrationText;
    [SerializeField] private TMP_Text movementHintText;
    [SerializeField] private TMP_Text gazeCueText;
    [SerializeField] private TMP_Text rejectionText;
    [SerializeField] private GameObject rejectionPanelRoot;
    [SerializeField] private TMP_Text debugText;
    [SerializeField] private TMP_Text researcherStatusText;

    private bool initialized;

    /// <summary>
    /// 当前组合根是否已完成一次成功初始化。
    /// </summary>
    public bool IsInitialized => initialized;

    /// <summary>
    /// 最近一次场景引用校验结果。
    /// </summary>
    public SceneReadinessReport Readiness { get; private set; } = SceneReadinessReport.NotEvaluated;

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
        return new SceneReadinessReport(missing);
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
        navigationTargetManager.Configure(userPoseTracker);
        navigationTargetManager.RebuildIndex();
        semanticTargetSelector.Configure(navigationTargetManager);
        arrivalDetector.Configure(userPoseTracker, navigationTargetManager);
        localGuidanceValidator.Configure(navigationTargetManager, riskEvaluator);
        narrationPlayer.Configure(narrationText);
        movementHintController.Configure(movementHintText);
        gazeCueController.Configure(gazeCueText);
        exhibitHighlightManager.RebuildIndex();

        if (guideAvatarController != null)
        {
            guideAvatarController.Configure(avatarRoot, pointingArm);
        }

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
            guideAvatarController,
            exhibitHighlightManager,
            rejectionPanel,
            guidanceDebugPanel);
        pauseResetController.Configure(
            guidanceExecutor,
            rejectionPanel,
            rotationGainController,
            userPoseTracker,
            riskEvaluator);
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
