using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// 保存一次 Guidance 决策的显式追踪身份与执行意图。
/// </summary>
public sealed class LogTraceContext
{
    /// <summary>不关联 Guidance 决策的空追踪上下文。</summary>
    public static readonly LogTraceContext Empty = new LogTraceContext();

    public string SessionId { get; }
    public string CalibrationId { get; }
    public string RequestId { get; }
    public string DecisionId { get; }
    public string CandidateSetId { get; }
    public string RiskSnapshotId { get; }
    public int StateVersion { get; }
    public string TargetId { get; }
    public string TargetRole { get; }
    public string GuidanceIntent { get; }
    public string MovementInstruction { get; }
    /// <summary>当前上下文是否关联一条具体 Guidance 决策。</summary>
    public bool HasGuidanceIdentity =>
        !string.IsNullOrWhiteSpace(RequestId) ||
        !string.IsNullOrWhiteSpace(DecisionId);

    /// <summary>创建不可变日志追踪上下文。</summary>
    public LogTraceContext(
        string sessionId = "",
        string calibrationId = "",
        string requestId = "",
        string decisionId = "",
        string candidateSetId = "",
        string riskSnapshotId = "",
        int stateVersion = 0,
        string targetId = "",
        string targetRole = "",
        string guidanceIntent = "",
        string movementInstruction = "")
    {
        SessionId = sessionId ?? string.Empty;
        CalibrationId = calibrationId ?? string.Empty;
        RequestId = requestId ?? string.Empty;
        DecisionId = decisionId ?? string.Empty;
        CandidateSetId = candidateSetId ?? string.Empty;
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        StateVersion = stateVersion;
        TargetId = targetId ?? string.Empty;
        TargetRole = targetRole ?? string.Empty;
        GuidanceIntent = guidanceIntent ?? string.Empty;
        MovementInstruction = movementInstruction ?? string.Empty;
    }

    /// <summary>从已发送请求创建追踪上下文。</summary>
    public static LogTraceContext FromRequest(GuidanceRequest request, PhysicalRiskState riskState)
    {
        return request == null
            ? FromRisk(riskState)
            : new LogTraceContext(
                request.session_id,
                FirstNonEmpty(request.calibration_id, riskState != null ? riskState.calibration_id : string.Empty),
                request.request_id,
                request.decision_id,
                request.candidate_set_id,
                FirstNonEmpty(request.risk_snapshot_id, riskState != null ? riskState.risk_snapshot_id : string.Empty),
                request.state_version,
                request.preferred_task_target);
    }

    /// <summary>从 Backend 响应创建追踪上下文。</summary>
    public static LogTraceContext FromResponse(GuidanceResponse response, PhysicalRiskState riskState)
    {
        return response == null
            ? FromRisk(riskState)
            : new LogTraceContext(
                response.session_id,
                FirstNonEmpty(response.calibration_id, riskState != null ? riskState.calibration_id : string.Empty),
                response.request_id,
                response.decision_id,
                response.candidate_set_id,
                FirstNonEmpty(response.risk_snapshot_id, riskState != null ? riskState.risk_snapshot_id : string.Empty),
                response.state_version,
                response.selected_target,
                response.target_role,
                response.physical_intent,
                response.movement_instruction);
    }

    /// <summary>从单独风险快照创建不含 Guidance 身份的上下文。</summary>
    public static LogTraceContext FromRisk(PhysicalRiskState riskState)
    {
        return riskState == null
            ? Empty
            : new LogTraceContext(
                calibrationId: riskState.calibration_id,
                riskSnapshotId: riskState.risk_snapshot_id);
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;
    }
}

/// <summary>
/// 将实验会话、离散业务事件和固定频率 Telemetry 写入独立文件，并镜像必要 Console 信息。
/// </summary>
public sealed class ExperimentLogger : MonoBehaviour
{
    private const string ConsolePrefix = "VirtualGuidance";
    private const double StorageErrorReportIntervalSeconds = 10d;

    [Header("文件日志")]
    [Tooltip("Editor 和测试环境使用的项目相对日志目录；Player 默认改用 persistentDataPath。")]
    [SerializeField] private string logFolderRelativeToProject = "Logs/TestRuns";
    [Tooltip("启用后按缓冲阈值或时间间隔批量 Flush；关闭时仅在关键生命周期强制 Flush。")]
    [SerializeField] private bool autoFlush = true;
    [Tooltip("单个 JSONL Sink 的缓冲阈值，单位：字节。")]
    [SerializeField, Min(4096)] private int bufferSizeBytes = 65536;
    [Tooltip("自动批量 Flush 的最长间隔，单位：秒。")]
    [SerializeField, Min(0.1f)] private float flushIntervalSeconds = 1f;
    [Tooltip("Player 构建启用后将日志写入 Application.persistentDataPath。")]
    [SerializeField] private bool usePersistentDataPathInPlayer = true;
    [Tooltip("写入 session_meta.json 的风险 Telemetry 采样率，单位：Hz；应与 PhysicalRiskEvaluator 保持一致。")]
    [SerializeField, Min(0.1f)] private float riskTelemetrySamplingRateHz = 5f;

    [Header("Unity Console")]
    [Tooltip("启用后将关键实验事件实时镜像到 Unity Console；完整数据仍写入 JSONL。")]
    [SerializeField] private bool mirrorToUnityConsole = true;

    private ExperimentSession session;
    private string logPath;
    private string telemetryLogPath;
    private string sessionMetaPath;
    private BufferedJsonlWriter eventWriter;
    private BufferedJsonlWriter telemetryWriter;
    private ExperimentRunState runState = ExperimentRunState.Preparing;
    private string lastRiskEventSignature = string.Empty;
    private string lastConsoleRiskSignature = string.Empty;
    private long lastStorageErrorTimestamp;
    private readonly List<PendingCalibrationEvent> pendingCalibrationEvents = new List<PendingCalibrationEvent>();

    /// <summary>当前实验会话；尚未开始时为 null。</summary>
    public ExperimentSession Session => session;
    /// <summary>当前 JSONL 日志的绝对路径；尚未开始时为空。</summary>
    public string LogPath => logPath;
    /// <summary>当前 Telemetry JSONL 的绝对路径；尚未开始时为空。</summary>
    public string TelemetryLogPath => telemetryLogPath;
    /// <summary>当前 Session Meta JSON 的绝对路径；尚未开始时为空。</summary>
    public string SessionMetaPath => sessionMetaPath;

    /// <summary>更新后续日志行使用的实验运行状态。</summary>
    public void SetRunState(ExperimentRunState state, LogTraceContext trace)
    {
        if (runState == state)
        {
            return;
        }

        runState = state;
        Write("session_state_changed", null, null, null, null, null, $"session state: {state}", trace);
    }

    /// <summary>
    /// 根据实验条件开始新会话并创建对应 JSONL 路径；null 配置不会改变当前会话。
    /// </summary>
    public void StartSession(ExperimentConditionSpec spec)
    {
        if (spec == null)
        {
            return;
        }

        CloseWriters();
        session = ExperimentSession.Start(spec);
        lastRiskEventSignature = string.Empty;
        lastConsoleRiskSignature = string.Empty;
        InitializeSessionFiles();
        Write("session_started", spec, null, null, null, null, "session started", LogTraceContext.Empty);
        FlushPendingCalibrationEvents();
    }

    /// <summary>记录条件已应用事件；没有有效会话时只输出 Unity Console。</summary>
    public void LogConditionSelected(ExperimentConditionSpec spec)
    {
        Write("condition_selected", spec, null, null, null, null, "condition selected", LogTraceContext.Empty);
    }

    /// <summary>记录发送给后端的请求及请求时的物理风险状态。</summary>
    public LogTraceContext LogRequest(
        GuidanceRequest request,
        PhysicalRiskState riskState,
        CandidateSetReservation candidateReservation = null)
    {
        var trace = LogTraceContext.FromRequest(request, riskState);
        Write(
            "guidance_requested",
            null,
            request,
            null,
            riskState,
            null,
            "guidance requested",
            trace,
            candidateReservation: candidateReservation);
        return trace;
    }

    /// <summary>记录后端响应及响应到达时的物理风险状态。</summary>
    public LogTraceContext LogResponse(GuidanceResponse response, PhysicalRiskState riskState)
    {
        var trace = LogTraceContext.FromResponse(response, riskState);
        Write("guidance_received", null, null, response, riskState, null, "guidance received", trace, "pending");
        return trace;
    }

    /// <summary>记录 Guidance 已通过条件与运行时验证。</summary>
    public void LogGuidanceVerified(LogTraceContext trace, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        Write("guidance_verified", null, null, null, riskState, runtimeState, "guidance verified", trace, "approved");
    }

    /// <summary>记录 Guidance 因协议或 Verifier 契约不成立而被拒绝。</summary>
    public void LogGuidanceRejected(LogTraceContext trace, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_rejected", null, null, null, riskState, runtimeState, reason, trace, "rejected");
    }

    /// <summary>记录 Guidance 因暂停、重规划或会话终止而取消。</summary>
    public void LogGuidanceCancelled(LogTraceContext trace, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_cancelled", null, null, null, riskState, runtimeState, reason, trace);
    }

    /// <summary>记录 Guidance 因请求身份或状态版本变化而过期。</summary>
    public void LogGuidanceExpired(LogTraceContext trace, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_expired", null, null, null, riskState, runtimeState, reason, trace);
    }

    /// <summary>记录一次 Physical Space 校准尝试开始；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationStarted()
    {
        LogOrQueueCalibrationEvent("calibration_started", string.Empty, "calibration started");
    }

    /// <summary>记录成功校准及其 Calibration ID；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationCompleted(string calibrationId)
    {
        LogOrQueueCalibrationEvent("calibration_completed", calibrationId, "calibration completed");
    }

    /// <summary>记录校准失效；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationInvalidated(string calibrationId, string reason)
    {
        LogOrQueueCalibrationEvent(
            "calibration_invalidated",
            calibrationId,
            string.IsNullOrWhiteSpace(reason) ? "calibration invalidated" : reason);
    }

