using System;
using UnityEngine;

/// <summary>
/// 为规划器提供任意 Physical 位置与连续方向的 Unity Exact Safety 纯查询。
/// </summary>
public interface IRdwPlanningDirectionEvaluator
{
    /// <summary>查询冻结规划身份下的连续 Physical 方向，不发布新风险快照。</summary>
    bool TryEvaluateDirection(
        RdwPlanningSnapshot snapshot,
        Vector2 physicalPositionXZ,
        Vector2 physicalDirectionXZ,
        out HeadingSample sample);
}

/// <summary>
/// 将现有 PhysicalRiskEvaluator 的连续方向查询适配给规划层。
/// </summary>
public sealed class PhysicalRiskPlanningDirectionEvaluator : IRdwPlanningDirectionEvaluator
{
    private readonly PhysicalRiskEvaluator evaluator;

    /// <summary>创建只读 Exact Direction Safety 适配器。</summary>
    public PhysicalRiskPlanningDirectionEvaluator(PhysicalRiskEvaluator riskEvaluator)
    {
        evaluator = riskEvaluator;
    }

    /// <summary>仅在风险、校准与空间身份仍一致时执行连续方向查询。</summary>
    public bool TryEvaluateDirection(
        RdwPlanningSnapshot snapshot,
        Vector2 physicalPositionXZ,
        Vector2 physicalDirectionXZ,
        out HeadingSample sample)
    {
        sample = default;
        return snapshot != null &&
               evaluator != null &&
               evaluator.IsRiskIdentityCurrent(snapshot.RiskState) &&
               evaluator.TryEvaluateDirection(physicalPositionXZ, physicalDirectionXZ, out sample);
    }
}

/// <summary>
/// 规划层唯一依赖的 RDW 边代价估计接口；实现必须无状态且不得修改 Transform。
/// </summary>
public interface IRdwPlanningCostEstimator
{
    /// <summary>估计器稳定 ID。</summary>
    string EstimatorId { get; }
    /// <summary>估计器算法版本。</summary>
    string EstimatorVersion { get; }

    /// <summary>
    /// 基于同一冻结快照和搜索分支值状态评估一条边。
    /// </summary>
    RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability);
}

/// <summary>
/// 使用无状态 S2C 几何近似计算 RDW benefit、gain cost 与 recovery pressure。
/// </summary>
public sealed class S2CPlanningCostEstimator : IRdwPlanningCostEstimator
{
    private readonly IRdwPlanningDirectionEvaluator directionEvaluator;
    private readonly float curvatureRadiusMeters;
    private readonly float maximumClearanceMeters;
    private readonly float yawCostWeight;
    private readonly float steeringConflictWeight;
    private readonly float curvatureEffortWeight;

    /// <summary>S2C 规划估计器稳定 ID。</summary>
    public string EstimatorId => "s2c-planning";
    /// <summary>S2C 规划估计器算法版本。</summary>
    public string EstimatorVersion => "1.0.0";

    /// <summary>
    /// 创建纯查询 S2C Estimator；不会接收或调用运行中的 SteerToCenterRedirector。
    /// </summary>
    public S2CPlanningCostEstimator(
        IRdwPlanningDirectionEvaluator exactDirectionEvaluator,
        float curvatureRadius = 10f,
        float maximumClearance = 3f,
        float relativeYawWeight = 0.45f,
        float centerConflictWeight = 0.35f,
        float curvatureWeight = 0.2f)
    {
        directionEvaluator = exactDirectionEvaluator;
        curvatureRadiusMeters = Mathf.Max(0.01f, curvatureRadius);
        maximumClearanceMeters = Mathf.Max(0.01f, maximumClearance);
        var sum = Mathf.Max(0.0001f, relativeYawWeight + centerConflictWeight + curvatureWeight);
        yawCostWeight = Mathf.Max(0f, relativeYawWeight) / sum;
        steeringConflictWeight = Mathf.Max(0f, centerConflictWeight) / sum;
        curvatureEffortWeight = Mathf.Max(0f, curvatureWeight) / sum;
    }

