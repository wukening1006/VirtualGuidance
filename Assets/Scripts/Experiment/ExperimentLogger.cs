using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 将实验会话、guidance 请求/响应及运行状态按 JSONL 追加到项目相对日志目录。
/// 字段优先取事件数据，再回退到条件或会话值，供离线实验分析使用。
/// </summary>
public sealed class ExperimentLogger : MonoBehaviour
{
    [SerializeField] private string logFolderRelativeToProject = "Logs/TestRuns";
    [SerializeField] private bool autoFlush = true;

    private ExperimentSession session;
    private string logPath;

    /// <summary>当前实验会话；尚未开始时为 null。</summary>
    public ExperimentSession Session => session;
    /// <summary>当前 JSONL 日志的绝对路径；尚未开始时为空。</summary>
    public string LogPath => logPath;

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
        var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFolderRelativeToProject));
        Directory.CreateDirectory(folder);
        logPath = Path.Combine(folder, $"unity_session_{session.sessionId}.jsonl");
        Write("session_started", spec, null, null, null, null, "session started");
    }

    /// <summary>记录条件已应用事件；没有有效会话时忽略。</summary>
    public void LogConditionSelected(ExperimentConditionSpec spec)
    {
        Write("condition_selected", spec, null, null, null, null, "condition selected");
    }

    /// <summary>记录发送给后端的请求及请求时的物理风险状态。</summary>
    public void LogRequest(GuidanceRequest request, PhysicalRiskState riskState)
    {
        Write("request_built", null, request, null, riskState, null, "request built");
    }

    /// <summary>记录后端响应及响应到达时的物理风险状态。</summary>
    public void LogResponse(GuidanceResponse response, PhysicalRiskState riskState)
    {
        Write("response_received", null, null, response, riskState, null, "response received");
    }

    /// <summary>记录任务、风险或请求失败等通用运行事件。</summary>
    public void LogStatus(string eventType, GuidanceRuntimeState runtimeState, PhysicalRiskState riskState, string message)
    {
        Write(eventType, null, null, null, riskState, runtimeState, message);
    }

    private void Write(
        string eventType,
        ExperimentConditionSpec spec,
        GuidanceRequest request,
        GuidanceResponse response,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        string message)
    {
        // 没有有效会话时不创建无归属日志，避免不同实验运行的数据混入同一文件。
        if (session == null || !session.IsActive || string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var builder = new StringBuilder(512);
        builder.Append('{');
        Append(builder, "timestamp", DateTime.UtcNow.ToString("o"), false);
        Append(builder, "event_type", eventType);
        Append(builder, "session_id", session.sessionId);
        // 事件携带的数据最接近发生时状态，缺失时才回退到条件和会话快照。
        Append(builder, "participant_id", FirstNonEmpty(request?.participant_id, spec?.participantId, session.participantId));
        Append(builder, "condition", FirstNonEmpty(request?.condition, spec?.condition, session.condition));
        Append(builder, "task_id", FirstNonEmpty(request?.task_id, spec?.taskId, session.taskId));
        Append(builder, "physical_space", FirstNonEmpty(request?.physical_space_id, spec?.physicalSpaceId, session.physicalSpaceId));
        Append(builder, "current_exhibit", FirstNonEmpty(request?.current_exhibit, spec?.startExhibitId, runtimeState?.currentExhibitId));
        Append(builder, "semantic_next_target", FirstNonEmpty(request?.semantic_next_target, spec?.semanticNextTargetId, runtimeState?.nextTargetId));
        Append(builder, "runtime_phase", runtimeState != null ? runtimeState.phase.ToString() : string.Empty);
        Append(builder, "state_version", runtimeState != null ? runtimeState.stateVersion.ToString(CultureInfo.InvariantCulture) : "0", raw: true);
        Append(builder, "selected_target", response != null ? response.selected_target : string.Empty);
        Append(builder, "movement_instruction", response != null ? response.movement_instruction : string.Empty);
        Append(builder, "verifier_approved", response != null && response.verifier != null && response.verifier.approved ? "true" : "false", raw: true);
        Append(builder, "verifier_reason", response != null && response.verifier != null ? response.verifier.reason : string.Empty);
        Append(builder, "boundary_risk", riskState != null ? riskState.boundary_risk : string.Empty);
        Append(builder, "reset_required", riskState != null && riskState.reset_required ? "true" : "false", raw: true);
        Append(builder, "unsafe_directions", riskState != null ? Join(riskState.unsafe_directions) : string.Empty);
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
}
