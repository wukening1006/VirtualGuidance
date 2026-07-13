using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class ExperimentLogger : MonoBehaviour
{
    [SerializeField] private string logFolderRelativeToProject = "Logs/TestRuns";
    [SerializeField] private bool autoFlush = true;

    private ExperimentSession session;
    private string logPath;

    public ExperimentSession Session => session;
    public string LogPath => logPath;

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

    public void LogConditionSelected(ExperimentConditionSpec spec)
    {
        Write("condition_selected", spec, null, null, null, null, "condition selected");
    }

    public void LogRequest(GuidanceRequest request, PhysicalRiskState riskState)
    {
        Write("request_built", null, request, null, riskState, null, "request built");
    }

    public void LogResponse(GuidanceResponse response, PhysicalRiskState riskState)
    {
        Write("response_received", null, null, response, riskState, null, "response received");
    }

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
        if (session == null || !session.IsActive || string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var builder = new StringBuilder(512);
        builder.Append('{');
        Append(builder, "timestamp", DateTime.UtcNow.ToString("o"), false);
        Append(builder, "event_type", eventType);
        Append(builder, "session_id", session.sessionId);
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
