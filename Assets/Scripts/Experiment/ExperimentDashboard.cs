using System;
using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// 研究员侧实验会话状态机。Play 只初始化环境；StartSession 才创建日志并启动正式任务。
/// </summary>
public sealed class ExperimentDashboard : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private string participantId = "P001";
    [SerializeField] private int conditionIndex = 5;
    [SerializeField] private int taskIndex = 1;
    [SerializeField] private int physicalSpaceIndex = 1;
    [SerializeField, Min(1f)] private float completionCountdownSeconds = 10f;

    [Header("Dependencies")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [SerializeField] private UserPoseTracker userPoseTracker;
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    [SerializeField] private XRTrackingAdapter xrTrackingAdapter;
    [SerializeField] private GuidanceTaskController taskController;
    [SerializeField] private GuidanceCoordinator guidanceCoordinator;
    [SerializeField] private AgentClient agentClient;
    [SerializeField] private ExperimentLogger experimentLogger;
    [SerializeField] private ArrivalDetector arrivalDetector;
    [SerializeField] private RotationGainController rotationGainController;
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [SerializeField] private PauseResetController pauseResetController;
    [SerializeField] private ParticipantUiController participantUiController;
    [SerializeField] private TMP_Text statusText;

    private ExperimentConditionSpec currentSpec;
    private ExperimentRunState runState = ExperimentRunState.Preparing;
    private ExperimentRunState stateBeforeSafetyHold = ExperimentRunState.Running;
    private Coroutine completionRoutine;
    private float completionSecondsRemaining;
    private string lastStatusMessage = "Initializing";
    private float nextReadinessRefreshTime;
    private bool runtimeInterventionActive;

    public event Action<ExperimentRunState> SessionStateChanged;
    public event Action<float> CompletionCountdownChanged;

    public ExperimentRunState RunState => runState;
    public float CompletionSecondsRemaining => completionSecondsRemaining;
    public ExperimentConditionSpec CurrentSpec => currentSpec;

    private void Awake()
    {
        ResolveReferences();
        participantUiController?.ShowWaiting();
        experimentLogger?.SetRunState(runState);
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        StopCompletionCountdown();
    }

    private void Update()
    {
        if ((runState != ExperimentRunState.Preparing && runState != ExperimentRunState.Ready) ||
            Time.unscaledTime < nextReadinessRefreshTime)
        {
            return;
        }

        nextReadinessRefreshTime = Time.unscaledTime + 0.25f;
        RefreshReadinessState();
    }

    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        PhysicalRiskEvaluator evaluator,
        XRTrackingAdapter trackingAdapter,
        GuidanceTaskController task,
        GuidanceCoordinator coordinator,
        AgentClient client,
        ExperimentLogger logger,
        ArrivalDetector detector,
        RotationGainController gainController,
        GuidanceExecutor executor,
        PauseResetController resetController,
        ParticipantUiController participantUi,
        TMP_Text status,
        PhysicalSpaceCalibration calibration = null)
    {
        Unsubscribe();
        if (spaceManager != null) physicalSpaceManager = spaceManager;
        if (poseTracker != null) userPoseTracker = poseTracker;
        if (calibration != null) physicalSpaceCalibration = calibration;
        if (evaluator != null) riskEvaluator = evaluator;
        if (trackingAdapter != null) xrTrackingAdapter = trackingAdapter;
        if (task != null) taskController = task;
        if (coordinator != null) guidanceCoordinator = coordinator;
        if (client != null) agentClient = client;
        if (logger != null) experimentLogger = logger;
        if (detector != null) arrivalDetector = detector;
        if (gainController != null) rotationGainController = gainController;
        if (executor != null) guidanceExecutor = executor;
        if (resetController != null) pauseResetController = resetController;
        if (participantUi != null) participantUiController = participantUi;
        if (status != null) statusText = status;
        experimentLogger?.SetRunState(runState);
        Subscribe();
        RefreshReadinessState();
    }

    public void SetParticipantId(string value)
    {
        if (IsConfigurationLocked()) return;
        participantId = value != null ? value.Trim() : string.Empty;
        PrepareForNextParticipant();
    }

    public void SetConditionIndex(int value)
    {
        if (IsConfigurationLocked()) return;
        conditionIndex = value;
        PrepareForNextParticipant();
    }

    public void SetTaskIndex(int value)
    {
        if (IsConfigurationLocked()) return;
        taskIndex = value;
        PrepareForNextParticipant();
    }

    public void SetPhysicalSpaceIndex(int value)
    {
        if (IsConfigurationLocked()) return;
        physicalSpaceIndex = value;
        PrepareForNextParticipant();
    }

    /// <summary>应用配置预览并切换待校准的 Physical Space，不创建日志或启动任务。</summary>
    public void ApplySelection()
    {
        if (IsConfigurationLocked()) return;
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        physicalSpaceManager?.SetPhysicalSpace(currentSpec.physicalSpaceId);
        RefreshReadinessState();
    }

    /// <summary>
    /// 使用最近一份 Raw Tracking Pose 对齐所选 Physical Space 的固定校准点。
    /// </summary>
    /// <returns>校准完成且可用于正式实验时返回 true。</returns>
    public bool CalibratePhysicalSpace()
    {
        if (IsConfigurationLocked())
        {
            return false;
        }

        ResolveReferences();
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        experimentLogger?.LogCalibrationStarted();
        if (physicalSpaceManager == null ||
            !physicalSpaceManager.SetPhysicalSpace(currentSpec.physicalSpaceId) ||
            physicalSpaceCalibration == null ||
            !physicalSpaceCalibration.CalibrateCurrentPose())
        {
            lastStatusMessage = "Calibration failed";
            RefreshReadinessState();
            return false;
        }

        experimentLogger?.LogCalibrationCompleted(physicalSpaceCalibration.CalibrationId);
        lastStatusMessage = "Calibration valid";
        RefreshReadinessState();
        return true;
    }

    /// <summary>研究员按钮入口：触发一次物理空间校准，忽略返回值。</summary>
    public void RequestCalibration()
    {
        CalibratePhysicalSpace();
    }

    public void NextCondition() => SetConditionIndex(conditionIndex + 1);
    public void NextTask() => SetTaskIndex(taskIndex + 1);
    public void NextPhysicalSpace() => SetPhysicalSpaceIndex(physicalSpaceIndex + 1);

    public void StartSession()
    {
        if (IsConfigurationLocked())
        {
            lastStatusMessage = $"Cannot start from state {runState}";
            WriteStatus();
            return;
        }

        ResolveReferences();
        if (!CanStartSession(out var missing))
        {
            lastStatusMessage = "Cannot start: " + missing;
            if (missing.Contains("backend") || missing.Contains("reference"))
            {
                participantUiController?.ShowSystemUnavailable();
            }
            else
            {
                participantUiController?.ShowWaiting();
            }
            WriteStatus();
            return;
        }

        StopCompletionCountdown();
        stateBeforeSafetyHold = ExperimentRunState.Running;
        completionSecondsRemaining = 0f;
        agentClient.CancelPendingRequest();
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        TransitionTo(ExperimentRunState.Starting, "Starting session");
        participantUiController?.ShowStarting();

        if (!physicalSpaceManager.SetPhysicalSpace(currentSpec.physicalSpaceId))
        {
            EnterError("Selected physical space could not be loaded.");
            return;
        }

        if (xrTrackingAdapter == null || !xrTrackingAdapter.IsHeadsetTrackingActive)
        {
            userPoseTracker.SetSimulatedPhysicalPose(currentSpec.simulatedPhysicalPosition, currentSpec.simulatedPhysicalYawDegrees);
        }

        experimentLogger.StartSession(currentSpec);
        experimentLogger.LogConditionSelected(currentSpec);
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(currentSpec.condition);
        rotationGainController.SetGainEnabled(
            conditionPolicy.UseRotationGain == ConditionCapabilityMode.Enabled);
        taskController.ConfigureTask(currentSpec.ToTaskDefinition());
        taskController.StartTask();

        var risk = riskEvaluator.EvaluateNow();
        if (IsCritical(risk))
        {
            EnterSafetyHold("critical risk at session start");
            return;
        }

        TransitionTo(ExperimentRunState.Running, "Session running");
        participantUiController?.ShowGuidance();
        guidanceCoordinator.RequestNextGuidance();
    }

    public void PauseSession()
    {
        if (runState != ExperimentRunState.Running)
        {
            return;
        }

        experimentLogger.Session?.BeginPause();
        FreezeExperimentBehavior();
        taskController?.PauseForResearcher();
        TransitionTo(ExperimentRunState.Paused, "Paused by researcher");
        participantUiController?.ShowResearcherPaused();
        experimentLogger?.LogStatus("session_paused", taskController?.RuntimeState, riskEvaluator?.CurrentRiskState, "researcher pause");
    }

    public void ResumeSession()
    {
        if (runState != ExperimentRunState.Paused && runState != ExperimentRunState.SafetyHold)
        {
            return;
        }

        if (runState == ExperimentRunState.SafetyHold &&
            pauseResetController != null &&
            pauseResetController.State != ResetLifecycleState.Completed)
        {
            return;
        }

        var risk = riskEvaluator?.EvaluateNow();
        ResumeSessionWithRisk(risk);
    }

    private void ResumeSessionWithRisk(PhysicalRiskState risk)
    {
        if (!IsSafeForResume(risk))
        {
            EnterSafetyHold("physical risk is not safe for resume");
            return;
        }

        if (pauseResetController != null &&
            pauseResetController.State != ResetLifecycleState.Inactive &&
            !pauseResetController.ResumeAfterReset())
        {
            EnterSafetyHold("reset turn has not completed");
            return;
        }
        runtimeInterventionActive = false;
        experimentLogger?.Session?.EndPause();
        var requestedDuringResetRecovery = false;
        if (taskController != null)
        {
            if (taskController.RuntimeState.phase == GuidanceRuntimePhase.PausedForReset)
            {
                if (guidanceCoordinator != null)
                    requestedDuringResetRecovery = guidanceCoordinator.ResumeAfterResetAndReplan();
                else
                    taskController.ResumeAfterReset();
            }
            else
                taskController.ResumeAfterResearcherPause();
        }

        if (stateBeforeSafetyHold == ExperimentRunState.Completing && taskController != null && taskController.RuntimeState.IsCompleted)
        {
            TransitionTo(ExperimentRunState.Completing, "Completion countdown resumed");
            participantUiController?.ShowCompleted();
            ResumeCompletionCountdown();
            return;
        }

        TransitionTo(ExperimentRunState.Running, "Session resumed");
        participantUiController?.ShowGuidance();
        arrivalDetector?.WatchTarget(taskController != null ? taskController.RuntimeState.nextTargetId : null);
        if (!requestedDuringResetRecovery)
            guidanceCoordinator?.RequestNextGuidance();
    }

    public void TogglePauseSession()
    {
        if (runState == ExperimentRunState.Running)
            PauseSession();
        else if (runState == ExperimentRunState.Paused || runState == ExperimentRunState.SafetyHold)
            ResumeSession();
    }

    public void EndSession()
    {
        EndSession("researcher_ended");
    }

    public void EndSession(string reason)
    {
        if (runState == ExperimentRunState.Ended || runState == ExperimentRunState.Preparing || runState == ExperimentRunState.Ready)
        {
            return;
        }

        StopCompletionCountdown();
        FreezeExperimentBehavior();
        experimentLogger?.Session?.EndPause();
        TransitionTo(ExperimentRunState.Ended, string.IsNullOrWhiteSpace(reason) ? "Session ended" : reason);
        taskController?.EndTask();
        experimentLogger?.EndSession(reason);
        participantUiController?.ShowEnded();
    }

    public bool CanStartSession(out string missingConditions)
    {
        if (physicalSpaceManager == null || userPoseTracker == null || riskEvaluator == null ||
            physicalSpaceCalibration == null || xrTrackingAdapter == null ||
            taskController == null || guidanceCoordinator == null ||
            agentClient == null || experimentLogger == null || arrivalDetector == null ||
            rotationGainController == null || guidanceExecutor == null)
        {
            missingConditions = "critical reference missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(participantId))
        {
            missingConditions = "participant ID";
            return false;
        }

        if (!agentClient.BackendOnline)
        {
            missingConditions = "backend health";
            return false;
        }

        if (!xrTrackingAdapter.IsHeadsetTrackingActive)
        {
            missingConditions = "XR tracking";
            return false;
        }

        if (!physicalSpaceCalibration.IsCalibrationValid ||
            !string.Equals(
                physicalSpaceCalibration.CalibratedSpaceId,
                physicalSpaceManager.CurrentSpaceId,
                StringComparison.Ordinal))
        {
            missingConditions = "physical calibration";
            return false;
        }

        if (physicalSpaceManager.CurrentSpace == null)
        {
            missingConditions = "physical space";
            return false;
        }

        missingConditions = string.Empty;
        return true;
    }

    private void EnterSafetyHold(string reason)
    {
        if (runState != ExperimentRunState.SafetyHold)
        {
            stateBeforeSafetyHold = runState;
            if (stateBeforeSafetyHold == ExperimentRunState.Completing)
            {
                StopCompletionCountdown();
            }
            experimentLogger?.Session?.BeginPause();
        }

        FreezeExperimentBehavior();
        TransitionTo(ExperimentRunState.SafetyHold, reason);
        if (stateBeforeSafetyHold != ExperimentRunState.Completing)
        {
            taskController?.PauseForReset();
        }
        pauseResetController?.PauseForReset(reason);
        if (runtimeInterventionActive)
            participantUiController?.ShowRuntimeIntervention();
        else
            participantUiController?.ShowCriticalSafety();
        experimentLogger?.LogStatus("safety_hold", taskController?.RuntimeState, riskEvaluator?.CurrentRiskState, reason);
    }

    private void EnterError(string reason)
    {
        FreezeExperimentBehavior();
        TransitionTo(ExperimentRunState.Error, reason);
        participantUiController?.ShowSystemUnavailable();
        experimentLogger?.LogStatus("task_failed", taskController?.RuntimeState, riskEvaluator?.CurrentRiskState, reason);
    }

    private void FreezeExperimentBehavior()
    {
        guidanceCoordinator?.CancelActiveGuidance();
        agentClient?.CancelPendingRequest();
        arrivalDetector?.WatchTarget(null);
        rotationGainController?.StopGain();
        guidanceExecutor?.Clear();
    }

    private void HandleTaskStateChanged(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus("task_state_changed", state, riskEvaluator?.CurrentRiskState, "task state changed");
        if ((runState == ExperimentRunState.Running || runState == ExperimentRunState.Paused) &&
            state != null && state.phase == GuidanceRuntimePhase.PausedForReset)
        {
            EnterSafetyHold("task requested physical safety reset");
            return;
        }

        if (runState == ExperimentRunState.Running && state != null && state.IsCompleted)
        {
            arrivalDetector?.WatchTarget(null);
            rotationGainController?.StopGain();
            guidanceExecutor?.Clear();
            TransitionTo(ExperimentRunState.Completing, "Task completed");
            participantUiController?.ShowCompleted();
            StartCompletionCountdown();
        }
        else
        {
            WriteStatus();
        }
    }

    private void HandleRiskStateChanged(PhysicalRiskState state)
    {
        experimentLogger?.LogRiskSnapshot(state, taskController?.RuntimeState);
        if ((runState == ExperimentRunState.Running || runState == ExperimentRunState.Paused || runState == ExperimentRunState.Completing) && IsCritical(state))
        {
            EnterSafetyHold("critical physical risk");
        }
    }

    private void HandleResetStarted(ResetEventInfo info)
    {
        experimentLogger?.LogStatus(
            "reset_started",
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            info != null ? $"safe direction: {info.SafeDirection}" : "reset started");
    }

    private void HandleResetCompleted(ResetEventInfo _)
    {
        experimentLogger?.LogStatus(
            "reset_completed",
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            "safe direction reached");
        if (runState != ExperimentRunState.SafetyHold)
        {
            return;
        }

        // Reset 完成只表示朝向达标；必须基于最新 Physical Pose 重算风险后才能恢复任务。
        ResumeSessionWithRisk(riskEvaluator?.EvaluateNow());
    }

    private void HandleBackendOnlineChanged(bool online)
    {
        experimentLogger?.LogStatus(
            online ? "backend_online" : "backend_unavailable",
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            online ? "backend online" : "backend unavailable");
        if (!online && IsSessionActiveState())
        {
            EnterError("backend unavailable");
            return;
        }

        RefreshReadinessState();
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        experimentLogger?.LogRequest(request, riskEvaluator?.CurrentRiskState);
    }

    private void HandleResponseReceived(GuidanceResponse response)
    {
        experimentLogger?.LogResponse(response, riskEvaluator?.CurrentRiskState);
    }

    private void HandleRequestFailed(string message)
    {
        experimentLogger?.LogStatus("request_failed", taskController?.RuntimeState, riskEvaluator?.CurrentRiskState, message);
        if (IsSessionActiveState())
        {
            EnterError(message);
        }
    }

    private void HandleGuidanceRejected(string reason)
    {
        experimentLogger?.LogGuidanceRejected(
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            string.IsNullOrWhiteSpace(reason) ? "guidance rejected" : reason);
        if (runState == ExperimentRunState.SafetyHold)
        {
            return;
        }

        if (IsSessionActiveState())
        {
            var resolvedReason = string.IsNullOrWhiteSpace(reason) ? "guidance validation failed" : reason;
            EnterError(resolvedReason);
        }
    }

    private void HandleRuntimeIntervention(RuntimeInterventionInfo intervention)
    {
        runtimeInterventionActive = true;
        participantUiController?.ShowRuntimeIntervention();
        experimentLogger?.LogRuntimeIntervention(
            intervention != null ? intervention.Response : null,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            intervention != null ? intervention.Reason : "runtime safety intervention");
    }

    private void HandleGuidanceExecuted(GuidanceResponse response)
    {
        experimentLogger?.LogGuidanceExecuted(
            response,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
    }

    private void HandleGuidanceExpired(string reason)
    {
        experimentLogger?.LogGuidanceExpired(
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            reason);
    }

    private void HandleRequestPhaseChanged(GuidanceRequestPhase phase)
    {
        if (phase == GuidanceRequestPhase.Verified)
        {
            experimentLogger?.LogGuidanceVerified(taskController?.RuntimeState, riskEvaluator?.CurrentRiskState);
        }
        else if (phase == GuidanceRequestPhase.Cancelled)
        {
            experimentLogger?.LogGuidanceCancelled(
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState,
                "guidance lifecycle cancelled");
        }
    }

    private void HandleReplanning(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus("replanning", state, riskEvaluator?.CurrentRiskState, "replanning requested");
    }

    private void HandleTaskCompleted(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus("task_completed", state, riskEvaluator?.CurrentRiskState, "task completed");
    }

    private void HandleGuidanceTargetSelected(GuidanceTarget target)
    {
        if (target != null && target.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            experimentLogger?.LogDetourSelected(target, taskController?.RuntimeState, riskEvaluator?.CurrentRiskState);
        }
    }

    private void HandleTargetReached(GuidanceTarget target)
    {
        experimentLogger?.LogTargetReached(target, taskController?.RuntimeState, riskEvaluator?.CurrentRiskState);
    }

    private void HandleDetourCompleted(GuidanceTarget target)
    {
        experimentLogger?.LogDetourCompleted(target, taskController?.RuntimeState, riskEvaluator?.CurrentRiskState);
    }

    private void HandleRotationGainEvent(RotationGainEventInfo info)
    {
        if (info == null)
        {
            return;
        }

        var eventType = GainEventName(info.EventType);
        var message = string.IsNullOrWhiteSpace(info.Reason)
            ? $"physical yaw: {info.PhysicalYawDegrees:F1}, extra yaw: {info.ExtraYawDegrees:F1}"
            : info.Reason;
        experimentLogger?.LogStatus(eventType, taskController?.RuntimeState, riskEvaluator?.CurrentRiskState, message);
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState state)
    {
        if (state == PhysicalCalibrationState.Invalid || state == PhysicalCalibrationState.CalibrationSuspect)
        {
            experimentLogger?.LogCalibrationInvalidated(
                physicalSpaceCalibration != null ? physicalSpaceCalibration.CalibrationId : string.Empty,
                $"calibration state: {state}");
        }
    }

    private static string GainEventName(RotationGainEventType eventType)
    {
        switch (eventType)
        {
            case RotationGainEventType.TurnRequested: return "turn_requested";
            case RotationGainEventType.UserTurnStarted: return "user_turn_started";
            case RotationGainEventType.GainStarted: return "gain_started";
            case RotationGainEventType.UserTurnCompleted: return "user_turn_completed";
            case RotationGainEventType.GainCompleted: return "gain_completed";
            case RotationGainEventType.GainCancelled: return "gain_cancelled";
            default: return "gain_event";
        }
    }

    private void StartCompletionCountdown()
    {
        StopCompletionCountdown();
        completionSecondsRemaining = Mathf.Max(1f, completionCountdownSeconds);
        CompletionCountdownChanged?.Invoke(completionSecondsRemaining);
        BeginCompletionCountdownRoutine();
    }

    private void ResumeCompletionCountdown()
    {
        StopCompletionCountdown();
        completionSecondsRemaining = Mathf.Max(0f, completionSecondsRemaining);
        CompletionCountdownChanged?.Invoke(completionSecondsRemaining);
        BeginCompletionCountdownRoutine();
    }

    private void BeginCompletionCountdownRoutine()
    {
        if (isActiveAndEnabled)
        {
            completionRoutine = StartCoroutine(CompletionCountdownRoutine());
        }
    }

    private IEnumerator CompletionCountdownRoutine()
    {
        while (completionSecondsRemaining > 0f && runState == ExperimentRunState.Completing)
        {
            completionSecondsRemaining = Mathf.Max(0f, completionSecondsRemaining - Time.unscaledDeltaTime);
            CompletionCountdownChanged?.Invoke(completionSecondsRemaining);
            yield return null;
        }

        completionRoutine = null;
        if (completionSecondsRemaining <= 0f && runState == ExperimentRunState.Completing)
        {
            EndSession("completed_auto");
        }
    }

    private void StopCompletionCountdown()
    {
        if (completionRoutine != null)
        {
            StopCoroutine(completionRoutine);
            completionRoutine = null;
        }
    }

    private void RefreshReadinessState()
    {
        if (IsConfigurationLocked())
        {
            WriteStatus();
            return;
        }

        if (CanStartSession(out var missing))
            TransitionTo(ExperimentRunState.Ready, "Ready");
        else
            TransitionTo(ExperimentRunState.Preparing, "Waiting for " + missing);
    }

    private void PrepareForNextParticipant()
    {
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        if (runState == ExperimentRunState.Ended)
        {
            TransitionTo(ExperimentRunState.Preparing, "Preparing next participant");
            participantUiController?.ShowWaiting();
        }
        RefreshReadinessState();
    }

    private void TransitionTo(ExperimentRunState next, string message)
    {
        lastStatusMessage = message;
        if (runState != next)
        {
            runState = next;
            experimentLogger?.SetRunState(runState);
            SessionStateChanged?.Invoke(runState);
        }
        WriteStatus();
    }

    private bool IsConfigurationLocked()
    {
        return runState == ExperimentRunState.Starting || runState == ExperimentRunState.Running ||
               runState == ExperimentRunState.Paused || runState == ExperimentRunState.SafetyHold ||
               runState == ExperimentRunState.Completing || runState == ExperimentRunState.Error;
    }

    private bool IsSessionActiveState()
    {
        return runState == ExperimentRunState.Starting || runState == ExperimentRunState.Running ||
               runState == ExperimentRunState.Paused || runState == ExperimentRunState.SafetyHold ||
               runState == ExperimentRunState.Completing;
    }

    private static bool IsCritical(PhysicalRiskState state)
    {
        return state != null && (state.reset_required || state.boundary_risk == "critical" || state.reset_risk == "critical");
    }

    private static bool IsSafeForResume(PhysicalRiskState state)
    {
        return state != null && !IsCritical(state) && state.safe_directions != null && state.safe_directions.Length > 0;
    }

    private void Subscribe()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceRequestBuilt += HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceReceived.AddListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
            agentClient.BackendOnlineChanged.RemoveListener(HandleBackendOnlineChanged);
            agentClient.BackendOnlineChanged.AddListener(HandleBackendOnlineChanged);
        }

        if (taskController != null)
        {
            taskController.StateChanged -= HandleTaskStateChanged;
            taskController.StateChanged += HandleTaskStateChanged;
        }

        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.GuidanceRejected -= HandleGuidanceRejected;
            guidanceCoordinator.GuidanceRejected += HandleGuidanceRejected;
            guidanceCoordinator.RuntimeIntervention -= HandleRuntimeIntervention;
            guidanceCoordinator.RuntimeIntervention += HandleRuntimeIntervention;
            guidanceCoordinator.GuidanceExecuted -= HandleGuidanceExecuted;
            guidanceCoordinator.GuidanceExecuted += HandleGuidanceExecuted;
            guidanceCoordinator.GuidanceExpired -= HandleGuidanceExpired;
            guidanceCoordinator.GuidanceExpired += HandleGuidanceExpired;
            guidanceCoordinator.RequestPhaseChanged -= HandleRequestPhaseChanged;
            guidanceCoordinator.RequestPhaseChanged += HandleRequestPhaseChanged;
            guidanceCoordinator.Replanning -= HandleReplanning;
            guidanceCoordinator.Replanning += HandleReplanning;
            guidanceCoordinator.TaskCompleted -= HandleTaskCompleted;
            guidanceCoordinator.TaskCompleted += HandleTaskCompleted;
            guidanceCoordinator.GuidanceTargetSelected -= HandleGuidanceTargetSelected;
            guidanceCoordinator.GuidanceTargetSelected += HandleGuidanceTargetSelected;
            guidanceCoordinator.TargetReached -= HandleTargetReached;
            guidanceCoordinator.TargetReached += HandleTargetReached;
            guidanceCoordinator.DetourCompleted -= HandleDetourCompleted;
            guidanceCoordinator.DetourCompleted += HandleDetourCompleted;
        }

        if (riskEvaluator != null)
        {
            riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
            riskEvaluator.RiskStateChanged += HandleRiskStateChanged;
        }

        if (pauseResetController != null)
        {
            pauseResetController.ResetStarted -= HandleResetStarted;
            pauseResetController.ResetStarted += HandleResetStarted;
            pauseResetController.ResetCompleted -= HandleResetCompleted;
            pauseResetController.ResetCompleted += HandleResetCompleted;
        }

        if (rotationGainController != null)
        {
            rotationGainController.LifecycleEventOccurred -= HandleRotationGainEvent;
            rotationGainController.LifecycleEventOccurred += HandleRotationGainEvent;
        }

        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
        }
    }

    private void Unsubscribe()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceRequestBuilt -= HandleRequestBuilt;
            agentClient.GuidanceReceived.RemoveListener(HandleResponseReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
            agentClient.BackendOnlineChanged.RemoveListener(HandleBackendOnlineChanged);
        }
        if (taskController != null) taskController.StateChanged -= HandleTaskStateChanged;
        if (guidanceCoordinator != null)
        {
            guidanceCoordinator.GuidanceRejected -= HandleGuidanceRejected;
            guidanceCoordinator.RuntimeIntervention -= HandleRuntimeIntervention;
            guidanceCoordinator.GuidanceExecuted -= HandleGuidanceExecuted;
            guidanceCoordinator.GuidanceExpired -= HandleGuidanceExpired;
            guidanceCoordinator.RequestPhaseChanged -= HandleRequestPhaseChanged;
            guidanceCoordinator.Replanning -= HandleReplanning;
            guidanceCoordinator.TaskCompleted -= HandleTaskCompleted;
            guidanceCoordinator.GuidanceTargetSelected -= HandleGuidanceTargetSelected;
            guidanceCoordinator.TargetReached -= HandleTargetReached;
            guidanceCoordinator.DetourCompleted -= HandleDetourCompleted;
        }
        if (riskEvaluator != null) riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        if (pauseResetController != null)
        {
            pauseResetController.ResetStarted -= HandleResetStarted;
            pauseResetController.ResetCompleted -= HandleResetCompleted;
        }
        if (rotationGainController != null) rotationGainController.LifecycleEventOccurred -= HandleRotationGainEvent;
        if (physicalSpaceCalibration != null) physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
    }

    private void WriteStatus()
    {
        if (statusText == null)
        {
            return;
        }

        var spec = currentSpec ?? ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        var countdown = runState == ExperimentRunState.Completing || stateBeforeSafetyHold == ExperimentRunState.Completing
            ? $"\nCountdown: {Mathf.CeilToInt(completionSecondsRemaining)}"
            : string.Empty;
        statusText.text =
            $"Participant: {spec.participantId}\nCondition: {spec.condition}\nTask: {spec.taskId}\n" +
            $"Physical Space: {spec.physicalSpaceId}\nBackend: {(agentClient != null && agentClient.BackendOnline ? "Online" : "Offline")}\n" +
            $"Tracking: {(xrTrackingAdapter != null && xrTrackingAdapter.IsHeadsetTrackingActive ? "Valid" : "Invalid")}\n" +
            $"Calibration: {(physicalSpaceCalibration != null ? physicalSpaceCalibration.State.ToString() : "Missing")}\n" +
            $"Risk: {(riskEvaluator != null && riskEvaluator.CurrentRiskState != null ? riskEvaluator.CurrentRiskState.boundary_risk : "unknown")}\n" +
            $"Task State: {(taskController != null ? taskController.RuntimeState.phase.ToString() : "missing")}\n" +
            $"Session State: {runState}\nStatus: {lastStatusMessage}{countdown}";
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null) physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        if (userPoseTracker == null) userPoseTracker = FindObjectOfType<UserPoseTracker>();
        if (physicalSpaceCalibration == null) physicalSpaceCalibration = FindObjectOfType<PhysicalSpaceCalibration>();
        if (riskEvaluator == null) riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        if (xrTrackingAdapter == null) xrTrackingAdapter = FindObjectOfType<XRTrackingAdapter>();
        if (taskController == null) taskController = FindObjectOfType<GuidanceTaskController>();
        if (guidanceCoordinator == null) guidanceCoordinator = FindObjectOfType<GuidanceCoordinator>();
        if (agentClient == null) agentClient = FindObjectOfType<AgentClient>();
        if (experimentLogger == null) experimentLogger = FindObjectOfType<ExperimentLogger>();
        if (arrivalDetector == null) arrivalDetector = FindObjectOfType<ArrivalDetector>();
        if (rotationGainController == null) rotationGainController = FindObjectOfType<RotationGainController>();
        if (guidanceExecutor == null) guidanceExecutor = FindObjectOfType<GuidanceExecutor>();
        if (pauseResetController == null) pauseResetController = FindObjectOfType<PauseResetController>();
        if (participantUiController == null) participantUiController = FindObjectOfType<ParticipantUiController>();
    }
}