    /// <summary>记录到达当前 Guidance Target，并保留目标 ID 与角色。</summary>
    public void LogTargetReserved(
        LogTraceContext trace,
        GuidanceTarget target,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        LogTargetEvent("target_reserved", trace, target, runtimeState, riskState, "target reserved");
    }

    /// <summary>记录当前 Target Reservation 已放弃。</summary>
    public void LogTargetAbandoned(
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string reason)
    {
        Write(
            "target_abandoned",
            null,
            null,
            null,
            riskState,
            runtimeState,
            string.IsNullOrWhiteSpace(reason) ? "target abandoned" : reason,
            trace,
            selectedTargetOverride: trace != null ? trace.TargetId : string.Empty,
            targetRoleOverride: trace != null ? trace.TargetRole : string.Empty);
    }

    /// <summary>记录当前 Target Reservation 已由有效到达完成。</summary>
    public void LogTargetReached(
        LogTraceContext trace,
        GuidanceTarget target,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        LogTargetEvent("target_completed", trace, target, runtimeState, riskState, "target completed");
    }

    /// <summary>记录一次 NavigableDetour 被选为当前 Guidance Target。</summary>
    public void LogDetourSelected(
        LogTraceContext trace,
        GuidanceTarget target,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        LogTargetEvent("detour_selected", trace, target, runtimeState, riskState, "detour selected");
    }

    /// <summary>记录 NavigableDetour 已完成且原路线进度保持不变。</summary>
    public void LogDetourCompleted(
        LogTraceContext trace,
        GuidanceTarget target,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        LogTargetEvent("detour_completed", trace, target, runtimeState, riskState, "detour completed");
    }

    /// <summary>记录运行期安全决策、原始 Guidance 与干预原因。</summary>
    public void LogRuntimeIntervention(
        LogTraceContext trace,
        GuidanceResponse response,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string reason,
        RuntimeGuidanceSafetyDecision decision,
        bool startReset,
        int resetAttempt = 0,
        bool safeCandidateAvailable = false,
        bool? trackingValid = null,
        bool? calibrationValid = null,
        RecoveryHeadingPlan recoveryPlan = null)
    {
        Write(
            "runtime_intervention",
            null,
            null,
            response,
            riskState,
            runtimeState,
            reason,
            trace,
            "rejected",
            runtimeIntervention: true,
            interventionDecision: decision,
            startReset: startReset,
            resetAttempt: resetAttempt,
            safeCandidateAvailable: safeCandidateAvailable,
            trackingValid: trackingValid,
            calibrationValid: calibrationValid,
            recoveryPlan: recoveryPlan);
    }

    /// <summary>记录自动恢复状态机的前后状态、原因与当时冻结的恢复计划。</summary>
    public void LogRecoveryTransition(
        GuidanceRecoveryTransitionInfo transition,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (transition == null) return;
        Write(
            "recovery_state_changed",
            null,
            null,
            null,
            riskState,
            runtimeState,
            transition.Reason,
            trace,
            recoveryPlan: transition.RecoveryPlan,
            recoveryTransition: transition);
    }

    /// <summary>记录一次 Continuous Recovery Heading Search 的完整可重建诊断。</summary>
    public void LogRecoverySearch(
        RecoveryHeadingSearchResult searchResult,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (searchResult == null) return;
        Write(
            "recovery_search_completed",
            null,
            null,
            null,
            riskState,
            runtimeState,
            searchResult.FailureReason,
            trace,
            recoverySearchResult: searchResult);
    }

    /// <summary>记录通过运行时验证并实际开始执行的 Guidance。</summary>
    public void LogGuidanceExecuted(
        LogTraceContext trace,
        GuidanceResponse response,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        Write(
            "guidance_executed",
            null,
            null,
            response,
            riskState,
            runtimeState,
            "guidance executed",
            trace,
            "approved",
            executed: true);
    }

