using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 表示恢复航向规划失败的明确原因。
/// </summary>
public enum RecoveryHeadingFailure
{
    /// <summary>规划成功。</summary>
    None,
    /// <summary>Heading Profile 缺失或不包含有效采样。</summary>
    InvalidHeadingProfile,
    /// <summary>所有采样均低于恢复所需的最低安全等级。</summary>
    NoAcceptableHeading
}

/// <summary>
/// 保存一次不可变的恢复航向决策；物理朝向在恢复期间保持冻结。
/// </summary>
public sealed class RecoveryHeadingPlan
{
    /// <summary>规划是否成功。</summary>
    public bool IsSuccess => Failure == RecoveryHeadingFailure.None;
    /// <summary>规划失败原因。</summary>
    public RecoveryHeadingFailure Failure { get; }
    /// <summary>相对规划时 Physical Forward 的有符号偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>冻结的 Physical Room 目标朝向。</summary>
    public Vector2 SafePhysicalForward { get; }
    /// <summary>规划时该朝向的走廊净空，单位：米。</summary>
    public float ClearanceMeters { get; }
    /// <summary>规划时该朝向的安全等级。</summary>
    public HeadingSafety Safety { get; }
    /// <summary>用于诊断选择结果的综合评分。</summary>
    public float Score { get; }

    private RecoveryHeadingPlan(RecoveryHeadingFailure failure)
    {
        Failure = failure;
        RelativeYawDegrees = 0f;
        SafePhysicalForward = Vector2.up;
        ClearanceMeters = 0f;
        Safety = HeadingSafety.Critical;
        Score = float.NegativeInfinity;
    }

    /// <summary>创建一条成功的冻结恢复航向计划。</summary>
    public RecoveryHeadingPlan(HeadingSample sample, float score)
    {
        Failure = RecoveryHeadingFailure.None;
        RelativeYawDegrees = Mathf.DeltaAngle(0f, sample.RelativeYawDegrees);
        SafePhysicalForward = sample.PhysicalForward;
        ClearanceMeters = sample.ClearanceMeters;
        Safety = sample.Safety;
        Score = score;
    }

    /// <summary>创建一条包含明确原因的失败结果。</summary>
    public static RecoveryHeadingPlan Failed(RecoveryHeadingFailure failure)
    {
        return new RecoveryHeadingPlan(failure == RecoveryHeadingFailure.None
            ? RecoveryHeadingFailure.InvalidHeadingProfile
            : failure);
    }
}

/// <summary>
/// 使用净空、语义方向、转角和方向抖动成本选择安全恢复航向。
/// </summary>
public sealed class RecoveryHeadingPlanner
{
    private readonly float clearanceWeight;
    private readonly float semanticAlignmentWeight;
    private readonly float turnAngleCostWeight;
    private readonly float directionChangeCostWeight;

    /// <summary>创建恢复航向规划器，并设置各评分项权重。</summary>
    public RecoveryHeadingPlanner(
        float clearanceWeight = 1f,
        float semanticAlignmentWeight = 0.75f,
        float turnAngleCostWeight = 0.35f,
        float directionChangeCostWeight = 0.4f)
    {
        this.clearanceWeight = Mathf.Max(0f, clearanceWeight);
        this.semanticAlignmentWeight = Mathf.Max(0f, semanticAlignmentWeight);
        this.turnAngleCostWeight = Mathf.Max(0f, turnAngleCostWeight);
        this.directionChangeCostWeight = Mathf.Max(0f, directionChangeCostWeight);
    }

    /// <summary>
    /// 从全部 Heading Profile 采样中选择恢复朝向；硬安全约束始终优先于评分。
    /// </summary>
    /// <param name="headingProfile">最新多角度物理净空事实。</param>
    /// <param name="semanticTargetYawDegrees">可选的语义目标相对偏航，单位：度。</param>
    /// <param name="previousPlan">上一次恢复计划，用于抑制左右方向抖动。</param>
    public RecoveryHeadingPlan Plan(
        IReadOnlyList<HeadingSample> headingProfile,
        float? semanticTargetYawDegrees = null,
        RecoveryHeadingPlan previousPlan = null)
    {
        if (headingProfile == null || headingProfile.Count == 0)
        {
            return RecoveryHeadingPlan.Failed(RecoveryHeadingFailure.InvalidHeadingProfile);
        }

        var found = false;
        var bestSample = default(HeadingSample);
        var bestScore = float.NegativeInfinity;
        foreach (var sample in headingProfile)
        {
            if (!IsFinite(sample.RelativeYawDegrees) ||
                !IsFinite(sample.ClearanceMeters) ||
                !PhysicalRiskEvaluator.IsRecoveryHeadingAcceptable(sample))
            {
                continue;
            }

            var score = Score(sample, semanticTargetYawDegrees, previousPlan);
            if (!found || score > bestScore)
            {
                found = true;
                bestSample = sample;
                bestScore = score;
            }
        }

        return found
            ? new RecoveryHeadingPlan(bestSample, bestScore)
            : RecoveryHeadingPlan.Failed(RecoveryHeadingFailure.NoAcceptableHeading);
    }

    private float Score(
        HeadingSample sample,
        float? semanticTargetYawDegrees,
        RecoveryHeadingPlan previousPlan)
    {
        var normalizedTurn = Mathf.Abs(Mathf.DeltaAngle(0f, sample.RelativeYawDegrees)) / 180f;
        var semanticBenefit = 0f;
        if (semanticTargetYawDegrees.HasValue)
        {
            semanticBenefit = 1f -
                              Mathf.Abs(Mathf.DeltaAngle(
                                  sample.RelativeYawDegrees,
                                  semanticTargetYawDegrees.Value)) / 180f;
        }

        var directionChange = 0f;
        if (previousPlan != null && previousPlan.IsSuccess)
        {
            directionChange = Vector2.Angle(
                previousPlan.SafePhysicalForward,
                sample.PhysicalForward) / 180f;
        }

        return sample.ClearanceMeters * clearanceWeight +
               semanticBenefit * semanticAlignmentWeight -
               normalizedTurn * turnAngleCostWeight -
               directionChange * directionChangeCostWeight;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
