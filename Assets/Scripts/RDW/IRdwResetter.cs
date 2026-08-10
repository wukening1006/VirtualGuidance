using UnityEngine;

/// <summary>
/// 保存一次 Mapping Reset 的逐帧累计事实，供生命周期、日志与测试使用。
/// </summary>
public readonly struct RdwResetProgress
{
    /// <summary>当前或最近一次 ResetPlan。</summary>
    public RdwResetPlan Plan { get; }
    /// <summary>Resetter 是否仍接受有效 Physical yaw delta。</summary>
    public bool IsActive { get; }
    /// <summary>用户的绝对 Physical heading 是否已进入目标容差。</summary>
    public bool PhysicalHeadingReached { get; }
    /// <summary>实际 applied yaw 是否已经达到 mapping target。</summary>
    public bool IsMappingComplete { get; }
    /// <summary>本计划累计消费的有符号 Physical turn，单位：度。</summary>
    public float AccumulatedPhysicalTurn { get; }
    /// <summary>本计划累计成功注入的有符号 Virtual Root yaw，单位：度。</summary>
    public float AccumulatedInjectedYaw { get; }
    /// <summary>最近一次完成 Evaluate 的 pose sequence。</summary>
    public long LastPoseSequence { get; }
    /// <summary>取消、超时或状态变化的诊断原因。</summary>
    public string Reason { get; }
    /// <summary>快照是否关联到一份 ResetPlan。</summary>
    public bool HasPlan => Plan != null;

    /// <summary>创建不可变 Reset 进度快照。</summary>
    public RdwResetProgress(
        RdwResetPlan plan,
        bool isActive,
        bool physicalHeadingReached,
        bool isMappingComplete,
        float accumulatedPhysicalTurn,
        float accumulatedInjectedYaw,
        long lastPoseSequence,
        string reason)
    {
        Plan = plan;
        IsActive = isActive;
        PhysicalHeadingReached = physicalHeadingReached;
        IsMappingComplete = isMappingComplete;
        AccumulatedPhysicalTurn = accumulatedPhysicalTurn;
        AccumulatedInjectedYaw = accumulatedInjectedYaw;
        LastPoseSequence = lastPoseSequence;
        Reason = reason ?? string.Empty;
    }
}

/// <summary>
/// Mapping Resetter 的纯计算边界；实现不得直接操作 Transform 或任务生命周期。
/// </summary>
public interface IRdwResetter
{
    /// <summary>当前执行的冻结 ResetPlan。</summary>
    RdwResetPlan Plan { get; }
    /// <summary>当前是否接受逐帧 Physical yaw delta。</summary>
    bool IsActive { get; }
    /// <summary>实际 applied yaw 是否达到 mapping target。</summary>
    bool IsMappingComplete { get; }
    /// <summary>累计消费的有符号 Physical turn，单位：度。</summary>
    float AccumulatedPhysicalTurn { get; }
    /// <summary>累计成功注入的有符号 Virtual Root yaw，单位：度。</summary>
    float AccumulatedInjectedYaw { get; }
    /// <summary>最近处理的 pose sequence。</summary>
    long LastPoseSequence { get; }
    /// <summary>当前取消或完成原因。</summary>
    string StatusReason { get; }

    /// <summary>使用冻结计划和首个有效位姿初始化 mapping。</summary>
    void Initialize(in RdwResetPlan plan, in RdwPoseFrame pose);

    /// <summary>基于同一冻结帧计算 mapping command。</summary>
    RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry);

    /// <summary>仅按 GainApplier 的实际结果累计 injected yaw。</summary>
    void Commit(in RdwGainApplyResult applyResult);

    /// <summary>取消当前 mapping，之后不再生成非零命令。</summary>
    void Cancel(string reason);
}

/// <summary>
/// 复用逐帧序号、时限、applied yaw 累计和完成判断的 yaw mapping 基类。
/// </summary>
public abstract class RdwYawMappingResetterBase : IRdwResetter
{
    private long pendingPoseSequence;

    /// <inheritdoc />
    public RdwResetPlan Plan { get; private set; }
    /// <inheritdoc />
    public bool IsActive { get; private set; }
    /// <inheritdoc />
    public bool IsMappingComplete { get; private set; }
    /// <inheritdoc />
    public float AccumulatedPhysicalTurn { get; private set; }
    /// <inheritdoc />
    public float AccumulatedInjectedYaw { get; private set; }
    /// <inheritdoc />
    public long LastPoseSequence { get; private set; }
    /// <inheritdoc />
    public string StatusReason { get; private set; } = string.Empty;

    /// <summary>当前策略写入日志的命令原因。</summary>
    protected abstract string CommandReason { get; }

