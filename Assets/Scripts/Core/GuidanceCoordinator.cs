using System;
using UnityEngine;

/// <summary>
/// 表示单次 Guidance 请求从规划到执行结束的生命周期。
/// </summary>
public enum GuidanceRequestPhase
{
    /// <summary>当前没有 Guidance 请求。</summary>
    None,
    /// <summary>请求已发出，等待响应。</summary>
    Requested,
    /// <summary>响应已到达，尚未完成校验。</summary>
    Received,
    /// <summary>响应已通过后端与本地校验。</summary>
    Verified,
    /// <summary>Guidance 正在展示并等待用户完成动作。</summary>
    Executing,
    /// <summary>用户完成当前 Guidance 对应动作。</summary>
    Completed,
    /// <summary>请求或执行因暂停、到达或新决策被取消。</summary>
    Cancelled,
    /// <summary>响应不再对应当前决策或状态。</summary>
    Expired,
    /// <summary>请求、解析或验证发生失败。</summary>
    Failed
}

/// <summary>
/// 描述一次 Hard Runtime Safety 干预，保留原始响应与拒绝原因供日志还原。
/// </summary>
public sealed class RuntimeInterventionInfo
{
    /// <summary>被 Hard Runtime Safety 阻止的原始 Guidance。</summary>
    public GuidanceResponse Response { get; }
    /// <summary>运行时拒绝原因。</summary>
    public string Reason { get; }

    /// <summary>创建一条不可变 Runtime Intervention 事件。</summary>
    public RuntimeInterventionInfo(GuidanceResponse response, string reason)
    {
        Response = response;
        Reason = reason;
    }
}

/// <summary>
/// 协调任务状态、物理风险与后端 guidance 请求，并将通过本地安全校验的响应交给表现层执行。
/// 输入来自任务、导航和 RDW 模块，执行结果提供给导览表现、到达检测与旋转增益控制器。
/// </summary>
public sealed class GuidanceCoordinator : MonoBehaviour
{
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private SemanticTargetSelector semanticTargetSelector;
    [SerializeField] private ArrivalDetector arrivalDetector;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private LocalGuidanceValidator localValidator;
    [SerializeField] private PauseResetController pauseResetController;
    [SerializeField] private RotationGainController rotationGainController;
    [SerializeField] private RejectionPanel rejectionPanel;

    private GuidanceRequestSnapshot activeSnapshot;
    private IGuidanceRequestClient requestClient;
    private GuidanceRequestPhase requestPhase;

    public event Action<string> GuidanceRejected;
    /// <summary>到达中间路线目标并准备请求下一段 Guidance 时触发。</summary>
    public event Action<GuidanceRuntimeState> Replanning;
    /// <summary>到达最终目标、任务进入 Completed 时触发。</summary>
    public event Action<GuidanceRuntimeState> TaskCompleted;
    /// <summary>Guidance 请求生命周期发生变化时触发。</summary>
    public event Action<GuidanceRequestPhase> RequestPhaseChanged;
    /// <summary>Hard Runtime Safety 阻止危险 Guidance 时触发。</summary>
    public event Action<RuntimeInterventionInfo> RuntimeIntervention;
    /// <summary>通过全部验证并开始执行 Guidance 时触发。</summary>
    public event Action<GuidanceResponse> GuidanceExecuted;
    /// <summary>响应因请求身份或状态变化过期时触发。</summary>
    public event Action<string> GuidanceExpired;

    /// <summary>
    /// 当前任务运行状态；任务控制器未配置时返回 null。
    /// </summary>
    public GuidanceRuntimeState RuntimeState => taskController != null ? taskController.RuntimeState : null;
    /// <summary>当前 Guidance 请求生命周期状态。</summary>
    public GuidanceRequestPhase RequestPhase => requestPhase;
    /// <summary>当前活动请求 ID；尚未构建请求时为空。</summary>
    public string ActiveRequestId => activeSnapshot != null ? activeSnapshot.requestId : string.Empty;
    /// <summary>当前活动决策 ID；尚未构建请求时为空。</summary>
    public string ActiveDecisionId => activeSnapshot != null ? activeSnapshot.decisionId : string.Empty;

