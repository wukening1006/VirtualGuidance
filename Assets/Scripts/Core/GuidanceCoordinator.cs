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
/// 描述一次运行期安全干预，保留原始响应、判定与原因供日志还原。
/// </summary>
public sealed class RuntimeInterventionInfo
{
    /// <summary>被运行期安全策略中断的原始 Guidance。</summary>
    public GuidanceResponse Response { get; }
    /// <summary>运行时拒绝原因。</summary>
    public string Reason { get; }
    /// <summary>本次运行期安全判定的动作类型。</summary>
    public RuntimeGuidanceSafetyDecision Decision { get; }
    /// <summary>本次 HardStop 是否由 Critical/Reset Required 触发并允许启动 Reset。</summary>
    public bool StartReset { get; }

    /// <summary>创建一条不可变 Runtime Intervention 事件。</summary>
    public RuntimeInterventionInfo(
        GuidanceResponse response,
        string reason,
        RuntimeGuidanceSafetyDecision decision = RuntimeGuidanceSafetyDecision.HardStop,
        bool startReset = false)
    {
        Response = response;
        Reason = reason;
        Decision = decision;
        StartReset = startReset;
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
    [SerializeField] private MotionDetector motionDetector;
    [SerializeField] private RejectionPanel rejectionPanel;

    [Header("TurnCue 物理完成")]
    [Tooltip("目标相对偏航不超过该角度时不创建 TurnCue，单位：度。")]
    [SerializeField] private float turnCueNoCueToleranceDegrees = 12f;
    [Tooltip("累计反向转动达到该角度时中止 TurnCue，单位：度。")]
    [SerializeField] private float turnCueReverseAbortDegrees = 5f;
    [Tooltip("进入目标朝向后需要保持稳定的时间，单位：秒。")]
    [SerializeField] private float turnCueStableCompletionSeconds = 0.25f;
    [Tooltip("等待 TurnCue 完成的最长时间，单位：秒。")]
    [SerializeField] private float turnCueTimeoutSeconds = 8f;

    private GuidanceRequestSnapshot activeSnapshot;
    private GuidanceResponse activeResponse;
    private IGuidanceRequestClient requestClient;
    private GuidanceRequestPhase requestPhase;
    private readonly RuntimeGuidanceSafetyPolicy runtimeSafetyPolicy = new RuntimeGuidanceSafetyPolicy();
    private int consecutiveUnsafeSamples;
    private string sampledUnsafeDirection = string.Empty;
    private string lastRuntimeRiskSnapshotId = string.Empty;
    private bool runtimeReplanPending;
    private GuidanceTarget pendingRuntimeSafetyTarget;
    private readonly TurnCueCompletionTracker turnCueCompletionTracker = new TurnCueCompletionTracker();
    private bool handlingGainTermination;

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
        BindRuntimeSafety();
        BindMotionDetector();
        ConfigureTurnCueTracker();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
        UnbindRuntimeSafety();
        UnbindMotionDetector();
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
        RejectionPanel rejection,
        MotionDetector turnDetector = null)
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
        UnbindRuntimeSafety();
        UnbindMotionDetector();

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
        if (turnDetector != null) motionDetector = turnDetector;
        if (rejection != null) rejectionPanel = rejection;

        BindRequestCapture();
        BindArrival();
        BindRotationGain();
        BindRuntimeSafety();
        BindMotionDetector();
        ConfigureTurnCueTracker();
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
        var selectedTarget = pendingRuntimeSafetyTarget ?? (semanticTargetSelector != null
            ? semanticTargetSelector.SelectNextGuidanceTarget(taskController.RuntimeState, riskState, conditionPolicy)
            : new GuidanceTarget(
                taskController.RuntimeState.nextTargetId,
                string.Equals(
                    taskController.RuntimeState.nextTargetId,
                    taskController.RuntimeState.finalTargetId,
                    StringComparison.Ordinal)
                    ? NavigationTargetRole.FinalTarget
                    : NavigationTargetRole.RouteSubgoal));
        pendingRuntimeSafetyTarget = null;

