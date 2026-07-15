using TMPro;
using UnityEngine;

/// <summary>
/// 为研究者提供实验条件切换与 guidance 请求入口，并将运行事件汇总到日志和状态文本。
/// 条件选择来自 ExperimentConditionMatrix，应用结果同步到空间、位姿、任务和风险模块。
/// </summary>
public sealed class ExperimentDashboard : MonoBehaviour
{
    [SerializeField] private string participantId = "P001";
    [SerializeField] private int conditionIndex = 5;
    [SerializeField] private int taskIndex = 1;
    [SerializeField] private int physicalSpaceIndex = 1;
    [SerializeField] private bool requestGuidanceOnApply;

    [Header("Dependencies")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private TMP_Text statusText;

    private ExperimentConditionSpec currentSpec;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    /// <summary>
    /// 注入实验运行依赖，迁移事件订阅并立即应用当前 Inspector 选择。
    /// </summary>
    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        PhysicalRiskEvaluator evaluator,
        GuidanceTaskController task,
        GuidanceCoordinator coordinator,
        AgentClient client,
        ExperimentLogger logger,
        TMP_Text status)
    {
        Unsubscribe();
        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;
        riskEvaluator = evaluator;
        taskController = task;
        guidanceCoordinator = coordinator;
        agentClient = client;
        experimentLogger = logger;
        statusText = status;
        Subscribe();
        ApplySelection();
    }

    /// <summary>
    /// 应用当前条件组合，开始新日志会话并同步空间、模拟位姿、任务和风险状态。
    /// </summary>
    public void ApplySelection()
    {
        ResolveReferences();
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        experimentLogger?.StartSession(currentSpec);
        experimentLogger?.LogConditionSelected(currentSpec);
        physicalSpaceManager?.SetPhysicalSpace(currentSpec.physicalSpaceId);
        userPoseTracker?.SetSimulatedPhysicalPose(currentSpec.simulatedPhysicalPosition, currentSpec.simulatedPhysicalYawDegrees);
        taskController?.ConfigureTask(currentSpec.ToTaskDefinition());
        riskEvaluator?.EvaluateNow();
        WriteStatus();

        if (requestGuidanceOnApply)
        {
            RequestGuidance();
        }
    }

    /// <summary>
    /// 优先通过完整协调器请求 guidance；协调器缺失时以当前实验配置直接调用 AgentClient。
    /// </summary>
    public void RequestGuidance()
    {
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.RequestNextGuidance();
            return;
        }

        if (agentClient != null && currentSpec != null)
        {
            agentClient.RequestGuidance(BuildFallbackContext(currentSpec));
        }
    }

    /// <summary>循环切换到下一个实验条件并开始新的实验会话。</summary>
    public void NextCondition()
    {
        conditionIndex++;
        ApplySelection();
    }

    /// <summary>循环切换到下一个任务并开始新的实验会话。</summary>
    public void NextTask()
    {
        taskIndex++;
        ApplySelection();
    }

    /// <summary>循环切换到下一个 physical space 并开始新的实验会话。</summary>
    public void NextPhysicalSpace()
    {
        physicalSpaceIndex++;
        ApplySelection();
    }

    private GuidanceRequestContext BuildFallbackContext(ExperimentConditionSpec spec)
    {
        return new GuidanceRequestContext
        {
            condition = spec.condition,
            task_id = spec.taskId,
            current_exhibit = spec.startExhibitId,
            semantic_next_target = spec.semanticNextTargetId,
            participant_id = spec.participantId,
            user_task = spec.userTask
        };
    }

    private void Subscribe()
    {
        if (agentClient != null)
        {
            // Configure 和 OnEnable 都可能连接事件，先移除可确保一次外部事件只记录一条日志。
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceReceived.AddListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
        }

        if (taskController != null)
        {
            taskController.StateChanged -= HandleTaskStateChanged;
            taskController.StateChanged += HandleTaskStateChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }
    }

    private void Unsubscribe()
    {
        // Dashboard 禁用或替换依赖后必须解绑，避免旧面板继续写日志或刷新已失效 UI。
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        }

        if (taskController != null)
        {
            taskController.StateChanged -= HandleTaskStateChanged;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        }
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        experimentLogger?.LogRequest(request, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void HandleResponseReceived(GuidanceResponse response)
    {
        experimentLogger?.LogResponse(response, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null);
    }

    private void HandleRequestFailed(string message)
    {
        experimentLogger?.LogStatus("request_failed", taskController != null ? taskController.RuntimeState : null, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null, message);
    }

    private void HandleTaskStateChanged(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus("task_state_changed", state, riskEvaluator != null ? riskEvaluator.CurrentRiskState : null, "task state changed");
        WriteStatus();
    }

    private void HandleRiskStateChanged(PhysicalRiskState state)
    {
        experimentLogger?.LogStatus("risk_state_changed", taskController != null ? taskController.RuntimeState : null, state, "risk state changed");
    }

    private void WriteStatus()
    {
        if (statusText == null || currentSpec == null)
        {
            return;
        }

        statusText.text =
            $"Participant: {currentSpec.participantId}\n" +
            $"Condition: {currentSpec.condition}\n" +
            $"Task: {currentSpec.taskId}\n" +
            $"Physical Space: {currentSpec.physicalSpaceId}\n" +
            $"Matrix: {ExperimentConditionMatrix.TotalCombinations} combinations";
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }

        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }

        if (taskController == null)
        {
            taskController = FindObjectOfType<GuidanceTaskController>();
        }

        if (guidanceCoordinator == null)
        {
            guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        }

        if (agentClient == null)
        {
            agentClient = FindObjectOfType<AgentClient>();
        }

        if (experimentLogger == null)
        {
            experimentLogger = FindObjectOfType<ExperimentLogger>();
        }
    }
}
