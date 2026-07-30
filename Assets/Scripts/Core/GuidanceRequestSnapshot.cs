using System;

/// <summary>
/// 保存 guidance 请求发出时的任务版本与物理风险特征，供响应执行前识别过期结果。
/// 快照由请求协调器创建，并由本地安全校验器与当前状态比较。
/// </summary>
public sealed class GuidanceRequestSnapshot
{
    /// <summary>请求所属实验会话 ID。</summary>
    public string sessionId { get; private set; }
    /// <summary>请求使用的 Physical Space Calibration ID。</summary>
    public string calibrationId { get; private set; }
    /// <summary>请求的唯一 ID。</summary>
    public string requestId { get; private set; }
    /// <summary>请求所属规划决策的唯一 ID。</summary>
    public string decisionId { get; private set; }
    /// <summary>请求冻结的 Candidate Set ID。</summary>
    public string candidateSetId { get; private set; }
    /// <summary>请求发出时的任务状态版本。</summary>
    public int stateVersion { get; private set; }
    /// <summary>请求所属任务 ID。</summary>
    public string taskId { get; private set; }
    /// <summary>请求决策使用的风险快照 ID。</summary>
    public string riskSnapshotId { get; private set; }
    /// <summary>请求使用的执行风险修订号。</summary>
    public int riskRevision { get; private set; }
    /// <summary>请求中的当前展品 ID。</summary>
    public string currentExhibitId { get; private set; }
    /// <summary>请求中的任务优先目标 ID。</summary>
    public string preferredTaskTargetId { get; private set; }
    /// <summary>请求使用的 physical space ID。</summary>
    public string physicalSpaceId { get; private set; }
    /// <summary>请求发出时由空间、reset 状态及安全方向组成的风险签名。</summary>
    public string riskSignature { get; private set; }

    /// <summary>
    /// 从即将发送的请求及当前运行状态捕获响应匹配所需的稳定字段。
    /// </summary>
    /// <returns>独立于后续状态变化的请求快照。</returns>
    public static GuidanceRequestSnapshot Capture(
        GuidanceRequest request,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState)
    {
        return new GuidanceRequestSnapshot
        {
            sessionId = request != null ? request.session_id : string.Empty,
            calibrationId = request != null ? request.calibration_id : string.Empty,
            requestId = request != null ? request.request_id : string.Empty,
            decisionId = request != null ? request.decision_id : string.Empty,
            candidateSetId = request != null ? request.candidate_set_id : string.Empty,
            stateVersion = request != null ? request.state_version : runtimeState != null ? runtimeState.stateVersion : 0,
            taskId = request != null && !string.IsNullOrWhiteSpace(request.task_id)
                ? request.task_id
                : runtimeState != null ? runtimeState.taskId : string.Empty,
            riskSnapshotId = request != null ? request.risk_snapshot_id : string.Empty,
            riskRevision = request != null && request.risk_snapshot != null
                ? request.risk_snapshot.risk_revision
                : riskState != null ? riskState.risk_revision : 0,
            currentExhibitId = request != null && !string.IsNullOrWhiteSpace(request.current_exhibit)
                ? request.current_exhibit
                : runtimeState != null ? runtimeState.currentExhibitId : string.Empty,
            preferredTaskTargetId = request != null && !string.IsNullOrWhiteSpace(request.preferred_task_target)
                ? request.preferred_task_target
                : runtimeState != null ? runtimeState.nextTargetId : string.Empty,
            physicalSpaceId = request != null ? request.physical_space_id : string.Empty,
            riskSignature = BuildRiskSignature(riskState)
        };
    }

    /// <summary>
    /// 检查响应是否精确对应当前请求、决策和状态版本。
    /// </summary>
    /// <returns>三个关联字段均一致时返回 true。</returns>
    public bool MatchesResponse(GuidanceResponse response)
    {
        return response != null &&
               string.Equals(sessionId, response.session_id, StringComparison.Ordinal) &&
               string.Equals(calibrationId, response.calibration_id, StringComparison.Ordinal) &&
               string.Equals(requestId, response.request_id, StringComparison.Ordinal) &&
               string.Equals(decisionId, response.decision_id, StringComparison.Ordinal) &&
               string.Equals(candidateSetId, response.candidate_set_id, StringComparison.Ordinal) &&
               stateVersion == response.state_version &&
               string.Equals(riskSnapshotId, response.risk_snapshot_id, StringComparison.Ordinal) &&
               riskRevision == response.risk_revision;
    }

    /// <summary>
    /// 检查任务版本、校准和物理空间身份是否仍与请求发出时一致。
    /// </summary>
    /// <returns>稳定上下文未变化、响应仍可基于最新风险继续校验时返回 true。</returns>
    public bool MatchesTaskContext(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        return runtimeState != null &&
               runtimeState.stateVersion == stateVersion &&
               string.Equals(taskId ?? string.Empty, runtimeState.taskId ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(
                   currentExhibitId ?? string.Empty,
                   runtimeState.currentExhibitId ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   preferredTaskTargetId ?? string.Empty,
                   runtimeState.nextTargetId ?? string.Empty,
                   StringComparison.Ordinal) &&
               riskState != null &&
               string.Equals(calibrationId ?? string.Empty, riskState.calibration_id, StringComparison.Ordinal) &&
               string.Equals(physicalSpaceId ?? string.Empty, riskState.physical_space, StringComparison.Ordinal);
    }

    /// <summary>兼容旧调用：按稳定任务上下文判断，不要求风险修订号保持不变。</summary>
    public bool Matches(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        return MatchesTaskContext(runtimeState, riskState);
    }

    private static string BuildRiskSignature(PhysicalRiskState riskState)
    {
        if (riskState == null)
        {
            return "risk:unknown";
        }

        return string.Join(
            "|",
            riskState.physical_space,
            riskState.reset_required ? "reset" : "no_reset",
            Join(riskState.unsafe_directions),
            Join(riskState.safe_directions));
    }

    private static string Join(System.Collections.Generic.IReadOnlyList<string> values)
    {
        return values == null || values.Count == 0 ? "none" : string.Join(",", values);
    }
}