        var decisionId = Guid.NewGuid().ToString("N");
        if (selectedTarget == null || string.IsNullOrWhiteSpace(selectedTarget.TargetId) ||
            !taskController.TryReserveGuidanceTarget(selectedTarget, decisionId))
        {
            TriggerRuntimeIntervention(
                null,
                "No eligible semantic navigation candidate is available.",
                false);
            return;
        }

        var reservation = taskController.RuntimeState.ActiveReservation;
        arrivalDetector?.WatchReservation(
            selectedTarget.TargetRole == NavigationTargetRole.TurnCueAnchor
                ? null
                : reservation);
        var context = taskController.BuildRequestContext(selectedTarget.TargetId);
        context.calibration_id = riskState != null ? riskState.calibration_id : string.Empty;
        context.risk_snapshot_id = riskState != null ? riskState.risk_snapshot_id : string.Empty;
        context.decision_id = decisionId;
        context.state_version = reservation.TaskStateVersion;
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

        if (activeSnapshot == null)
        {
            return;
        }

        if (response == null)
        {
            SetRequestPhase(GuidanceRequestPhase.Failed);
            GuidanceRejected?.Invoke("Guidance response is null.");
            return;
        }

        if (!activeSnapshot.MatchesResponse(response))
        {
            // 旧响应不得改变当前请求阶段、取消新请求或触发重规划。
            return;
        }

        if (runtimeState == null || !runtimeState.IsNavigating)
        {
            guidanceExecutor?.Clear();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Received);

        if (response.verifier == null)
        {
            guidanceExecutor?.Apply(response);
            var verifierReason = "Guidance verifier result is missing.";
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
        var activeReservation = runtimeState.ActiveReservation;
        if (activeReservation == null ||
            !string.Equals(activeReservation.TargetId, response.selected_target, StringComparison.Ordinal) ||
            !taskController.CommitReservation(activeReservation))
        {
            TriggerRuntimeIntervention(response, "Guidance response target does not match the active reservation.");
            return;
        }
        guidanceExecutor?.Apply(response);
        if (response != null && !string.IsNullOrWhiteSpace(response.selected_target) &&
            (CurrentGuidanceTarget == null ||
             CurrentGuidanceTarget.TargetRole != NavigationTargetRole.TurnCueAnchor))
        {
            arrivalDetector?.WatchReservation(activeReservation);
        }

        activeResponse = response;
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Executing);
        GuidanceExecuted?.Invoke(response);
        if (CurrentGuidanceTarget != null &&
            CurrentGuidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor)
        {
            BeginTurnCueTracking(response);
        }
        else
        {
            turnCueCompletionTracker.Cancel();
            rotationGainController?.QueueFromMovementInstruction(
                response.movement_instruction,
                activeSnapshot != null ? activeSnapshot.decisionId : string.Empty);
        }
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
    public void HandleTargetArrived(TargetReservation reservation)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        var guidanceTarget = state != null ? state.CurrentGuidanceTarget : null;
        if (state == null || !state.IsNavigating || guidanceTarget == null ||
            !state.MatchesActiveReservation(reservation) ||
            guidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor ||
            !string.Equals(reservation.TargetId, guidanceTarget.TargetId, StringComparison.Ordinal))
        {
            return;
        }

        FinishActiveGuidanceForProgress();

        if (guidanceTarget.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            if (!taskController.HandleDetourArrived(reservation.TargetId))
            {
                return;
            }

            TargetReached?.Invoke(guidanceTarget);
            DetourCompleted?.Invoke(guidanceTarget);
            Replanning?.Invoke(state);
            RequestNextGuidance();
            return;
        }