    /// <summary>
    /// 将固定频率风险快照写入 Telemetry；仅当安全语义变化时额外生成 risk_changed Event。
    /// </summary>
    public void LogRiskSnapshot(
        LogTraceContext trace,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        bool trackingValid,
        bool calibrationValid,
        Vector3 virtualPosition,
        Vector3 virtualForward)
    {
        if (riskState == null)
        {
            return;
        }

        WriteTelemetry(
            trace,
            riskState,
            runtimeState,
            trackingValid,
            calibrationValid,
            virtualPosition,
            virtualForward);

        var signature = BuildRiskSignature(riskState);
        if (string.Equals(signature, lastRiskEventSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastRiskEventSignature = signature;
        Write("risk_changed", null, null, null, riskState, runtimeState, "risk state changed", trace);
    }

    /// <summary>记录任务、风险或请求失败等通用运行事件。</summary>
    public void LogStatus(
        string eventType,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string message)
    {
        Write(eventType, null, null, null, riskState, runtimeState, message, trace);
    }

    /// <summary>记录 Rotation Gain 生命周期及本轮配置、有效倍率和角度预算。</summary>
    public void LogRotationGain(
        RotationGainEventInfo gainEvent,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (gainEvent == null) return;
        var eventType = GainEventType(gainEvent.EventType, gainEvent.Reason);
        Write(
            eventType,
            null,
            null,
            null,
            riskState,
            runtimeState,
            gainEvent.Reason,
            trace,
            rotationGainEvent: gainEvent);
    }

    /// <summary>记录一次 RDW 帧的 requested、limited、applied 和 owner 信息。</summary>
    public void LogRdwFrame(
        RdwFrameEventInfo frameInfo,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (frameInfo == null) return;
        Write(
            "rdw_frame",
            null,
            null,
            null,
            riskState,
            runtimeState,
            frameInfo.LimitedCommand.Reason,
            trace,
            emitUnityConsole: false,
            rdwFrameInfo: frameInfo);
    }

    /// <summary>
    /// 记录 RDW 路线规划、Shadow 对比、调用身份和 PlanCache 生命周期。
    /// </summary>
    public void LogRdwPlanning(
        string eventType,
        RdwPlanningMode mode,
        RdwRoutePlanningResult planningResult,
        PlanningCallIdentity callIdentity,
        RdwRouteSelectionResponse selectionResponse,
        RoutePlan routePlan,
        RoutePlanInvalidationReason invalidationReason,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string message,
        string formalSelectedTargetId = "")
    {
        Write(
            eventType,
            null,
            null,
            null,
            riskState,
            runtimeState,
            message,
            LogTraceContext.Empty,
            selectedTargetOverride: formalSelectedTargetId,
            rdwPlanningMode: mode,
            rdwPlanningResult: planningResult,
            rdwPlanningIdentity: callIdentity,
            rdwRouteSelectionResponse: selectionResponse,
            rdwRoutePlan: routePlan,
            rdwPlanInvalidationReason: invalidationReason);
    }

    /// <summary>记录重规划等待的规划指纹、风险修订与退避次数。</summary>
    public void LogReplanHold(
        ReplanHoldInfo holdInfo,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (holdInfo == null) return;
        Write(
            holdInfo.IsHolding ? "holding_for_replan" : "replan_hold_resumed",
            null,
            null,
            null,
            riskState,
            runtimeState,
            holdInfo.Reason,
            trace,
            replanHoldInfo: holdInfo);
    }

    /// <summary>记录后端健康状态、连续失败次数与宽限期进度。</summary>
    public void LogBackendHealth(
        BackendHealthStatusInfo healthInfo,
        LogTraceContext trace,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState)
    {
        if (healthInfo == null) return;
        Write(
            BackendHealthEventType(healthInfo.Status),
            null,
            null,
            null,
            riskState,
            runtimeState,
            healthInfo.Status.ToString(),
            trace,
            backendHealthInfo: healthInfo);
    }

    /// <summary>先写入 session_ended，再关闭活动会话。</summary>
    public void EndSession(string reason, LogTraceContext trace)
    {
        if (session == null || !session.IsActive)
        {
            return;
        }

        Write(
            "session_ended",
            null,
            null,
            null,
            null,
            null,
            string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason,
            trace);
        Flush();
        session.End(reason);
        CloseWriters();
    }

    /// <summary>立即 Flush 当前 Event 与 Telemetry 缓冲。</summary>
    public void Flush()
    {
        FlushWriter(eventWriter, "events");
        FlushWriter(telemetryWriter, "telemetry");
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            Flush();
        }
    }

    private void OnApplicationQuit()
    {
        Flush();
    }

    private void OnDestroy()
    {
        CloseWriters();
    }

    private void Write(
        string eventType,
        ExperimentConditionSpec spec,
        GuidanceRequest request,
        GuidanceResponse response,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        string message,
        LogTraceContext trace,
        string runtimeValidationResult = "",
        bool runtimeIntervention = false,
        bool executed = false,
        string calibrationIdOverride = "",
        string timestampOverride = "",
        string selectedTargetOverride = "",
        string targetRoleOverride = "",
        bool emitUnityConsole = true,
        RuntimeGuidanceSafetyDecision interventionDecision = RuntimeGuidanceSafetyDecision.Continue,
        bool startReset = false,
        int resetAttempt = 0,
        bool safeCandidateAvailable = false,
        bool? trackingValid = null,
        bool? calibrationValid = null,
        CandidateSetReservation candidateReservation = null,
        RecoveryHeadingPlan recoveryPlan = null,
        GuidanceRecoveryTransitionInfo recoveryTransition = null,
        RecoveryHeadingSearchResult recoverySearchResult = null,
        RotationGainEventInfo rotationGainEvent = null,
        ReplanHoldInfo replanHoldInfo = null,
        BackendHealthStatusInfo backendHealthInfo = null,
        RdwFrameEventInfo rdwFrameInfo = null,
        RdwPlanningMode? rdwPlanningMode = null,
        RdwRoutePlanningResult rdwPlanningResult = null,
        PlanningCallIdentity rdwPlanningIdentity = null,
        RdwRouteSelectionResponse rdwRouteSelectionResponse = null,
        RoutePlan rdwRoutePlan = null,
        RoutePlanInvalidationReason rdwPlanInvalidationReason = RoutePlanInvalidationReason.None)
    {
        trace = trace ?? LogTraceContext.Empty;
        var calibrationId = FirstNonEmpty(
            calibrationIdOverride,
            riskState?.calibration_id,
            request?.calibration_id,
            response?.calibration_id,
            trace.CalibrationId);
        var condition = FirstNonEmpty(request?.condition, spec?.condition, session?.condition);
        var participantId = FirstNonEmpty(request?.participant_id, spec?.participantId, session?.participantId);
        var taskId = FirstNonEmpty(runtimeState?.taskId, request?.task_id, spec?.taskId, session?.taskId);
        var physicalSpaceId = FirstNonEmpty(
            riskState?.physical_space,
            request?.physical_space_id,
            spec?.physicalSpaceId,
            session?.physicalSpaceId);
        var currentExhibit = FirstNonEmpty(runtimeState?.currentExhibitId, request?.current_exhibit, spec?.startExhibitId);
        var preferredTaskTarget = FirstNonEmpty(runtimeState?.nextTargetId, request?.preferred_task_target, spec?.semanticNextTargetId);
        var requestId = FirstNonEmpty(request?.request_id, response?.request_id, trace.RequestId);
        var decisionId = FirstNonEmpty(request?.decision_id, response?.decision_id, trace.DecisionId);
        var candidateSetId = FirstNonEmpty(request?.candidate_set_id, response?.candidate_set_id, trace.CandidateSetId);
        var riskSnapshotId = FirstNonEmpty(
            riskState?.risk_snapshot_id,
            request?.risk_snapshot_id,
            response?.risk_snapshot_id,
            trace.RiskSnapshotId);
        var runtimeTargetRole = runtimeState != null && !string.IsNullOrWhiteSpace(runtimeState.guidanceTargetId)
            ? GuidanceTypeUtility.TargetRoleToProtocol(runtimeState.guidanceTargetRole)
            : string.Empty;
        var targetRole = FirstNonEmpty(targetRoleOverride, response?.target_role, trace.TargetRole, runtimeTargetRole);
        var selectedTarget = FirstNonEmpty(selectedTargetOverride, response?.selected_target, trace.TargetId, runtimeState?.guidanceTargetId);
        var stateVersion = request != null
            ? request.state_version
            : response != null
                ? response.state_version
                : trace.HasGuidanceIdentity
                    ? trace.StateVersion
                    : runtimeState != null
                    ? runtimeState.stateVersion
                    : 0;

        var descriptor = ResolveEventDescriptor(eventType, riskState);
        if (emitUnityConsole && mirrorToUnityConsole && ShouldEmitUnityConsole(eventType, riskState))
        {
            EmitUnityConsole(
                eventType,
                descriptor.EventName,
                descriptor.Category,
                descriptor.Level,
                taskId,
                calibrationId,
                requestId,
                decisionId,
                riskSnapshotId,
                stateVersion,
                runtimeState,
                selectedTarget,
                targetRole,
                riskState,
                response,
                message);
        }

        // Session 前事件只实时输出，不创建无归属 JSONL；Calibration 事件会在 Session 开始后补写。
        if (session == null || !session.IsActive || string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            var timestamp = FirstNonEmpty(timestampOverride, DateTime.UtcNow.ToString("o"));
            var elapsedMilliseconds = ResolveElapsedMilliseconds(timestampOverride);
            var traceObject = new JObject
            {
                ["calibration_id"] = calibrationId,
                ["request_id"] = requestId,
                ["decision_id"] = decisionId,
                ["candidate_set_id"] = candidateSetId,
                ["risk_snapshot_id"] = riskSnapshotId,
                ["state_version"] = stateVersion
            };
            var data = new JObject
            {
                ["participant_id"] = participantId,
                ["condition"] = condition,
                ["policy_version"] = ExperimentConditionPolicy.CurrentVersion,
                ["task_id"] = taskId,
                ["physical_space_id"] = physicalSpaceId,
                ["current_exhibit"] = currentExhibit,
                ["preferred_task_target"] = preferredTaskTarget,
                ["final_target"] = FirstNonEmpty(runtimeState?.finalTargetId, request?.final_target),
                ["runtime_phase"] = runtimeState != null ? runtimeState.phase.ToString() : string.Empty,
                ["run_state"] = runState.ToString().ToLowerInvariant(),
                ["target_role"] = targetRole,
                ["selected_target"] = selectedTarget,
                ["movement_instruction"] = FirstNonEmpty(response?.movement_instruction, trace.MovementInstruction),
                ["physical_intent"] = FirstNonEmpty(response?.physical_intent, trace.GuidanceIntent),
                ["runtime_validation_result"] = runtimeValidationResult,
                ["runtime_intervention"] = runtimeIntervention,
                // 协议键名保持不变，但值只表示当前真实 Physical Forward 的即时风险。
                ["boundary_risk"] = riskState != null ? riskState.boundary_risk : string.Empty,
                ["reset_risk"] = riskState != null ? riskState.reset_risk : string.Empty,
                ["reset_required"] = riskState != null && riskState.reset_required,
                ["current_heading"] = SerializeHeadingSample(
                    riskState != null ? riskState.current_heading : default(HeadingSample))
            };

            if (request != null)
            {
                data["request"] = JToken.FromObject(request);
            }
            if (candidateReservation != null)
            {
                data["candidate_assessments"] = SerializeCandidateAssessments(candidateReservation.Assessments);
            }
            if (response != null && response.verifier != null)
            {
                data["condition_verifier_result"] = response.verifier.approved ? "approved" : "rejected";
                data["verifier_reason"] = response.verifier.reason ?? string.Empty;
            }
            if (string.Equals(eventType, "guidance_received", StringComparison.Ordinal) && response != null)
            {
                data["raw_guidance"] = JToken.FromObject(response);
            }
            if (string.Equals(eventType, "guidance_executed", StringComparison.Ordinal) && executed && response != null)
            {
                data["executed_guidance"] = JToken.FromObject(response);
            }
            if (runtimeIntervention)
            {
                data["intervention_type"] = SafetyDecisionLabel(interventionDecision);
                data["start_reset"] = startReset;
                data["reset_attempt"] = Math.Max(0, resetAttempt);
                data["safe_candidate_available"] = safeCandidateAvailable;
                if (trackingValid.HasValue) data["tracking_valid"] = trackingValid.Value;
                if (calibrationValid.HasValue) data["calibration_valid"] = calibrationValid.Value;
            }
            if (recoveryPlan != null)
            {
                data["recovery_plan"] = SerializeRecoveryPlan(recoveryPlan);
            }
            if (recoveryTransition != null)
            {
                data["previous_state"] = RecoveryStateLabel(recoveryTransition.PreviousState);
                data["next_state"] = RecoveryStateLabel(recoveryTransition.NextState);
                data["transition_reason"] = recoveryTransition.Reason;
            }
            if (recoverySearchResult != null)
            {
                data["recovery_search"] = SerializeRecoverySearch(recoverySearchResult, riskState);
            }
            if (rotationGainEvent != null)
            {
                data["target_physical_yaw_degrees"] = rotationGainEvent.TargetPhysicalYawDegrees;
                data["configured_gain_multiplier"] = rotationGainEvent.ConfiguredGainMultiplier;
                data["effective_gain_multiplier"] = rotationGainEvent.EffectiveGainMultiplier;
                data["max_extra_yaw_degrees"] = rotationGainEvent.MaxExtraYawDegrees;
                data["physical_yaw_degrees"] = rotationGainEvent.PhysicalYawDegrees;
                data["applied_extra_yaw_degrees"] = rotationGainEvent.ExtraYawDegrees;
                data["gain_termination_reason"] = rotationGainEvent.Reason;
            }
            if (replanHoldInfo != null)
            {
                data["hold_reason"] = replanHoldInfo.Reason;
                data["planning_fingerprint"] = replanHoldInfo.PlanningFingerprint;
                data["hold_risk_revision"] = replanHoldInfo.RiskRevision;
                data["replan_retry_count"] = replanHoldInfo.RetryCount;
                data["replan_retry_delay_seconds"] = replanHoldInfo.RetryDelaySeconds;
            }
            if (backendHealthInfo != null)
            {
                data["backend_health_status"] = backendHealthInfo.Status.ToString().ToLowerInvariant();
                data["health_check_failure_count"] = backendHealthInfo.ConsecutiveFailures;
                data["health_failure_elapsed_seconds"] = backendHealthInfo.FailureElapsedSeconds;
                data["health_check_failure_threshold"] = backendHealthInfo.FailureThreshold;
                data["backend_offline_grace_seconds"] = backendHealthInfo.OfflineGraceSeconds;
            }
            if (rdwFrameInfo != null)
            {
                data["pose_sequence"] = rdwFrameInfo.Pose.Sequence;
                data["pose_timestamp"] = rdwFrameInfo.Pose.Timestamp;
                data["physical_position_x"] = rdwFrameInfo.Pose.PhysicalPositionXZ.x;
                data["physical_position_z"] = rdwFrameInfo.Pose.PhysicalPositionXZ.y;
                data["physical_yaw_deg"] = rdwFrameInfo.Pose.PhysicalYaw;
                data["physical_forward_x"] = rdwFrameInfo.Pose.PhysicalForwardXZ.x;
                data["physical_forward_z"] = rdwFrameInfo.Pose.PhysicalForwardXZ.y;
                data["delta_physical_position_x"] = rdwFrameInfo.Pose.DeltaPhysicalPositionXZ.x;
                data["delta_physical_position_z"] = rdwFrameInfo.Pose.DeltaPhysicalPositionXZ.y;
                data["delta_physical_yaw_deg"] = rdwFrameInfo.Pose.DeltaPhysicalYaw;
                data["rdw_delta_time_seconds"] = rdwFrameInfo.Pose.DeltaTime;
                data["virtual_position_x"] = rdwFrameInfo.Pose.VirtualPositionXZ.x;
                data["virtual_position_z"] = rdwFrameInfo.Pose.VirtualPositionXZ.y;
                data["virtual_forward_x"] = rdwFrameInfo.Pose.VirtualForwardXZ.x;
                data["virtual_forward_z"] = rdwFrameInfo.Pose.VirtualForwardXZ.y;
                data["virtual_yaw_deg"] = rdwFrameInfo.Pose.VirtualYaw;
                data["rdw_active_owner"] = rdwFrameInfo.ActiveOwner.ToString();
                data["redirector_type"] = rdwFrameInfo.RedirectorDiagnostics.RedirectorType;
                data["steering_center_x"] = rdwFrameInfo.RedirectorDiagnostics.SteeringCenterXZ.x;
                data["steering_center_z"] = rdwFrameInfo.RedirectorDiagnostics.SteeringCenterXZ.y;
                data["center_direction_x"] = rdwFrameInfo.RedirectorDiagnostics.CenterDirectionXZ.x;
                data["center_direction_z"] = rdwFrameInfo.RedirectorDiagnostics.CenterDirectionXZ.y;
                data["desired_steering_direction_x"] = rdwFrameInfo.RedirectorDiagnostics.DesiredSteeringDirectionXZ.x;
                data["desired_steering_direction_z"] = rdwFrameInfo.RedirectorDiagnostics.DesiredSteeringDirectionXZ.y;
                data["steering_angle_deg"] = rdwFrameInfo.RedirectorDiagnostics.SteeringAngleDegrees;
                data["steering_side"] = rdwFrameInfo.RedirectorDiagnostics.SteeringSide;
                data["raw_rotation_proposal_deg"] = rdwFrameInfo.RedirectorDiagnostics.RawRotationProposalDegrees;
                data["raw_curvature_proposal_deg"] = rdwFrameInfo.RedirectorDiagnostics.RawCurvatureProposalDegrees;
                data["smoothed_rotation_deg"] = rdwFrameInfo.RedirectorDiagnostics.SmoothedRotationDegrees;
                data["smoothed_curvature_deg"] = rdwFrameInfo.RedirectorDiagnostics.SmoothedCurvatureDegrees;
                data["redirector_reason"] = rdwFrameInfo.RedirectorDiagnostics.Reason;
                data["orbit_radius_m"] = rdwFrameInfo.RedirectorDiagnostics.OrbitRadiusMeters;
                data["orbit_radius"] = rdwFrameInfo.RedirectorDiagnostics.OrbitRadiusMeters;
                data["orbit_side"] = rdwFrameInfo.RedirectorDiagnostics.OrbitSide;
                data["orbit_target_x"] = rdwFrameInfo.RedirectorDiagnostics.OrbitTargetXZ.x;
                data["orbit_target_z"] = rdwFrameInfo.RedirectorDiagnostics.OrbitTargetXZ.y;
                data["boundary_force_x"] = rdwFrameInfo.RedirectorDiagnostics.BoundaryForceXZ.x;
                data["boundary_force_z"] = rdwFrameInfo.RedirectorDiagnostics.BoundaryForceXZ.y;
                data["boundary_force_xz_x"] = rdwFrameInfo.RedirectorDiagnostics.BoundaryForceXZ.x;
                data["boundary_force_xz_z"] = rdwFrameInfo.RedirectorDiagnostics.BoundaryForceXZ.y;
                data["obstacle_force_x"] = rdwFrameInfo.RedirectorDiagnostics.ObstacleForceXZ.x;
                data["obstacle_force_z"] = rdwFrameInfo.RedirectorDiagnostics.ObstacleForceXZ.y;
                data["obstacle_force_xz_x"] = rdwFrameInfo.RedirectorDiagnostics.ObstacleForceXZ.x;
                data["obstacle_force_xz_z"] = rdwFrameInfo.RedirectorDiagnostics.ObstacleForceXZ.y;
                data["total_force_x"] = rdwFrameInfo.RedirectorDiagnostics.TotalForceXZ.x;
                data["total_force_z"] = rdwFrameInfo.RedirectorDiagnostics.TotalForceXZ.y;
                data["total_force_xz_x"] = rdwFrameInfo.RedirectorDiagnostics.TotalForceXZ.x;
                data["total_force_xz_z"] = rdwFrameInfo.RedirectorDiagnostics.TotalForceXZ.y;
                data["total_force_magnitude"] = rdwFrameInfo.RedirectorDiagnostics.TotalForceMagnitude;
                data["apf_neutral_reason"] = rdwFrameInfo.RedirectorDiagnostics.ApfNeutralReason;
                data["apf_force_clamped"] = rdwFrameInfo.RedirectorDiagnostics.ApfForceClamped;
                data["navigation_target_id"] = rdwFrameInfo.NavigationIntent.TargetId;
                data["navigation_target_role"] = rdwFrameInfo.NavigationIntent.TargetRole.ToString();
                data["requested_rotation_delta_deg"] = rdwFrameInfo.RequestedCommand.RotationYawDeltaDeg;
                data["requested_curvature_delta_deg"] = rdwFrameInfo.RequestedCommand.CurvatureYawDeltaDeg;
                data["requested_total_yaw_delta_deg"] = rdwFrameInfo.RequestedCommand.RotationYawDeltaDeg + rdwFrameInfo.RequestedCommand.CurvatureYawDeltaDeg;
                data["requested_translation_x"] = rdwFrameInfo.RequestedCommand.TranslationDeltaXZ.x;
                data["requested_translation_z"] = rdwFrameInfo.RequestedCommand.TranslationDeltaXZ.y;
                data["limited_rotation_delta_deg"] = rdwFrameInfo.LimitedCommand.RotationYawDeltaDeg;
                data["limited_curvature_delta_deg"] = rdwFrameInfo.LimitedCommand.CurvatureYawDeltaDeg;
                data["limited_total_yaw_delta_deg"] = rdwFrameInfo.LimitedCommand.RotationYawDeltaDeg + rdwFrameInfo.LimitedCommand.CurvatureYawDeltaDeg;
                data["limited_translation_x"] = rdwFrameInfo.LimitedCommand.TranslationDeltaXZ.x;
                data["limited_translation_z"] = rdwFrameInfo.LimitedCommand.TranslationDeltaXZ.y;
                data["applied_rotation_delta_deg"] = rdwFrameInfo.ApplyResult.AppliedRotationYawDeltaDeg;
                data["applied_curvature_delta_deg"] = rdwFrameInfo.ApplyResult.AppliedCurvatureYawDeltaDeg;
                data["applied_total_yaw_delta_deg"] = rdwFrameInfo.ApplyResult.AppliedYawDeltaDeg;
                data["rdw_apply_status"] = rdwFrameInfo.ApplyResult.Status.ToString();
                data["rdw_reason"] = rdwFrameInfo.LimitedCommand.Reason;
                data["rdw_veto_reason"] = IsRdwVetoReason(rdwFrameInfo.LimitedCommand.Reason) ? rdwFrameInfo.LimitedCommand.Reason : string.Empty;
                data["rdw_tracking_valid"] = rdwFrameInfo.Pose.IsValid;
                data["rdw_calibration_id"] = rdwFrameInfo.Pose.CalibrationId;
                data["rdw_physical_space_id"] = rdwFrameInfo.Pose.PhysicalSpaceId;
                data["rdw_risk_snapshot_id"] = rdwFrameInfo.Geometry.RiskSnapshotId;
                data["rdw_risk_revision"] = rdwFrameInfo.Geometry.RiskRevision;
                var resetProgress = rdwFrameInfo.ResetProgress;
                if (resetProgress.HasPlan)
                {
                    data["reset_id"] = resetProgress.Plan.ResetId;
                    data["reset_start_pose_sequence"] = resetProgress.Plan.StartPoseSequence;
                    data["reset_start_physical_yaw_deg"] = resetProgress.Plan.StartPhysicalYaw;
                    data["reset_start_virtual_yaw_deg"] = resetProgress.Plan.StartVirtualYaw;
                    data["reset_target_physical_heading_deg"] = resetProgress.Plan.TargetPhysicalHeading;
                    data["reset_target_physical_turn_delta_deg"] = resetProgress.Plan.TargetPhysicalTurnDelta;
                    data["reset_mapping_strategy"] = resetProgress.Plan.MappingStrategy.ToString();
                    data["reset_injection_ratio"] = resetProgress.Plan.InjectionRatio;
                    data["reset_target_injected_yaw_deg"] = resetProgress.Plan.TargetInjectedYawDelta;
                    data["reset_max_duration_seconds"] = resetProgress.Plan.MaxDuration;
                    data["reset_physical_turn_accumulated_deg"] = resetProgress.AccumulatedPhysicalTurn;
                    data["reset_injected_yaw_accumulated_deg"] = resetProgress.AccumulatedInjectedYaw;
                    data["reset_physical_heading_reached"] = resetProgress.PhysicalHeadingReached;
                    data["reset_mapping_completed"] = resetProgress.IsMappingComplete;
                    data["reset_mapping_active"] = resetProgress.IsActive;
                    data["reset_mapping_reason"] = resetProgress.Reason;
                }
            }
            if (rdwPlanningMode.HasValue)
            {
                var planningSnapshot = rdwPlanningResult?.Snapshot;
                data["planning_mode"] = rdwPlanningMode.Value.ToString().ToLowerInvariant();
                data["snapshot_id"] = FirstNonEmpty(
                    planningSnapshot?.SnapshotId,
                    rdwPlanningIdentity?.SnapshotId,
                    rdwRouteSelectionResponse?.snapshot_id,
                    rdwRoutePlan?.PlanningSnapshotId);
                data["plan_id"] = FirstNonEmpty(
                    rdwPlanningIdentity?.PlanId,
                    rdwRouteSelectionResponse?.plan_id,
                    rdwRoutePlan?.PlanId);
                data["planning_request_id"] = FirstNonEmpty(
                    rdwPlanningIdentity?.RequestId,
                    rdwRouteSelectionResponse?.request_id);
                data["generation"] = rdwPlanningIdentity != null
                    ? rdwPlanningIdentity.Generation
                    : rdwRouteSelectionResponse != null
                        ? rdwRouteSelectionResponse.generation
                        : rdwRoutePlan?.Generation ?? 0L;
                data["route_id"] = FirstNonEmpty(
                    rdwRouteSelectionResponse?.route_id,
                    rdwRoutePlan?.SelectedRouteId);
                data["formal_selected_target"] = selectedTargetOverride;
                data["planning_invalidation_reason"] =
                    rdwPlanInvalidationReason.ToString().ToLowerInvariant();
                if (planningSnapshot != null)
                {
                    data["planning_pose_sequence"] = planningSnapshot.PoseSequence;
                    data["planning_task_state_version"] = planningSnapshot.TaskStateVersion;
                    data["planning_risk_snapshot_id"] = planningSnapshot.RiskSnapshotId;
                    data["planning_risk_revision"] = planningSnapshot.RiskRevision;
                    data["planning_registry_revision"] = planningSnapshot.RegistryRevision;
                    data["planning_calibration_id"] = planningSnapshot.CalibrationId;
                    data["planning_physical_space_id"] = planningSnapshot.PhysicalSpaceId;
                    data["planning_estimator_id"] = planningSnapshot.Capability?.EstimatorId ?? string.Empty;
                    data["planning_estimator_version"] = planningSnapshot.Capability?.EstimatorVersion ?? string.Empty;
                }
                if (rdwPlanningResult != null)
                {
                    data["route_candidates"] = SerializeRdwRouteCandidates(rdwPlanningResult.Routes);
                    data["planning_rejection_reasons"] = new JArray(rdwPlanningResult.RejectionReasons);
                }
                if (rdwRoutePlan != null)
                {
                    data["active_route_step_id"] = rdwRoutePlan.ActiveStepId;
                    data["remaining_route_steps"] = new JArray(rdwRoutePlan.RemainingSteps);
                }
            }

            if (descriptor.Level == LogType.Error)
            {
                data["error_code"] = eventType.ToUpperInvariant();
                data["component"] = descriptor.Category;
                data["recoverable"] = false;
                data["action_taken"] = "event recorded";
            }

            var record = new JObject
            {
                ["timestamp"] = timestamp,
                ["elapsed_ms"] = elapsedMilliseconds,
                ["record_type"] = "event",
                ["level"] = ConsoleLevelLabel(descriptor.Level),
                ["category"] = descriptor.Category,
                ["event_name"] = descriptor.EventName,
                ["message"] = message ?? string.Empty,
                ["session_id"] = session.sessionId,
                ["trace"] = traceObject,
                ["data"] = data
            };

            var forceFlush = descriptor.Level == LogType.Error ||
                             string.Equals(eventType, "session_ended", StringComparison.Ordinal);
            if (eventWriter == null || !eventWriter.TryWriteLine(record.ToString(Formatting.None), autoFlush, forceFlush))
            {
                ReportStorageError("events", eventWriter != null ? eventWriter.LastError : null);
            }
        }
        catch (Exception exception)
        {
            ReportStorageError("event_serialize", exception);
        }
    }

    private static bool IsRdwVetoReason(string reason)
    {
        return string.Equals(reason, "safety_veto", StringComparison.Ordinal) ||
               string.Equals(reason, "owner_mismatch", StringComparison.Ordinal) ||
               string.Equals(reason, "invalid_command", StringComparison.Ordinal);
    }

    private bool ShouldEmitUnityConsole(string eventType, PhysicalRiskState riskState)
    {
        if (!string.Equals(eventType, "risk_changed", StringComparison.Ordinal))
        {
            return true;
        }

        // RiskEvaluator 每 0.2 秒生成快照；Console 与 Event 只报告安全语义变化。
        var signature = BuildRiskSignature(riskState);
        if (string.Equals(signature, lastConsoleRiskSignature, StringComparison.Ordinal))
        {
            return false;
        }

        lastConsoleRiskSignature = signature;
        return true;
    }

    private void EmitUnityConsole(
        string eventType,
        string eventName,
        string category,
        LogType level,
        string taskId,
        string calibrationId,
        string requestId,
        string decisionId,
        string riskSnapshotId,
        int stateVersion,
        GuidanceRuntimeState runtimeState,
        string selectedTarget,
        string targetRole,
        PhysicalRiskState riskState,
        GuidanceResponse response,
        string message)
    {
        var builder = new StringBuilder(512);
        builder.Append('[').Append(ConsolePrefix).Append(']');
        builder.Append('[').Append(ConsoleLevelLabel(level)).Append(']');
        builder.Append('[').Append(FirstNonEmpty(eventType, "unspecified_event")).Append(']');
        builder.Append('[').Append(ConsoleCategoryLabel(category)).Append("] ");
        builder.Append(ConsoleEventTitle(eventType)).Append(" | ");
        builder.Append("event_name=").Append(FirstNonEmpty(eventName, "system.unspecified_event"));
        builder.Append(" session_id=").Append(FirstNonEmpty(session?.sessionId, "pre-session"));
        AppendConsoleValue(builder, "calibration_id", calibrationId);
        AppendConsoleValue(builder, "request_id", requestId);
        AppendConsoleValue(builder, "decision_id", decisionId);
        if (stateVersion > 0)
        {
            AppendConsoleValue(builder, "state_version", stateVersion.ToString(CultureInfo.InvariantCulture));
        }
        AppendConsoleValue(builder, "risk_snapshot_id", riskSnapshotId);
        AppendConsoleValue(builder, "task_id", taskId);
        AppendConsoleValue(builder, "phase", runtimeState != null ? runtimeState.phase.ToString() : string.Empty);
        AppendConsoleValue(builder, "target", FirstNonEmpty(selectedTarget, response?.selected_target));
        AppendConsoleValue(builder, "target_role", targetRole);
        AppendConsoleValue(builder, "risk", riskState?.boundary_risk);
        AppendConsoleValue(builder, "movement", response?.movement_instruction);
        if (ShouldAppendConsoleDetail(eventType, message))
        {
            AppendConsoleValue(builder, "detail", message);
        }

        if (level == LogType.Error)
            Debug.LogError(builder.ToString(), this);
        else if (level == LogType.Warning)
            Debug.LogWarning(builder.ToString(), this);
        else
            Debug.Log(builder.ToString(), this);
    }

    private static LogType ResolveConsoleLogType(string eventType, PhysicalRiskState riskState)
    {
        switch (eventType)
        {
            case "backend_unavailable":
            case "backend_fatal_timeout":
            case "session_error":
            case "task_failed":
                return LogType.Error;
            case "calibration_invalidated":
            case "tracking_lost":
            case "guidance_rejected":
            case "guidance_expired":
            case "guidance_cancelled":
            case "runtime_intervention":
            case "gain_cancelled":
            case "gain_aborted":
            case "reset_started":
            case "pause_reset_retry":
            case "reset_cancelled":
            case "hard_stop":
            case "safety_hold":
            case "target_abandoned":
            case "request_failed":
            case "backend_degraded":
            case "backend_hold_started":
                return LogType.Warning;
            case "risk_changed":
                return riskState != null &&
                       (riskState.reset_required ||
                        string.Equals(riskState.boundary_risk, "critical", StringComparison.OrdinalIgnoreCase))
                    ? LogType.Warning
                    : LogType.Log;
            default:
                return LogType.Log;
        }
    }

    private static EventDescriptor ResolveEventDescriptor(string eventType, PhysicalRiskState riskState)
    {
        var level = ResolveConsoleLogType(eventType, riskState);
        switch (eventType)
        {
            case "session_started": return new EventDescriptor("session.started", "Session", level);
            case "session_ended": return new EventDescriptor("session.ended", "Session", level);
            case "session_state_changed": return new EventDescriptor("session.state_changed", "Session", level);
            case "session_paused": return new EventDescriptor("session.paused", "Session", level);
            case "condition_selected": return new EventDescriptor("session.condition_selected", "Session", level);
            case "calibration_started": return new EventDescriptor("tracking.calibration_started", "Tracking", level);
            case "calibration_completed": return new EventDescriptor("tracking.calibration_completed", "Tracking", level);
            case "calibration_invalidated": return new EventDescriptor("tracking.calibration_invalidated", "Tracking", level);
            case "tracking_lost": return new EventDescriptor("tracking.lost", "Tracking", level);
            case "tracking_recovered": return new EventDescriptor("tracking.recovered", "Tracking", level);
            case "guidance_requested": return new EventDescriptor("guidance.requested", "Guidance", level);
            case "guidance_received": return new EventDescriptor("guidance.received", "Guidance", level);
            case "guidance_verified": return new EventDescriptor("guidance.verified", "Guidance", level);
            case "guidance_rejected": return new EventDescriptor("guidance.rejected", "Guidance", level);
            case "guidance_cancelled": return new EventDescriptor("guidance.cancelled", "Guidance", level);
            case "guidance_expired": return new EventDescriptor("guidance.expired", "Guidance", level);
            case "guidance_executed": return new EventDescriptor("guidance.executed", "Guidance", level);
            case "runtime_intervention": return new EventDescriptor("safety.decision", "Safety", level);
            case "risk_changed": return new EventDescriptor("safety.risk_changed", "Safety", level);
            case "safety_hold": return new EventDescriptor("safety.hold", "Safety", level);
            case "reset_started": return new EventDescriptor("safety.pause_reset_started", "Safety", level);
            case "reset_completed": return new EventDescriptor("safety.pause_reset_completed", "Safety", level);
            case "pause_reset_retry": return new EventDescriptor("safety.pause_reset_retry", "Safety", level);
            case "reset_cancelled": return new EventDescriptor("safety.reset_cancelled", "Safety", level);
            case "hard_stop": return new EventDescriptor("safety.hard_stop", "Safety", level);
            case "backend_online": return new EventDescriptor("network.backend_online", "Network", level);
            case "backend_unavailable": return new EventDescriptor("network.backend_unavailable", "Network", level);
            case "backend_unknown": return new EventDescriptor("backend.unknown", "Network", level);
            case "backend_degraded": return new EventDescriptor("backend.degraded", "Network", level);
            case "backend_hold_started": return new EventDescriptor("backend.hold_started", "Network", level);
            case "backend_recovered": return new EventDescriptor("backend.recovered", "Network", level);
            case "backend_fatal_timeout": return new EventDescriptor("backend.fatal_timeout", "Network", level);
            case "request_failed": return new EventDescriptor("network.request_failed", "Network", level);
            case "task_state_changed": return new EventDescriptor("task.state_changed", "Task", level);
            case "task_completed": return new EventDescriptor("task.completed", "Task", level);
            case "task_failed": return new EventDescriptor("task.failed", "Task", level);
            case "target_reserved": return new EventDescriptor("task.target_reserved", "Task", level);
            case "target_abandoned": return new EventDescriptor("task.target_abandoned", "Task", level);
            case "target_completed": return new EventDescriptor("task.target_completed", "Task", level);
            case "detour_selected": return new EventDescriptor("task.detour_selected", "Task", level);
            case "detour_completed": return new EventDescriptor("task.detour_completed", "Task", level);
            case "replanning":
            case "replanning_started": return new EventDescriptor("task.replanning_started", "Task", level);
            case "holding_for_replan": return new EventDescriptor("guidance.holding_for_replan", "Guidance", level);
            case "replan_hold_resumed": return new EventDescriptor("guidance.replan_resumed", "Guidance", level);
            case "turn_requested": return new EventDescriptor("rdw.turn_requested", "RDW", level);
            case "user_turn_started": return new EventDescriptor("rdw.user_turn_started", "RDW", level);
            case "user_turn_completed": return new EventDescriptor("rdw.user_turn_completed", "RDW", level);
            case "gain_started": return new EventDescriptor("rdw.gain_started", "RDW", level);
            case "gain_completed": return new EventDescriptor("rdw.gain_completed", "RDW", level);
            case "gain_cancelled": return new EventDescriptor("rdw.gain_cancelled", "RDW", level);
            case "gain_aborted": return new EventDescriptor("rdw.gain_aborted", "RDW", level);
            case "rdw_frame": return new EventDescriptor("rdw.frame", "RDW", level);
            case "planning_routes_generated": return new EventDescriptor("rdw.planning.routes_generated", "RDW", level);
            case "planning_route_selection_requested": return new EventDescriptor("rdw.planning.selection_requested", "RDW", level);
            case "planning_route_selected": return new EventDescriptor("rdw.planning.route_selected", "RDW", level);
            case "planning_shadow_compared": return new EventDescriptor("rdw.planning.shadow_compared", "RDW", level);
            case "planning_first_step_revalidated": return new EventDescriptor("rdw.planning.first_step_revalidated", "RDW", level);
            case "planning_cached_step_revalidated": return new EventDescriptor("rdw.planning.cached_step_revalidated", "RDW", level);
            case "planning_step_completed": return new EventDescriptor("rdw.planning.step_completed", "RDW", level);
            case "planning_plan_invalidated": return new EventDescriptor("rdw.planning.plan_invalidated", "RDW", level);
            case "planning_route_selection_failed": return new EventDescriptor("rdw.planning.selection_failed", "Network", level);
            case "planning_unavailable": return new EventDescriptor("rdw.planning.unavailable", "RDW", level);
            case "recovery_state_changed": return new EventDescriptor("rdw.recovery_state_changed", "RDW", level);
            case "recovery_search_completed": return new EventDescriptor("rdw.recovery_search_completed", "RDW", level);
            default:
                return new EventDescriptor(
                    "system." + FirstNonEmpty(eventType, "unspecified_event").Replace('_', '.'),
                    "System",
                    level);
        }
    }

    private void WriteTelemetry(
        LogTraceContext trace,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        bool trackingValid,
        bool calibrationValid,
        Vector3 virtualPosition,
        Vector3 virtualForward)
    {
        if (session == null || !session.IsActive || telemetryWriter == null || riskState == null)
        {
            return;
        }

        try
        {
            trace = trace ?? LogTraceContext.Empty;
            var calibrationId = FirstNonEmpty(riskState.calibration_id, trace.CalibrationId);
            var stateVersion = trace.HasGuidanceIdentity
                ? trace.StateVersion
                : runtimeState != null ? runtimeState.stateVersion : 0;
            var traceObject = new JObject
            {
                ["calibration_id"] = calibrationId,
                ["request_id"] = trace.RequestId,
                ["decision_id"] = trace.DecisionId,
                ["risk_snapshot_id"] = riskState.risk_snapshot_id,
                ["state_version"] = stateVersion
            };
            var data = new JObject
            {
                ["task_id"] = FirstNonEmpty(runtimeState?.taskId, session.taskId),
                ["runtime_phase"] = runtimeState != null ? runtimeState.phase.ToString() : string.Empty,
                ["physical_space_id"] = FirstNonEmpty(riskState.physical_space, session.physicalSpaceId),
                ["tracking_valid"] = trackingValid,
                ["calibration_valid"] = calibrationValid,
                ["calibration_id"] = calibrationId,
                ["physical_position_m"] = new JObject
                {
                    ["x"] = riskState.user_physical_position.x,
                    ["z"] = riskState.user_physical_position.y
                },
                ["physical_forward"] = new JObject
                {
                    ["x"] = riskState.user_physical_forward.x,
                    ["z"] = riskState.user_physical_forward.y
                },
                ["virtual_position_m"] = new JObject
                {
                    ["x"] = virtualPosition.x,
                    ["y"] = virtualPosition.y,
                    ["z"] = virtualPosition.z
                },
                ["virtual_forward"] = new JObject
                {
                    ["x"] = virtualForward.x,
                    ["y"] = virtualForward.y,
                    ["z"] = virtualForward.z
                },
                ["current_heading"] = SerializeHeadingSample(riskState.current_heading),
                ["boundary_risk"] = riskState.boundary_risk ?? string.Empty,
                ["reset_risk"] = riskState.reset_risk ?? string.Empty,
                ["reset_required"] = riskState.reset_required,
                ["active_target"] = FirstNonEmpty(runtimeState?.guidanceTargetId, trace.TargetId),
                ["active_decision_id"] = trace.DecisionId,
                ["active_guidance_intent"] = trace.GuidanceIntent,
                ["active_movement_instruction"] = trace.MovementInstruction
            };
            var record = new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["elapsed_ms"] = session.ElapsedMilliseconds,
                ["record_type"] = "telemetry",
                ["category"] = "Safety",
                ["sample_name"] = "risk.snapshot",
                ["session_id"] = session.sessionId,
                ["trace"] = traceObject,
                ["data"] = data
            };

            if (!telemetryWriter.TryWriteLine(record.ToString(Formatting.None), autoFlush))
            {
                ReportStorageError("telemetry", telemetryWriter.LastError);
            }
        }
        catch (Exception exception)
        {
            ReportStorageError("telemetry_serialize", exception);
        }
    }

