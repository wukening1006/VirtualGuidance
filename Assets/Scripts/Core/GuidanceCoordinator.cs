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
    /// <summary>本次决策是否真正启动了自动 Reset。</summary>
    public bool StartReset { get; }
    /// <summary>本次自动 Reset 是会话内第几次尝试；非 Reset 决策为 0。</summary>
    public int ResetAttempt { get; }
    /// <summary>作出决策时是否存在本地验证通过的安全语义候选。</summary>
    public bool SafeCandidateAvailable { get; }

    /// <summary>创建一条不可变 Runtime Intervention 事件。</summary>
    public RuntimeInterventionInfo(
        GuidanceResponse response,
        string reason,
        RuntimeGuidanceSafetyDecision decision = RuntimeGuidanceSafetyDecision.HardStop,
        bool startReset = false,
        int resetAttempt = 0,
        bool safeCandidateAvailable = false)
    {
        Response = response;
        Reason = reason;
        Decision = decision;
        StartReset = startReset;
        ResetAttempt = Math.Max(0, resetAttempt);
        SafeCandidateAvailable = safeCandidateAvailable;
    }
}

/// <summary>
/// 表示自动物理朝向恢复的显式生命周期。
/// </summary>
public enum GuidanceRecoveryState
{
    /// <summary>当前没有自动恢复。</summary>
    Inactive,
    /// <summary>正在停止 Guidance、Gain 与在途请求。</summary>
    StoppingGuidance,
    /// <summary>正在从 Heading Profile 选择安全朝向。</summary>
    SelectingHeading,
    /// <summary>等待参与者原地转向冻结朝向。</summary>
    WaitingForUserTurn,
    /// <summary>参与者已到达目标朝向，正在等待稳定。</summary>
    Stabilizing,
    /// <summary>正在使用最新物理事实复验冻结朝向。</summary>
    Revalidating,
    /// <summary>物理恢复已完成，正在恢复任务并重新规划。</summary>
    Replanning,
    /// <summary>物理状态已明确不可恢复，流程终止。</summary>
    Terminated
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
    private readonly TurnCueCompletionTracker turnCueCompletionTracker = new TurnCueCompletionTracker();
    private bool handlingGainTermination;
    private bool recoveryDecisionPending;
    private int automaticResetAttempts;
    private string resetAbandonedTargetId = string.Empty;
    private readonly RecoveryHeadingPlanner recoveryHeadingPlanner = new RecoveryHeadingPlanner();
    private RecoveryHeadingPlan frozenRecoveryPlan;
    private GuidanceRecoveryState recoveryState = GuidanceRecoveryState.Inactive;
    private bool holdForReplan;
    private string holdRiskSnapshotId = string.Empty;

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
    /// <summary>自动物理恢复状态发生变化时触发。</summary>
    public event Action<GuidanceRecoveryState> RecoveryStateChanged;

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
    /// <summary>当前会话已真正启动的自动 Reset 次数。</summary>
    public int AutomaticResetAttempts => automaticResetAttempts;
    /// <summary>当前自动物理恢复状态。</summary>
    public GuidanceRecoveryState RecoveryState => recoveryState;
    /// <summary>当前恢复流程冻结的具体物理朝向计划。</summary>
    public RecoveryHeadingPlan FrozenRecoveryPlan => frozenRecoveryPlan;
    /// <summary>当前是否正在执行自动物理朝向恢复。</summary>
    public bool IsAutomaticRecoveryActive => IsResetRecoveryActive;
    /// <summary>当前是否因没有可执行语义候选而等待下一份事实重规划。</summary>
    public bool IsHoldingForReplan => holdForReplan;