        if (!string.Equals(reservation.TargetId, state.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        var previousVersion = state.stateVersion;
        taskController.HandleArrived(reservation.TargetId);
        if (state.stateVersion == previousVersion)
        {
            return;
        }

        TargetReached?.Invoke(guidanceTarget);
        if (state.IsCompleted)
        {
            arrivalDetector?.WatchReservation(null);
            TaskCompleted?.Invoke(state);
            return;
        }

        Replanning?.Invoke(state);
        RequestNextGuidance();
    }

    /// <summary>测试与研究工具入口：仅把当前活动预留对应的目标视为有效到达。</summary>
    public void HandleTargetArrived(string targetId)
    {
        var reservation = taskController != null ? taskController.RuntimeState.ActiveReservation : null;
        if (reservation != null && string.Equals(targetId, reservation.TargetId, StringComparison.Ordinal))
        {
            HandleTargetArrived(reservation);
        }
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
        arrivalDetector?.WatchReservation(null);
        taskController?.AbandonReservation();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        pendingRuntimeSafetyTarget = null;
        ResetRuntimeSafetySampling();
        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received ||
            requestPhase == GuidanceRequestPhase.Verified ||
            requestPhase == GuidanceRequestPhase.Executing)
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
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        pendingRuntimeSafetyTarget = null;
        ResetRuntimeSafetySampling();
    }