    /// <inheritdoc />
    public void Initialize(in RdwResetPlan plan, in RdwPoseFrame pose)
    {
        Plan = plan;
        AccumulatedPhysicalTurn = 0f;
        AccumulatedInjectedYaw = 0f;
        LastPoseSequence = pose.Sequence;
        pendingPoseSequence = 0;
        StatusReason = plan == null || !pose.IsValid ? "reset_plan_invalid" : "reset_mapping_started";
        IsMappingComplete = plan != null &&
                            Mathf.Abs(plan.TargetInjectedYawDelta) <= plan.MappingCompletionTolerance;
        IsActive = plan != null && pose.IsValid && !IsMappingComplete;
        if (IsMappingComplete)
        {
            StatusReason = "reset_mapping_completed";
        }
    }

    /// <inheritdoc />
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry)
    {
        if (!IsActive || Plan == null)
        {
            return Zero(pose.Sequence, IsMappingComplete ? "reset_mapping_completed" : StatusReason);
        }

        if (!pose.IsValid)
        {
            Cancel("reset_pose_invalid");
            return Zero(pose.Sequence, StatusReason);
        }

        if (pose.Sequence <= LastPoseSequence)
        {
            return Zero(pose.Sequence, "reset_pose_sequence_reused");
        }

        if (pose.Timestamp - Plan.StartTimestamp > Plan.MaxDuration)
        {
            Cancel("reset_mapping_timeout");
            return Zero(pose.Sequence, StatusReason);
        }

        LastPoseSequence = pose.Sequence;
        pendingPoseSequence = pose.Sequence;
        AccumulatedPhysicalTurn += pose.DeltaPhysicalYaw;
        // 只消费朝向冻结目标方向的真实转动；反向修正不会制造反向 mapping。
        var directedDeltaYaw = IsTowardPhysicalTarget(pose.DeltaPhysicalYaw)
            ? pose.DeltaPhysicalYaw
            : 0f;
        var requestedYaw = ClampToRemainingTarget(CalculateRequestedYaw(directedDeltaYaw));
        return new RdwGainCommand(
            requestedYaw,
            0f,
            Vector2.zero,
            RdwGainOwner.Resetter,
            CommandReason,
            pose.Sequence);
    }

    /// <inheritdoc />
    public void Commit(in RdwGainApplyResult applyResult)
    {
        if (Plan == null || pendingPoseSequence == 0 ||
            applyResult.Command.PoseSequence != pendingPoseSequence)
        {
            return;
        }

        pendingPoseSequence = 0;
        if (!applyResult.WasAccepted)
        {
            Cancel("reset_gain_not_applied");
            return;
        }

        AccumulatedInjectedYaw += applyResult.AppliedYawDeltaDeg;
        if (Mathf.Abs(Plan.TargetInjectedYawDelta - AccumulatedInjectedYaw) <=
            Plan.MappingCompletionTolerance)
        {
            AccumulatedInjectedYaw = Plan.TargetInjectedYawDelta;
            IsMappingComplete = true;
            IsActive = false;
            StatusReason = "reset_mapping_completed";
        }
    }

    /// <inheritdoc />
    public void Cancel(string reason)
    {
        IsActive = false;
        IsMappingComplete = false;
        pendingPoseSequence = 0;
        StatusReason = string.IsNullOrWhiteSpace(reason) ? "reset_mapping_cancelled" : reason;
    }

    /// <summary>由具体策略把 Physical yaw delta 转换为请求注入 yaw。</summary>
    protected abstract float CalculateRequestedYaw(float physicalDeltaYaw);

    private bool IsTowardPhysicalTarget(float deltaYaw)
    {
        if (Mathf.Abs(deltaYaw) <= 0.0001f || Plan == null ||
            Mathf.Abs(Plan.TargetPhysicalTurnDelta) <= Plan.MappingCompletionTolerance)
        {
            return false;
        }

        return Mathf.Sign(deltaYaw) == Mathf.Sign(Plan.TargetPhysicalTurnDelta);
    }

    private float ClampToRemainingTarget(float requestedYaw)
    {
        var remaining = Plan.TargetInjectedYawDelta - AccumulatedInjectedYaw;
        if (Mathf.Abs(remaining) <= Plan.MappingCompletionTolerance)
        {
            return 0f;
        }

        if (Mathf.Sign(requestedYaw) == Mathf.Sign(remaining) &&
            Mathf.Abs(requestedYaw) > Mathf.Abs(remaining))
        {
            return remaining;
        }

        return requestedYaw;
    }

    private static RdwGainCommand Zero(long poseSequence, string reason)
    {
        return new RdwGainCommand(
            0f,
            0f,
            Vector2.zero,
            RdwGainOwner.Resetter,
            reason,
            poseSequence);
    }
}