    private void OnEnable()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }
        BindRequestCapture();
        BindArrival();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
    }

    /// <summary>
    /// 注入完整导览链依赖，并重新绑定请求快照与到达事件。
    /// </summary>
    public void Configure(
        AgentClient client,
        GuidanceExecutor executor,
        GuidanceTaskController task,
        NavigationTargetManager targetManager,
        SemanticTargetSelector selector,
        ArrivalDetector detector,
        PhysicalRiskEvaluator evaluator,
        LocalGuidanceValidator validator,
        PauseResetController resetController,
        RotationGainController gainController,
        RejectionPanel rejection)
    {
        UnbindRequestCapture();
        UnbindArrival();

        if (client != null)
        {
            agentClient = client;
            requestClient = client;
        }
        if (executor != null) guidanceExecutor = executor;
        if (task != null) taskController = task;
        if (targetManager != null) navigationTargets = targetManager;
        if (selector != null) semanticTargetSelector = selector;
        if (detector != null) arrivalDetector = detector;
        if (evaluator != null) riskEvaluator = evaluator;
        if (validator != null) localValidator = validator;
        if (resetController != null) pauseResetController = resetController;
        if (gainController != null) rotationGainController = gainController;
        if (rejection != null) rejectionPanel = rejection;

        BindRequestCapture();
        BindArrival();
    }

    /// <summary>
    /// 替换请求传输实现；正式运行使用 AgentClient，流程测试可使用内存客户端。
    /// </summary>
    public void ConfigureRequestClient(IGuidanceRequestClient client)
    {
        requestClient = client;
    }

    /// <summary>
    /// 根据最新物理风险选择安全语义目标并发起 guidance 请求；无可用目标时进入暂停重置流程。
    /// </summary>
    public void RequestNextGuidance()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }

        if (requestClient == null || taskController == null || !taskController.RuntimeState.IsNavigating ||
            requestClient.RequestInFlight)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        var selectedTarget = semanticTargetSelector != null
            ? semanticTargetSelector.SelectNextTarget(taskController.RuntimeState, riskState, conditionPolicy)
            : taskController.RuntimeState.nextTargetId;

        if (string.IsNullOrWhiteSpace(selectedTarget))
        {
            SetRequestPhase(GuidanceRequestPhase.Failed);
            pauseResetController?.PauseForReset("No safe semantic navigation candidate is available.");
            taskController.PauseForReset();
            return;
        }

        arrivalDetector?.WatchTarget(selectedTarget);
        var context = taskController.BuildRequestContext(selectedTarget);
        context.decision_id = Guid.NewGuid().ToString("N");
        context.state_version = taskController.RuntimeState.stateVersion;
        SetRequestPhase(GuidanceRequestPhase.Requested);
        requestClient.RequestGuidance(context);
    }

    /// <summary>
    /// 对后端响应执行本地时效与安全校验，通过后更新导览表现、到达目标和旋转增益。
    /// </summary>
    /// <param name="response">后端返回的 guidance；null 会被本地校验拒绝。</param>
    public void HandleGuidanceResponse(GuidanceResponse response)
    {
        var runtimeState = taskController != null ? taskController.RuntimeState : null;
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;

        if (runtimeState == null || !runtimeState.IsNavigating)
        {
            guidanceExecutor?.Clear();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Received);

        if (activeSnapshot != null && !activeSnapshot.MatchesResponse(response))
        {
            ExpireAndReplan("Guidance response identity does not match the active request.");
            return;
        }

        if (response == null || response.verifier == null)
        {
            guidanceExecutor?.Apply(response);
            var verifierReason = response == null
                ? "Guidance response is null."
                : "Guidance verifier result is missing.";
            SetRequestPhase(GuidanceRequestPhase.Failed);
            GuidanceRejected?.Invoke(string.IsNullOrWhiteSpace(verifierReason) ? "Guidance rejected by verifier." : verifierReason);
            return;
        }

        if (!response.verifier.approved)
        {
            TriggerRuntimeIntervention(response, response.verifier.reason);
            return;
        }

        // 请求发出后任务或物理风险可能已变化，旧响应必须在任何表现执行前被拒绝。
        if (localValidator != null &&
            !localValidator.Validate(response, activeSnapshot, runtimeState, riskState, out var reason))
        {
            if (reason.IndexOf("state changed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExpireAndReplan(reason);
                return;
            }

            TriggerRuntimeIntervention(response, reason);
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Verified);
        guidanceExecutor?.Apply(response);
        if (response != null && !string.IsNullOrWhiteSpace(response.selected_target))
        {
            arrivalDetector?.WatchTarget(response.selected_target);
        }

        rotationGainController?.QueueFromMovementInstruction(response != null ? response.movement_instruction : string.Empty);
        SetRequestPhase(GuidanceRequestPhase.Executing);
        GuidanceExecuted?.Invoke(response);
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        activeSnapshot = GuidanceRequestSnapshot.Capture(
            request,
            riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
            taskController != null ? taskController.RuntimeState : null);
        SetRequestPhase(GuidanceRequestPhase.Requested);
    }

    /// <summary>
    /// 处理当前路线目标到达：取消旧执行、推进任务，并在未完成时自动重新规划下一段 Guidance。
    /// </summary>
    public void HandleTargetArrived(string targetId)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        if (state == null || !state.IsNavigating ||
            !string.Equals(targetId, state.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        if (requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Completed);
        }
        else if (requestPhase == GuidanceRequestPhase.Requested ||
                 requestPhase == GuidanceRequestPhase.Received ||
                 requestPhase == GuidanceRequestPhase.Verified)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }

        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.StopGain();
        activeSnapshot = null;

        var previousVersion = state.stateVersion;
        taskController.HandleArrived(targetId);
        if (state.stateVersion == previousVersion)
        {
            return;
        }

        if (state.IsCompleted)
        {
            arrivalDetector?.WatchTarget(null);
            TaskCompleted?.Invoke(state);
            return;
        }

        Replanning?.Invoke(state);
        RequestNextGuidance();
    }

    /// <summary>
    /// 取消当前 Guidance 请求和表现，使暂停后的旧响应无法继续执行。
    /// </summary>
    public void CancelActiveGuidance()
    {
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.StopGain();
        arrivalDetector?.WatchTarget(null);
        activeSnapshot = null;
        if (requestPhase != GuidanceRequestPhase.None &&
            requestPhase != GuidanceRequestPhase.Completed &&
            requestPhase != GuidanceRequestPhase.Cancelled)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }
    }

    private void SetRequestPhase(GuidanceRequestPhase nextPhase)
    {
        if (requestPhase == nextPhase)
        {
            return;
        }

        requestPhase = nextPhase;
        RequestPhaseChanged?.Invoke(requestPhase);
    }

    private void TriggerRuntimeIntervention(GuidanceResponse response, string reason)
    {
        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Hard Runtime Safety rejected unsafe guidance."
            : reason;
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.StopGain();
        arrivalDetector?.WatchTarget(null);
        activeSnapshot = null;
        SetRequestPhase(GuidanceRequestPhase.Failed);
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(response, resolvedReason));
        pauseResetController?.PauseForReset("请停止移动，等待安全引导。");
        taskController?.PauseForReset();
    }

    private void ExpireAndReplan(string reason)
    {
        guidanceExecutor?.Clear();
        rotationGainController?.StopGain();
        activeSnapshot = null;
        SetRequestPhase(GuidanceRequestPhase.Expired);
        GuidanceExpired?.Invoke(reason);
        RequestNextGuidance();
    }

    private void BindRequestCapture()
    {
        if (agentClient != null)
        {
            // 先解除再订阅，避免 Configure 与 OnEnable 交错时重复捕获同一次请求。
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
        }
    }

    private void UnbindRequestCapture()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        }
    }

    private void HandleRequestFailed(string _)
    {
        SetRequestPhase(GuidanceRequestPhase.Failed);
    }

    private void BindArrival()
    {
        if (arrivalDetector != null)
        {
            // 到达事件会推进任务状态，重复订阅将导致一次到达跨越多个路线节点。
            arrivalDetector.TargetArrived -= HandleTargetArrived;
            arrivalDetector.TargetArrived += HandleTargetArrived;
        }
    }

    private void UnbindArrival()
    {
        if (arrivalDetector != null)
        {
            arrivalDetector.TargetArrived -= HandleTargetArrived;
        }
    }
}
