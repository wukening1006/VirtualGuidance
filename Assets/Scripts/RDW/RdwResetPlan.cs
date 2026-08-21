using System;
using UnityEngine;

/// <summary>
/// 定义 Reset 期间使用的 Virtual/Physical yaw mapping 策略。
/// </summary>
public enum RdwResetMappingStrategy
{
    /// <summary>固定 Physical 180 度转身并注入 Virtual 180 度，实现 Virtual 360 度。</summary>
    FixedTwoOneTurn,
    /// <summary>围绕冻结 Recovery Heading 执行配置化 mapping 的生产策略。</summary>
    SafeHeadingMapping
}

/// <summary>
/// 冻结一次 Mapping Reset 的起点、目标与配置；Resetter 不得改写该计划。
/// </summary>
public sealed class RdwResetPlan
{
    /// <summary>本次 Mapping Reset 的唯一标识。</summary>
    public string ResetId { get; }
    /// <summary>创建计划时的 pose sequence。</summary>
    public long StartPoseSequence { get; }
    /// <summary>创建计划时的单调时间戳，单位：秒。</summary>
    public double StartTimestamp { get; }
    /// <summary>计划开始时的 Physical yaw，单位：度。</summary>
    public float StartPhysicalYaw { get; }
    /// <summary>计划开始时的 Virtual yaw，单位：度。</summary>
    public float StartVirtualYaw { get; }
    /// <summary>冻结 Recovery Heading 对应的绝对 Physical yaw，单位：度。</summary>
    public float TargetPhysicalHeading { get; }
    /// <summary>从起始 Physical yaw 到目标 heading 的最短有符号转角，右转为正，单位：度。</summary>
    public float TargetPhysicalTurnDelta { get; }
    /// <summary>本次计划采用的 mapping 策略。</summary>
    public RdwResetMappingStrategy MappingStrategy { get; }
    /// <summary>每度 Physical yaw 对应的请求注入倍率。</summary>
    public float InjectionRatio { get; }
    /// <summary>本次 mapping 需要累计完成的有符号 Virtual Root yaw，单位：度。</summary>
    public float TargetInjectedYawDelta { get; }
    /// <summary>判断累计注入完成的误差容差，单位：度。</summary>
    public float MappingCompletionTolerance { get; }
    /// <summary>单次 mapping 允许的最长时长，单位：秒。</summary>
    public float MaxDuration { get; }

    /// <summary>
    /// 创建不可变 Mapping Reset 计划。
    /// </summary>
    public RdwResetPlan(
        string resetId,
        long startPoseSequence,
        double startTimestamp,
        float startPhysicalYaw,
        float startVirtualYaw,
        float targetPhysicalHeading,
        float targetPhysicalTurnDelta,
        RdwResetMappingStrategy mappingStrategy,
        float injectionRatio,
        float targetInjectedYawDelta,
        float mappingCompletionTolerance,
        float maxDuration)
    {
        ResetId = string.IsNullOrWhiteSpace(resetId) ? Guid.NewGuid().ToString("N") : resetId;
        StartPoseSequence = startPoseSequence;
        StartTimestamp = Math.Max(0d, startTimestamp);
        StartPhysicalYaw = RdwYawConvention.NormalizeYaw(startPhysicalYaw);
        StartVirtualYaw = RdwYawConvention.NormalizeYaw(startVirtualYaw);
        TargetPhysicalHeading = RdwYawConvention.NormalizeYaw(targetPhysicalHeading);
        TargetPhysicalTurnDelta = Mathf.Clamp(targetPhysicalTurnDelta, -180f, 180f);
        MappingStrategy = mappingStrategy;
        InjectionRatio = Mathf.Max(0f, injectionRatio);
        TargetInjectedYawDelta = targetInjectedYawDelta;
        MappingCompletionTolerance = Mathf.Max(0.01f, mappingCompletionTolerance);
        MaxDuration = Mathf.Max(0.1f, maxDuration);
    }

    /// <summary>
    /// 根据冻结 Recovery Heading 和首个有效 RDW 帧创建计划。
    /// </summary>
    public static RdwResetPlan Create(
        RecoveryHeadingPlan recoveryPlan,
        in RdwPoseFrame startPose,
        RdwResetMappingStrategy strategy,
        float injectionRatio,
        float mappingCompletionTolerance,
        float maxDuration,
        string resetId = null)
    {
        if (recoveryPlan == null || !recoveryPlan.IsSuccess || !startPose.IsValid)
        {
            return null;
        }

        var recoveryTargetHeading = RdwYawConvention.ForwardToYaw(recoveryPlan.SafePhysicalForward);
        var recoveryTurn = RdwYawConvention.SignedDeltaYaw(startPose.PhysicalYaw, recoveryTargetHeading);
        var fixedTwoOneTurn = strategy == RdwResetMappingStrategy.FixedTwoOneTurn;
        var targetPhysicalTurn = fixedTwoOneTurn
            ? DirectionSign(recoveryTurn) * 180f
            : recoveryTurn;
        var targetHeading = RdwYawConvention.NormalizeYaw(startPose.PhysicalYaw + targetPhysicalTurn);
        // 固定 2:1 模式只允许 1:1 的 mapping 增量；配置倍率不影响该实验条件。
        var ratio = fixedTwoOneTurn ? 1f : Mathf.Max(0f, injectionRatio);
        var targetInjectedYaw = fixedTwoOneTurn
            ? DirectionSign(targetPhysicalTurn) * 180f
            : targetPhysicalTurn * ratio;
        return new RdwResetPlan(
            resetId,
            startPose.Sequence,
            startPose.Timestamp,
            startPose.PhysicalYaw,
            startPose.VirtualYaw,
            targetHeading,
            targetPhysicalTurn,
            strategy,
            ratio,
            targetInjectedYaw,
            mappingCompletionTolerance,
            maxDuration);
    }

    private static float DirectionSign(float signedTurnDegrees)
    {
        return Mathf.Abs(signedTurnDegrees) > 0.01f ? Mathf.Sign(signedTurnDegrees) : 1f;
    }
}
