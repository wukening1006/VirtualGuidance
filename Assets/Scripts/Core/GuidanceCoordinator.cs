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
    /// <summary>新 Guidance Target 已选定并已发起对应请求时触发。</summary>
    public event Action<GuidanceTarget> GuidanceTargetSelected;
    /// <summary>用户到达并确认完成当前可导航目标时触发。</summary>
    public event Action<GuidanceTarget> TargetReached;
    /// <summary>NavigableDetour 完成且准备返回原路线时触发。</summary>
    public event Action<GuidanceTarget> DetourCompleted;

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
    /// <summary>当前由任务状态持有的 Guidance Target。</summary>
    public GuidanceTarget CurrentGuidanceTarget => taskController != null
        ? taskController.RuntimeState.CurrentGuidanceTarget
        : null;

    private void OnEnable()
    {
        if (requestClient == null)
        {
            requestClient = agentClient;
        }
        BindRequestCapture();
        BindArrival();
        BindRotationGain();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
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
        UnbindRotationGain();

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
        BindRotationGain();
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
            ? semanticTargetSelector.SelectNextGuidanceTarget(taskController.RuntimeState, riskState, conditionPolicy)
            : new GuidanceTarget(
                taskController.RuntimeState.nextTargetId,
                string.Equals(
                    taskController.RuntimeState.nextTargetId,
                    taskController.RuntimeState.finalTargetId,
                    StringComparison.Ordinal)
                    ? NavigationTargetRole.FinalTarget
                    : NavigationTargetRole.RouteSubgoal);

        if (selectedTarget == null || string.IsNullOrWhiteSpace(selectedTarget.TargetId) ||
            !taskController.TrySetGuidanceTarget(selectedTarget))
        {
            SetRequestPhase(GuidanceRequestPhase.Failed);
            pauseResetController?.PauseForReset("No safe semantic navigation candidate is available.");
            taskController.PauseForReset();
            return;
        }

        arrivalDetector?.WatchTarget(
            selectedTarget.TargetRole == NavigationTargetRole.TurnCueAnchor
                ? null
                : selectedTarget.TargetId);
        var context = taskController.BuildRequestContext(selectedTarget.TargetId);
        context.calibration_id = riskState != null ? riskState.calibration_id : string.Empty;
        context.risk_snapshot_id = riskState != null ? riskState.risk_snapshot_id : string.Empty;
        context.decision_id = Guid.NewGuid().ToString("N");
        context.state_version = taskController.RuntimeState.stateVersion;
        context.target_role = GuidanceTypeUtility.TargetRoleToProtocol(selectedTarget.TargetRole);
        SetRequestPhase(GuidanceRequestPhase.Requested);
        requestClient.RequestGuidance(context);
        GuidanceTargetSelected?.Invoke(selectedTarget);
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
        if (response != null && !string.IsNullOrWhiteSpace(response.selected_target) &&
            (CurrentGuidanceTarget == null ||
             CurrentGuidanceTarget.TargetRole != NavigationTargetRole.TurnCueAnchor))
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
        var guidanceTarget = state != null ? state.CurrentGuidanceTarget : null;
        if (state == null || !state.IsNavigating || guidanceTarget == null ||
            guidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor ||
            !string.Equals(targetId, guidanceTarget.TargetId, StringComparison.Ordinal))
        {
            return;
        }

        FinishActiveGuidanceForProgress();

        if (guidanceTarget.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            if (!taskController.HandleDetourArrived(targetId))
            {
                return;
            }

            TargetReached?.Invoke(guidanceTarget);
            DetourCompleted?.Invoke(guidanceTarget);
            Replanning?.Invoke(state);
            RequestNextGuidance();
            return;
        }

        if (!string.Equals(targetId, state.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        var previousVersion = state.stateVersion;
        taskController.HandleArrived(targetId);
        if (state.stateVersion == previousVersion)
        {
            return;
        }

        TargetReached?.Invoke(guidanceTarget);
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
    /// 接收用户已完成 TurnCueAnchor 转向的反馈；不触发普通 Arrival Task Progress。
    /// </summary>
    public void HandleTurnCueCompleted()
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        var target = state != null ? state.CurrentGuidanceTarget : null;
        if (state == null || !state.IsNavigating || target == null ||
            target.TargetRole != NavigationTargetRole.TurnCueAnchor)
        {
            return;
        }

        FinishActiveGuidanceForProgress();
        if (!taskController.HandleTurnCueCompleted(target.TargetId))
        {
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
        rotationGainController?.CancelGain("guidance_cancelled");
        arrivalDetector?.WatchTarget(null);
        activeSnapshot = null;
        if (requestPhase != GuidanceRequestPhase.None &&
            requestPhase != GuidanceRequestPhase.Completed &&
            requestPhase != GuidanceRequestPhase.Cancelled)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }
    }

    /// <summary>
    /// ResetCompleted 且风险重算安全后恢复原任务，发布 Replanning 并请求下一段 Guidance。
    /// </summary>
    /// <returns>任务从 PausedForReset 恢复并进入导航时返回 true。</returns>
    public bool ResumeAfterResetAndReplan()
    {
        if (taskController == null ||
            taskController.RuntimeState.phase != GuidanceRuntimePhase.PausedForReset)
        {
            return false;
        }

        taskController.ResumeAfterReset();
        var state = taskController.RuntimeState;
        if (!state.IsNavigating)
        {
            return false;
        }

        Replanning?.Invoke(state);
        RequestNextGuidance();
        return true;
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

    private void FinishActiveGuidanceForProgress()
    {
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
        rotationGainController?.CancelGain("guidance_completed");
        activeSnapshot = null;
    }

    private void TriggerRuntimeIntervention(GuidanceResponse response, string reason)
    {
        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Hard Runtime Safety rejected unsafe guidance."
            : reason;
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.AbortGain("runtime_intervention");
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
        rotationGainController?.CancelGain("guidance_expired");
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

    private void BindRotationGain()
    {
        if (rotationGainController != null)
        {
            // 完成物理转向后必须先重算风险，不能由 Guidance 展示直接推进 TurnCue。
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
            rotationGainController.ReevaluationRequested += HandleGainReevaluationRequested;
        }
    }

    private void UnbindRotationGain()
    {
        if (rotationGainController != null)
        {
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
        }
    }

    private void HandleGainReevaluationRequested()
    {
        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var resetRequired = riskState != null && riskState.reset_required;
        var criticalRisk = riskState != null &&
                           (string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(riskState.reset_risk, "critical", StringComparison.OrdinalIgnoreCase));
        if (resetRequired || criticalRisk)
        {
            var reason = resetRequired ? "reset_required" : "risk_critical";
            rotationGainController?.CompleteReevaluation(false, reason);
            CancelActiveGuidance();
            pauseResetController?.PauseForReset("请停止移动并按安全提示完成重置。");
            taskController?.PauseForReset();
            return;
        }

        rotationGainController?.CompleteReevaluation(true);
        if (CurrentGuidanceTarget != null &&
            CurrentGuidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor)
        {
            HandleTurnCueCompleted();
        }
    }
}
