using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 将实验会话、guidance 请求/响应及运行状态写入 JSONL，并实时镜像到 Unity Console。
/// 字段优先取事件数据，再回退到条件或会话值，兼顾运行观察与离线分析。
/// </summary>
public sealed class ExperimentLogger : MonoBehaviour
{
    private const string ConsolePrefix = "VirtualGuidance";

    [Header("文件日志")]
    [SerializeField] private string logFolderRelativeToProject = "Logs/TestRuns";
    [SerializeField] private bool autoFlush = true;

    [Header("Unity Console")]
    [Tooltip("启用后将关键实验事件实时镜像到 Unity Console；完整数据仍写入 JSONL。")]
    [SerializeField] private bool mirrorToUnityConsole = true;

    private ExperimentSession session;
    private string logPath;
    private ExperimentRunState runState = ExperimentRunState.Preparing;
    private GuidanceRequest activeRequest;
    private GuidanceResponse activeResponse;
    private PhysicalRiskState activeRiskState;
    private string activeCalibrationId = string.Empty;
    private string lastConsoleRiskSignature = string.Empty;
    private readonly List<PendingCalibrationEvent> pendingCalibrationEvents = new List<PendingCalibrationEvent>();

    /// <summary>当前实验会话；尚未开始时为 null。</summary>
    public ExperimentSession Session => session;
    /// <summary>当前 JSONL 日志的绝对路径；尚未开始时为空。</summary>
    public string LogPath => logPath;

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

        session = ExperimentSession.Start(spec);
        activeRequest = null;
        activeResponse = null;
        activeRiskState = null;
        activeCalibrationId = string.Empty;
        lastConsoleRiskSignature = string.Empty;
        var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFolderRelativeToProject));
        Directory.CreateDirectory(folder);
        logPath = Path.Combine(folder, $"unity_session_{session.sessionId}.jsonl");
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

    /// <summary>记录任务、风险或请求失败等通用运行事件。</summary>
    public void LogStatus(string eventType, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string message)
    {
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
        session.End(reason);
        activeRequest = null;
        activeResponse = null;
        activeRiskState = null;
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

        if (emitUnityConsole && mirrorToUnityConsole && ShouldEmitUnityConsole(eventType, tracedRiskState))
        {
            EmitUnityConsole(
                eventType,
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

        var builder = new StringBuilder(512);
        builder.Append('{');
        Append(builder, "timestamp", FirstNonEmpty(timestampOverride, DateTime.UtcNow.ToString("o")), false);
        Append(builder, "event_type", eventType);
        Append(builder, "session_id", session.sessionId);
        Append(builder, "calibration_id", calibrationId);
        // 事件携带的数据最接近发生时状态，缺失时才回退到条件和会话快照。
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
        Append(builder, "raw_guidance", tracedResponse != null ? JsonUtility.ToJson(tracedResponse) : string.Empty);
        Append(
            builder,
            "condition_verifier_result",
            tracedResponse == null || tracedResponse.verifier == null
                ? string.Empty
                : tracedResponse.verifier.approved ? "approved" : "rejected");
        Append(builder, "runtime_validation_result", runtimeValidationResult);
        Append(builder, "runtime_intervention", runtimeIntervention ? "true" : "false", raw: true);
        Append(builder, "executed_guidance", executed && tracedResponse != null ? JsonUtility.ToJson(tracedResponse) : string.Empty);
        Append(builder, "boundary_risk", tracedRiskState != null ? tracedRiskState.boundary_risk : string.Empty);
        Append(builder, "reset_required", tracedRiskState != null && tracedRiskState.reset_required ? "true" : "false", raw: true);
        Append(builder, "unsafe_directions", tracedRiskState != null ? Join(tracedRiskState.unsafe_directions) : string.Empty);
        Append(builder, "message", message);
        builder.Append('}');

        using (var writer = new StreamWriter(logPath, append: true, Encoding.UTF8))
        {
            writer.WriteLine(builder.ToString());
            if (autoFlush)
            {
                writer.Flush();
            }
        }
    }

    private bool ShouldEmitUnityConsole(string eventType, PhysicalRiskState riskState)
    {
        if (!string.Equals(eventType, "risk_changed", StringComparison.Ordinal))
        {
            return true;
        }

        // RiskEvaluator 每 0.2 秒生成快照；Console 只报告安全语义变化，完整快照仍全部写入 JSONL。
        var signature = riskState == null
            ? "none"
            : string.Join(
                "|",
                riskState.boundary_risk,
                riskState.reset_risk,
                riskState.reset_required,
                Join(riskState.safe_directions),
                Join(riskState.unsafe_directions));
        if (string.Equals(signature, lastConsoleRiskSignature, StringComparison.Ordinal))
        {
            return false;
        }

        lastConsoleRiskSignature = signature;
        return true;
    }

    private void EmitUnityConsole(
        string eventType,
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
        var level = ResolveConsoleLogType(eventType, riskState);
        var builder = new StringBuilder(512);
        builder.Append('[').Append(ConsolePrefix).Append(']');
        builder.Append('[').Append(ConsoleLevelLabel(level)).Append(']');
        builder.Append('[').Append(FirstNonEmpty(eventType, "unspecified_event")).Append("] ");
        builder.Append("session_id=").Append(FirstNonEmpty(session?.sessionId, "pre-session"));
        builder.Append(" calibration_id=").Append(FirstNonEmpty(calibrationId, "--"));
        builder.Append(" request_id=").Append(FirstNonEmpty(requestId, "--"));
        builder.Append(" decision_id=").Append(FirstNonEmpty(decisionId, "--"));
        builder.Append(" state_version=").Append(stateVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append(" risk_snapshot_id=").Append(FirstNonEmpty(riskSnapshotId, "--"));
        builder.Append(" task_id=").Append(FirstNonEmpty(taskId, "--"));
        builder.Append(" phase=").Append(runtimeState != null ? runtimeState.phase.ToString() : "--");
        builder.Append(" target=").Append(FirstNonEmpty(selectedTarget, response?.selected_target, "--"));
        builder.Append(" target_role=").Append(FirstNonEmpty(targetRole, "--"));
        builder.Append(" risk=").Append(FirstNonEmpty(riskState?.boundary_risk, "--"));
        builder.Append(" movement=").Append(FirstNonEmpty(response?.movement_instruction, "--"));
        builder.Append(" message=").Append(FirstNonEmpty(message, "--"));

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

    private static string ConsoleLevelLabel(LogType logType)
    {
        if (logType == LogType.Error) return "ERROR";
        if (logType == LogType.Warning) return "WARNING";
        return "INFO";
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
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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