    private void TriggerRuntimeIntervention(GuidanceResponse response, string reason, bool startReset = false)
    {
        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Hard Runtime Safety rejected unsafe guidance."
            : reason;
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        rotationGainController?.AbortGain("runtime_intervention");
        arrivalDetector?.WatchReservation(null);
        taskController?.AbandonReservation();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        pendingRuntimeSafetyTarget = null;
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Failed);
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            response,
            resolvedReason,
            RuntimeGuidanceSafetyDecision.HardStop,
            startReset));
    }

    private void ExpireAndReplan(string reason)
    {
        guidanceExecutor?.Clear();
        rotationGainController?.CancelGain("guidance_expired");
        arrivalDetector?.WatchReservation(null);
        taskController?.AbandonReservation();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        pendingRuntimeSafetyTarget = null;
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Expired);
        GuidanceExpired?.Invoke(reason);
        RequestNextGuidance();
    }

    private void BindRuntimeSafety()
    {
        if (riskEvaluator == null || !isActiveAndEnabled)
        {
            return;
        }

        riskEvaluator.RiskStateChanged -= HandleRuntimeRiskStateChanged;
        riskEvaluator.RiskStateChanged += HandleRuntimeRiskStateChanged;
    }

    private void UnbindRuntimeSafety()
    {
        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRuntimeRiskStateChanged;
        }
    }

    private void HandleRuntimeRiskStateChanged(PhysicalRiskState riskState)
    {
        if (requestPhase != GuidanceRequestPhase.Executing ||
            activeSnapshot == null ||
            activeResponse == null ||
            runtimeReplanPending)
        {
            return;
        }

        var plannedDirection = string.Empty;
        var directionAvailable = navigationTargets != null &&
                                 navigationTargets.TryGetTargetDirection(
                                     activeResponse.selected_target,
                                     out plannedDirection);
        var plannedClearance = 0f;
        var clearanceAvailable = directionAvailable &&
                                 riskEvaluator != null &&
                                 riskEvaluator.TryGetDirectionClearance(
                                     riskState,
                                     plannedDirection,
                                     out plannedClearance);

        UpdateUnsafeSampleCount(riskState, plannedDirection, directionAvailable);
        var safeCandidate = FindRuntimeSafetyCandidate(riskState, activeResponse.selected_target);
        var input = new RuntimeGuidanceSafetyInput(
            activeSnapshot.decisionId,
            activeResponse.selected_target,
            activeResponse.movement_instruction,
            riskState,
            riskEvaluator != null && riskEvaluator.IsTrackingValid,
            riskEvaluator != null && riskEvaluator.IsCalibrationValid,
            riskEvaluator != null && riskEvaluator.IsRiskIdentityCurrent(riskState),
            plannedDirection,
            plannedClearance,
            directionAvailable && clearanceAvailable,
            clearanceAvailable && riskEvaluator.IsCriticalClearance(plannedClearance),
            pauseResetController != null ? pauseResetController.State : ResetLifecycleState.Inactive,
            consecutiveUnsafeSamples,
            safeCandidate != null);
        var decision = runtimeSafetyPolicy.Evaluate(input);
        if (decision == RuntimeGuidanceSafetyDecision.SoftReroute)
        {
            TriggerSoftReroute(safeCandidate);
        }
        else if (decision == RuntimeGuidanceSafetyDecision.HardStop)
        {
            var startReset = riskState != null &&
                             (riskState.reset_required || input.PlannedDirectionCritical);
            TriggerRuntimeIntervention(
                activeResponse,
                RuntimeSafetyReason(input),
                startReset);
        }
    }

    private void TriggerSoftReroute(
        GuidanceTarget safeCandidate,
        string reason = "Active planned direction remained unsafe for two consecutive risk samples.")
    {
        if (runtimeReplanPending || safeCandidate == null)
        {
            return;
        }

        runtimeReplanPending = true;
        var interruptedResponse = activeResponse;
        try
        {
            requestClient?.CancelPendingRequest();
            guidanceExecutor?.Clear();
            rotationGainController?.AbortGain("soft_reroute");
            arrivalDetector?.WatchReservation(null);
            taskController?.AbandonReservation();
            activeSnapshot = null;
            activeResponse = null;
            turnCueCompletionTracker.Cancel();
            ResetRuntimeSafetySampling();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
                interruptedResponse,
                reason,
                RuntimeGuidanceSafetyDecision.SoftReroute));
            pendingRuntimeSafetyTarget = safeCandidate;
            Replanning?.Invoke(taskController != null ? taskController.RuntimeState : null);
            RequestNextGuidance();
        }
        finally
        {
            runtimeReplanPending = false;
        }
    }

    private GuidanceTarget FindRuntimeSafetyCandidate(PhysicalRiskState riskState, string activeTargetId)
    {
        if (semanticTargetSelector == null || taskController == null || riskState == null)
        {
            return null;
        }

        var candidate = semanticTargetSelector.SelectNextGuidanceTarget(
            taskController.RuntimeState,
            riskState,
            ExperimentConditionMatrix.GetPolicy("ours_full"));
        return candidate != null &&
               !string.Equals(candidate.TargetId, activeTargetId, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private void UpdateUnsafeSampleCount(
        PhysicalRiskState riskState,
        string plannedDirection,
        bool directionAvailable)
    {
        if (!directionAvailable || !Contains(riskState != null ? riskState.unsafe_directions : null, plannedDirection))
        {
            ResetRuntimeSafetySampling();
            return;
        }

        if (riskState != null &&
            !string.IsNullOrWhiteSpace(riskState.risk_snapshot_id) &&
            string.Equals(lastRuntimeRiskSnapshotId, riskState.risk_snapshot_id, StringComparison.Ordinal))
        {
            return;
        }

        lastRuntimeRiskSnapshotId = riskState != null ? riskState.risk_snapshot_id : string.Empty;

        if (!string.Equals(sampledUnsafeDirection, plannedDirection, StringComparison.Ordinal))
        {
            sampledUnsafeDirection = plannedDirection;
            consecutiveUnsafeSamples = 1;
            return;
        }

        consecutiveUnsafeSamples++;
    }

    private void ResetRuntimeSafetySampling()
    {
        consecutiveUnsafeSamples = 0;
        sampledUnsafeDirection = string.Empty;
        lastRuntimeRiskSnapshotId = string.Empty;
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<string> values, string expected)
    {
        if (values == null || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RuntimeSafetyReason(RuntimeGuidanceSafetyInput input)
    {
        if (input == null || input.LatestRisk == null) return "Runtime risk snapshot is unavailable.";
        if (!input.TrackingValid) return "XR tracking is invalid.";
        if (!input.CalibrationValid) return "Physical calibration is invalid.";
        if (!input.PhysicalSpaceMatches) return "Physical space or risk identity does not match.";
        if (input.ResetState == ResetLifecycleState.Required ||
            input.ResetState == ResetLifecycleState.TurningToSafeDirection ||
            input.ResetState == ResetLifecycleState.Completed) return "Physical reset is active.";
        if (input.LatestRisk.reset_required) return "Physical risk requires reset.";
        if (!input.PlannedDirectionAvailable) return "Planned physical direction clearance is unavailable.";
        if (input.PlannedDirectionCritical) return "Planned physical direction clearance is critical.";
        return "No eligible runtime safety candidate is available.";
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
        arrivalDetector?.WatchReservation(null);
        taskController?.AbandonReservation();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        pendingRuntimeSafetyTarget = null;
        ResetRuntimeSafetySampling();
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

    private void ConfigureTurnCueTracker()
    {
        turnCueCompletionTracker.Configure(
            turnCueNoCueToleranceDegrees,
            turnCueReverseAbortDegrees,
            turnCueStableCompletionSeconds,
            turnCueTimeoutSeconds);
    }

    private void BindMotionDetector()
    {
        if (motionDetector == null || !isActiveAndEnabled)
        {
            return;
        }

        motionDetector.TurnSampled -= HandleTurnSampled;
        motionDetector.TurnSampled += HandleTurnSampled;
    }

    private void UnbindMotionDetector()
    {
        if (motionDetector != null)
        {
            motionDetector.TurnSampled -= HandleTurnSampled;
        }
    }

    private void BeginTurnCueTracking(GuidanceResponse response)
    {
        if (response == null || navigationTargets == null ||
            !navigationTargets.TryGetTargetRelativeYawDegrees(response.selected_target, out var relativeYawDegrees))
        {
            TriggerRuntimeIntervention(response, "TurnCue target relative yaw is unavailable.");
            return;
        }

        var directionSign = response.movement_instruction == "turn_left_slowly" ||
                            response.movement_instruction == "look_left"
            ? 1
            : -1;
        var result = turnCueCompletionTracker.Begin(directionSign, Mathf.Abs(relativeYawDegrees));
        if (result == TurnCueTrackingResult.Completed)
        {
            CompleteTurnCueAfterRiskReevaluation();
            return;
        }

        rotationGainController?.QueueFromMovementInstruction(
            response.movement_instruction,
            activeSnapshot != null ? activeSnapshot.decisionId : string.Empty,
            turnCueCompletionTracker.RequiredTurnYawDegrees);
    }

    private void HandleTurnSampled(bool isTurning, float signedYawDeltaDegrees, float deltaTimeSeconds)
    {
        if (requestPhase != GuidanceRequestPhase.Executing || !turnCueCompletionTracker.IsTracking)
        {
            return;
        }

        var result = turnCueCompletionTracker.ProcessSample(
            isTurning,
            signedYawDeltaDegrees,
            deltaTimeSeconds);
        if (result == TurnCueTrackingResult.Completed)
        {
            if (rotationGainController == null || !rotationGainController.IsActive)
            {
                CompleteTurnCueAfterRiskReevaluation();
            }
        }
        else if (result == TurnCueTrackingResult.Reversed)
        {
            HandleTurnCueSoftFailure("turn_cue_reverse");
        }
        else if (result == TurnCueTrackingResult.TimedOut)
        {
            HandleTurnCueSoftFailure("turn_cue_timeout");
        }
    }

    private void HandleTurnCueSoftFailure(string reason)
    {
        if (requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        handlingGainTermination = true;
        rotationGainController?.AbortGain(reason);
        handlingGainTermination = false;
        turnCueCompletionTracker.Cancel();
        TriggerSoftRerouteFromLatestRisk(reason);
    }

    private void TriggerSoftRerouteFromLatestRisk(string reason)
    {
        var riskState = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        if (riskState == null || riskEvaluator == null || !riskEvaluator.IsRiskIdentityCurrent(riskState))
        {
            TriggerRuntimeIntervention(activeResponse, $"{reason}: current risk snapshot is unavailable or stale.");
            return;
        }

        var activeTargetId = activeResponse != null ? activeResponse.selected_target : string.Empty;
        var safeCandidate = FindRuntimeSafetyCandidate(riskState, activeTargetId);
        if (safeCandidate != null)
        {
            TriggerSoftReroute(safeCandidate, reason);
            return;
        }

        TriggerRuntimeIntervention(activeResponse, $"{reason}: no eligible reroute candidate.");
    }

    private void CompleteTurnCueAfterRiskReevaluation()
    {
        if (requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var resetRequired = riskState != null && riskState.reset_required;
        var criticalRisk = riskState != null &&
                           (string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(riskState.reset_risk, "critical", StringComparison.OrdinalIgnoreCase));
        if (riskState == null ||
            riskEvaluator == null ||
            !riskEvaluator.IsRiskIdentityCurrent(riskState) ||
            resetRequired ||
            criticalRisk)
        {
            TriggerRuntimeIntervention(
                activeResponse,
                riskState == null ? "TurnCue reevaluation has no valid risk snapshot." : "TurnCue reevaluation is unsafe.",
                resetRequired || criticalRisk);
            return;
        }

        HandleTurnCueCompleted();
    }

    private void BindRotationGain()
    {
        if (rotationGainController != null)
        {
            // 完成物理转向后必须先重算风险，不能由 Guidance 展示直接推进 TurnCue。
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
            rotationGainController.ReevaluationRequested += HandleGainReevaluationRequested;
            rotationGainController.LifecycleEventOccurred -= HandleRotationGainLifecycleEvent;
            rotationGainController.LifecycleEventOccurred += HandleRotationGainLifecycleEvent;
        }
    }

    private void UnbindRotationGain()
    {
        if (rotationGainController != null)
        {
            rotationGainController.ReevaluationRequested -= HandleGainReevaluationRequested;
            rotationGainController.LifecycleEventOccurred -= HandleRotationGainLifecycleEvent;
        }
    }

    private void HandleRotationGainLifecycleEvent(RotationGainEventInfo info)
    {
        if (handlingGainTermination || info == null ||
            info.EventType != RotationGainEventType.GainCancelled ||
            requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        if (activeSnapshot == null ||
            !string.Equals(info.DecisionId, activeSnapshot.decisionId, StringComparison.Ordinal))
        {
            return;
        }

        if (info.Reason == "reverse_turn" ||
            info.Reason == "turn_cue_reverse" ||
            info.Reason == "turn_cue_timeout" ||
            info.Reason == "turn_stopped_timeout" ||
            info.Reason == "gain_limit_exceeded")
        {
            turnCueCompletionTracker.Cancel();
            TriggerSoftRerouteFromLatestRisk(info.Reason);
        }
        else if (info.Reason == "tracking_invalid" ||
                 info.Reason == "risk_critical" ||
                 info.Reason == "reset_required" ||
                 info.Reason == "rotation_root_invalid")
        {
            turnCueCompletionTracker.Cancel();
            TriggerRuntimeIntervention(
                activeResponse,
                info.Reason,
                info.Reason == "risk_critical" || info.Reason == "reset_required");
        }
    }

    private void HandleGainReevaluationRequested()
    {
        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var riskUnavailable = riskState == null ||
                              riskEvaluator == null ||
                              !riskEvaluator.IsRiskIdentityCurrent(riskState);
        var resetRequired = riskState != null && riskState.reset_required;
        var criticalRisk = riskState != null &&
                           (string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(riskState.reset_risk, "critical", StringComparison.OrdinalIgnoreCase));
        if (riskUnavailable || resetRequired || criticalRisk)
        {
            var reason = riskUnavailable
                ? "risk_snapshot_unavailable"
                : resetRequired ? "reset_required" : "risk_critical";
            handlingGainTermination = true;
            rotationGainController?.CompleteReevaluation(false, reason);
            handlingGainTermination = false;
            turnCueCompletionTracker.Cancel();
            TriggerRuntimeIntervention(activeResponse, reason, resetRequired || criticalRisk);
            return;
        }

        rotationGainController?.CompleteReevaluation(true);
        if (CurrentGuidanceTarget != null &&
            CurrentGuidanceTarget.TargetRole == NavigationTargetRole.TurnCueAnchor &&
            turnCueCompletionTracker.State == TurnCueTrackingState.Completed)
        {
            CompleteTurnCueAfterRiskReevaluation();
        }
    }
}