    private static string GainEventType(RotationGainEventType eventType, string reason)
    {
        switch (eventType)
        {
            case RotationGainEventType.TurnRequested: return "turn_requested";
            case RotationGainEventType.UserTurnStarted: return "user_turn_started";
            case RotationGainEventType.GainStarted: return "gain_started";
            case RotationGainEventType.UserTurnCompleted: return "user_turn_completed";
            case RotationGainEventType.GainCompleted: return "gain_completed";
            case RotationGainEventType.GainCancelled:
                return IsGainCancellation(reason) ? "gain_cancelled" : "gain_aborted";
            default: return "gain_cancelled";
        }
    }

    private static bool IsGainCancellation(string reason)
    {
        return string.Equals(reason, "gain_disabled", StringComparison.Ordinal) ||
               string.Equals(reason, "guidance_completed", StringComparison.Ordinal) ||
               string.Equals(reason, "stopped", StringComparison.Ordinal) ||
               string.Equals(reason, "session_ended", StringComparison.Ordinal) ||
               string.Equals(reason, "cancelled", StringComparison.Ordinal);
    }

    private static string BackendHealthEventType(BackendHealthStatus status)
    {
        switch (status)
        {
            case BackendHealthStatus.Online: return "backend_recovered";
            case BackendHealthStatus.Degraded: return "backend_degraded";
            case BackendHealthStatus.Offline: return "backend_hold_started";
            default: return "backend_unknown";
        }
    }

