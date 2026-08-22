using System;
using UnityEngine;

/// <summary>
/// 表示 SafeHeadingMapping Reset 的软件生命周期。
/// </summary>
public enum ResetLifecycleState
{
    /// <summary>当前没有 Reset。</summary>
    Inactive,
    /// <summary>已收到 Reset 要求，正在计算安全朝向。</summary>
    Required,
    /// <summary>等待用户原地转向固定的安全 Physical Forward。</summary>
    TurningToSafeHeading,
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
    /// <summary>本次 Reset 的稳定唯一标识，供事件与逐帧 telemetry 关联。</summary>
    public string ResetId { get; }
    /// <summary>协调层选择并冻结的恢复航向计划。</summary>
    public RecoveryHeadingPlan RecoveryPlan { get; }
    /// <summary>相对恢复开始朝向的有符号偏航角，单位：度；负值为左转。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>参与者需要完成的大致转角，单位：度。</summary>
    public float ApproximateTurnDegrees => Mathf.Abs(RelativeYawDegrees);
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward { get; }
    /// <summary>本次 Reset 使用的 mapping 策略。</summary>
    public RdwResetMappingStrategy MappingStrategy { get; }
    /// <summary>本次 Reset 实际要求的 Physical 转角，单位：度。</summary>
    public float PhysicalTurnDegrees { get; }
    /// <summary>本次 Reset 实际注入的 Virtual Root yaw，单位：度。</summary>
    public float InjectedYawDegrees { get; }
    /// <summary>本次 Reset 预计产生的 Virtual 总偏航，单位：度。</summary>
    public float TotalVirtualYawDegrees { get; }
    /// <summary>触发本次 Reset 的原因。</summary>
    public string Reason { get; }
    /// <summary>触发本次 Reset 的碰撞预测 ID。</summary>
    public string PredictionId { get; }
    /// <summary>当前锁定目标经历的 Reset 代际。</summary>
    public int ResetGeneration { get; }
    /// <summary>事件发生时的 Physical XZ 位置，单位：米。</summary>
    public Vector2 PhysicalPosition { get; }
    /// <summary>事件发生时的 Physical Forward。</summary>
    public Vector2 PhysicalForward { get; }
    /// <summary>Reset 从开始到该事件的持续时间，单位：秒。</summary>
    public float ResetDurationSeconds { get; }

    /// <summary>使用协调层冻结的恢复计划创建不可变 Reset 事件。</summary>
    public ResetEventInfo(
        RecoveryHeadingPlan recoveryPlan,
        string reason,
        float? relativeYawOverride = null,
        Vector2? safePhysicalForwardOverride = null,
        string resetId = null,
        RdwResetMappingStrategy mappingStrategy = RdwResetMappingStrategy.SafeHeadingMapping,
        float? physicalTurnOverride = null,
        float? injectedYawOverride = null,
        float? totalVirtualYawOverride = null,
        string predictionId = null,
        int resetGeneration = 0,
        Vector2 physicalPosition = default(Vector2),
        Vector2 physicalForward = default(Vector2),
        float resetDurationSeconds = 0f)
    {
        ResetId = resetId ?? string.Empty;
        RecoveryPlan = recoveryPlan;
        RelativeYawDegrees = relativeYawOverride ??
                             (recoveryPlan != null && recoveryPlan.IsSuccess
                                 ? recoveryPlan.RelativeYawDegrees
                                 : 0f);
        SafePhysicalForward = safePhysicalForwardOverride ??
                              (recoveryPlan != null && recoveryPlan.IsSuccess
                                  ? recoveryPlan.SafePhysicalForward
                                  : Vector2.up);
        MappingStrategy = mappingStrategy;
        PhysicalTurnDegrees = physicalTurnOverride ?? ApproximateTurnDegrees;
        InjectedYawDegrees = injectedYawOverride ?? 0f;
        TotalVirtualYawDegrees = totalVirtualYawOverride ??
                                 PhysicalTurnDegrees + InjectedYawDegrees;
        Reason = reason ?? string.Empty;
        PredictionId = predictionId ?? string.Empty;
        ResetGeneration = Math.Max(0, resetGeneration);
        PhysicalPosition = physicalPosition;
        PhysicalForward = physicalForward.sqrMagnitude > 0.0001f
            ? physicalForward.normalized
            : Vector2.up;
        ResetDurationSeconds = Mathf.Max(0f, resetDurationSeconds);
    }
}

