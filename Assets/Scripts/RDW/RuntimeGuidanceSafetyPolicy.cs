using System;

/// <summary>
/// 表示运行期导览安全策略允许协调器采取的唯一动作。
/// </summary>
public enum RuntimeGuidanceSafetyDecision
{
    /// <summary>当前导览可以继续执行。</summary>
    Continue,
    /// <summary>停止当前导览，并基于最新风险重新规划。</summary>
    SoftReroute,
    /// <summary>当前物理动作不安全且无法语义绕行，执行自动安全朝向恢复。</summary>
    PauseReset,
    /// <summary>立即停止全部导览行为并进入安全保持。</summary>
    HardStop
}

/// <summary>
/// 汇总一次运行期导览安全复验所需的不可变事实，不负责读取场景或修改业务状态。
/// </summary>
public sealed class RuntimeGuidanceSafetyInput
{
    private const int UnsafeSamplesRequiredForIntervention = 2;

    /// <summary>当前活动决策 ID。</summary>
    public string ActiveDecisionId { get; }
    /// <summary>当前活动目标 ID。</summary>
    public string TargetId { get; }
    /// <summary>当前执行的移动指令。</summary>
    public string MovementInstruction { get; }
    /// <summary>最近一次物理风险快照。</summary>
    public PhysicalRiskState LatestRisk { get; }
    /// <summary>当前 XR Tracking 是否有效。</summary>
    public bool TrackingValid { get; }
    /// <summary>当前 Physical Space Calibration 是否有效。</summary>
    public bool CalibrationValid { get; }
    /// <summary>风险快照、校准与当前 Physical Space 是否匹配。</summary>
    public bool PhysicalSpaceMatches { get; }
    /// <summary>当前目标相对 Virtual Forward 映射出的计划物理方向。</summary>
    public string PlannedDirection { get; }
    /// <summary>计划物理方向的 clearance，单位：米。</summary>
    public float PlannedDirectionClearanceMeters { get; }
    /// <summary>计划方向和 clearance 是否成功解析。</summary>
    public bool PlannedDirectionAvailable { get; }
    /// <summary>计划方向 clearance 是否已进入风险计算器的 critical 阈值。</summary>
    public bool PlannedDirectionCritical { get; }
    /// <summary>当前 Reset 生命周期状态。</summary>
    public ResetLifecycleState ResetState { get; }
    /// <summary>当前计划方向连续进入 unsafe 的风险样本数。</summary>
    public int ConsecutiveUnsafeSamples { get; }
    /// <summary>最新风险下是否存在明确 Eligible 的替代候选。</summary>
    public bool HasSafeSemanticCandidate { get; }
    /// <summary>当前风险下是否存在可供自动恢复的明确 Safe Heading。</summary>
    public bool HasRecoveryHeading { get; }
    /// <summary>当前计划方向是否已经需要安全层介入。</summary>
    public bool RequiresSafetyIntervention { get; }
    /// <summary>兼容旧日志字段的安全语义候选别名。</summary>
    public bool SafeCandidateAvailable => HasSafeSemanticCandidate;

    /// <summary>
    /// 创建一次不依赖 Unity 场景对象的运行期安全输入。
    /// </summary>
    public RuntimeGuidanceSafetyInput(
        string activeDecisionId,
        string targetId,
        string movementInstruction,
        PhysicalRiskState latestRisk,
        bool trackingValid,
        bool calibrationValid,
        bool physicalSpaceMatches,
        string plannedDirection,
        float plannedDirectionClearanceMeters,
        bool plannedDirectionAvailable,
        bool plannedDirectionCritical,
        ResetLifecycleState resetState,
        int consecutiveUnsafeSamples,
        bool safeCandidateAvailable)
        : this(
            activeDecisionId,
            targetId,
            movementInstruction,
            latestRisk,
            trackingValid,
            calibrationValid,
            physicalSpaceMatches,
            plannedDirection,
            plannedDirectionClearanceMeters,
            plannedDirectionAvailable,
            plannedDirectionCritical,
            resetState,
            consecutiveUnsafeSamples,
            ComputeRequiresSafetyIntervention(latestRisk, plannedDirection, plannedDirectionCritical, consecutiveUnsafeSamples),
            safeCandidateAvailable,
            HasRecoveryHeadingInRisk(latestRisk))
    {
    }

