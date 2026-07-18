using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// 将实验会话、guidance 请求/响应及运行状态写入 JSONL，并实时镜像到 Unity Console。
/// 字段优先取事件数据，再回退到条件或会话值，兼顾运行观察与离线分析。
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
    private GuidanceRequest activeRequest;
    private GuidanceResponse activeResponse;
    private PhysicalRiskState activeRiskState;
    private string activeCalibrationId = string.Empty;
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
    public void SetRunState(ExperimentRunState state)
    {
        if (runState == state)
        {
            return;
        }

        runState = state;
        Write("session_state_changed", null, null, null, null, null, $"session state: {state}");
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
        activeRequest = null;
        activeResponse = null;
        activeRiskState = null;
        activeCalibrationId = string.Empty;
        lastRiskEventSignature = string.Empty;
        lastConsoleRiskSignature = string.Empty;
        InitializeSessionFiles();
        Write("session_started", spec, null, null, null, null, "session started");
        FlushPendingCalibrationEvents();
    }

    /// <summary>记录条件已应用事件；没有有效会话时只输出 Unity Console。</summary>
    public void LogConditionSelected(ExperimentConditionSpec spec)
    {
        Write("condition_selected", spec, null, null, null, null, "condition selected");
    }

    /// <summary>记录发送给后端的请求及请求时的物理风险状态。</summary>
    public void LogRequest(GuidanceRequest request, PhysicalRiskState riskState)
    {
        activeRequest = request;
        activeResponse = null;
        activeRiskState = riskState;
        activeCalibrationId = FirstNonEmpty(request?.calibration_id, riskState?.calibration_id, activeCalibrationId);
        Write("guidance_requested", null, request, null, riskState, null, "guidance requested");
    }

    /// <summary>记录后端响应及响应到达时的物理风险状态。</summary>
    public void LogResponse(GuidanceResponse response, PhysicalRiskState riskState)
    {
        activeResponse = response;
        if (riskState != null)
        {
            activeRiskState = riskState;
        }
        activeCalibrationId = FirstNonEmpty(response?.calibration_id, riskState?.calibration_id, activeCalibrationId);
        Write("guidance_received", null, null, response, riskState, null, "guidance received", "pending");
    }

    /// <summary>记录 Guidance 已通过条件与运行时验证。</summary>
    public void LogGuidanceVerified(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        Write("guidance_verified", null, null, null, riskState, runtimeState, "guidance verified", "approved");
    }

    /// <summary>记录 Guidance 因协议或 Verifier 契约不成立而被拒绝。</summary>
    public void LogGuidanceRejected(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_rejected", null, null, null, riskState, runtimeState, reason, "rejected");
    }

    /// <summary>记录 Guidance 因暂停、重规划或会话终止而取消。</summary>
    public void LogGuidanceCancelled(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_cancelled", null, null, null, riskState, runtimeState, reason);
    }

    /// <summary>记录 Guidance 因请求身份或状态版本变化而过期。</summary>
    public void LogGuidanceExpired(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string reason)
    {
        Write("guidance_expired", null, null, null, riskState, runtimeState, reason);
    }

    /// <summary>记录一次 Physical Space 校准尝试开始；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationStarted()
    {
        LogOrQueueCalibrationEvent("calibration_started", string.Empty, "calibration started");
    }

    /// <summary>记录成功校准及其 Calibration ID；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationCompleted(string calibrationId)
    {
        activeCalibrationId = FirstNonEmpty(calibrationId, activeCalibrationId);
        LogOrQueueCalibrationEvent("calibration_completed", calibrationId, "calibration completed");
    }

    /// <summary>记录校准失效；会话未开始时暂存到下一份会话日志。</summary>
    public void LogCalibrationInvalidated(string calibrationId, string reason)
    {
        activeCalibrationId = FirstNonEmpty(calibrationId, activeCalibrationId);
        LogOrQueueCalibrationEvent(
            "calibration_invalidated",
            calibrationId,
            string.IsNullOrWhiteSpace(reason) ? "calibration invalidated" : reason);
    }

    /// <summary>记录到达当前 Guidance Target，并保留目标 ID 与角色。</summary>
    public void LogTargetReached(GuidanceTarget target, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        LogTargetEvent("target_reached", target, runtimeState, riskState, "target reached");
    }

    /// <summary>记录一次 NavigableDetour 被选为当前 Guidance Target。</summary>
    public void LogDetourSelected(GuidanceTarget target, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        LogTargetEvent("detour_selected", target, runtimeState, riskState, "detour selected");
    }

    /// <summary>记录 NavigableDetour 已完成且原路线进度保持不变。</summary>
    public void LogDetourCompleted(GuidanceTarget target, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        LogTargetEvent("detour_completed", target, runtimeState, riskState, "detour completed");
    }

    /// <summary>记录 Hard Runtime Safety 阻止的原始 Guidance 与干预原因。</summary>
    public void LogRuntimeIntervention(
        GuidanceResponse response,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string reason)
    {
        Write(
            "runtime_intervention",
            null,
            null,
            response,
            riskState,
            runtimeState,
            reason,
            "rejected",
            runtimeIntervention: true);
    }

    /// <summary>记录通过运行时验证并实际开始执行的 Guidance。</summary>
    public void LogGuidanceExecuted(
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
            "approved",
            executed: true);
    }

    /// <summary>
    /// 将固定频率风险快照写入 Telemetry；仅当安全语义变化时额外生成 risk_changed Event。
    /// </summary>
    public void LogRiskSnapshot(PhysicalRiskState riskState, GuidanceRuntimeState runtimeState)
    {
        if (riskState == null)
        {
            return;
        }

        activeRiskState = riskState;
        activeCalibrationId = FirstNonEmpty(riskState.calibration_id, activeCalibrationId);
        WriteTelemetry(riskState, runtimeState);

        var signature = BuildRiskSignature(riskState);
        if (string.Equals(signature, lastRiskEventSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastRiskEventSignature = signature;
        Write("risk_changed", null, null, null, riskState, runtimeState, "risk state changed");
    }

    /// <summary>记录任务、风险或请求失败等通用运行事件。</summary>
    public void LogStatus(string eventType, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string message)
    {
        if (string.Equals(eventType, "risk_changed", StringComparison.Ordinal))
        {
            LogRiskSnapshot(riskState, runtimeState);
            return;
        }

        if (riskState != null)
        {
            activeRiskState = riskState;
            activeCalibrationId = FirstNonEmpty(riskState.calibration_id, activeCalibrationId);
        }
        Write(eventType, null, null, null, riskState, runtimeState, message);
    }

    /// <summary>先写入 session_ended，再关闭活动会话。</summary>
    public void EndSession(string reason)
    {
        if (session == null || !session.IsActive)
        {
            return;
        }

        Write("session_ended", null, null, null, null, null, string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
        Flush();
        session.End(reason);
        activeRequest = null;
        activeResponse = null;
        activeRiskState = null;
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
        string runtimeValidationResult = "",
        bool runtimeIntervention = false,
        bool executed = false,
        string calibrationIdOverride = "",
        string timestampOverride = "",
        string selectedTargetOverride = "",
        string targetRoleOverride = "",
        bool emitUnityConsole = true)
    {
        var tracedRequest = request ?? activeRequest;
        var tracedResponse = response ?? activeResponse;
        var tracedRiskState = riskState ?? activeRiskState;
        var calibrationId = FirstNonEmpty(
            calibrationIdOverride,
            riskState?.calibration_id,
            request?.calibration_id,
            response?.calibration_id,
            activeCalibrationId,
            tracedRiskState?.calibration_id,
            tracedRequest?.calibration_id,
            tracedResponse?.calibration_id);
        var condition = FirstNonEmpty(tracedRequest?.condition, spec?.condition, session?.condition);
        var participantId = FirstNonEmpty(tracedRequest?.participant_id, spec?.participantId, session?.participantId);
        var taskId = FirstNonEmpty(runtimeState?.taskId, tracedRequest?.task_id, spec?.taskId, session?.taskId);
        var physicalSpaceId = FirstNonEmpty(tracedRequest?.physical_space_id, spec?.physicalSpaceId, session?.physicalSpaceId);
        var currentExhibit = FirstNonEmpty(runtimeState?.currentExhibitId, tracedRequest?.current_exhibit, spec?.startExhibitId);
        var semanticNextTarget = FirstNonEmpty(runtimeState?.nextTargetId, tracedRequest?.semantic_next_target, spec?.semanticNextTargetId);
        var requestId = FirstNonEmpty(request?.request_id, response?.request_id, tracedRequest?.request_id, tracedResponse?.request_id);
        var decisionId = FirstNonEmpty(request?.decision_id, response?.decision_id, tracedRequest?.decision_id, tracedResponse?.decision_id);
        var riskSnapshotId = FirstNonEmpty(
            riskState?.risk_snapshot_id,
            request?.risk_snapshot_id,
            response?.risk_snapshot_id,
            tracedRiskState?.risk_snapshot_id,
            tracedRequest?.risk_snapshot_id,
            tracedResponse?.risk_snapshot_id);
        var targetRole = FirstNonEmpty(targetRoleOverride, request?.target_role, response?.target_role, tracedRequest?.target_role, tracedResponse?.target_role);
        var selectedTarget = FirstNonEmpty(selectedTargetOverride, tracedResponse?.selected_target);
        var stateVersion = request != null
            ? request.state_version
            : response != null
                ? response.state_version
                : runtimeState != null
                    ? runtimeState.stateVersion
                    : tracedRequest != null
                        ? tracedRequest.state_version
                        : tracedResponse != null ? tracedResponse.state_version : 0;

        var descriptor = ResolveEventDescriptor(eventType, tracedRiskState);
        if (emitUnityConsole && mirrorToUnityConsole && ShouldEmitUnityConsole(eventType, tracedRiskState))
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
                tracedRiskState,
                tracedResponse,
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
        var rawGuidance = string.Equals(eventType, "guidance_received", StringComparison.Ordinal) && response != null
            ? JsonUtility.ToJson(response)
            : string.Empty;
        var executedGuidance = string.Equals(eventType, "guidance_executed", StringComparison.Ordinal) && executed && response != null
            ? JsonUtility.ToJson(response)
            : string.Empty;

        var builder = new StringBuilder(1536);
        builder.Append('{');
        Append(builder, "timestamp", timestamp, false);
        Append(builder, "elapsed_ms", elapsedMilliseconds.ToString(CultureInfo.InvariantCulture), raw: true);
        Append(builder, "record_type", "event");
        Append(builder, "level", ConsoleLevelLabel(descriptor.Level));
        Append(builder, "category", descriptor.Category);
        Append(builder, "event_name", descriptor.EventName);
        Append(builder, "event_type", eventType);
        Append(builder, "message", message);
        Append(builder, "session_id", session.sessionId);

        builder.Append(",\"trace\":{");
        Append(builder, "calibration_id", calibrationId, false);
        Append(builder, "request_id", requestId);
        Append(builder, "decision_id", decisionId);
        Append(builder, "risk_snapshot_id", riskSnapshotId);
        Append(builder, "state_version", stateVersion.ToString(CultureInfo.InvariantCulture), raw: true);
        builder.Append('}');

        // 兼容期保留现有平铺字段，离线脚本可逐步迁移到 event_name、trace 与 data。
        Append(builder, "calibration_id", calibrationId);
        Append(builder, "participant_id", participantId);
        Append(builder, "condition", condition);
        Append(builder, "policy_version", ExperimentConditionPolicy.CurrentVersion);
        Append(builder, "task_id", taskId);
        Append(builder, "physical_space", physicalSpaceId);
        Append(builder, "current_exhibit", currentExhibit);
        Append(builder, "semantic_next_target", semanticNextTarget);
        Append(builder, "runtime_phase", runtimeState != null ? runtimeState.phase.ToString() : string.Empty);
        Append(builder, "run_state", runState.ToString().ToLowerInvariant());
        Append(builder, "request_id", requestId);
        Append(builder, "decision_id", decisionId);
        Append(builder, "risk_snapshot_id", riskSnapshotId);
        Append(builder, "target_role", targetRole);
        Append(builder, "state_version", stateVersion.ToString(CultureInfo.InvariantCulture), raw: true);
        Append(builder, "selected_target", selectedTarget);
        Append(builder, "movement_instruction", tracedResponse != null ? tracedResponse.movement_instruction : string.Empty);
        Append(builder, "verifier_approved", tracedResponse != null && tracedResponse.verifier != null && tracedResponse.verifier.approved ? "true" : "false", raw: true);
        Append(builder, "verifier_reason", tracedResponse != null && tracedResponse.verifier != null ? tracedResponse.verifier.reason : string.Empty);
        if (!string.IsNullOrWhiteSpace(rawGuidance))
        {
            Append(builder, "raw_guidance", rawGuidance);
        }
        Append(
            builder,
            "condition_verifier_result",
            tracedResponse == null || tracedResponse.verifier == null
                ? string.Empty
                : tracedResponse.verifier.approved ? "approved" : "rejected");
        Append(builder, "runtime_validation_result", runtimeValidationResult);
        Append(builder, "runtime_intervention", runtimeIntervention ? "true" : "false", raw: true);
        if (!string.IsNullOrWhiteSpace(executedGuidance))
        {
            Append(builder, "executed_guidance", executedGuidance);
        }
        Append(builder, "boundary_risk", tracedRiskState != null ? tracedRiskState.boundary_risk : string.Empty);
        Append(builder, "reset_required", tracedRiskState != null && tracedRiskState.reset_required ? "true" : "false", raw: true);
        Append(builder, "unsafe_directions", tracedRiskState != null ? Join(tracedRiskState.unsafe_directions) : string.Empty);

        builder.Append(",\"data\":{");
        Append(builder, "participant_id", participantId, false);
        Append(builder, "condition", condition);
        Append(builder, "policy_version", ExperimentConditionPolicy.CurrentVersion);
        Append(builder, "task_id", taskId);
        Append(builder, "physical_space_id", physicalSpaceId);
        Append(builder, "current_exhibit", currentExhibit);
        Append(builder, "semantic_next_target", semanticNextTarget);
        Append(builder, "runtime_phase", runtimeState != null ? runtimeState.phase.ToString() : string.Empty);
        Append(builder, "run_state", runState.ToString().ToLowerInvariant());
        Append(builder, "target_role", targetRole);
        Append(builder, "selected_target", selectedTarget);
        Append(builder, "movement_instruction", tracedResponse != null ? tracedResponse.movement_instruction : string.Empty);
        Append(builder, "runtime_validation_result", runtimeValidationResult);
        Append(builder, "runtime_intervention", runtimeIntervention ? "true" : "false", raw: true);
        Append(builder, "boundary_risk", tracedRiskState != null ? tracedRiskState.boundary_risk : string.Empty);
        Append(builder, "reset_required", tracedRiskState != null && tracedRiskState.reset_required ? "true" : "false", raw: true);
        AppendStringArray(builder, "safe_directions", tracedRiskState != null ? tracedRiskState.safe_directions : null);
        AppendStringArray(builder, "unsafe_directions", tracedRiskState != null ? tracedRiskState.unsafe_directions : null);
        if (descriptor.Level == LogType.Error)
        {
            Append(builder, "error_code", eventType.ToUpperInvariant());
            Append(builder, "component", descriptor.Category);
            Append(builder, "recoverable", "false", raw: true);
            Append(builder, "action_taken", "event recorded");
        }
        builder.Append('}');
        builder.Append('}');

        var forceFlush = descriptor.Level == LogType.Error ||
                         string.Equals(eventType, "session_ended", StringComparison.Ordinal);
        if (eventWriter == null || !eventWriter.TryWriteLine(builder.ToString(), autoFlush, forceFlush))
        {
            ReportStorageError("events", eventWriter != null ? eventWriter.LastError : null);
        }
        }
        catch (Exception exception)
        {
            ReportStorageError("event_serialize", exception);
        }
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
            case "session_error":
            case "task_failed":
                return LogType.Error;
            case "calibration_invalidated":
            case "guidance_rejected":
            case "guidance_expired":
            case "guidance_cancelled":
            case "runtime_intervention":
            case "gain_cancelled":
            case "reset_started":
            case "safety_hold":
            case "request_failed":
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
            case "guidance_requested": return new EventDescriptor("guidance.requested", "Guidance", level);
            case "guidance_received": return new EventDescriptor("guidance.received", "Guidance", level);
            case "guidance_verified": return new EventDescriptor("guidance.verified", "Guidance", level);
            case "guidance_rejected": return new EventDescriptor("guidance.rejected", "Guidance", level);
            case "guidance_cancelled": return new EventDescriptor("guidance.cancelled", "Guidance", level);
            case "guidance_expired": return new EventDescriptor("guidance.expired", "Guidance", level);
            case "guidance_executed": return new EventDescriptor("guidance.executed", "Guidance", level);
            case "runtime_intervention": return new EventDescriptor("safety.runtime_intervention", "Safety", level);
            case "risk_changed": return new EventDescriptor("safety.risk_changed", "Safety", level);
            case "safety_hold": return new EventDescriptor("safety.hold", "Safety", level);
            case "reset_started": return new EventDescriptor("safety.reset_started", "Safety", level);
            case "reset_completed": return new EventDescriptor("safety.reset_completed", "Safety", level);
            case "backend_online": return new EventDescriptor("network.backend_online", "Network", level);
            case "backend_unavailable": return new EventDescriptor("network.backend_unavailable", "Network", level);
            case "request_failed": return new EventDescriptor("network.request_failed", "Network", level);
            case "task_state_changed": return new EventDescriptor("task.state_changed", "Task", level);
            case "task_completed": return new EventDescriptor("task.completed", "Task", level);
            case "task_failed": return new EventDescriptor("task.failed", "Task", level);
            case "target_reached": return new EventDescriptor("task.target_reached", "Task", level);
            case "detour_selected": return new EventDescriptor("task.detour_selected", "Task", level);
            case "detour_completed": return new EventDescriptor("task.detour_completed", "Task", level);
            case "replanning": return new EventDescriptor("task.replanning", "Task", level);
            case "turn_requested": return new EventDescriptor("rdw.turn_requested", "RDW", level);
            case "user_turn_started": return new EventDescriptor("rdw.user_turn_started", "RDW", level);
            case "user_turn_completed": return new EventDescriptor("rdw.user_turn_completed", "RDW", level);
            case "gain_started": return new EventDescriptor("rdw.gain_started", "RDW", level);
            case "gain_completed": return new EventDescriptor("rdw.gain_completed", "RDW", level);
            case "gain_cancelled": return new EventDescriptor("rdw.gain_cancelled", "RDW", level);
            default:
                return new EventDescriptor(
                    "system." + FirstNonEmpty(eventType, "unspecified_event").Replace('_', '.'),
                    "System",
                    level);
        }
    }

    private void WriteTelemetry(PhysicalRiskState riskState, GuidanceRuntimeState runtimeState)
    {
        if (session == null || !session.IsActive || telemetryWriter == null || riskState == null)
        {
            return;
        }

        try
        {
        var tracedRequest = activeRequest;
        var tracedResponse = activeResponse;
        var requestId = FirstNonEmpty(tracedRequest?.request_id, tracedResponse?.request_id);
        var decisionId = FirstNonEmpty(tracedRequest?.decision_id, tracedResponse?.decision_id);
        var calibrationId = FirstNonEmpty(riskState.calibration_id, activeCalibrationId, tracedRequest?.calibration_id, tracedResponse?.calibration_id);
        var taskId = FirstNonEmpty(runtimeState?.taskId, tracedRequest?.task_id, session.taskId);
        var stateVersion = runtimeState != null
            ? runtimeState.stateVersion
            : tracedRequest != null
                ? tracedRequest.state_version
                : tracedResponse != null ? tracedResponse.state_version : 0;

        var builder = new StringBuilder(1024);
        builder.Append('{');
        Append(builder, "timestamp", DateTime.UtcNow.ToString("o"), false);
        Append(builder, "elapsed_ms", session.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), raw: true);
        Append(builder, "record_type", "telemetry");
        Append(builder, "level", "INFO");
        Append(builder, "category", "Safety");
        Append(builder, "event_name", "safety.risk_snapshot");
        Append(builder, "event_type", "risk_snapshot");
        Append(builder, "message", string.Empty);
        Append(builder, "session_id", session.sessionId);

        builder.Append(",\"trace\":{");
        Append(builder, "calibration_id", calibrationId, false);
        Append(builder, "request_id", requestId);
        Append(builder, "decision_id", decisionId);
        Append(builder, "risk_snapshot_id", riskState.risk_snapshot_id);
        Append(builder, "state_version", stateVersion.ToString(CultureInfo.InvariantCulture), raw: true);
        builder.Append('}');

        // 保留常用平铺字段，便于既有实验脚本在兼容期读取。
        Append(builder, "calibration_id", calibrationId);
        Append(builder, "request_id", requestId);
        Append(builder, "decision_id", decisionId);
        Append(builder, "risk_snapshot_id", riskState.risk_snapshot_id);
        Append(builder, "task_id", taskId);
        Append(builder, "state_version", stateVersion.ToString(CultureInfo.InvariantCulture), raw: true);

        builder.Append(",\"data\":{");
        Append(builder, "physical_space_id", FirstNonEmpty(riskState.physical_space, session.physicalSpaceId), false);
        builder.Append(",\"physical_position_m\":{");
        Append(builder, "x", Float(riskState.user_physical_position.x), false, raw: true);
        Append(builder, "z", Float(riskState.user_physical_position.y), raw: true);
        builder.Append('}');
        builder.Append(",\"physical_forward\":{");
        Append(builder, "x", Float(riskState.user_physical_forward.x), false, raw: true);
        Append(builder, "z", Float(riskState.user_physical_forward.y), raw: true);
        builder.Append('}');
        builder.Append(",\"clearance_m\":{");
        Append(builder, "forward", Float(riskState.clearance_forward), false, raw: true);
        Append(builder, "left", Float(riskState.clearance_left), raw: true);
        Append(builder, "right", Float(riskState.clearance_right), raw: true);
        builder.Append('}');
        Append(builder, "boundary_risk", riskState.boundary_risk);
        Append(builder, "reset_risk", riskState.reset_risk);
        Append(builder, "reset_required", riskState.reset_required ? "true" : "false", raw: true);
        AppendStringArray(builder, "safe_directions", riskState.safe_directions);
        AppendStringArray(builder, "unsafe_directions", riskState.unsafe_directions);
        builder.Append('}');
        builder.Append('}');

        if (!telemetryWriter.TryWriteLine(builder.ToString(), autoFlush))
        {
            ReportStorageError("telemetry", telemetryWriter.LastError);
        }
        }
        catch (Exception exception)
        {
            ReportStorageError("telemetry_serialize", exception);
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
            case "backend_online": return "后端服务已连接";
            case "backend_unavailable": return "后端服务不可用";
            case "request_failed": return "引导请求失败";
            case "task_state_changed": return "任务状态已更新";
            case "task_completed": return "任务已完成";
            case "task_failed": return "任务执行失败";
            case "target_reached": return "已到达引导目标";
            case "detour_selected": return "已选择绕行目标";
            case "detour_completed": return "绕行任务已完成";
            case "replanning": return "正在重新规划";
            case "turn_requested": return "已请求用户转向";
            case "user_turn_started": return "用户开始转向";
            case "user_turn_completed": return "用户转向已完成";
            case "gain_started": return "旋转增益已开始";
            case "gain_completed": return "旋转增益已完成";
            case "gain_cancelled": return "旋转增益已取消";
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
            case "target_reached": return "target reached";
            case "detour_selected": return "detour selected";
            case "detour_completed": return "detour completed";
            case "replanning": return "replanning requested";
            case "turn_requested": return "turn requested";
            case "user_turn_started": return "user turn started";
            case "user_turn_completed": return "user turn completed";
            case "gain_started": return "gain started";
            case "gain_completed": return "gain completed";
            default: return string.Empty;
        }
    }

    private void LogTargetEvent(
        string eventType,
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
            calibrationIdOverride: calibrationId,
            timestampOverride: timestamp);
        pendingCalibrationEvents.Add(new PendingCalibrationEvent(eventType, calibrationId, message, timestamp));
    }

    private void FlushPendingCalibrationEvents()
    {
        foreach (var calibrationEvent in pendingCalibrationEvents)
        {
            activeCalibrationId = FirstNonEmpty(calibrationEvent.CalibrationId, activeCalibrationId);
            Write(
                calibrationEvent.EventType,
                null,
                null,
                null,
                null,
                null,
                calibrationEvent.Message,
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
                Join(riskState.safe_directions),
                Join(riskState.unsafe_directions));
    }

    private static void AppendConsoleValue(StringBuilder builder, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(' ').Append(key).Append('=').Append(value.Replace('\n', ' ').Replace('\r', ' '));
        }
    }

    private static void AppendStringArray(StringBuilder builder, string key, string[] values, bool comma = true)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"').Append(Escape(key)).Append("\":[");
        if (values != null)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append('"').Append(Escape(values[i])).Append('"');
            }
        }
        builder.Append(']');
    }

    private static string Float(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void Append(StringBuilder builder, string key, string value, bool comma = true, bool raw = false)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"').Append(Escape(key)).Append("\":");
        if (raw)
        {
            builder.Append(string.IsNullOrWhiteSpace(value) ? "null" : value);
        }
        else
        {
            builder.Append('"').Append(Escape(value)).Append('"');
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

    private static string Join(string[] values)
    {
        return values == null || values.Length == 0 ? string.Empty : string.Join(",", values);
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        return builder.ToString();
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