    /// <summary>
    /// 使用连续 signed yaw、S2C center relation 和 Exact Safety 生成标准化边代价。
    /// </summary>
    public RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability)
    {
        if (snapshot == null || !snapshot.IsValid || from == null || to == null || capability == null)
        {
            return Rejected("planning_input_invalid", traversal.PhysicalPositionXZ);
        }
        if (!string.Equals(capability.EstimatorId, EstimatorId, StringComparison.Ordinal) ||
            !string.Equals(capability.EstimatorVersion, EstimatorVersion, StringComparison.Ordinal))
        {
            return Rejected("estimator_capability_mismatch", traversal.PhysicalPositionXZ);
        }

        var virtualDelta = to.VirtualPositionXZ - from.VirtualPositionXZ;
        if (virtualDelta.sqrMagnitude <= 0.000001f)
        {
            return Rejected("zero_length_edge", traversal.PhysicalPositionXZ);
        }

        var desiredVirtualForward = virtualDelta.normalized;
        var relativeYaw = SignedAngle(traversal.VirtualForwardXZ, desiredVirtualForward);
        var predictedPhysicalHeading = Rotate(traversal.PhysicalForwardXZ, relativeYaw);
        if (directionEvaluator == null ||
            !directionEvaluator.TryEvaluateDirection(
                snapshot,
                traversal.PhysicalPositionXZ,
                predictedPhysicalHeading,
                out var heading))
        {
            return new RdwEdgeEvaluation(
                relativeYaw,
                predictedPhysicalHeading,
                traversal.PhysicalPositionXZ,
                0f,
                HeadingSafety.Unknown,
                0f,
                1f,
                false,
                "exact_direction_evaluation_failed",
                1f);
        }

        var toCenter = snapshot.Geometry.SteeringCenterXZ - traversal.PhysicalPositionXZ;
        var centerDirection = toCenter.sqrMagnitude > 0.000001f
            ? toCenter.normalized
            : predictedPhysicalHeading;
        var centerAlignment = Vector2.Dot(predictedPhysicalHeading, centerDirection);
        var s2cAvailable = string.Equals(
            capability.ContinuousRedirectorType,
            "S2C",
            StringComparison.Ordinal);
        var rdwBenefit = s2cAvailable
            ? Mathf.Clamp01((centerAlignment + 1f) * 0.5f)
            : 0f;
        var yawCost = Mathf.Clamp01(Mathf.Abs(relativeYaw) / 180f);
        var centerConflict = s2cAvailable ? 1f - rdwBenefit : 0f;
        var edgeDistance = virtualDelta.magnitude;
        var curvatureEffort = s2cAvailable && capability.CurvatureGainAllowed
            ? Mathf.Clamp01(edgeDistance / curvatureRadiusMeters)
            : 0f;
        var gainCost = Mathf.Clamp01(
            yawCost * yawCostWeight +
            centerConflict * steeringConflictWeight +
            curvatureEffort * curvatureEffortWeight);
        var clearancePressure = 1f - Mathf.Clamp01(heading.ClearanceMeters / maximumClearanceMeters);
        var resetPressure = s2cAvailable
            ? Mathf.Clamp01(clearancePressure * 0.65f + centerConflict * 0.35f)
            : clearancePressure;

        // 只推进到当前精确走廊能够支持的范围；该预测仅供后续边比较，不是执行许可。
        var predictedTravel = Mathf.Min(edgeDistance, heading.ClearanceMeters * 0.8f);
        var predictedPhysicalPosition = traversal.PhysicalPositionXZ +
                                        predictedPhysicalHeading * Mathf.Max(0f, predictedTravel);
        var feasible = heading.Safety == HeadingSafety.Safe;
        return new RdwEdgeEvaluation(
            relativeYaw,
            predictedPhysicalHeading,
            predictedPhysicalPosition,
            heading.ClearanceMeters,
            heading.Safety,
            rdwBenefit,
            gainCost,
            feasible,
            feasible ? string.Empty : SafetyReason(heading.Safety),
            resetPressure);
    }

    private static RdwEdgeEvaluation Rejected(string reason, Vector2 position)
    {
        return new RdwEdgeEvaluation(
            0f,
            Vector2.up,
            position,
            0f,
            HeadingSafety.Unknown,
            0f,
            1f,
            false,
            reason,
            1f);
    }

    private static string SafetyReason(HeadingSafety safety)
    {
        switch (safety)
        {
            case HeadingSafety.Critical: return "exact_direction_critical";
            case HeadingSafety.Unsafe: return "exact_direction_unsafe";
            default: return "exact_direction_not_safe";
        }
    }

    private static float SignedAngle(Vector2 from, Vector2 to)
    {
        return Mathf.DeltaAngle(RdwYawConvention.ForwardToYaw(from), RdwYawConvention.ForwardToYaw(to));
    }

    private static Vector2 Rotate(Vector2 forward, float yawDegrees)
    {
        return RdwYawConvention.YawToForward(RdwYawConvention.ForwardToYaw(forward) + yawDegrees);
    }
}