    /// <summary>创建包含三项内部安全事实的不可变策略输入。</summary>
    public RuntimeGuidanceSafetyInput(
        string activeDecisionId,
        string targetId,
        string movementInstruction,
        PhysicalRiskState latestRisk,
        bool trackingValid,
        bool calibrationValid,
        bool physicalSpaceMatches,
        string plannedDirection,
        float plannedDirectionClearanceMeters,
        bool plannedDirectionAvailable,
        bool plannedDirectionCritical,
        ResetLifecycleState resetState,
        int consecutiveUnsafeSamples,
        bool requiresSafetyIntervention,
        bool hasSafeSemanticCandidate,
        bool hasRecoveryHeading)
    {
        ActiveDecisionId = activeDecisionId ?? string.Empty;
        TargetId = targetId ?? string.Empty;
        MovementInstruction = movementInstruction ?? string.Empty;
        LatestRisk = latestRisk;
        TrackingValid = trackingValid;
        CalibrationValid = calibrationValid;
        PhysicalSpaceMatches = physicalSpaceMatches;
        PlannedDirection = plannedDirection ?? string.Empty;
        PlannedDirectionClearanceMeters = plannedDirectionClearanceMeters;
        PlannedDirectionAvailable = plannedDirectionAvailable;
        PlannedDirectionCritical = plannedDirectionCritical;
        ResetState = resetState;
        ConsecutiveUnsafeSamples = Math.Max(0, consecutiveUnsafeSamples);
        RequiresSafetyIntervention = requiresSafetyIntervention;
        HasSafeSemanticCandidate = hasSafeSemanticCandidate;
        HasRecoveryHeading = hasRecoveryHeading;
    }

    private static bool ComputeRequiresSafetyIntervention(
        PhysicalRiskState risk,
        string plannedDirection,
        bool plannedDirectionCritical,
        int consecutiveUnsafeSamples)
    {
        if (risk == null)
        {
            return true;
        }

        // Runtime Safety 的连续 HeadingSample 由 Coordinator 计算；labels 仅保留为兼容 DTO。
        var requiresIntervention = plannedDirectionCritical || risk.reset_required;
        if (!requiresIntervention)
        {
            return false;
        }

        return plannedDirectionCritical ||
               risk.reset_required ||
               consecutiveUnsafeSamples >= UnsafeSamplesRequiredForIntervention;
    }

    private static bool HasRecoveryHeadingInRisk(PhysicalRiskState risk)
    {
        if (risk == null || risk.heading_profile == null)
        {
            return false;
        }

        foreach (var sample in risk.heading_profile)
        {
            if (PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(sample))
            {
                return true;
            }
        }

        return false;
    }

}

/// <summary>
/// 依据当前导览、物理风险与 Reset 事实返回唯一安全动作。
/// 本策略不访问 GameObject、不发请求，也不修改任务状态。
/// </summary>
public sealed class RuntimeGuidanceSafetyPolicy
{
    /// <summary>
    /// 对一份运行期安全输入做无副作用判定。
    /// </summary>
    public RuntimeGuidanceSafetyDecision Evaluate(RuntimeGuidanceSafetyInput input)
    {
        if (input == null ||
            string.IsNullOrWhiteSpace(input.ActiveDecisionId) ||
            string.IsNullOrWhiteSpace(input.TargetId) ||
            string.IsNullOrWhiteSpace(input.MovementInstruction) ||
            input.LatestRisk == null ||
            !input.TrackingValid ||
            !input.CalibrationValid ||
            !input.PhysicalSpaceMatches ||
            !input.PlannedDirectionAvailable)
        {
            return RuntimeGuidanceSafetyDecision.HardStop;
        }

        // Reset 期间由恢复链路统一复验，普通策略不能恢复旧 Guidance 或重复启动 Reset。
        if (IsResetActive(input.ResetState))
        {
            return RuntimeGuidanceSafetyDecision.Continue;
        }

        if (!input.RequiresSafetyIntervention)
        {
            return RuntimeGuidanceSafetyDecision.Continue;
        }

        if (input.HasSafeSemanticCandidate)
        {
            return RuntimeGuidanceSafetyDecision.SoftReroute;
        }

        return input.HasRecoveryHeading || HasTrustedRecoveryHoldFacts(input)
            ? RuntimeGuidanceSafetyDecision.PauseReset
            : RuntimeGuidanceSafetyDecision.HardStop;
    }

    private static bool HasTrustedRecoveryHoldFacts(RuntimeGuidanceSafetyInput input)
    {
        var risk = input != null ? input.LatestRisk : null;
        return input != null &&
               input.TrackingValid &&
               input.CalibrationValid &&
               input.PhysicalSpaceMatches &&
               risk != null &&
               risk.boundary_context != null &&
               risk.boundary_context.Count >= 3 &&
               PhysicalSpaceGeometry.ContainsPoint(
                   risk.boundary_context,
                   risk.user_physical_position);
    }

    private static bool IsResetActive(ResetLifecycleState state)
    {
        return state == ResetLifecycleState.Required ||
               state == ResetLifecycleState.TurningToSafeDirection ||
               state == ResetLifecycleState.Completed;
    }
}