    private static JObject SerializeHeadingSample(HeadingSample sample)
    {
        return new JObject
        {
            ["relative_yaw_deg"] = sample.RelativeYawDegrees,
            ["physical_forward"] = new JObject
            {
                ["x"] = sample.PhysicalForward.x,
                ["z"] = sample.PhysicalForward.y
            },
            ["clearance_m"] = sample.ClearanceMeters,
            ["safety"] = sample.Safety.ToString().ToLowerInvariant()
        };
    }

    private static JArray SerializeHeadingEvaluations(IReadOnlyList<HeadingSample> evaluations)
    {
        var result = new JArray();
        if (evaluations == null) return result;
        foreach (var sample in evaluations)
        {
            result.Add(SerializeHeadingSample(sample));
        }
        return result;
    }

    private static JArray SerializeHeadingEvaluationDiagnostics(
        IReadOnlyList<RecoveryHeadingEvaluation> evaluations)
    {
        var result = new JArray();
        if (evaluations == null) return result;
        foreach (var evaluation in evaluations)
        {
            result.Add(new JObject
            {
                ["relative_yaw_deg"] = evaluation.RelativeYawDegrees,
                ["evaluation_succeeded"] = evaluation.Succeeded,
                ["clearance_m"] = evaluation.Succeeded ? evaluation.Sample.ClearanceMeters : 0f,
                ["safety"] = evaluation.Succeeded
                    ? evaluation.Sample.Safety.ToString().ToLowerInvariant()
                    : string.Empty,
                ["failure_reason"] = evaluation.FailureReason
            });
        }
        return result;
    }

