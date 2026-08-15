using System;
using System.Collections;
using System.Diagnostics;
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

    [Header("自动恢复")]
    [Tooltip("Tracking 暂时丢失后允许系统自动恢复的最长时间，单位：秒。")]
    [SerializeField, Min(0.1f)] private float trackingRecoveryTimeoutSeconds = 5f;

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
    private ExperimentRunState stateBeforeBackendHold = ExperimentRunState.Running;
    private Coroutine completionRoutine;
    private Coroutine runtimePrerequisiteRecoveryRoutine;
    private Coroutine backendHoldTimeoutRoutine;
    private bool runtimePrerequisiteRecoveryPending;
    private float completionSecondsRemaining;
    private string lastStatusMessage = "Initializing";
    private float nextReadinessRefreshTime;
    private bool runtimeInterventionActive;
    private SceneReadinessReport sceneReadiness;
    private LogTraceContext activeGuidanceTrace = LogTraceContext.Empty;

    public event Action<ExperimentRunState> SessionStateChanged;
    public event Action<float> CompletionCountdownChanged;

    public ExperimentRunState RunState => runState;
    public float CompletionSecondsRemaining => completionSecondsRemaining;
    public ExperimentConditionSpec CurrentSpec => currentSpec;

    private void Awake()
    {
        ResolveReferences();
        participantUiController?.ShowWaiting();
        experimentLogger?.SetRunState(runState, activeGuidanceTrace);
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        StopCompletionCountdown();
        StopRuntimePrerequisiteRecovery();
        StopBackendHoldTimeout();
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
        experimentLogger?.SetRunState(runState, activeGuidanceTrace);
        Subscribe();
        RefreshReadinessState();
    }

    /// <summary>
    /// 接收组合根的只读场景就绪结果，缺失关键引用时保持 Preparing。
    /// </summary>
    public void SetSceneReadiness(SceneReadinessReport readiness)
    {
        sceneReadiness = readiness;
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
        if (xrTrackingAdapter == null || !xrTrackingAdapter.IsTrackingActive ||
            physicalSpaceManager == null ||
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

    /// <summary>
    /// 在 SafetyHold 中使用稳定 Tracking 重新建立当前 Physical Space 校准；成功后仍等待显式恢复。
    /// </summary>
    /// <returns>重新校准成功并生成匹配风险快照时返回 true。</returns>
    public bool RecalibratePhysicalSpace()
    {
        if (runState != ExperimentRunState.SafetyHold)
        {
            return false;
        }

        ResolveReferences();
        experimentLogger?.LogCalibrationStarted();
        if (xrTrackingAdapter == null || !xrTrackingAdapter.IsTrackingActive ||
            physicalSpaceManager == null || physicalSpaceManager.CurrentSpace == null ||
            physicalSpaceCalibration == null || !physicalSpaceCalibration.CalibrateCurrentPose())
        {
            lastStatusMessage = "Recalibration failed";
            WriteStatus();
            return false;
        }

        var risk = riskEvaluator?.EvaluateNow();
        experimentLogger?.LogCalibrationCompleted(physicalSpaceCalibration.CalibrationId);
        lastStatusMessage = risk != null ? "Recalibration valid; waiting for resume" : "Recalibration waiting for risk";
        WriteStatus();
        return risk != null;
    }

    /// <summary>研究员按钮入口：准备阶段校准，或在 SafetyHold 中重新校准。</summary>
    public void RequestCalibration()
    {
        if (runState == ExperimentRunState.SafetyHold)
            RecalibratePhysicalSpace();
        else
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
        activeGuidanceTrace = LogTraceContext.Empty;
        currentSpec = ExperimentConditionMatrix.Build(conditionIndex, taskIndex, physicalSpaceIndex, participantId);
        TransitionTo(ExperimentRunState.Starting, "Starting session");
        participantUiController?.ShowStarting();

        if (!physicalSpaceManager.SetPhysicalSpace(currentSpec.physicalSpaceId))
        {
            EnterError("Selected physical space could not be loaded.");
            return;
        }

        experimentLogger.StartSession(currentSpec);
        experimentLogger.LogConditionSelected(currentSpec);
        var conditionPolicy = ExperimentConditionMatrix.GetPolicy(currentSpec.condition);
        rotationGainController.SetGainEnabled(
            conditionPolicy.UseRotationGain == ConditionCapabilityMode.Enabled);
        taskController.ConfigureTask(currentSpec.ToTaskDefinition());
        taskController.StartTask();

        var risk = riskEvaluator.EvaluateNow();
        if (runState != ExperimentRunState.Starting)
        {
            return;
        }

        TransitionTo(ExperimentRunState.Running, "Session running");
        if (IsCritical(risk))
        {
            if (guidanceCoordinator.BeginAutomaticRecovery(
                    risk,
                    "Critical physical risk at session start; selecting a safe recovery heading."))
            {
                return;
            }

            if (IsSessionActiveState())
            {
                EndSession("unrecoverable_critical_risk_at_session_start");
            }
            return;
        }

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
        LogTargetAbandoned(activeGuidanceTrace, "researcher pause");
        FreezeExperimentBehavior();
        taskController?.PauseForResearcher();
        TransitionTo(ExperimentRunState.Paused, "Paused by researcher");
        participantUiController?.ShowResearcherPaused();
        experimentLogger?.LogStatus(
            "session_paused",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            "researcher pause");
    }

    public void ResumeSession()
    {
        if (runState != ExperimentRunState.Paused && runState != ExperimentRunState.SafetyHold)
        {
            return;
        }

        if (runState == ExperimentRunState.SafetyHold &&
            pauseResetController != null &&
            pauseResetController.IsActive &&
            pauseResetController.State != ResetLifecycleState.Completed)
        {
            return;
        }

        if (xrTrackingAdapter == null || !xrTrackingAdapter.IsTrackingActive ||
            physicalSpaceCalibration == null || !physicalSpaceCalibration.IsCalibrationValid)
        {
            RejectResearcherResume("tracking or calibration is not ready");
            return;
        }

        var risk = riskEvaluator?.EvaluateNow();
        ResumeSessionWithRisk(risk);
    }

    private void ResumeSessionWithRisk(PhysicalRiskState risk)
    {
        var restoreResearcherPause = runState == ExperimentRunState.SafetyHold &&
                                     stateBeforeSafetyHold == ExperimentRunState.Paused;
        if (!IsSafeForResume(risk))
        {
            RejectResearcherResume("physical risk is not safe for resume");
            return;
        }

        if (pauseResetController != null &&
            pauseResetController.State == ResetLifecycleState.Cancelled)
        {
            RejectResearcherResume("reset turn was interrupted");
            return;
        }

        var requestedDuringResetRecovery = false;
        if (taskController != null &&
            taskController.RuntimeState.phase == GuidanceRuntimePhase.PausedForReset)
        {
            if (guidanceCoordinator == null || !guidanceCoordinator.ResumeAfterResetAndReplan())
            {
                RejectResearcherResume("reset recovery is still stabilizing or not explicitly safe");
                return;
            }

            requestedDuringResetRecovery = true;
        }
        else if (pauseResetController != null &&
                 pauseResetController.State == ResetLifecycleState.Completed &&
                 !pauseResetController.ResumeAfterReset())
        {
            RejectResearcherResume("manual reset turn has not completed");
            return;
        }
        if (restoreResearcherPause)
        {
            guidanceCoordinator?.CancelActiveGuidance();
            taskController?.PauseForResearcher();
            runtimeInterventionActive = false;
            stateBeforeSafetyHold = ExperimentRunState.Running;
            TransitionTo(ExperimentRunState.Paused, "Researcher pause restored after reset");
            participantUiController?.ShowResearcherPaused();
            return;
        }
        runtimeInterventionActive = false;
        experimentLogger?.Session?.EndPause();
        if (taskController != null)
        {
            if (!requestedDuringResetRecovery)
            {
                taskController.ResumeAfterResearcherPause();
            }
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
        arrivalDetector?.WatchReservation(taskController != null ? taskController.RuntimeState.ActiveReservation : null);
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

        var terminalTrace = activeGuidanceTrace;
        StopRuntimePrerequisiteRecovery();
        StopBackendHoldTimeout();
        StopCompletionCountdown();
        LogTargetAbandoned(terminalTrace, "session ended");
        FreezeExperimentBehavior();
        pauseResetController?.CancelReset("session_ended");
        experimentLogger?.Session?.EndPause();
        TransitionTo(ExperimentRunState.Ended, string.IsNullOrWhiteSpace(reason) ? "Session ended" : reason);
        taskController?.EndTask();
        experimentLogger?.EndSession(reason, terminalTrace);
        activeGuidanceTrace = LogTraceContext.Empty;
        participantUiController?.ShowEnded();
    }

    public bool CanStartSession(out string missingConditions)
    {
        if (sceneReadiness != null && !sceneReadiness.IsReady)
        {
            missingConditions = sceneReadiness.ErrorSummary;
            return false;
        }

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

        if (!xrTrackingAdapter.IsTrackingActive)
        {
            missingConditions = "XR tracking";
            return false;
        }

        if (Application.platform == RuntimePlatform.Android &&
            xrTrackingAdapter.InputMode == TrackingInputMode.EditorSimulation)
        {
            missingConditions = "device tracking mode";
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

    private void EnterSafetyHold(string reason, bool startReset = true)
    {
        var terminalTrace = activeGuidanceTrace;
        if (runState != ExperimentRunState.SafetyHold)
        {
            stateBeforeSafetyHold = runState;
            if (stateBeforeSafetyHold == ExperimentRunState.Completing)
            {
                StopCompletionCountdown();
            }
            experimentLogger?.Session?.BeginPause();
        }

        rotationGainController?.AbortGain(startReset ? "reset_required" : "runtime_intervention");
        LogTargetAbandoned(terminalTrace, reason);
        FreezeExperimentBehavior();
        TransitionTo(ExperimentRunState.SafetyHold, reason);
        if (!startReset)
        {
            pauseResetController?.CancelReset(reason);
        }
        if (startReset)
        {
            guidanceCoordinator?.BeginAutomaticRecovery(
                riskEvaluator != null ? riskEvaluator.CurrentRiskState : null,
                reason);
        }
        if (runtimeInterventionActive)
            participantUiController?.ShowRuntimeIntervention();
        else
            participantUiController?.ShowCriticalSafety();
        experimentLogger?.LogStatus(
            "safety_hold",
            terminalTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            reason);
        activeGuidanceTrace = LogTraceContext.Empty;
    }

    private void RejectResearcherResume(string reason)
    {
        lastStatusMessage = string.IsNullOrWhiteSpace(reason) ? "Resume rejected" : reason;
        if (runState == ExperimentRunState.Paused)
        {
            participantUiController?.ShowResearcherPaused();
        }
        else
        {
            participantUiController?.ShowRuntimeIntervention();
        }
        WriteStatus();
    }

    private void EnterError(string reason)
    {
        StopBackendHoldTimeout();
        var terminalTrace = activeGuidanceTrace;
        LogTargetAbandoned(terminalTrace, reason);
        FreezeExperimentBehavior();
        pauseResetController?.CancelReset("session_error");
        TransitionTo(ExperimentRunState.Error, reason);
        participantUiController?.ShowSystemUnavailable();
        experimentLogger?.LogStatus(
            "task_failed",
            terminalTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            reason);
        activeGuidanceTrace = LogTraceContext.Empty;
    }

    private void FreezeExperimentBehavior()
    {
        guidanceCoordinator?.CancelActiveGuidance();
        agentClient?.CancelPendingRequest();
        arrivalDetector?.WatchReservation(null);
        rotationGainController?.StopGain();
        guidanceExecutor?.Clear();
    }

    private void LogTargetAbandoned(LogTraceContext trace, string reason)
    {
        if (trace == null || !trace.HasGuidanceIdentity)
        {
            return;
        }

        experimentLogger?.LogTargetAbandoned(
            trace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            reason);
    }

    private void HandleTaskStateChanged(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus(
            "task_state_changed",
            activeGuidanceTrace,
            state,
            riskEvaluator?.CurrentRiskState,
            "task state changed");
        if (runState == ExperimentRunState.Running &&
            state != null && state.phase == GuidanceRuntimePhase.PausedForReset)
        {
            runtimeInterventionActive = false;
            participantUiController?.ShowAutomaticReset();
            lastStatusMessage = "Automatic safety reset in progress";
            WriteStatus();
            return;
        }

        if (runState == ExperimentRunState.Running && state != null && state.IsCompleted)
        {
            arrivalDetector?.WatchReservation(null);
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
        experimentLogger?.LogRiskSnapshot(
            activeGuidanceTrace,
            state,
            taskController?.RuntimeState,
            xrTrackingAdapter != null && xrTrackingAdapter.IsTrackingActive,
            physicalSpaceCalibration != null && physicalSpaceCalibration.IsCalibrationValid,
            userPoseTracker != null ? userPoseTracker.UserVirtualPosition : Vector3.zero,
            userPoseTracker != null ? userPoseTracker.UserVirtualForward : Vector3.forward);
        if (!IsCritical(state) || !IsSessionActiveState())
        {
            return;
        }

        if (runState == ExperimentRunState.Paused)
        {
            // 研究员暂停保持独立语义，不转换为自动恢复或 SafetyHold。
            return;
        }

        if (guidanceCoordinator == null)
        {
            EndSession("critical_risk_without_guidance_coordinator");
        }
        else if (runState == ExperimentRunState.Completing)
        {
            EndSession("critical_risk_during_completion");
        }
        else if (!guidanceCoordinator.IsAutomaticRecoveryActive)
        {
            guidanceCoordinator.BeginAutomaticRecovery(
                state,
                "Critical physical risk without an executable guidance action.");
        }
    }

    private void HandleResetStarted(ResetEventInfo info)
    {
        if (runState != ExperimentRunState.Paused)
        {
            participantUiController?.ShowRecoveryStarted(info);
        }
        experimentLogger?.LogStatus(
            "reset_started",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            info != null ? $"safe heading yaw: {info.RelativeYawDegrees:F1} deg" : "reset started");
    }

    private void HandleResetCompleted(ResetEventInfo _)
    {
        if (runState != ExperimentRunState.Paused)
        {
            participantUiController?.HideResetHud();
            participantUiController?.ShowRecoveryReplanning();
        }
        experimentLogger?.LogStatus(
            "reset_completed",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            "safe heading reached");
        if (taskController != null &&
            taskController.RuntimeState.phase == GuidanceRuntimePhase.PausedForReset)
        {
            if (guidanceCoordinator != null)
            {
                // 自动 Reset 必须由 Coordinator 使用最新 Physical Pose 统一复验。
                guidanceCoordinator.ReevaluateAfterReset();
            }
            else if (IsSessionActiveState())
            {
                EndSession("reset_completed_without_guidance_coordinator");
            }
            return;
        }

        if (runState == ExperimentRunState.SafetyHold)
        {
            // 研究员暂停中的手动 Reset 保留原有显式恢复语义。
            ResumeSessionWithRisk(riskEvaluator?.EvaluateNow());
        }
    }

    private void HandleBackendOnlineChanged(bool online)
    {
        if (online && runState == ExperimentRunState.BackendHold)
        {
            RecoverFromBackendHold();
        }

        RefreshReadinessState();
    }

    private void HandleBackendHealthChanged(BackendHealthStatusInfo info)
    {
        if (info == null)
        {
            return;
        }

        experimentLogger?.LogBackendHealth(
            info,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);

        if (info.Status == BackendHealthStatus.Offline && IsSessionActiveState())
        {
            EnterBackendHold();
            return;
        }

        if (info.Status == BackendHealthStatus.Online && runState == ExperimentRunState.BackendHold)
        {
            RecoverFromBackendHold();
            return;
        }

        RefreshReadinessState();
    }

    private void EnterBackendHold()
    {
        if (runState == ExperimentRunState.BackendHold)
        {
            return;
        }

        stateBeforeBackendHold = runState;
        StopCompletionCountdown();
        FreezeExperimentBehavior();
        TransitionTo(ExperimentRunState.BackendHold, "Backend unavailable; waiting for recovery");
        participantUiController?.ShowBackendHold();
        StopBackendHoldTimeout();
        backendHoldTimeoutRoutine = StartCoroutine(BackendHoldTimeoutRoutine());
    }

    private void RecoverFromBackendHold()
    {
        if (runState != ExperimentRunState.BackendHold)
        {
            return;
        }

        StopBackendHoldTimeout();
        if (stateBeforeBackendHold == ExperimentRunState.Paused)
        {
            TransitionTo(ExperimentRunState.Paused, "Backend recovered; researcher pause restored");
            participantUiController?.ShowResearcherPaused();
            return;
        }

        if (stateBeforeBackendHold == ExperimentRunState.SafetyHold)
        {
            TransitionTo(ExperimentRunState.SafetyHold, "Backend recovered; safety hold restored");
            participantUiController?.ShowAutomaticReset();
            return;
        }

        if (stateBeforeBackendHold == ExperimentRunState.Completing &&
            taskController != null && taskController.RuntimeState.IsCompleted)
        {
            TransitionTo(ExperimentRunState.Completing, "Backend recovered; completion resumed");
            participantUiController?.ShowCompleted();
            ResumeCompletionCountdown();
            return;
        }

        TransitionTo(ExperimentRunState.Running, "Backend recovered; replanning");
        participantUiController?.ShowReplanningHold();
        guidanceCoordinator?.RequestNextGuidance();
    }

    private IEnumerator BackendHoldTimeoutRoutine()
    {
        var startedAt = Time.unscaledTime;
        var timeoutSeconds = agentClient != null ? agentClient.BackendFatalTimeoutSeconds : 30f;
        while (runState == ExperimentRunState.BackendHold &&
               Time.unscaledTime - startedAt < timeoutSeconds)
        {
            yield return null;
        }

        backendHoldTimeoutRoutine = null;
        if (runState == ExperimentRunState.BackendHold)
        {
            experimentLogger?.LogStatus(
                "backend_fatal_timeout",
                activeGuidanceTrace,
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState,
                $"backend did not recover within {timeoutSeconds:F1} seconds");
            EnterError("backend recovery timeout");
        }
    }

    private void StopBackendHoldTimeout()
    {
        if (backendHoldTimeoutRoutine == null)
        {
            return;
        }

        StopCoroutine(backendHoldTimeoutRoutine);
        backendHoldTimeoutRoutine = null;
    }

    private void HandleRequestBuilt(GuidanceRequest request)
    {
        activeGuidanceTrace = experimentLogger != null
            ? experimentLogger.LogRequest(
                request,
                riskEvaluator?.CurrentRiskState,
                taskController?.RuntimeState.ActiveCandidateSetReservation)
            : LogTraceContext.FromRequest(request, riskEvaluator?.CurrentRiskState);
    }

    private void HandleResponseReceived(GuidanceResponse response)
    {
        activeGuidanceTrace = experimentLogger != null
            ? experimentLogger.LogResponse(response, riskEvaluator?.CurrentRiskState)
            : LogTraceContext.FromResponse(response, riskEvaluator?.CurrentRiskState);
    }

    private void HandleRequestFailed(string message)
    {
        experimentLogger?.LogStatus(
            "request_failed",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            message);
        if (IsSessionActiveState())
        {
            if (agentClient != null && agentClient.LastRequestFailureRecoverable)
            {
                EnterBackendHold();
            }
            else
            {
                EnterError(message);
            }
        }
    }

    private void HandleGuidanceRejected(string reason)
    {
        experimentLogger?.LogGuidanceRejected(
            activeGuidanceTrace,
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
        var interventionTrace = intervention != null && intervention.Response != null
            ? LogTraceContext.FromResponse(intervention.Response, riskEvaluator?.CurrentRiskState)
            : activeGuidanceTrace;
        experimentLogger?.LogRuntimeIntervention(
            interventionTrace,
            intervention != null ? intervention.Response : null,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            intervention != null ? intervention.Reason : "runtime safety intervention",
            intervention != null ? intervention.Decision : RuntimeGuidanceSafetyDecision.HardStop,
            intervention != null && intervention.StartReset,
            intervention != null ? intervention.ResetAttempt : 0,
            intervention != null && intervention.SafeCandidateAvailable,
            xrTrackingAdapter != null && xrTrackingAdapter.IsTrackingActive,
            physicalSpaceCalibration != null && physicalSpaceCalibration.IsCalibrationValid,
            guidanceCoordinator?.FrozenRecoveryPlan);
        if (intervention != null && intervention.Decision == RuntimeGuidanceSafetyDecision.SoftReroute)
        {
            if (intervention.Response != null)
            {
                LogTargetAbandoned(interventionTrace, intervention.Reason);
            }
            activeGuidanceTrace = LogTraceContext.Empty;
            participantUiController?.ShowRuntimeIntervention();
            return;
        }

        if (intervention != null && intervention.Decision == RuntimeGuidanceSafetyDecision.PauseReset)
        {
            LogTargetAbandoned(interventionTrace, intervention.Reason);
            if (intervention.ResetAttempt > 1)
            {
                experimentLogger?.LogStatus(
                    "pause_reset_retry",
                    interventionTrace,
                    taskController?.RuntimeState,
                    riskEvaluator?.CurrentRiskState,
                    $"automatic reset retry {intervention.ResetAttempt}");
            }
            activeGuidanceTrace = LogTraceContext.Empty;
            runtimeInterventionActive = false;
            participantUiController?.ShowAutomaticReset();
            lastStatusMessage = $"Automatic reset attempt {intervention.ResetAttempt}";
            WriteStatus();
            return;
        }

        activeGuidanceTrace = interventionTrace;
        runtimeInterventionActive = true;
        experimentLogger?.LogStatus(
            "hard_stop",
            interventionTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            intervention != null ? intervention.Reason : "runtime safety intervention");
        if (IsSessionActiveState())
        {
            EndSession(intervention != null && !string.IsNullOrWhiteSpace(intervention.Reason)
                ? $"automatic_safety_termination: {intervention.Reason}"
                : "automatic_safety_termination");
        }
    }

    private void HandleGuidanceExecuted(GuidanceResponse response)
    {
        experimentLogger?.LogGuidanceExecuted(
            activeGuidanceTrace,
            response,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
        if (runState == ExperimentRunState.Running)
        {
            participantUiController?.ShowGuidance();
        }
    }

    private void HandleGuidanceExpired(string reason)
    {
        experimentLogger?.LogGuidanceExpired(
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            reason);
        LogTargetAbandoned(activeGuidanceTrace, reason);
        activeGuidanceTrace = LogTraceContext.Empty;
    }

    private void HandleRequestPhaseChanged(GuidanceRequestPhase phase)
    {
        if (phase == GuidanceRequestPhase.Verified)
        {
            experimentLogger?.LogGuidanceVerified(
                activeGuidanceTrace,
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState);
        }
        else if (phase == GuidanceRequestPhase.Cancelled)
        {
            experimentLogger?.LogGuidanceCancelled(
                activeGuidanceTrace,
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState,
                "guidance lifecycle cancelled");
            activeGuidanceTrace = LogTraceContext.Empty;
        }
    }

    private void HandleReplanning(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus(
            "replanning_started",
            activeGuidanceTrace,
            state,
            riskEvaluator?.CurrentRiskState,
            "replanning requested");
    }

    private void HandleReplanHoldChanged(ReplanHoldInfo info)
    {
        if (info == null)
        {
            return;
        }

        experimentLogger?.LogReplanHold(
            info,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
        if (info.IsHolding && runState == ExperimentRunState.Running)
        {
            participantUiController?.ShowReplanningHold();
        }
    }

    private void HandleTaskCompleted(GuidanceRuntimeState state)
    {
        experimentLogger?.LogStatus(
            "task_completed",
            activeGuidanceTrace,
            state,
            riskEvaluator?.CurrentRiskState,
            "task completed");
        activeGuidanceTrace = LogTraceContext.Empty;
    }

    private void HandleGuidanceTargetSelected(GuidanceTarget target)
    {
        experimentLogger?.LogTargetReserved(
            activeGuidanceTrace,
            target,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
        if (target != null && target.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            experimentLogger?.LogDetourSelected(
                activeGuidanceTrace,
                target,
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState);
        }
    }

    private void HandleTargetReached(GuidanceTarget target)
    {
        experimentLogger?.LogTargetReached(
            activeGuidanceTrace,
            target,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
    }

    private void HandleDetourCompleted(GuidanceTarget target)
    {
        experimentLogger?.LogDetourCompleted(
            activeGuidanceTrace,
            target,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
    }

    private void HandleRotationGainEvent(RotationGainEventInfo info)
    {
        if (info == null)
        {
            return;
        }

        experimentLogger?.LogRotationGain(
            info,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
    }

    private void HandleCalibrationStateChanged(PhysicalCalibrationState state)
    {
        if (state == PhysicalCalibrationState.Calibrated)
        {
            CompleteRuntimePrerequisiteRecovery();
            return;
        }

        experimentLogger?.LogCalibrationInvalidated(
            physicalSpaceCalibration != null ? physicalSpaceCalibration.CalibrationId : string.Empty,
            $"calibration state: {state}");

        if (!IsSessionActiveState())
        {
            return;
        }

        rotationGainController?.AbortGain("tracking_invalid");
        if (state == PhysicalCalibrationState.TemporarilyTrackingLost)
        {
            BeginRuntimePrerequisiteRecovery("temporary tracking loss");
            return;
        }

        EndSession($"automatic_safety_termination: calibration state {state}");
    }

    private void HandleTrackingStateChanged(bool trackingActive)
    {
        if (!IsSessionActiveState())
        {
            return;
        }

        experimentLogger?.LogStatus(
            trackingActive ? "tracking_recovered" : "tracking_lost",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            trackingActive ? "tracking recovered" : "tracking lost");
        if (trackingActive)
        {
            CompleteRuntimePrerequisiteRecovery();
            return;
        }

        rotationGainController?.AbortGain("tracking_invalid");
        BeginRuntimePrerequisiteRecovery("XR tracking lost");
    }

    private void BeginRuntimePrerequisiteRecovery(string reason)
    {
        if (!IsSessionActiveState())
        {
            return;
        }

        if (!runtimePrerequisiteRecoveryPending)
        {
            runtimePrerequisiteRecoveryPending = true;
            FreezeExperimentBehavior();
            experimentLogger?.LogStatus(
                "runtime_prerequisite_recovery_started",
                activeGuidanceTrace,
                taskController?.RuntimeState,
                riskEvaluator?.CurrentRiskState,
                reason);
            if (isActiveAndEnabled)
            {
                runtimePrerequisiteRecoveryRoutine = StartCoroutine(RuntimePrerequisiteRecoveryTimeoutRoutine());
            }
        }

        if (runState == ExperimentRunState.Paused)
        {
            participantUiController?.ShowResearcherPaused();
        }
        else
        {
            participantUiController?.ShowTrackingRecovery();
        }
        lastStatusMessage = reason;
        WriteStatus();
    }

    private void CompleteRuntimePrerequisiteRecovery()
    {
        if (!runtimePrerequisiteRecoveryPending ||
            xrTrackingAdapter == null || !xrTrackingAdapter.IsTrackingActive ||
            physicalSpaceCalibration == null || !physicalSpaceCalibration.IsCalibrationValid)
        {
            return;
        }

        StopRuntimePrerequisiteRecovery();
        experimentLogger?.LogStatus(
            "runtime_prerequisite_recovered",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            "tracking and calibration recovered");
        if (runState == ExperimentRunState.Paused)
        {
            participantUiController?.ShowResearcherPaused();
            return;
        }

        if (runState == ExperimentRunState.Completing)
        {
            participantUiController?.ShowCompleted();
            return;
        }

        if (guidanceCoordinator != null && guidanceCoordinator.IsAutomaticRecoveryActive)
        {
            var plan = pauseResetController != null ? pauseResetController.ActivePlan : null;
            participantUiController?.ShowRecoveryStarted(new ResetEventInfo(plan, "tracking recovered"));
            return;
        }

        participantUiController?.ShowRecoveryReplanning();
        guidanceCoordinator?.RequestNextGuidance();
    }

    private IEnumerator RuntimePrerequisiteRecoveryTimeoutRoutine()
    {
        var timeoutSeconds = Mathf.Max(0.1f, trackingRecoveryTimeoutSeconds);
        var startedTimestamp = Stopwatch.GetTimestamp();
        while (ElapsedSeconds(startedTimestamp) < timeoutSeconds && IsSessionActiveState())
        {
            yield return null;
        }

        runtimePrerequisiteRecoveryRoutine = null;
        if (!IsSessionActiveState())
        {
            yield break;
        }

        if (xrTrackingAdapter != null && xrTrackingAdapter.IsTrackingActive &&
            physicalSpaceCalibration != null && physicalSpaceCalibration.IsCalibrationValid)
        {
            CompleteRuntimePrerequisiteRecovery();
            yield break;
        }

        EndSession("automatic_safety_termination: tracking recovery timeout");
    }

    private static double ElapsedSeconds(long startedTimestamp)
    {
        return Math.Max(0d, Stopwatch.GetTimestamp() - startedTimestamp) / Stopwatch.Frequency;
    }

    private void StopRuntimePrerequisiteRecovery()
    {
        runtimePrerequisiteRecoveryPending = false;
        if (runtimePrerequisiteRecoveryRoutine == null)
        {
            return;
        }

        StopCoroutine(runtimePrerequisiteRecoveryRoutine);
        runtimePrerequisiteRecoveryRoutine = null;
    }

    private void HandleResetStateChanged(ResetLifecycleState state)
    {
        if (state != ResetLifecycleState.Cancelled)
        {
            return;
        }

        participantUiController?.HideResetHud();
        experimentLogger?.LogStatus(
            "reset_cancelled",
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState,
            "reset cancelled");
    }

    private void HandleRecoveryStateChanged(GuidanceRecoveryState state)
    {
        if (runState == ExperimentRunState.Paused)
        {
            return;
        }

        if (state == GuidanceRecoveryState.RecoveryHold)
        {
            participantUiController?.ShowReplanningHold();
            return;
        }

        if (state == GuidanceRecoveryState.HoldingForSafeHeading ||
            state == GuidanceRecoveryState.Stabilizing ||
            state == GuidanceRecoveryState.Revalidating ||
            state == GuidanceRecoveryState.Replanning ||
            state == GuidanceRecoveryState.NavigationRecovery)
        {
            participantUiController?.ShowRecoveryReplanning();
        }
    }

    private void HandleRecoveryTransitioned(GuidanceRecoveryTransitionInfo transition)
    {
        if (transition == null) return;
        experimentLogger?.LogRecoveryTransition(
            transition,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
        HandleRecoveryStateChanged(transition.NextState);
    }

    private void HandleRecoverySearchCompleted(RecoveryHeadingSearchResult result)
    {
        experimentLogger?.LogRecoverySearch(
            result,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
    }

    private void HandleNavigationRecoveryDiagnosed(NavigationRecoveryDiagnosticInfo diagnostic)
    {
        experimentLogger?.LogNavigationRecoveryDiagnostic(
            diagnostic,
            activeGuidanceTrace,
            taskController?.RuntimeState,
            riskEvaluator?.CurrentRiskState);
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
            experimentLogger?.SetRunState(runState, activeGuidanceTrace);
            SessionStateChanged?.Invoke(runState);
        }
        WriteStatus();
    }

    private bool IsConfigurationLocked()
    {
        return runState == ExperimentRunState.Starting || runState == ExperimentRunState.Running ||
               runState == ExperimentRunState.Paused || runState == ExperimentRunState.SafetyHold ||
               runState == ExperimentRunState.Completing || runState == ExperimentRunState.Error ||
               runState == ExperimentRunState.BackendHold;
    }

    private bool IsSessionActiveState()
    {
        return runState == ExperimentRunState.Starting || runState == ExperimentRunState.Running ||
               runState == ExperimentRunState.Paused || runState == ExperimentRunState.SafetyHold ||
               runState == ExperimentRunState.Completing;
    }

    private static bool IsCritical(PhysicalRiskState state)
    {
        // boundary_risk/reset_risk 均只描述当前真实 Physical Forward 的即时风险；恢复能力由 Coordinator 搜索。
        return state != null && (state.reset_required || state.boundary_risk == "critical" || state.reset_risk == "critical");
    }

    private bool IsSafeForResume(PhysicalRiskState state)
    {
        return state != null &&
               xrTrackingAdapter != null && xrTrackingAdapter.IsTrackingActive &&
               physicalSpaceCalibration != null && physicalSpaceCalibration.IsCalibrationValid &&
               physicalSpaceManager != null && physicalSpaceManager.CurrentSpace != null &&
               !string.IsNullOrWhiteSpace(state.risk_snapshot_id) &&
               string.Equals(state.calibration_id, physicalSpaceCalibration.CalibrationId, StringComparison.Ordinal) &&
               string.Equals(state.physical_space, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal) &&
                !IsCritical(state);
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
            agentClient.BackendHealthChanged -= HandleBackendHealthChanged;
            agentClient.BackendHealthChanged += HandleBackendHealthChanged;
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
            guidanceCoordinator.RecoveryTransitioned -= HandleRecoveryTransitioned;
            guidanceCoordinator.RecoveryTransitioned += HandleRecoveryTransitioned;
            guidanceCoordinator.RecoverySearchCompleted -= HandleRecoverySearchCompleted;
            guidanceCoordinator.RecoverySearchCompleted += HandleRecoverySearchCompleted;
            guidanceCoordinator.NavigationRecoveryDiagnosed -= HandleNavigationRecoveryDiagnosed;
            guidanceCoordinator.NavigationRecoveryDiagnosed += HandleNavigationRecoveryDiagnosed;
            guidanceCoordinator.ReplanHoldChanged -= HandleReplanHoldChanged;
            guidanceCoordinator.ReplanHoldChanged += HandleReplanHoldChanged;
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
            pauseResetController.StateChanged -= HandleResetStateChanged;
            pauseResetController.StateChanged += HandleResetStateChanged;
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
        if (xrTrackingAdapter != null)
        {
            xrTrackingAdapter.TrackingStateChanged -= HandleTrackingStateChanged;
            xrTrackingAdapter.TrackingStateChanged += HandleTrackingStateChanged;
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
            agentClient.BackendHealthChanged -= HandleBackendHealthChanged;
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
            guidanceCoordinator.RecoveryTransitioned -= HandleRecoveryTransitioned;
            guidanceCoordinator.RecoverySearchCompleted -= HandleRecoverySearchCompleted;
            guidanceCoordinator.NavigationRecoveryDiagnosed -= HandleNavigationRecoveryDiagnosed;
            guidanceCoordinator.ReplanHoldChanged -= HandleReplanHoldChanged;
        }
        if (riskEvaluator != null) riskEvaluator.RiskStateChanged -= HandleRiskStateChanged;
        if (pauseResetController != null)
        {
            pauseResetController.ResetStarted -= HandleResetStarted;
            pauseResetController.ResetCompleted -= HandleResetCompleted;
            pauseResetController.StateChanged -= HandleResetStateChanged;
        }
        if (rotationGainController != null) rotationGainController.LifecycleEventOccurred -= HandleRotationGainEvent;
        if (physicalSpaceCalibration != null) physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        if (xrTrackingAdapter != null) xrTrackingAdapter.TrackingStateChanged -= HandleTrackingStateChanged;
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
            $"Tracking: {(xrTrackingAdapter != null && xrTrackingAdapter.IsTrackingActive ? "Valid" : "Invalid")}\n" +
            $"Calibration: {(physicalSpaceCalibration != null ? physicalSpaceCalibration.State.ToString() : "Missing")}\n" +
            $"Current Physical Forward Risk: {(riskEvaluator != null && riskEvaluator.CurrentRiskState != null ? riskEvaluator.CurrentRiskState.boundary_risk : "unknown")}\n" +
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