/// <summary>
/// 执行 SafeHeadingMapping Reset：停止 Guidance/Gain 表现并执行协调层冻结的 Physical Forward，
/// 在 Physical heading 与 mapping target 均完成后发布 ResetCompleted。航向选择、复验与重规划由协调层处理。
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
    [Header("安全航向")]
    [Tooltip("用户朝向与安全目标的最大完成误差，单位：度。")]
    [SerializeField] private float completionToleranceDegrees = 12f;

    [Header("Mapping Reset")]
    [Tooltip("运行时使用的 Mapping Resetter；FixedTwoOneTurn 固定 Physical 180 度转身并实现 Virtual 360 度。")]
    [SerializeField] private RdwResetMappingStrategy mappingStrategy = RdwResetMappingStrategy.SafeHeadingMapping;
    [Tooltip("每度 Physical yaw 请求注入的 Virtual Root yaw 倍率。")]
    [SerializeField, Min(0f)] private float mappingInjectionRatio = 1f;
    [Tooltip("累计 applied yaw 距离目标不超过该值时视为 mapping 完成，单位：度。")]
    [SerializeField, Min(0.01f)] private float mappingCompletionToleranceDegrees = 0.25f;
    [Tooltip("单次 Mapping Reset 的最长执行时间，单位：秒。")]
    [SerializeField, Min(0.1f)] private float mappingMaxDurationSeconds = 12f;

    private ResetLifecycleState state = ResetLifecycleState.Inactive;
    private string resetReason = string.Empty;
    private string activeResetId = string.Empty;
    private string activePredictionId = string.Empty;
    private int activeResetGeneration;
    private float activeResetStartedAt;
    private Vector2 safePhysicalForward = Vector2.up;
    private RecoveryHeadingPlan activePlan;
    private bool mappingResetEnabled;
    private bool physicalHeadingReached;
    private bool mappingFailureRaised;
    private IRdwResetter activeResetter;
    private RdwResetPlan activeMappingPlan;

    /// <summary>Reset 状态发生变化时触发。</summary>
    public event Action<ResetLifecycleState> StateChanged;
    /// <summary>选定安全朝向并开始等待用户转向时触发。</summary>
    public event Action<ResetEventInfo> ResetStarted;
    /// <summary>Physical heading 与 mapping target 均完成时触发。</summary>
    public event Action<ResetEventInfo> ResetCompleted;
    /// <summary>Reset 被取消时触发，携带与开始/完成相同的 Reset 标识。</summary>
    public event Action<ResetEventInfo> ResetCancelled;
    /// <summary>Mapping Reset 超时或内部终止时触发，由 GuidanceCoordinator 决定 Hold 或 HardStop。</summary>
    public event Action<string> MappingFailed;

    /// <summary>当前是否因 Reset 暂停导览。</summary>
    public bool IsPaused { get; private set; }
    /// <summary>当前是否仍有待完成或待风险复验的 Reset。</summary>
    public bool IsActive => state == ResetLifecycleState.Required ||
                            state == ResetLifecycleState.TurningToSafeHeading ||
                            state == ResetLifecycleState.Completed;
    /// <summary>当前 Reset 生命周期状态。</summary>
    public ResetLifecycleState State => state;
    /// <summary>本次 Reset 固定的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward => safePhysicalForward;
    /// <summary>本次 Reset 正在执行的冻结恢复计划。</summary>
    public RecoveryHeadingPlan ActivePlan => activePlan;
    /// <summary>当前 Mapping Reset 计划；尚未收到首个有效 RDW 帧时为空。</summary>
    public RdwResetPlan ActiveMappingPlan => activeMappingPlan;
    /// <summary>当前 Mapping 实际要求到达的 Physical Room 目标朝向。</summary>
    public Vector2 MappingTargetPhysicalForward => activeMappingPlan != null
        ? RdwYawConvention.YawToForward(activeMappingPlan.TargetPhysicalHeading)
        : safePhysicalForward;
    /// <summary>当前 mapping 是否已接入 RDW 帧事务。</summary>
    public bool MappingResetEnabled => mappingResetEnabled;
    /// <summary>当前是否使用固定 Physical 180 度到 Virtual 360 度模式。</summary>
    public bool IsFixedTwoOneTurn => mappingStrategy == RdwResetMappingStrategy.FixedTwoOneTurn;
    /// <summary>当前用户绝对 Physical heading 是否已进入目标容差。</summary>
    public bool PhysicalHeadingReached => physicalHeadingReached;
    /// <summary>当前 Resetter 是否已累计完成 mapping target。</summary>
    public bool IsMappingComplete => !mappingResetEnabled ||
                                     (activeResetter != null && activeResetter.IsMappingComplete);
    /// <summary>当前 Mapping Reset 的累计进度。</summary>
    public RdwResetProgress MappingProgress
    {
        get
        {
            if (!mappingResetEnabled ||
                (state == ResetLifecycleState.Inactive && activeMappingPlan == null))
            {
                return default;
            }

            return new RdwResetProgress(
                activeMappingPlan ?? activeResetter?.Plan,
                activeResetter != null && activeResetter.IsActive,
                physicalHeadingReached,
                IsMappingComplete,
                activeResetter != null ? activeResetter.AccumulatedPhysicalTurn : 0f,
                activeResetter != null ? activeResetter.AccumulatedInjectedYaw : 0f,
                activeResetter != null ? activeResetter.LastPoseSequence : 0,
                activeResetter != null ? activeResetter.StatusReason : string.Empty);
        }
    }
    /// <summary>当前 Physical Forward 到安全目标的剩余角度，单位：度。</summary>
    public float RemainingYawDegrees => userPoseTracker == null
        ? 180f
        : Vector2.Angle(userPoseTracker.PhysicalForwardXZ, MappingTargetPhysicalForward);

    /// <summary>
    /// 预测从当前 Physical Forward 转到目标朝向后，Virtual Forward 的总偏航变化。
    /// 该值包含用户真实转身与 Mapping Reset 注入的 Virtual Root yaw。
    /// </summary>
    public float PredictVirtualYawDelta(
        Vector2 currentPhysicalForward,
        Vector2 targetPhysicalForward)
    {
        var physicalTurn = RdwYawConvention.SignedDeltaYaw(
            NormalizeForward(currentPhysicalForward),
            NormalizeForward(targetPhysicalForward));
        var injectedYaw = mappingStrategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? DirectionSign(physicalTurn) * 180f
            : physicalTurn * Mathf.Max(0f, mappingInjectionRatio);
        var effectivePhysicalTurn = mappingStrategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? DirectionSign(physicalTurn) * 180f
            : physicalTurn;
        return effectivePhysicalTurn + injectedYaw;
    }

    private void Update()
    {
        if (state == ResetLifecycleState.TurningToSafeHeading && userPoseTracker != null)
        {
            ProcessPhysicalForward(userPoseTracker.PhysicalForwardXZ);
        }
    }

    /// <summary>
    /// 注入 Reset 使用的表现、Gain 与 Physical Pose 依赖。
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
        // evaluator 仅保留调用签名；航向搜索与终验统一由 GuidanceCoordinator 负责。
    }

    /// <summary>
    /// 启用 Step 2B Mapping Reset；生产组合根调用一次，测试可注入指定纯计算 Resetter。
    /// </summary>
    public void ConfigureMappingReset(IRdwResetter resetter = null, bool enabled = true)
    {
        mappingResetEnabled = enabled;
        activeResetter = resetter ?? CreateResetter(mappingStrategy);
        activeMappingPlan = null;
        physicalHeadingReached = false;
        mappingFailureRaised = false;
    }

    /// <summary>
    /// 使用已冻结的恢复航向立即停止表现并启动安全转向；重复风险刷新不会重启活动 Reset。
    /// </summary>
    /// <param name="reason">触发 Reset 的安全原因。</param>
    /// <param name="plan">由协调层选择并冻结的恢复航向。</param>
    /// <returns>成功启动恢复时返回 true。</returns>
    public bool PauseForReset(string reason, RecoveryHeadingPlan plan)
    {
        // 自动连续模式禁止无 CollisionPrediction 的旧入口启动 Reset。
        return false;
    }

    /// <summary>
    /// 使用新的碰撞预测启动独立 Reset；活动 Reset 期间重复调用保持幂等。
    /// </summary>
    /// <param name="reason">触发 Reset 的诊断原因。</param>
    /// <param name="plan">只依据物理净空选择的冻结恢复航向。</param>
    /// <param name="prediction">触发本次 Reset 的新碰撞预测。</param>
    /// <param name="resetGeneration">当前锁定目标的 Reset 代际。</param>
    /// <returns>已处于或成功进入 Reset 时返回 true。</returns>
    public bool PauseForReset(
        string reason,
        RecoveryHeadingPlan plan,
        CollisionPrediction prediction,
        int resetGeneration)
    {
        if (plan == null || !plan.IsSuccess || prediction == null || !prediction.IsValid)
        {
            return false;
        }

        if (state == ResetLifecycleState.Required ||
            state == ResetLifecycleState.TurningToSafeHeading)
        {
            // 已提交的 Reset 航向保持不变；新风险由 Reset 完成后的统一复验处理。
            return true;
        }

        IsPaused = true;
        rotationGainController?.AbortGain("reset_required");
        guidanceExecutor?.Clear();

        // 新 frozen heading 必须创建全新的 ResetPlan，不能沿用上一条 delta chain。
        InvalidateMappingChain("reset_plan_replaced");
        physicalHeadingReached = false;
        mappingFailureRaised = false;

        if (state == ResetLifecycleState.TurningToSafeHeading || state == ResetLifecycleState.Required)
        {
            activePlan = plan;
            safePhysicalForward = plan.SafePhysicalForward;
            ResetStarted?.Invoke(CreateEventInfo());
            return true;
        }

        resetReason = string.IsNullOrWhiteSpace(reason) ? "reset_required" : reason;
        activeResetId = Guid.NewGuid().ToString("N");
        activePredictionId = prediction != null ? prediction.PredictionId : string.Empty;
        activeResetGeneration = Math.Max(0, resetGeneration);
        activeResetStartedAt = Time.realtimeSinceStartup;
        TransitionTo(ResetLifecycleState.Required);
        activePlan = plan;
        safePhysicalForward = plan.SafePhysicalForward;
        TransitionTo(ResetLifecycleState.TurningToSafeHeading);
        ResetStarted?.Invoke(CreateEventInfo());
        return true;
    }

    /// <summary>
    /// 处理一份校准后的 Physical Forward；进入安全目标容差后完成 Reset。
    /// </summary>
    /// <param name="currentForward">当前 Physical Room 二维朝向。</param>
    public void ProcessPhysicalForward(Vector2 currentForward)
    {
        if (state != ResetLifecycleState.TurningToSafeHeading)
        {
            return;
        }

        currentForward = NormalizeForward(currentForward);
        physicalHeadingReached = IsPhysicalMappingTargetReached(currentForward);
        TryCompleteReset();
    }

    /// <summary>
    /// 在首个有效 Reset-owner 帧冻结起始 yaw，并创建本次不可变 RdwResetPlan。
    /// </summary>
    public bool TryInitializeMapping(in RdwPoseFrame pose)
    {
        if (!mappingResetEnabled || state != ResetLifecycleState.TurningToSafeHeading ||
            activePlan == null || !activePlan.IsSuccess || !pose.IsValid)
        {
            return false;
        }

        if (activeMappingPlan != null && activeResetter != null)
        {
            return true;
        }

        activeResetter = activeResetter ?? CreateResetter(mappingStrategy);
        activeMappingPlan = RdwResetPlan.Create(
            activePlan,
            pose,
            mappingStrategy,
            mappingInjectionRatio,
            mappingCompletionToleranceDegrees,
            mappingMaxDurationSeconds,
            activeResetId);
        if (activeMappingPlan == null)
        {
            return false;
        }

        activeResetter.Initialize(activeMappingPlan, pose);
        mappingFailureRaised = false;
        physicalHeadingReached = IsPhysicalMappingTargetReached(pose.PhysicalForwardXZ);
        return true;
    }

    /// <summary>由 RDW 帧事务请求当前 Resetter 计算 mapping command。</summary>
    public RdwGainCommand EvaluateMapping(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry)
    {
        if (!TryInitializeMapping(pose) || activeResetter == null)
        {
            return ResetZero(pose.Sequence, "reset_mapping_waiting_for_valid_plan");
        }

        var command = activeResetter.Evaluate(pose, geometry);
        if (!activeResetter.IsActive && !activeResetter.IsMappingComplete &&
            !mappingFailureRaised && activeMappingPlan != null)
        {
            mappingFailureRaised = true;
            MappingFailed?.Invoke(activeResetter.StatusReason);
        }

        return command;
    }

    /// <summary>
    /// 仅提交 GainApplier 的实际结果，并在 physical 与 mapping 两个目标都完成后结束 Reset。
    /// </summary>
    public void CommitMapping(in RdwGainApplyResult applyResult, Vector2 currentPhysicalForward)
    {
        if (!mappingResetEnabled || activeResetter == null ||
            state != ResetLifecycleState.TurningToSafeHeading)
        {
            return;
        }

        activeResetter.Commit(applyResult);
        if (!applyResult.WasAccepted && !mappingFailureRaised)
        {
            mappingFailureRaised = true;
            MappingFailed?.Invoke(string.IsNullOrWhiteSpace(activeResetter.StatusReason)
                ? "reset_gain_not_applied"
                : activeResetter.StatusReason);
            return;
        }

        physicalHeadingReached = IsPhysicalMappingTargetReached(currentPhysicalForward);
        TryCompleteReset();
    }

    /// <summary>
    /// tracking/calibration/recenter/pose jump 后取消当前命令并丢弃 delta chain；恢复后从新帧创建新计划。
    /// </summary>
    public void InvalidateMappingChain(string reason)
    {
        activeResetter?.Cancel(reason);
        activeMappingPlan = null;
        physicalHeadingReached = false;
        mappingFailureRaised = false;
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
        activeMappingPlan = null;
        activeResetId = string.Empty;
        activePredictionId = string.Empty;
        activeResetGeneration = 0;
        activeResetStartedAt = 0f;
        TransitionTo(ResetLifecycleState.Inactive);
        return true;
    }

    /// <summary>会话终止时取消尚未完成的 Reset，并清除提示。</summary>
    public void CancelReset(string reason, bool preservePause = false)
    {
        if (state == ResetLifecycleState.Inactive || state == ResetLifecycleState.Cancelled)
        {
            return;
        }

        resetReason = string.IsNullOrWhiteSpace(reason) ? "reset_cancelled" : reason;
        IsPaused = preservePause;
        ResetCancelled?.Invoke(CreateEventInfo());
        InvalidateMappingChain(resetReason);
        activePlan = null;
        activeResetId = string.Empty;
        activePredictionId = string.Empty;
        activeResetGeneration = 0;
        activeResetStartedAt = 0f;
        TransitionTo(ResetLifecycleState.Cancelled);
    }

    private void TryCompleteReset()
    {
        if (state != ResetLifecycleState.TurningToSafeHeading ||
            !physicalHeadingReached || !IsMappingComplete)
        {
            return;
        }

        TransitionTo(ResetLifecycleState.Completed);
        ResetCompleted?.Invoke(CreateEventInfo());
    }

    private static IRdwResetter CreateResetter(RdwResetMappingStrategy strategy)
    {
        return strategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? (IRdwResetter)new TwoOneTurnLikeResetter()
            : new SafeHeadingMappingResetter();
    }

    private static RdwGainCommand ResetZero(long poseSequence, string reason)
    {
        return new RdwGainCommand(
            0f, 0f, Vector2.zero, RdwGainOwner.Resetter, reason, poseSequence);
    }

    /// <summary>创建当前 Reset 的不可变事件快照，供 UI、日志和恢复链路复用。</summary>
    public ResetEventInfo CreateEventInfo(string reasonOverride = null)
    {
        var strategy = activeMappingPlan != null ? activeMappingPlan.MappingStrategy : mappingStrategy;
        var direction = DirectionSign(activeMappingPlan != null
            ? activeMappingPlan.TargetPhysicalTurnDelta
            : activePlan != null ? activePlan.RelativeYawDegrees : 0f);
        var relativeYawOverride = strategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? direction * 180f
            : (float?)null;
        var physicalForwardOverride = strategy == RdwResetMappingStrategy.FixedTwoOneTurn &&
                                      userPoseTracker != null
            ? activeMappingPlan != null
                ? MappingTargetPhysicalForward
                : -NormalizeForward(userPoseTracker.PhysicalForwardXZ)
            : (Vector2?)null;
        var physicalTurn = activeMappingPlan != null
            ? activeMappingPlan.TargetPhysicalTurnDelta
            : strategy == RdwResetMappingStrategy.FixedTwoOneTurn ? direction * 180f : (float?)null;
        var injectedYaw = activeMappingPlan != null
            ? activeMappingPlan.TargetInjectedYawDelta
            : strategy == RdwResetMappingStrategy.FixedTwoOneTurn ? direction * 180f : (float?)null;
        var totalVirtualYaw = strategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? direction * 360f
            : (float?)null;
        return new ResetEventInfo(
            activePlan,
            reasonOverride ?? resetReason,
            relativeYawOverride,
            physicalForwardOverride,
            activeResetId,
            strategy,
            physicalTurn,
            injectedYaw,
            totalVirtualYaw,
            activePredictionId,
            activeResetGeneration,
            userPoseTracker != null ? userPoseTracker.PhysicalPositionXZ : Vector2.zero,
            userPoseTracker != null ? userPoseTracker.PhysicalForwardXZ : Vector2.up,
            activeResetStartedAt > 0f
                ? Time.realtimeSinceStartup - activeResetStartedAt
                : 0f);
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

    private static float DirectionSign(float signedTurnDegrees)
    {
        return Mathf.Abs(signedTurnDegrees) > 0.01f ? Mathf.Sign(signedTurnDegrees) : 1f;
    }

    /// <summary>按当前 mapping 模式判断用户是否到达对应的 Physical 目标朝向。</summary>
    private bool IsPhysicalMappingTargetReached(Vector2 currentForward)
    {
        var targetForward = activeMappingPlan != null &&
                            activeMappingPlan.MappingStrategy == RdwResetMappingStrategy.FixedTwoOneTurn
            ? MappingTargetPhysicalForward
            : safePhysicalForward;
        return Vector2.Angle(NormalizeForward(currentForward), targetForward) <=
               Mathf.Max(0.1f, completionToleranceDegrees);
    }

}
