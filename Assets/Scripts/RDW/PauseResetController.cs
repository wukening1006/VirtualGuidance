using System;
using UnityEngine;

/// <summary>
/// 表示 Turn-to-Safe-Direction Reset 的软件生命周期。
/// </summary>
public enum ResetLifecycleState
{
    /// <summary>当前没有 Reset。</summary>
    Inactive,
    /// <summary>已收到 Reset 要求，正在计算安全朝向。</summary>
    Required,
    /// <summary>等待用户原地转向固定的安全 Physical Forward。</summary>
    TurningToSafeDirection,
    /// <summary>用户已到达安全朝向，等待风险重算与任务恢复。</summary>
    Completed,
    /// <summary>Reset 被会话终止或研究员操作取消。</summary>
    Cancelled
}

/// <summary>
/// 保存一次 Reset 开始或完成时的固定安全朝向信息。
/// </summary>
public sealed class ResetEventInfo
{
    /// <summary>协调层选择并冻结的恢复航向计划。</summary>
    public RecoveryHeadingPlan RecoveryPlan { get; }
    /// <summary>本次 Reset 选择的相对方向。</summary>
    public string SafeDirection { get; }
    /// <summary>相对恢复开始朝向的有符号偏航角，单位：度；负值为左转。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>参与者需要完成的大致转角，单位：度。</summary>
    public float ApproximateTurnDegrees => Mathf.Abs(RelativeYawDegrees);
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward { get; }
    /// <summary>触发本次 Reset 的原因。</summary>
    public string Reason { get; }

    /// <summary>使用协调层冻结的恢复计划创建不可变 Reset 事件。</summary>
    public ResetEventInfo(RecoveryHeadingPlan recoveryPlan, string reason)
    {
        RecoveryPlan = recoveryPlan;
        RelativeYawDegrees = recoveryPlan != null && recoveryPlan.IsSuccess
            ? recoveryPlan.RelativeYawDegrees
            : 0f;
        SafeDirection = DirectionForYaw(RelativeYawDegrees);
        SafePhysicalForward = recoveryPlan != null && recoveryPlan.IsSuccess
            ? recoveryPlan.SafePhysicalForward
            : Vector2.up;
        Reason = reason ?? string.Empty;
    }

    /// <summary>兼容旧日志与测试边界；生产恢复事件应直接传入 RecoveryHeadingPlan。</summary>
    public ResetEventInfo(string safeDirection, Vector2 safePhysicalForward, string reason)
    {
        RecoveryPlan = null;
        SafeDirection = safeDirection ?? string.Empty;
        RelativeYawDegrees = string.Equals(SafeDirection, "left", StringComparison.Ordinal)
            ? -90f
            : string.Equals(SafeDirection, "right", StringComparison.Ordinal) ? 90f : 0f;
        SafePhysicalForward = safePhysicalForward;
        Reason = reason ?? string.Empty;
    }

    /// <summary>把连续恢复偏航映射为仅供 UI 与兼容日志使用的方向标签。</summary>
    public static string DirectionForYaw(float relativeYawDegrees)
    {
        if (relativeYawDegrees < -5f) return "left";
        if (relativeYawDegrees > 5f) return "right";
        return "forward";
    }
}