    private static JArray SerializeCandidateAssessments(IReadOnlyList<CandidateAssessment> assessments)
    {
        var result = new JArray();
        if (assessments == null) return result;
        foreach (var assessment in assessments)
        {
            if (assessment?.Candidate == null) continue;
            result.Add(new JObject
            {
                ["target_id"] = assessment.Candidate.TargetId,
                ["target_role"] = GuidanceTypeUtility.TargetRoleToProtocol(assessment.Candidate.TargetRole),
                ["relative_yaw_deg"] = assessment.Bearing?.RelativeYawDegrees ?? 0f,
                ["display_label"] = assessment.Candidate.RelativeDirection,
                ["semantic_eligible"] = assessment.IsSemanticallyEligible,
                ["semantic_exclusion_reason"] = assessment.SemanticExclusionReason,
                ["action_type"] = ActionTypeLabel(assessment.SuggestedAction),
                ["turn_side"] = assessment.SuggestedAction != null
                    ? assessment.SuggestedAction.TurnSide.ToString().ToLowerInvariant()
                    : "none",
                ["target_yaw_deg"] = assessment.SuggestedAction?.TargetYawDegrees ?? 0f,
                ["execution_state"] = assessment.ExecutionState.ToString().ToLowerInvariant(),
                ["execution_reason"] = assessment.Reason
            });
        }
        return result;
    }

