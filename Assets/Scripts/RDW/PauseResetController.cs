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
    /// <summary>本次 Reset 选择的相对方向。</summary>
    public string SafeDirection { get; }
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward { get; }
    /// <summary>触发本次 Reset 的原因。</summary>
    public string Reason { get; }

    /// <summary>创建一条不可变 Reset 事件。</summary>
    public ResetEventInfo(string safeDirection, Vector2 safePhysicalForward, string reason)
    {
        SafeDirection = safeDirection ?? string.Empty;
        SafePhysicalForward = safePhysicalForward;
        Reason = reason ?? string.Empty;
    }
}

/// <summary>
/// 执行 Turn-to-Safe-Direction Reset：停止 Guidance/Gain 表现，选择安全 Physical Forward，
/// 并在用户原地转入容差后发布 ResetCompleted。任务恢复与 Replanning 由实验协调层处理。
/// </summary>
public sealed class PauseResetController : MonoBehaviour
{
    [Header("Reset 依赖")]
    [Tooltip("Reset 开始时需要清除的 Guidance 表现。")]
    [SerializeField] private GuidanceExecutor guidanceExecutor;
    [Tooltip("向参与者显示中性安全提示的面板。")]
    [SerializeField] private RejectionPanel rejectionPanel;
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
    /// <summary>本次 Reset 选择的 forward、left 或 right。</summary>
    public string SafeDirection => safeDirection;
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward => safePhysicalForward;
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
        RejectionPanel panel,
        RotationGainController gainController = null,
        UserPoseTracker poseTracker = null,
        PhysicalRiskEvaluator evaluator = null)
    {
        if (executor != null) guidanceExecutor = executor;
        if (panel != null) rejectionPanel = panel;
        if (gainController != null) rotationGainController = gainController;
        if (poseTracker != null) userPoseTracker = poseTracker;
        if (evaluator != null) riskEvaluator = evaluator;
    }

    /// <summary>
    /// 立即停止可能改变用户朝向的表现并启动安全转向；重复风险刷新不会重启活动 Reset。
    /// </summary>
    /// <param name="reason">触发 Reset 的安全原因。</param>
    /// <param name="riskState">触发时风险快照；为空时使用风险计算器最近状态。</param>
    public void PauseForReset(string reason, PhysicalRiskState riskState = null)
    {
        IsPaused = true;
        rotationGainController?.AbortGain("reset_required");
        guidanceExecutor?.Clear();

        if (state == ResetLifecycleState.TurningToSafeDirection || state == ResetLifecycleState.Required)
        {
            return;
        }

        resetReason = string.IsNullOrWhiteSpace(reason) ? "reset_required" : reason;
        TransitionTo(ResetLifecycleState.Required);
        SelectSafeDirection(riskState ?? riskEvaluator?.CurrentRiskState);
        rejectionPanel?.Show(BuildInstruction(safeDirection));
        TransitionTo(ResetLifecycleState.TurningToSafeDirection);
        ResetStarted?.Invoke(CreateEventInfo());
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
        rejectionPanel?.Show("安全转向已完成，正在重新评估空间风险。");
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
        rejectionPanel?.Hide();
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
        rejectionPanel?.Hide();
        TransitionTo(ResetLifecycleState.Cancelled);
    }

    private void SelectSafeDirection(PhysicalRiskState riskState)
    {
        var currentForward = userPoseTracker != null
            ? NormalizeForward(userPoseTracker.PhysicalForwardXZ)
            : Vector2.up;
        safeDirection = "forward";
        var bestClearance = riskState != null ? riskState.clearance_forward : 0f;

        if (riskState != null && riskState.clearance_left > bestClearance)
        {
            safeDirection = "left";
            bestClearance = riskState.clearance_left;
        }

        if (riskState != null && riskState.clearance_right > bestClearance)
        {
            safeDirection = "right";
        }

        if (safeDirection == "left")
        {
            safePhysicalForward = new Vector2(-currentForward.y, currentForward.x);
        }
        else if (safeDirection == "right")
        {
            safePhysicalForward = new Vector2(currentForward.y, -currentForward.x);
        }
        else
        {
            safePhysicalForward = currentForward;
        }
    }

    private ResetEventInfo CreateEventInfo()
    {
        return new ResetEventInfo(safeDirection, safePhysicalForward, resetReason);
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

    private static string BuildInstruction(string direction)
    {
        if (direction == "left") return "请停止移动并原地缓慢向左转向安全方向。";
        if (direction == "right") return "请停止移动并原地缓慢向右转向安全方向。";
        return "请停止移动并保持当前安全朝向。";
    }
}