/// <summary>
/// 执行 Turn-to-Safe-Direction Reset：停止 Guidance/Gain 表现并执行协调层冻结的 Physical Forward，
/// 在用户原地转入容差后发布 ResetCompleted。航向选择、复验与重规划由实验协调层处理。
/// </summary>
public sealed class PauseResetController : MonoBehaviour
{
    [Header("Reset 依赖")]
    [Tooltip("Reset 开始时需要清除的 Guidance 表现。")]
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [Tooltip("Reset 开始时需要中止的 Rotation Gain。")]
    [SerializeField] private RotationGainController rotationGainController;
    [Tooltip("提供校准后的 Physical Room Forward，用于检测用户安全转向。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("提供最近一次 forward/left/right clearance。")]
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;

    [Header("安全转向")]
    [Tooltip("用户朝向与安全目标的最大完成误差，单位：度。")]
    [SerializeField] private float completionToleranceDegrees = 12f;

    private ResetLifecycleState state = ResetLifecycleState.Inactive;
    private string resetReason = string.Empty;
    private string safeDirection = string.Empty;
    private Vector2 safePhysicalForward = Vector2.up;
    private RecoveryHeadingPlan activePlan;
    private readonly RecoveryHeadingPlanner compatibilityPlanner = new RecoveryHeadingPlanner();

    /// <summary>Reset 状态发生变化时触发。</summary>
    public event Action<ResetLifecycleState> StateChanged;
    /// <summary>选定安全朝向并开始等待用户转向时触发。</summary>
    public event Action<ResetEventInfo> ResetStarted;
    /// <summary>用户进入安全朝向容差时触发。</summary>
    public event Action<ResetEventInfo> ResetCompleted;

    /// <summary>当前是否因 Reset 暂停导览。</summary>
    public bool IsPaused { get; private set; }
    /// <summary>当前是否仍有待完成或待风险复验的 Reset。</summary>
    public bool IsActive => state == ResetLifecycleState.Required ||
                            state == ResetLifecycleState.TurningToSafeDirection ||
                            state == ResetLifecycleState.Completed;
    /// <summary>当前 Reset 生命周期状态。</summary>
    public ResetLifecycleState State => state;
    /// <summary>本次 Reset 选择的显示方向。</summary>
    public string SafeDirection => safeDirection;
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward => safePhysicalForward;
    /// <summary>本次 Reset 正在执行的冻结恢复计划。</summary>
    public RecoveryHeadingPlan ActivePlan => activePlan;
    /// <summary>当前 Physical Forward 到安全目标的剩余角度，单位：度。</summary>
    public float RemainingYawDegrees => userPoseTracker == null
        ? 180f
        : Vector2.Angle(userPoseTracker.PhysicalForwardXZ, safePhysicalForward);

    private void Update()
    {
        if (state == ResetLifecycleState.TurningToSafeDirection && userPoseTracker != null)
        {
            ProcessPhysicalForward(userPoseTracker.PhysicalForwardXZ);
        }
    }

    /// <summary>
    /// 注入 Reset 使用的表现、Gain、Physical Pose 与风险依赖。
    /// </summary>
    public void Configure(
        GuidanceExecutor executor,
        RejectionPanel legacyPanel,
        RotationGainController gainController = null,
        UserPoseTracker poseTracker = null,
        PhysicalRiskEvaluator evaluator = null)
    {
        if (executor != null) guidanceExecutor = executor;
        if (gainController != null) rotationGainController = gainController;
        if (poseTracker != null) userPoseTracker = poseTracker;
        if (evaluator != null) riskEvaluator = evaluator;
    }

    /// <summary>
    /// 使用已冻结的恢复航向立即停止表现并启动安全转向；重复风险刷新不会重启活动 Reset。
    /// </summary>
    /// <param name="reason">触发 Reset 的安全原因。</param>
    /// <param name="plan">由协调层选择并冻结的恢复航向。</param>
    /// <returns>成功启动恢复时返回 true。</returns>
    public bool PauseForReset(string reason, RecoveryHeadingPlan plan)
    {
        if (plan == null || !plan.IsSuccess)
        {
            return false;
        }

        IsPaused = true;
        rotationGainController?.AbortGain("reset_required");
        guidanceExecutor?.Clear();

        if (state == ResetLifecycleState.TurningToSafeDirection || state == ResetLifecycleState.Required)
        {
            activePlan = plan;
            safePhysicalForward = plan.SafePhysicalForward;
            safeDirection = ResetEventInfo.DirectionForYaw(plan.RelativeYawDegrees);
            ResetStarted?.Invoke(CreateEventInfo());
            return true;
        }

        resetReason = string.IsNullOrWhiteSpace(reason) ? "reset_required" : reason;
        TransitionTo(ResetLifecycleState.Required);
        activePlan = plan;
        safePhysicalForward = plan.SafePhysicalForward;
        safeDirection = ResetEventInfo.DirectionForYaw(plan.RelativeYawDegrees);
        TransitionTo(ResetLifecycleState.TurningToSafeDirection);
        ResetStarted?.Invoke(CreateEventInfo());
        return true;
    }

    /// <summary>
    /// 兼容旧调用边界：从风险快照规划一次航向后立即冻结；生产流程应显式传入 RecoveryHeadingPlan。
    /// </summary>
    /// <param name="reason">触发 Reset 的安全原因。</param>
    /// <param name="riskState">触发时风险快照；为空时使用风险计算器最近状态。</param>
    public void PauseForReset(string reason, PhysicalRiskState riskState = null)
    {
        var facts = riskState ?? riskEvaluator?.CurrentRiskState;
        var plan = compatibilityPlanner.Plan(facts != null ? facts.heading_profile : null);
        if (!plan.IsSuccess)
        {
            plan = CreateLegacyForwardPlan(facts);
        }

        PauseForReset(reason, plan);
    }

    /// <summary>
    /// 处理一份校准后的 Physical Forward；进入安全目标容差后完成 Reset。
    /// </summary>
    /// <param name="currentForward">当前 Physical Room 二维朝向。</param>
    public void ProcessPhysicalForward(Vector2 currentForward)
    {
        if (state != ResetLifecycleState.TurningToSafeDirection)
        {
            return;
        }

        currentForward = NormalizeForward(currentForward);
        if (Vector2.Angle(currentForward, safePhysicalForward) > Mathf.Max(0.1f, completionToleranceDegrees))
        {
            return;
        }

        TransitionTo(ResetLifecycleState.Completed);
        ResetCompleted?.Invoke(CreateEventInfo());
    }

    /// <summary>
    /// 风险重算确认安全后解除 Reset 暂停；未完成安全转向时拒绝恢复。
    /// </summary>
    /// <returns>Reset 已完成并成功解除暂停时返回 true。</returns>
    public bool ResumeAfterReset()
    {
        if (state != ResetLifecycleState.Completed)
        {
            return false;
        }

        IsPaused = false;
        activePlan = null;
        TransitionTo(ResetLifecycleState.Inactive);
        return true;
    }

    /// <summary>会话终止时取消尚未完成的 Reset，并清除提示。</summary>
    public void CancelReset(string reason)
    {
        if (state == ResetLifecycleState.Inactive || state == ResetLifecycleState.Cancelled)
        {
            return;
        }

        resetReason = string.IsNullOrWhiteSpace(reason) ? "reset_cancelled" : reason;
        IsPaused = false;
        activePlan = null;
        TransitionTo(ResetLifecycleState.Cancelled);
    }

    private ResetEventInfo CreateEventInfo()
    {
        return new ResetEventInfo(activePlan, resetReason);
    }

    private void TransitionTo(ResetLifecycleState nextState)
    {
        if (state == nextState)
        {
            return;
        }

        state = nextState;
        StateChanged?.Invoke(state);
    }

    private static Vector2 NormalizeForward(Vector2 forward)
    {
        return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
    }

    private RecoveryHeadingPlan CreateLegacyForwardPlan(PhysicalRiskState riskState)
    {
        var currentForward = userPoseTracker != null
            ? NormalizeForward(userPoseTracker.PhysicalForwardXZ)
            : Vector2.up;
        var samples = new[]
        {
            CreateLegacySample(0f, currentForward, "forward", riskState != null ? riskState.clearance_forward : 0f, riskState),
            CreateLegacySample(-90f, new Vector2(-currentForward.y, currentForward.x), "left", riskState != null ? riskState.clearance_left : 0f, riskState),
            CreateLegacySample(90f, new Vector2(currentForward.y, -currentForward.x), "right", riskState != null ? riskState.clearance_right : 0f, riskState)
        };
        return compatibilityPlanner.Plan(samples);
    }

    private HeadingSample CreateLegacySample(
        float relativeYawDegrees,
        Vector2 physicalForward,
        string direction,
        float clearanceMeters,
        PhysicalRiskState riskState)
    {
        var safety = clearanceMeters <= 0f || float.IsNaN(clearanceMeters) || float.IsInfinity(clearanceMeters)
            ? HeadingSafety.Critical
            : Contains(riskState != null ? riskState.unsafe_directions : null, direction)
                ? HeadingSafety.Unsafe
                : Contains(riskState != null ? riskState.safe_directions : null, direction)
                    ? HeadingSafety.Safe
                    : riskEvaluator != null && riskEvaluator.IsCriticalClearance(clearanceMeters)
                        ? HeadingSafety.Critical
                        : HeadingSafety.Unknown;
        return new HeadingSample(relativeYawDegrees, physicalForward, clearanceMeters, safety);
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<string> values, string expected)
    {
        if (values == null)
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

}