    private static JArray SerializeRdwRouteCandidates(IReadOnlyList<RouteCandidate> routes)
    {
        var result = new JArray();
        if (routes == null) return result;
        foreach (var route in routes)
        {
            if (route == null) continue;
            var edges = new JArray();
            foreach (var evaluation in route.EdgeEvaluations)
            {
                if (evaluation == null) continue;
                edges.Add(new JObject
                {
                    ["relative_yaw_deg"] = evaluation.RelativeYawDegrees,
                    ["clearance_m"] = evaluation.ClearanceMeters,
                    ["safety"] = evaluation.SafetyLevel.ToString().ToLowerInvariant(),
                    ["rdw_benefit"] = evaluation.RdwBenefit,
                    ["gain_cost"] = evaluation.GainCost,
                    ["reset_pressure"] = evaluation.ResetPressure
                });
            }
            result.Add(new JObject
            {
                ["route_id"] = route.RouteId,
                ["first_target_id"] = route.FirstTargetId,
                ["ordered_node_ids"] = new JArray(route.OrderedNodeIds),
                ["route_utility"] = route.RouteUtility,
                ["intervention_level"] = route.InterventionLevel.ToString().ToLowerInvariant(),
                ["estimator_id"] = route.EstimatorId,
                ["estimator_version"] = route.EstimatorVersion,
                ["edges"] = edges
            });
        }
        return result;
    }

    private static JObject SerializeRecoveryPlan(RecoveryHeadingPlan plan)
    {
        return new JObject
        {
            ["success"] = plan != null && plan.IsSuccess,
            ["failure"] = plan != null ? plan.Failure.ToString().ToLowerInvariant() : string.Empty,
            ["relative_yaw_deg"] = plan?.RelativeYawDegrees ?? 0f,
            ["physical_forward"] = new JObject
            {
                ["x"] = plan?.SafePhysicalForward.x ?? 0f,
                ["z"] = plan?.SafePhysicalForward.y ?? 0f
            },
            ["clearance_m"] = plan?.ClearanceMeters ?? 0f,
            ["safety"] = plan != null ? plan.Safety.ToString().ToLowerInvariant() : string.Empty,
            ["score"] = plan != null && !float.IsInfinity(plan.Score) ? plan.Score : (float?)null
        };
    }

    private static JObject SerializeRecoverySearch(
        RecoveryHeadingSearchResult result,
        PhysicalRiskState riskState)
    {
        var settings = result.Settings;
        return new JObject
        {
            ["search_origin_position_m"] = new JObject
            {
                ["x"] = riskState?.user_physical_position.x ?? 0f,
                ["z"] = riskState?.user_physical_position.y ?? 0f
            },
            ["search_origin_forward"] = new JObject
            {
                ["x"] = riskState?.user_physical_forward.x ?? 0f,
                ["z"] = riskState?.user_physical_forward.y ?? 0f
            },
            ["coarse_step_deg"] = settings.coarseStepDegrees,
            ["minimum_resolution_deg"] = settings.minimumResolutionDegrees,
            ["refinement_factor"] = settings.refinementFactor,
            ["maximum_evaluations"] = settings.maximumEvaluations,
            ["clearance_normalization_m"] = settings.clearanceNormalizationMeters,
            ["clearance_weight"] = settings.clearanceWeight,
            ["future_distance_weight"] = settings.futureDistanceWeight,
            ["turn_cost_weight"] = settings.turnCostWeight,
            ["completed_coverage_resolution_deg"] =
                float.IsInfinity(result.CompletedCoverageResolutionDegrees)
                    ? (float?)null
                    : result.CompletedCoverageResolutionDegrees,
            ["attempted_evaluation_count"] = result.AttemptedEvaluationCount,
            ["successful_evaluation_count"] = result.SuccessfulEvaluationCount,
            ["evaluated_heading_count"] = result.Evaluations.Count,
            ["evaluation_diagnostics"] = SerializeHeadingEvaluationDiagnostics(
                result.EvaluationDiagnostics),
            ["budget_exhausted"] = result.BudgetExhausted,
            ["evaluations"] = SerializeHeadingEvaluations(result.Evaluations),
            ["best_heading"] = SerializeRecoveryPlan(result.Plan),
            ["final_validation_attempted"] = result.FinalValidationAttempted,
            ["final_validation_passed"] = result.FinalValidationPassed,
            ["search_failure_reason"] = result.FailureReason
        };
    }

    private static string ActionTypeLabel(GuidanceAction action)
    {
        if (action == null) return string.Empty;
        switch (action.Type)
        {
            case GuidanceActionType.TurnInPlace: return "turn_in_place";
            case GuidanceActionType.MoveForward: return "move_forward";
            case GuidanceActionType.Reevaluate: return "reevaluate";
            default: return "hold";
        }
    }

    private static string RecoveryStateLabel(GuidanceRecoveryState state)
    {
        switch (state)
        {
            case GuidanceRecoveryState.StoppingGuidance: return "stopping_guidance";
            case GuidanceRecoveryState.SelectingHeading: return "selecting_heading";
            case GuidanceRecoveryState.HoldingForSafeHeading: return "holding_for_safe_heading";
            case GuidanceRecoveryState.WaitingForUserTurn: return "waiting_for_user_turn";
            case GuidanceRecoveryState.Stabilizing: return "stabilizing";
            case GuidanceRecoveryState.Revalidating: return "revalidating";
            case GuidanceRecoveryState.Replanning: return "replanning";
            case GuidanceRecoveryState.Terminated: return "terminated";
            default: return "inactive";
        }
    }

    private static string SafetyDecisionLabel(RuntimeGuidanceSafetyDecision decision)
    {
        switch (decision)
        {
            case RuntimeGuidanceSafetyDecision.SoftReroute: return "soft_reroute";
            case RuntimeGuidanceSafetyDecision.PauseReset: return "pause_reset";
            case RuntimeGuidanceSafetyDecision.HardStop: return "hard_stop";
            default: return "continue";
        }
    }

    private static string ConsoleLevelLabel(LogType logType)
    {
        if (logType == LogType.Error) return "ERROR";
        if (logType == LogType.Warning) return "WARNING";
        return "INFO";
    }

    private static string ConsoleCategoryLabel(string category)
    {
        switch (category)
        {
            case "Session": return "会话";
            case "Tracking": return "追踪";
            case "Guidance": return "引导";
            case "Safety": return "安全";
            case "Network": return "网络";
            case "Task": return "任务";
            case "RDW": return "重定向行走";
            case "Storage": return "存储";
            default: return "系统";
        }
    }

    private static string ConsoleEventTitle(string eventType)
    {
        switch (eventType)
        {
            case "session_started": return "实验会话已开始";
            case "session_ended": return "实验会话已结束";
            case "session_state_changed": return "会话状态已更新";
            case "session_paused": return "实验会话已暂停";
            case "condition_selected": return "实验条件已选择";
            case "calibration_started": return "开始物理空间校准";
            case "calibration_completed": return "物理空间校准已完成";
            case "calibration_invalidated": return "物理空间校准已失效";
            case "tracking_lost": return "XR 追踪已丢失";
            case "tracking_recovered": return "XR 追踪已恢复";
            case "guidance_requested": return "已发送引导请求";
            case "guidance_received": return "已收到引导响应";
            case "guidance_verified": return "引导验证已通过";
            case "guidance_rejected": return "引导已被拒绝";
            case "guidance_cancelled": return "引导已取消";
            case "guidance_expired": return "引导已过期";
            case "guidance_executed": return "引导已执行";
            case "runtime_intervention": return "安全系统已介入";
            case "risk_changed": return "安全风险已变化";
            case "safety_hold": return "已进入安全暂停";
            case "reset_started": return "安全重置已开始";
            case "reset_completed": return "安全重置已完成";
            case "reset_cancelled": return "安全重置已取消";
            case "backend_online": return "后端服务已连接";
            case "backend_unavailable": return "后端服务不可用";
            case "request_failed": return "引导请求失败";
            case "task_state_changed": return "任务状态已更新";
            case "task_completed": return "任务已完成";
            case "task_failed": return "任务执行失败";
            case "target_reserved": return "已预留引导目标";
            case "target_abandoned": return "已放弃引导目标";
            case "target_completed": return "已完成引导目标";
            case "detour_selected": return "已选择绕行目标";
            case "detour_completed": return "绕行任务已完成";
            case "replanning": return "正在重新规划";
            case "turn_requested": return "已请求用户转向";
            case "user_turn_started": return "用户开始转向";
            case "user_turn_completed": return "用户转向已完成";
            case "gain_started": return "旋转增益已开始";
            case "gain_completed": return "旋转增益已完成";
            case "gain_cancelled": return "旋转增益已取消";
            case "gain_aborted": return "旋转增益已中止";
            default: return "记录系统事件";
        }
    }