    private bool IsResetRecoveryActive => pauseResetController != null && pauseResetController.IsActive;

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
        BindRecoveryController();
        BindMotionDetector();
        ConfigureTurnCueTracker();
    }

    private void OnDisable()
    {
        UnbindRequestCapture();
        UnbindArrival();
        UnbindRotationGain();
        UnbindRuntimeSafety();
        UnbindRecoveryController();
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
        UnbindRecoveryController();
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
        BindRecoveryController();
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
    /// 根据任务、Target Registry 与最新风险构建并冻结 Candidate Set；请求前不修改 Current Subgoal。
    /// </summary>
    public void RequestNextGuidance()
    {
        holdForReplan = false;
        holdRiskSnapshotId = string.Empty;
        if (requestClient == null)
        {
            requestClient = agentClient;
        }

        if (taskController == null || requestClient == null || semanticTargetSelector == null || riskEvaluator == null)
        {
            TriggerRuntimeIntervention(null, "Guidance planning dependencies are unavailable.");
            return;
        }

        if (!taskController.RuntimeState.IsNavigating || requestClient.RequestInFlight)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        var candidateSet = semanticTargetSelector != null
            ? semanticTargetSelector.BuildCandidateSet(taskController.RuntimeState, riskState, conditionPolicy)
            : null;
        var requestId = Guid.NewGuid().ToString("N");
        var decisionId = Guid.NewGuid().ToString("N");
        var candidateSetId = Guid.NewGuid().ToString("N");
        if (riskState == null || candidateSet == null)
        {
            TriggerRuntimeIntervention(null, "Guidance planning could not obtain current risk or candidate facts.");
            return;
        }

        if (!candidateSet.HasModelCandidates)
        {
            HandleUnavailableCandidatePlanning(riskState);
            return;
        }

        if (!taskController.TryReserveCandidateSet(
                candidateSet,
                candidateSetId,
                requestId,
                decisionId,
                riskState,
                out var reservation))
        {
            TriggerRuntimeIntervention(null, "Candidate set reservation could not be created.");
            return;
        }

        var context = taskController.BuildRequestContext(reservation);
        if (context == null)
        {
            taskController.AbandonCandidateSetReservation();
            TriggerRuntimeIntervention(null, "Candidate set request context could not be built.", false);
            return;
        }

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

        if (activeSnapshot == null)
        {
            return;
        }

        if (response == null)
        {
            TriggerRuntimeIntervention(null, "Guidance response is null.");
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
            TriggerRuntimeIntervention(response, "Guidance verifier result is missing.");
            return;
        }

        if (!response.verifier.approved)
        {
            TriggerRuntimeIntervention(response, response.verifier.reason);
            return;
        }

        var candidateReservation = runtimeState.ActiveCandidateSetReservation;
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        if (candidateReservation == null ||
            candidateReservation.Phase != CandidateSetReservationPhase.Reserved ||
            !candidateReservation.ContainsModelCandidate(response.selected_target) ||
            !candidateReservation.TryGetCandidate(response.selected_target, out var frozenCandidate) ||
            !string.Equals(
                response.target_role,
                GuidanceTypeUtility.TargetRoleToProtocol(frozenCandidate.TargetRole),
                StringComparison.Ordinal))
        {
            ExpireAndReplan("Guidance response does not match the active Candidate-Set Reservation.");
            return;
        }

        // 只重验模型实际选择的目标；其他候选变化不应使仍安全的响应过期。
        if (!semanticTargetSelector.IsSelectedCandidateCurrent(
                candidateReservation,
                response.selected_target,
                runtimeState,
                riskState,
                conditionPolicy,
                riskEvaluator,
                out var candidateReason))
        {
            ExpireAndReplan(candidateReason);
            return;
        }

        // 后端只选择语义目标；最终身体动作始终由最新连续偏航和本地物理安全层生成。
        if (!semanticTargetSelector.TryAssessCandidate(
                response.selected_target,
                frozenCandidate.TargetRole,
                riskState,
                out var selectedAssessment) ||
            !selectedAssessment.IsExecutable)
        {
            ExpireAndReplan("Selected candidate cannot produce an executable local action.");
            return;
        }
        response.movement_instruction =
            GuidanceActionProtocolMapper.ToMovementInstruction(selectedAssessment.SuggestedAction);

        if (localValidator != null &&
            !localValidator.Validate(response, activeSnapshot, runtimeState, riskState, out var reason))
        {
            if (reason.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExpireAndReplan(reason);
                return;
            }

            TriggerRuntimeIntervention(response, reason);
            return;
        }

        if (conditionPolicy.UseSafeCandidateFilter == ConditionCapabilityMode.Enabled &&
            !Contains(candidateReservation.AllowedCandidateIds, response.selected_target))
        {
            TriggerRuntimeIntervention(response, "Guidance selected_target is outside allowed_candidate_ids.");
            return;
        }

        if (!taskController.TryValidateCandidateSelection(
                candidateReservation,
                response.selected_target,
                out var selectedCandidate) ||
            !taskController.CommitCurrentSubgoal(
                candidateReservation,
                selectedCandidate,
                out var targetReservation))
        {
            taskController.AbandonCandidateSetReservation();
            TriggerRuntimeIntervention(response, "Candidate selection could not be committed atomically.");
            return;
        }

        SetRequestPhase(GuidanceRequestPhase.Verified);
        guidanceExecutor?.Apply(response);
        if (selectedCandidate.TargetRole != NavigationTargetRole.TurnCueAnchor)
        {
            arrivalDetector?.WatchReservation(targetReservation);
        }
        GuidanceTargetSelected?.Invoke(new GuidanceTarget(
            selectedCandidate.TargetId,
            selectedCandidate.TargetRole));

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
                activeSnapshot != null ? activeSnapshot.decisionId : string.Empty,
                Mathf.Abs(selectedAssessment.SuggestedAction.TargetYawDegrees));
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
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        automaticResetAttempts = 0;
        resetAbandonedTargetId = string.Empty;
        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received ||
            requestPhase == GuidanceRequestPhase.Verified ||
            requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }
    }

    /// <summary>
    /// ResetCompleted 且物理复验安全后恢复原任务，发布 Replanning 并请求下一段 Guidance。
    /// </summary>
    /// <returns>任务从 PausedForReset 恢复并进入导航时返回 true。</returns>
    public bool ResumeAfterResetAndReplan()
    {
        if (taskController == null ||
            taskController.RuntimeState.phase != GuidanceRuntimePhase.PausedForReset)
        {
            return false;
        }

        if (pauseResetController != null &&
            pauseResetController.State == ResetLifecycleState.Completed &&
            !pauseResetController.ResumeAfterReset())
        {
            return false;
        }

        taskController.ResumeAfterReset();
        var state = taskController.RuntimeState;
        if (!state.IsNavigating)
        {
            return false;
        }

        automaticResetAttempts = 0;
        resetAbandonedTargetId = string.Empty;
        frozenRecoveryPlan = null;
        TransitionRecoveryTo(GuidanceRecoveryState.Inactive);
        Replanning?.Invoke(state);
        RequestNextGuidance();
        return true;
    }

    /// <summary>
    /// Reset 完成后基于最新 Physical Pose 统一复验；不以语义候选存在作为恢复完成条件。
    /// </summary>
    public void ReevaluateAfterReset()
    {
        if (recoveryDecisionPending || pauseResetController == null || taskController == null ||
            pauseResetController.State != ResetLifecycleState.Completed ||
            taskController.RuntimeState.phase != GuidanceRuntimePhase.PausedForReset)
        {
            return;
        }

        recoveryDecisionPending = true;
        try
        {
            TransitionRecoveryTo(GuidanceRecoveryState.Revalidating);
            // EvaluateNow 会同步发布 RiskStateChanged；Reset 抑制门保证该事件只更新事实与日志。
            var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
            if (!HasTrustedRecoveryFacts(riskState))
            {
                TriggerRecoveryHardStop(
                    "Reset completed but the latest risk state is unavailable or invalid.");
                return;
            }

            // 兼容由 Dashboard 或旧场景直接启动的 Reset：桥层已经持有同一冻结物理朝向。
            if (frozenRecoveryPlan == null && pauseResetController.ActivePlan != null)
            {
                frozenRecoveryPlan = pauseResetController.ActivePlan;
            }

            if (recoveryHeadingPlanner.IsFrozenHeadingCritical(frozenRecoveryPlan, riskState.heading_profile))
            {
                if (!pauseResetController.ResumeAfterReset())
                {
                    TriggerRecoveryHardStop("Completed Reset could not return to the inactive state.");
                    return;
                }

                TriggerPauseReset(
                    null,
                    riskState,
                    "Frozen recovery heading became critical; selecting a new physical heading.");
                return;
            }

            CompleteResetAndReplan();
        }
        finally
        {
            recoveryDecisionPending = false;
        }
    }

    private void CompleteResetAndReplan()
    {
        TransitionRecoveryTo(GuidanceRecoveryState.Replanning);
        if (!pauseResetController.ResumeAfterReset())
        {
            TriggerRecoveryHardStop("Reset completed but could not be released.");
            return;
        }

        taskController.ResumeAfterReset();
        var state = taskController.RuntimeState;
        if (!state.IsNavigating)
        {
            TriggerRuntimeIntervention(null, "Task could not resume navigation after Reset.");
            return;
        }

        automaticResetAttempts = 0;
        resetAbandonedTargetId = string.Empty;
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            null,
            "Physical heading recovery completed; replanning with the latest facts.",
            RuntimeGuidanceSafetyDecision.SoftReroute,
            false,
            0,
            false));
        frozenRecoveryPlan = null;
        TransitionRecoveryTo(GuidanceRecoveryState.Inactive);
        Replanning?.Invoke(state);
        RequestNextGuidance();
    }

    private void TriggerRecoveryHardStop(string reason)
    {
        TransitionRecoveryTo(GuidanceRecoveryState.Terminated);
        pauseResetController?.CancelReset(reason);
        taskController?.EndTask();
        TriggerRuntimeIntervention(null, reason);
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
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Failed);
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            response,
            resolvedReason,
            RuntimeGuidanceSafetyDecision.HardStop,
            startReset,
            automaticResetAttempts,
            false));
    }

    private void ExpireAndReplan(string reason)
    {
        guidanceExecutor?.Clear();
        rotationGainController?.CancelGain("guidance_expired");
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
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
        if (IsResetRecoveryActive)
        {
            HandleActiveRecoveryRiskChanged(riskState);
            return;
        }

        if (runtimeReplanPending)
        {
            return;
        }

        if (holdForReplan)
        {
            if (riskState == null ||
                string.Equals(holdRiskSnapshotId, riskState.risk_snapshot_id, StringComparison.Ordinal))
            {
                return;
            }

            holdRiskSnapshotId = riskState.risk_snapshot_id ?? string.Empty;
            RequestNextGuidance();
            return;
        }

        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received)
        {
            HandlePendingRequestRiskChanged(riskState);
            return;
        }

        if (requestPhase != GuidanceRequestPhase.Executing ||
            activeSnapshot == null ||
            activeResponse == null)
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
        else if (decision == RuntimeGuidanceSafetyDecision.PauseReset)
        {
            if (CanStartAutomaticReset(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, RuntimeSafetyReason(input));
            }
            else
            {
                TriggerRuntimeIntervention(
                    activeResponse,
                    IsUserInsidePhysicalBoundary(riskState)
                        ? "No meaningful safe turning direction is available for automatic reset."
                        : "The user is outside the physical boundary.");
            }
        }
        else if (decision == RuntimeGuidanceSafetyDecision.HardStop)
        {
            TriggerRuntimeIntervention(
                activeResponse,
                RuntimeSafetyReason(input));
        }
    }

    private void BindRecoveryController()
    {
        if (pauseResetController == null)
        {
            return;
        }

        pauseResetController.StateChanged -= HandleResetStateChanged;
        pauseResetController.StateChanged += HandleResetStateChanged;
        pauseResetController.ResetCompleted -= HandleResetCompleted;
        pauseResetController.ResetCompleted += HandleResetCompleted;
    }

    private void UnbindRecoveryController()
    {
        if (pauseResetController == null)
        {
            return;
        }

        pauseResetController.StateChanged -= HandleResetStateChanged;
        pauseResetController.ResetCompleted -= HandleResetCompleted;
    }

    private void HandleResetStateChanged(ResetLifecycleState state)
    {
        if (state == ResetLifecycleState.Completed)
        {
            TransitionRecoveryTo(GuidanceRecoveryState.Stabilizing);
        }
        else if (state == ResetLifecycleState.Cancelled &&
                 recoveryState != GuidanceRecoveryState.Terminated)
        {
            TransitionRecoveryTo(GuidanceRecoveryState.Inactive);
        }
    }

    private void HandleResetCompleted(ResetEventInfo _)
    {
        ReevaluateAfterReset();
    }

    private void HandleActiveRecoveryRiskChanged(PhysicalRiskState riskState)
    {
        if (recoveryDecisionPending ||
            recoveryState != GuidanceRecoveryState.WaitingForUserTurn ||
            frozenRecoveryPlan == null)
        {
            return;
        }

        // Tracking 短暂丢失由会话层按配置时限等待；恢复计划保持冻结，不在此处误判为不可恢复。
        if (riskEvaluator != null &&
            (!riskEvaluator.IsTrackingValid || !riskEvaluator.IsCalibrationValid))
        {
            return;
        }

        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRecoveryHardStop("Automatic recovery lost trusted boundary or risk identity facts.");
            return;
        }

        if (!recoveryHeadingPlanner.IsFrozenHeadingCritical(frozenRecoveryPlan, riskState.heading_profile))
        {
            return;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.SelectingHeading);
        var replacement = recoveryHeadingPlanner.Plan(
            riskState.heading_profile,
            TryGetRecoverySemanticYaw(null),
            frozenRecoveryPlan);
        if (!replacement.IsSuccess)
        {
            TriggerRecoveryHardStop(RecoveryFailureReason(replacement.Failure));
            return;
        }

        frozenRecoveryPlan = replacement;
        automaticResetAttempts++;
        if (!pauseResetController.PauseForReset(
                "Frozen recovery heading became critical; using a newly selected physical heading.",
                replacement))
        {
            TriggerRecoveryHardStop("Automatic recovery controller rejected the replacement heading plan.");
            return;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.WaitingForUserTurn);
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
            AbandonReservations();
            activeSnapshot = null;
            activeResponse = null;
            turnCueCompletionTracker.Cancel();
            ResetRuntimeSafetySampling();
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
            RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
                interruptedResponse,
                reason,
                RuntimeGuidanceSafetyDecision.SoftReroute,
                false,
                0,
                true));
            Replanning?.Invoke(taskController != null ? taskController.RuntimeState : null);
            RequestNextGuidance();
        }
        finally
        {
            runtimeReplanPending = false;
        }
    }

    private void TriggerPauseReset(
        GuidanceResponse response,
        PhysicalRiskState riskState,
        string reason)
    {
        if (pauseResetController == null || taskController == null)
        {
            TriggerRuntimeIntervention(response, "Automatic reset dependencies are unavailable.");
            return;
        }

        if (IsResetRecoveryActive)
        {
            return;
        }

        var resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "Planned movement is physically unsafe; starting automatic heading recovery."
            : reason;
        if (string.IsNullOrWhiteSpace(resetAbandonedTargetId) && response != null)
        {
            resetAbandonedTargetId = response.selected_target ?? string.Empty;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.StoppingGuidance);
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        handlingGainTermination = true;
        rotationGainController?.AbortGain("pause_reset");
        handlingGainTermination = false;
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Cancelled);
        TransitionRecoveryTo(GuidanceRecoveryState.SelectingHeading);
        var semanticYaw = TryGetRecoverySemanticYaw(response);
        var nextPlan = recoveryHeadingPlanner.Plan(
            riskState != null ? riskState.heading_profile : null,
            semanticYaw,
            frozenRecoveryPlan);
        if (!nextPlan.IsSuccess)
        {
            TriggerRecoveryHardStop(RecoveryFailureReason(nextPlan.Failure));
            return;
        }

        taskController.PauseForReset();
        automaticResetAttempts++;
        frozenRecoveryPlan = nextPlan;
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            response,
            resolvedReason,
            RuntimeGuidanceSafetyDecision.PauseReset,
            true,
            automaticResetAttempts,
            false));
        if (!pauseResetController.PauseForReset(resolvedReason, frozenRecoveryPlan))
        {
            TriggerRecoveryHardStop("Automatic recovery controller rejected the frozen heading plan.");
            return;
        }

        TransitionRecoveryTo(GuidanceRecoveryState.WaitingForUserTurn);
    }

    /// <summary>
    /// 在尚无活动 Guidance 时根据最新物理事实启动自动朝向恢复；不可恢复时自动终止任务。
    /// </summary>
    /// <param name="riskState">当前可信物理风险快照。</param>
    /// <param name="reason">触发恢复的诊断原因。</param>
    /// <returns>已处于或成功进入自动恢复状态时返回 true。</returns>
    public bool BeginAutomaticRecovery(PhysicalRiskState riskState, string reason)
    {
        if (IsResetRecoveryActive)
        {
            return true;
        }

        if (taskController == null || !taskController.RuntimeState.IsNavigating)
        {
            TriggerRecoveryHardStop("Automatic recovery requires an active navigating task.");
            return false;
        }

        if (!CanStartAutomaticReset(riskState))
        {
            TriggerRecoveryHardStop(
                HasTrustedRecoveryFacts(riskState)
                    ? "No acceptable physical recovery heading is available."
                    : "Automatic recovery has no trusted tracking, calibration, or boundary facts.");
            return false;
        }

        TriggerPauseReset(null, riskState, reason);
        return IsResetRecoveryActive;
    }

    private GuidanceTarget FindRuntimeSafetyCandidate(
        PhysicalRiskState riskState,
        string activeTargetId,
        bool allowPausedForReset = false)
    {
        if (semanticTargetSelector == null || taskController == null || riskState == null)
        {
            return null;
        }

        var candidateSet = semanticTargetSelector.BuildCandidateSet(
            taskController.RuntimeState,
            riskState,
            ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition),
            allowPausedForReset);
        if (candidateSet == null)
        {
            return null;
        }

        foreach (var candidate in candidateSet.Candidates)
        {
            if (candidate == null ||
                string.Equals(candidate.TargetId, activeTargetId, StringComparison.Ordinal) ||
                semanticTargetSelector.GetCandidateSafetyState(candidate.TargetId, riskState) !=
                CandidateSafetyState.Eligible ||
                riskEvaluator == null ||
                !riskEvaluator.TryGetDirectionClearance(
                    riskState,
                    candidate.RelativeDirection,
                    out var clearanceMeters) ||
                riskEvaluator.IsCriticalClearance(clearanceMeters))
            {
                continue;
            }

            return new GuidanceTarget(candidate.TargetId, candidate.TargetRole);
        }

        return null;
    }

    private void HandlePendingRequestRiskChanged(PhysicalRiskState riskState)
    {
        if (taskController == null)
        {
            TriggerRuntimeIntervention(null, "Pending guidance request has no stable request context.");
            return;
        }

        // AgentClient 构建请求与发布快照之间存在同帧窗口，此时等待 RequestBuilt 完成即可。
        if (activeSnapshot == null)
        {
            return;
        }

        if (!HasTrustedRecoveryFacts(riskState) || !activeSnapshot.MatchesTaskContext(taskController.RuntimeState, riskState))
        {
            TriggerRuntimeIntervention(null, "Pending guidance request received unavailable or mismatched safety facts.");
            return;
        }

        var reservation = taskController.RuntimeState.ActiveCandidateSetReservation;
        if (HasCurrentFrozenSafetyCandidate(reservation, riskState))
        {
            return;
        }

        EnterHoldAndReplan(
            riskState,
            "Pending guidance candidates are not currently executable; holding for replanning.");
    }

    private bool HasCurrentFrozenSafetyCandidate(
        CandidateSetReservation reservation,
        PhysicalRiskState riskState)
    {
        if (reservation == null || semanticTargetSelector == null || taskController == null)
        {
            return false;
        }

        var policy = ExperimentConditionMatrix.GetPolicy(taskController.CurrentCondition);
        foreach (var targetId in reservation.ModelCandidateIds)
        {
            if (semanticTargetSelector.IsSelectedCandidateCurrent(
                    reservation,
                    targetId,
                    taskController.RuntimeState,
                    riskState,
                    policy,
                    riskEvaluator,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private void HandleUnavailableCandidatePlanning(PhysicalRiskState riskState)
    {
        if (!HasTrustedRecoveryFacts(riskState))
        {
            TriggerRuntimeIntervention(
                null,
                "Guidance planning safety facts are unavailable or invalid.");
            return;
        }

        EnterHoldAndReplan(
            riskState,
            "No semantic candidate is currently executable; holding while the task remains active.");
    }

    private bool HasTrustedRecoveryFacts(PhysicalRiskState riskState)
    {
        return riskEvaluator != null &&
               riskState != null &&
               riskEvaluator.IsTrackingValid &&
               riskEvaluator.IsCalibrationValid &&
               riskEvaluator.IsRiskIdentityCurrent(riskState) &&
               riskState.boundary_context != null &&
               riskState.boundary_context.Count >= 3 &&
               riskState.safe_directions != null &&
               riskState.unsafe_directions != null &&
               HasFiniteNaturalDirectionClearances(riskState) &&
               IsUserInsidePhysicalBoundary(riskState);
    }

    private bool CanStartAutomaticReset(PhysicalRiskState riskState)
    {
        if (!HasTrustedRecoveryFacts(riskState))
        {
            return false;
        }

        return recoveryHeadingPlanner.Plan(riskState.heading_profile).IsSuccess;
    }

    private void EnterHoldAndReplan(PhysicalRiskState riskState, string reason)
    {
        requestClient?.CancelPendingRequest();
        guidanceExecutor?.Clear();
        handlingGainTermination = true;
        rotationGainController?.AbortGain("hold_and_replan");
        handlingGainTermination = false;
        arrivalDetector?.WatchReservation(null);
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        if (requestPhase == GuidanceRequestPhase.Requested ||
            requestPhase == GuidanceRequestPhase.Received ||
            requestPhase == GuidanceRequestPhase.Verified ||
            requestPhase == GuidanceRequestPhase.Executing)
        {
            SetRequestPhase(GuidanceRequestPhase.Cancelled);
        }

        holdForReplan = true;
        holdRiskSnapshotId = riskState != null ? riskState.risk_snapshot_id ?? string.Empty : string.Empty;
        RuntimeIntervention?.Invoke(new RuntimeInterventionInfo(
            null,
            reason,
            RuntimeGuidanceSafetyDecision.SoftReroute,
            false,
            0,
            false));
        Replanning?.Invoke(taskController != null ? taskController.RuntimeState : null);
    }

    private float? TryGetRecoverySemanticYaw(GuidanceResponse response)
    {
        var targetId = response != null ? response.selected_target : resetAbandonedTargetId;
        if (string.IsNullOrWhiteSpace(targetId) && taskController != null)
        {
            targetId = taskController.RuntimeState.nextTargetId;
        }

        return navigationTargets != null &&
               navigationTargets.TryGetTargetRelativeYawDegrees(targetId, out var relativeYawDegrees)
            ? relativeYawDegrees
            : (float?)null;
    }

    private static string RecoveryFailureReason(RecoveryHeadingFailure failure)
    {
        return failure == RecoveryHeadingFailure.NoAcceptableHeading
            ? "No acceptable physical recovery heading is available."
            : "Automatic recovery has no valid Heading Profile.";
    }

    private void TransitionRecoveryTo(GuidanceRecoveryState nextState)
    {
        if (recoveryState == nextState)
        {
            return;
        }

        recoveryState = nextState;
        RecoveryStateChanged?.Invoke(recoveryState);
    }

    private bool HasFiniteNaturalDirectionClearances(PhysicalRiskState riskState)
    {
        return riskEvaluator.TryGetDirectionClearance(riskState, "forward", out _) &&
               riskEvaluator.TryGetDirectionClearance(riskState, "left", out _) &&
               riskEvaluator.TryGetDirectionClearance(riskState, "right", out _);
    }

    private static bool IsUserInsidePhysicalBoundary(PhysicalRiskState riskState)
    {
        if (riskState == null || riskState.boundary_context == null || riskState.boundary_context.Count < 3)
        {
            return false;
        }

        var boundary = new Vector2[riskState.boundary_context.Count];
        for (var index = 0; index < boundary.Length; index++)
        {
            boundary[index] = riskState.boundary_context[index];
        }

        return PhysicalSpaceGeometry.ContainsPoint(boundary, riskState.user_physical_position);
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
        if (!input.PlannedDirectionAvailable) return "Planned physical direction clearance is unavailable.";
        if (input.ResetState == ResetLifecycleState.Required ||
            input.ResetState == ResetLifecycleState.TurningToSafeDirection ||
            input.ResetState == ResetLifecycleState.Completed) return "Physical reset is active.";
        if (input.LatestRisk.reset_required) return "All natural movement directions are unsafe; automatic reset is required.";
        if (input.PlannedDirectionCritical) return "Planned physical direction is critical and no eligible safety candidate is available.";
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
        AbandonReservations();
        activeSnapshot = null;
        activeResponse = null;
        turnCueCompletionTracker.Cancel();
        ResetRuntimeSafetySampling();
        SetRequestPhase(GuidanceRequestPhase.Failed);
    }

    private void AbandonReservations()
    {
        taskController?.AbandonCandidateSetReservation();
        taskController?.AbandonReservation();
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
        if (IsResetRecoveryActive)
        {
            return;
        }

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

        if (CanStartAutomaticReset(riskState))
        {
            TriggerPauseReset(activeResponse, riskState, $"{reason}: no eligible reroute candidate.");
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
        if (IsResetRecoveryActive || requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var resetRequired = riskState != null && riskState.reset_required;
        var currentHeadingCritical = IsCurrentHeadingCritical(riskState);
        var outsideBoundary = riskState != null && !IsUserInsidePhysicalBoundary(riskState);
        if (riskState == null ||
            riskEvaluator == null ||
            !riskEvaluator.IsRiskIdentityCurrent(riskState) ||
            resetRequired ||
            currentHeadingCritical ||
            outsideBoundary)
        {
            if (riskState != null && CanStartAutomaticReset(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, "TurnCue reevaluation is unsafe.");
            }
            else
            {
                TriggerRuntimeIntervention(
                    activeResponse,
                    riskState == null ? "TurnCue reevaluation has no valid risk snapshot." : "TurnCue reevaluation is unsafe.");
            }
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
        if (IsResetRecoveryActive || handlingGainTermination || info == null ||
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
                  info.Reason == "planned_heading_critical" ||
                  info.Reason == "reset_required" ||
                  info.Reason == "outside_physical_boundary" ||
                  info.Reason == "rotation_root_invalid")
        {
            turnCueCompletionTracker.Cancel();
            if ((info.Reason == "planned_heading_critical" || info.Reason == "reset_required") &&
                riskEvaluator != null &&
                CanStartAutomaticReset(riskEvaluator.CurrentRiskState))
            {
                TriggerPauseReset(activeResponse, riskEvaluator.CurrentRiskState, info.Reason);
            }
            else
            {
                TriggerRuntimeIntervention(activeResponse, info.Reason);
            }
        }
    }

    private void HandleGainReevaluationRequested()
    {
        if (IsResetRecoveryActive)
        {
            return;
        }

        var riskState = riskEvaluator != null ? riskEvaluator.EvaluateNow() : null;
        if (IsResetRecoveryActive || requestPhase != GuidanceRequestPhase.Executing)
        {
            return;
        }

        var riskUnavailable = riskState == null ||
                              riskEvaluator == null ||
                              !riskEvaluator.IsRiskIdentityCurrent(riskState);
        var resetRequired = riskState != null && riskState.reset_required;
        var currentHeadingCritical = IsCurrentHeadingCritical(riskState);
        var outsideBoundary = riskState != null && !IsUserInsidePhysicalBoundary(riskState);
        if (riskUnavailable || resetRequired || currentHeadingCritical || outsideBoundary)
        {
            var reason = riskUnavailable
                ? "risk_snapshot_unavailable"
                : resetRequired
                    ? "reset_required"
                    : outsideBoundary ? "outside_physical_boundary" : "planned_heading_critical";
            handlingGainTermination = true;
            rotationGainController?.CompleteReevaluation(false, reason);
            handlingGainTermination = false;
            turnCueCompletionTracker.Cancel();
            if (!riskUnavailable && CanStartAutomaticReset(riskState))
            {
                TriggerPauseReset(activeResponse, riskState, reason);
            }
            else
            {
                TriggerRuntimeIntervention(activeResponse, reason);
            }
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

    private static bool IsCurrentHeadingCritical(PhysicalRiskState riskState)
    {
        if (riskState == null)
        {
            return false;
        }

        if (riskState.heading_profile != null && riskState.heading_profile.Count > 0)
        {
            return riskState.TryGetNearestHeading(0f, out var currentHeading) &&
                   currentHeading.Safety == HeadingSafety.Critical;
        }

        return string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(riskState.reset_risk, "critical", StringComparison.OrdinalIgnoreCase);
    }
}