    private static bool ShouldAppendConsoleDetail(string eventType, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var defaultMessage = DefaultConsoleMessage(eventType);
        if (string.IsNullOrWhiteSpace(defaultMessage))
        {
            return true;
        }

        return !string.Equals(message, defaultMessage, StringComparison.OrdinalIgnoreCase) &&
               !message.StartsWith(defaultMessage + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultConsoleMessage(string eventType)
    {
        switch (eventType)
        {
            case "session_started": return "session started";
            case "session_paused": return "researcher pause";
            case "condition_selected": return "condition selected";
            case "calibration_started": return "calibration started";
            case "calibration_completed": return "calibration completed";
            case "calibration_invalidated": return "calibration invalidated";
            case "tracking_lost": return "tracking lost";
            case "tracking_recovered": return "tracking recovered";
            case "guidance_requested": return "guidance requested";
            case "guidance_received": return "guidance received";
            case "guidance_verified": return "guidance verified";
            case "guidance_rejected": return "guidance rejected";
            case "guidance_cancelled": return "guidance cancelled";
            case "guidance_expired": return "guidance expired";
            case "guidance_executed": return "guidance executed";
            case "risk_changed": return "risk state changed";
            case "task_state_changed": return "task state changed";
            case "task_completed": return "task completed";
            case "target_reserved": return "target reserved";
            case "target_abandoned": return "target abandoned";
            case "target_completed": return "target completed";
            case "detour_selected": return "detour selected";
            case "detour_completed": return "detour completed";
            case "replanning": return "replanning requested";
            case "turn_requested": return "turn requested";
            case "user_turn_started": return "user turn started";
            case "user_turn_completed": return "user turn completed";
            case "gain_started": return "gain started";
            case "gain_completed": return "gain completed";
            case "gain_cancelled": return "gain cancelled";
            case "gain_aborted": return "gain aborted";
            default: return string.Empty;
        }
    }

    private void LogTargetEvent(
        string eventType,
        LogTraceContext trace,
        GuidanceTarget target,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string message)
    {
        Write(
            eventType,
            null,
            null,
            null,
            riskState,
            runtimeState,
            target != null && !string.IsNullOrWhiteSpace(target.TargetId)
                ? $"{message}: {target.TargetId}"
                : message,
            trace,
            selectedTargetOverride: target != null ? target.TargetId : string.Empty,
            targetRoleOverride: target != null
                ? GuidanceTypeUtility.TargetRoleToProtocol(target.TargetRole)
                : string.Empty);
    }

    private void LogOrQueueCalibrationEvent(string eventType, string calibrationId, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        if (session != null && session.IsActive && !string.IsNullOrWhiteSpace(logPath))
        {
            Write(
                eventType,
                null,
                null,
                null,
                null,
                null,
                message,
                LogTraceContext.Empty,
                calibrationIdOverride: calibrationId,
                timestampOverride: timestamp);
            return;
        }

        Write(
            eventType,
            null,
            null,
            null,
            null,
            null,
            message,
            LogTraceContext.Empty,
            calibrationIdOverride: calibrationId,
            timestampOverride: timestamp);
        pendingCalibrationEvents.Add(new PendingCalibrationEvent(eventType, calibrationId, message, timestamp));
    }

    private void FlushPendingCalibrationEvents()
    {
        foreach (var calibrationEvent in pendingCalibrationEvents)
        {
            Write(
                calibrationEvent.EventType,
                null,
                null,
                null,
                null,
                null,
                calibrationEvent.Message,
                LogTraceContext.Empty,
                calibrationIdOverride: calibrationEvent.CalibrationId,
                timestampOverride: calibrationEvent.Timestamp,
                emitUnityConsole: false);
        }

        pendingCalibrationEvents.Clear();
    }

    private void InitializeSessionFiles()
    {
        logPath = string.Empty;
        telemetryLogPath = string.Empty;
        sessionMetaPath = string.Empty;

        try
        {
            var rootFolder = ResolveLogRootPath();
            var sessionFolderPath = Path.Combine(rootFolder, "Sessions", session.sessionId);
            Directory.CreateDirectory(sessionFolderPath);
            logPath = Path.Combine(sessionFolderPath, "events.jsonl");
            telemetryLogPath = Path.Combine(sessionFolderPath, "telemetry.jsonl");
            sessionMetaPath = Path.Combine(sessionFolderPath, "session_meta.json");
            eventWriter = new BufferedJsonlWriter(logPath, bufferSizeBytes, flushIntervalSeconds);
            telemetryWriter = new BufferedJsonlWriter(telemetryLogPath, bufferSizeBytes, flushIntervalSeconds);
            WriteSessionMeta();

            if (eventWriter.LastError != null)
            {
                ReportStorageError("events", eventWriter.LastError);
            }
            if (telemetryWriter.LastError != null)
            {
                ReportStorageError("telemetry", telemetryWriter.LastError);
            }
        }
        catch (Exception exception)
        {
            CloseWriters();
            logPath = string.Empty;
            telemetryLogPath = string.Empty;
            sessionMetaPath = string.Empty;
            ReportStorageError("session", exception);
        }
    }

    private string ResolveLogRootPath()
    {
#if UNITY_EDITOR
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFolderRelativeToProject));
#else
        if (usePersistentDataPathInPlayer)
        {
            return Path.Combine(Application.persistentDataPath, "VirtualGuidance", "Logs");
        }

        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFolderRelativeToProject));
#endif
    }

    private void WriteSessionMeta()
    {
        if (string.IsNullOrWhiteSpace(sessionMetaPath) || session == null)
        {
            return;
        }

        try
        {
            var meta = new SessionMeta
            {
                session_id = session.sessionId,
                participant_id = session.participantId,
                condition = session.condition,
                task_id = session.taskId,
                physical_space_id = session.physicalSpaceId,
                started_at_utc = session.startedAtUtc,
                application_version = Application.version,
                unity_version = Application.unityVersion,
                scene_name = SceneManager.GetActiveScene().name,
                device_model = SystemInfo.deviceModel,
                operating_system = SystemInfo.operatingSystem,
                telemetry_sampling_hz = riskTelemetrySamplingRateHz,
                flush_interval_seconds = flushIntervalSeconds,
                buffer_bytes = bufferSizeBytes,
                position_unit = "meter",
                rotation_unit = "degree",
                time_unit = "millisecond",
                coordinate_convention = "Unity left-handed world; calibrated physical XZ plane"
            };
            File.WriteAllText(sessionMetaPath, JsonUtility.ToJson(meta, true), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            ReportStorageError("session_meta", exception);
        }
    }

    private void FlushWriter(BufferedJsonlWriter writer, string sinkName)
    {
        if (writer != null && !writer.TryFlush() && writer.LastError != null)
        {
            ReportStorageError(sinkName, writer.LastError);
        }
    }

    private void CloseWriters()
    {
        if (eventWriter != null)
        {
            eventWriter.Dispose();
            if (eventWriter.LastError != null)
            {
                ReportStorageError("events", eventWriter.LastError);
            }
            eventWriter = null;
        }

        if (telemetryWriter != null)
        {
            telemetryWriter.Dispose();
            if (telemetryWriter.LastError != null)
            {
                ReportStorageError("telemetry", telemetryWriter.LastError);
            }
            telemetryWriter = null;
        }
    }

    private void ReportStorageError(string sinkName, Exception exception)
    {
        var now = Stopwatch.GetTimestamp();
        if (lastStorageErrorTimestamp > 0 &&
            now - lastStorageErrorTimestamp < StorageErrorReportIntervalSeconds * Stopwatch.Frequency)
        {
            return;
        }

        lastStorageErrorTimestamp = now;
        var exceptionType = exception != null ? exception.GetType().Name : "UnavailableSink";
        Debug.LogError(
            $"[{ConsolePrefix}][ERROR][storage_write_failed][存储] 日志写入失败，已切换为降级模式 | " +
            $"event_name=storage.write_failed sink={FirstNonEmpty(sinkName, "unknown")} " +
            $"error_code=LOG_WRITE_001 exception_type={exceptionType} recoverable=true action_taken=logging_degraded",
            this);
    }

    private long ResolveElapsedMilliseconds(string timestampOverride)
    {
        if (session == null || string.IsNullOrWhiteSpace(timestampOverride))
        {
            return session != null ? session.ElapsedMilliseconds : 0L;
        }

        if (DateTime.TryParse(timestampOverride, null, DateTimeStyles.RoundtripKind, out var eventTime) &&
            DateTime.TryParse(session.startedAtUtc, null, DateTimeStyles.RoundtripKind, out var startedTime))
        {
            return Math.Max(0L, (long)(eventTime.ToUniversalTime() - startedTime.ToUniversalTime()).TotalMilliseconds);
        }

        return session.ElapsedMilliseconds;
    }

    private static string BuildRiskSignature(PhysicalRiskState riskState)
    {
        return riskState == null
            ? "none"
            : string.Join(
                "|",
                riskState.boundary_risk,
                riskState.reset_risk,
                riskState.reset_required,
                riskState.current_heading.ClearanceMeters.ToString("0.###"),
                riskState.current_heading.Safety);
    }

    private static void AppendConsoleValue(StringBuilder builder, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(' ').Append(key).Append('=').Append(value.Replace('\n', ' ').Replace('\r', ' '));
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>保存稳定事件名、固定分类和日志等级的目录项。</summary>
    private readonly struct EventDescriptor
    {
        public string EventName { get; }
        public string Category { get; }
        public LogType Level { get; }

        public EventDescriptor(string eventName, string category, LogType level)
        {
            EventName = eventName;
            Category = category;
            Level = level;
        }
    }

    /// <summary>写入 session_meta.json 的稳定会话配置快照。</summary>
    [Serializable]
    private sealed class SessionMeta
    {
        public string session_id;
        public string participant_id;
        public string condition;
        public string task_id;
        public string physical_space_id;
        public string started_at_utc;
        public string application_version;
        public string unity_version;
        public string scene_name;
        public string device_model;
        public string operating_system;
        public float telemetry_sampling_hz;
        public float flush_interval_seconds;
        public int buffer_bytes;
        public string position_unit;
        public string rotation_unit;
        public string time_unit;
        public string coordinate_convention;
    }

    private sealed class PendingCalibrationEvent
    {
        public string EventType { get; }
        public string CalibrationId { get; }
        public string Message { get; }
        public string Timestamp { get; }

        public PendingCalibrationEvent(string eventType, string calibrationId, string message, string timestamp)
        {
            EventType = eventType;
            CalibrationId = calibrationId;
            Message = message;
            Timestamp = timestamp;
        }
    }
}
