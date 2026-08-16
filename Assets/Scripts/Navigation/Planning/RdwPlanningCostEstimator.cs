using System;
using System.Collections.Generic;
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

/// <summary>规划估算器共用的纯边输入事实。</summary>
internal readonly struct RdwPlanningEdgeInputs
{
    public readonly Vector2 VirtualDelta;
    public readonly float RelativeYawDegrees;
    public readonly Vector2 PredictedPhysicalHeading;
    public readonly HeadingSample Heading;

    public RdwPlanningEdgeInputs(
        Vector2 virtualDelta,
        float relativeYawDegrees,
        Vector2 predictedPhysicalHeading,
        HeadingSample heading)
    {
        VirtualDelta = virtualDelta;
        RelativeYawDegrees = relativeYawDegrees;
        PredictedPhysicalHeading = predictedPhysicalHeading;
        Heading = heading;
    }
}

/// <summary>
/// 为不同 RDW 算法复用输入校验、Exact Safety 和边结果收敛；不持有运行时状态。
/// </summary>
internal static class RdwPlanningCostEstimatorMath
{
    private const float Epsilon = 0.000001f;

    public static bool TryPrepare(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability,
        IRdwPlanningDirectionEvaluator directionEvaluator,
        string estimatorId,
        string estimatorVersion,
        string expectedRedirectorType,
        out RdwPlanningEdgeInputs inputs,
        out string rejectionReason)
    {
        inputs = default(RdwPlanningEdgeInputs);
        rejectionReason = string.Empty;
        if (snapshot == null || !snapshot.IsValid || from == null || to == null || capability == null)
        {
            rejectionReason = "planning_input_invalid";
            return false;
        }
        if (!string.Equals(capability.EstimatorId, estimatorId, StringComparison.Ordinal) ||
            !string.Equals(capability.EstimatorVersion, estimatorVersion, StringComparison.Ordinal))
        {
            rejectionReason = "estimator_capability_mismatch";
            return false;
        }
        if (!string.IsNullOrEmpty(expectedRedirectorType) &&
            !string.Equals(capability.ContinuousRedirectorType, expectedRedirectorType, StringComparison.Ordinal))
        {
            rejectionReason = "redirector_capability_mismatch";
            return false;
        }

        var virtualDelta = to.VirtualPositionXZ - from.VirtualPositionXZ;
        if (virtualDelta.sqrMagnitude <= Epsilon)
        {
            rejectionReason = "zero_length_edge";
            return false;
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
            rejectionReason = "exact_direction_evaluation_failed";
            return false;
        }

        inputs = new RdwPlanningEdgeInputs(
            virtualDelta,
            relativeYaw,
            predictedPhysicalHeading,
            heading);
        return true;
    }

    public static RdwEdgeEvaluation Rejected(string reason, Vector2 position)
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

    public static RdwEdgeEvaluation Finish(
        in RdwPlanningTraversalState traversal,
        in RdwPlanningEdgeInputs inputs,
        float rdwBenefit,
        float gainCost,
        float resetPressure)
    {
        var predictedTravel = Mathf.Min(inputs.VirtualDelta.magnitude, inputs.Heading.ClearanceMeters * 0.8f);
        var predictedPhysicalPosition = traversal.PhysicalPositionXZ +
                                        inputs.PredictedPhysicalHeading * Mathf.Max(0f, predictedTravel);
        var feasible = inputs.Heading.Safety == HeadingSafety.Safe;
        return new RdwEdgeEvaluation(
            inputs.RelativeYawDegrees,
            inputs.PredictedPhysicalHeading,
            predictedPhysicalPosition,
            inputs.Heading.ClearanceMeters,
            inputs.Heading.Safety,
            rdwBenefit,
            gainCost,
            feasible,
            feasible ? string.Empty : SafetyReason(inputs.Heading.Safety),
            resetPressure);
    }

    public static float YawCost(float relativeYawDegrees)
    {
        return Mathf.Clamp01(Mathf.Abs(relativeYawDegrees) / 180f);
    }

    public static float ClearancePressure(float clearanceMeters, float maximumClearanceMeters)
    {
        return 1f - Mathf.Clamp01(clearanceMeters / Mathf.Max(0.01f, maximumClearanceMeters));
    }

    public static float CurvatureEffort(float edgeDistance, float curvatureRadiusMeters, bool allowed)
    {
        return allowed ? Mathf.Clamp01(edgeDistance / Mathf.Max(0.01f, curvatureRadiusMeters)) : 0f;
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

/// <summary>
/// 纯 S2O 规划代价估算器；以冻结 orbit geometry 做确定性 CW/CCW 选择，不读取 runtime activeOrbitSide。
/// </summary>
public sealed class S2OPlanningCostEstimator : IRdwPlanningCostEstimator
{
    private const float DirectionEpsilon = 0.000001f;
    private readonly IRdwPlanningDirectionEvaluator directionEvaluator;
    private readonly float curvatureRadiusMeters;
    private readonly float maximumClearanceMeters;
    private readonly float orbitRadiusMeters;
    private readonly float orbitBoundarySafetyMarginMeters;
    private readonly float sideSwitchHysteresisDegrees;

    /// <summary>S2O 规划估计器稳定 ID。</summary>
    public string EstimatorId => "s2o-planning";
    /// <summary>S2O 规划估计器算法版本。</summary>
    public string EstimatorVersion => "1.0.0";

    /// <summary>创建无状态 S2O 规划估计器。</summary>
    public S2OPlanningCostEstimator(
        IRdwPlanningDirectionEvaluator exactDirectionEvaluator,
        float curvatureRadius = 10f,
        float maximumClearance = 3f,
        float configuredOrbitRadius = 1.5f,
        float orbitBoundarySafetyMargin = 0.25f,
        float sideSwitchHysteresis = 12f)
    {
        directionEvaluator = exactDirectionEvaluator;
        curvatureRadiusMeters = Mathf.Max(0.01f, curvatureRadius);
        maximumClearanceMeters = Mathf.Max(0.01f, maximumClearance);
        orbitRadiusMeters = Mathf.Max(0f, configuredOrbitRadius);
        orbitBoundarySafetyMarginMeters = Mathf.Max(0f, orbitBoundarySafetyMargin);
        sideSwitchHysteresisDegrees = Mathf.Max(0f, sideSwitchHysteresis);
    }

    /// <summary>计算 S2O 轨道一致性、转向代价和恢复压力。</summary>
    public RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability)
    {
        if (!RdwPlanningCostEstimatorMath.TryPrepare(
                snapshot,
                traversal,
                from,
                to,
                capability,
                directionEvaluator,
                EstimatorId,
                EstimatorVersion,
                "S2O",
                out var inputs,
                out var rejectionReason))
        {
            return RdwPlanningCostEstimatorMath.Rejected(rejectionReason, traversal.PhysicalPositionXZ);
        }

        CalculateOrbitDirection(
            snapshot.Geometry,
            traversal.PhysicalPositionXZ,
            traversal.PhysicalForwardXZ,
            out var desiredDirection,
            out var sideConfidence,
            out var orbitPressure);
        var directionalBenefit = desiredDirection.sqrMagnitude > DirectionEpsilon
            ? Mathf.Clamp01((Vector2.Dot(inputs.PredictedPhysicalHeading, desiredDirection) + 1f) * 0.5f)
            : 0.5f;
        var benefit = Mathf.Lerp(0.5f, directionalBenefit, sideConfidence);
        var steeringConflict = Mathf.Lerp(0.25f, 1f - benefit, sideConfidence);
        var curvatureEffort = RdwPlanningCostEstimatorMath.CurvatureEffort(
            inputs.VirtualDelta.magnitude,
            curvatureRadiusMeters,
            capability.CurvatureGainAllowed);
        var gainCost = Mathf.Clamp01(
            RdwPlanningCostEstimatorMath.YawCost(inputs.RelativeYawDegrees) * 0.4f +
            steeringConflict * 0.4f +
            curvatureEffort * 0.2f);
        var clearancePressure = RdwPlanningCostEstimatorMath.ClearancePressure(
            inputs.Heading.ClearanceMeters,
            maximumClearanceMeters);
        var resetPressure = Mathf.Clamp01(
            clearancePressure * 0.55f +
            steeringConflict * 0.3f +
            orbitPressure * 0.15f);
        return RdwPlanningCostEstimatorMath.Finish(
            traversal,
            inputs,
            benefit,
            gainCost,
            resetPressure);
    }

    private bool CalculateOrbitDirection(
        in RdwGeometrySnapshot geometry,
        Vector2 physicalPosition,
        Vector2 physicalForward,
        out Vector2 desiredDirection,
        out float sideConfidence,
        out float orbitPressure)
    {
        desiredDirection = Vector2.zero;
        sideConfidence = 0f;
        orbitPressure = 1f;
        if (!geometry.IsValid || orbitRadiusMeters <= DirectionEpsilon)
        {
            return false;
        }

        var availableRadius = DistanceToBoundary(geometry.Boundary, geometry.SteeringCenterXZ) -
                              orbitBoundarySafetyMarginMeters;
        var clampedAvailableRadius = Mathf.Max(0f, availableRadius);
        var radius = Mathf.Min(orbitRadiusMeters, clampedAvailableRadius);
        orbitPressure = Mathf.Clamp01(
            (orbitRadiusMeters - clampedAvailableRadius) / Mathf.Max(orbitRadiusMeters, DirectionEpsilon));
        if (radius <= DirectionEpsilon)
        {
            return false;
        }

        var radial = physicalPosition - geometry.SteeringCenterXZ;
        var radialDistance = radial.magnitude;
        var baseDirection = radialDistance > DirectionEpsilon
            ? -radial / radialDistance
            : -NormalizeOrDefault(physicalForward, Vector2.up);
        var alpha = radialDistance < radius
            ? 60f
            : Mathf.Acos(Mathf.Clamp(radius / Mathf.Max(radialDistance, DirectionEpsilon), -1f, 1f)) * Mathf.Rad2Deg;
        var positiveTarget = geometry.SteeringCenterXZ + Rotate(baseDirection, alpha) * radius;
        var negativeTarget = geometry.SteeringCenterXZ + Rotate(baseDirection, -alpha) * radius;
        var positiveDirection = positiveTarget - physicalPosition;
        var negativeDirection = negativeTarget - physicalPosition;
        var positiveAngle = CandidateAngle(physicalForward, positiveDirection);
        var negativeAngle = CandidateAngle(physicalForward, negativeDirection);
        var angleDifference = Mathf.Abs(positiveAngle - negativeAngle);
        if (positiveDirection.sqrMagnitude <= DirectionEpsilon ||
            negativeDirection.sqrMagnitude <= DirectionEpsilon ||
            angleDifference <= sideSwitchHysteresisDegrees)
        {
            return true;
        }

        var target = positiveAngle < negativeAngle ? positiveTarget : negativeTarget;
        desiredDirection = (target - physicalPosition).normalized;
        sideConfidence = Mathf.Clamp01(
            (angleDifference - sideSwitchHysteresisDegrees) /
            Mathf.Max(1f, 180f - sideSwitchHysteresisDegrees));
        return true;
    }

    private static float CandidateAngle(Vector2 forward, Vector2 direction)
    {
        if (direction.sqrMagnitude <= DirectionEpsilon) return 180f;
        return Mathf.Abs(Mathf.DeltaAngle(
            RdwYawConvention.ForwardToYaw(forward),
            RdwYawConvention.ForwardToYaw(direction.normalized)));
    }

    private static Vector2 Rotate(Vector2 vector, float degrees)
    {
        var radians = degrees * Mathf.Deg2Rad;
        var cosine = Mathf.Cos(radians);
        var sine = Mathf.Sin(radians);
        return new Vector2(vector.x * cosine - vector.y * sine, vector.x * sine + vector.y * cosine);
    }

    private static Vector2 NormalizeOrDefault(Vector2 vector, Vector2 fallback)
    {
        return vector.sqrMagnitude > DirectionEpsilon ? vector.normalized : fallback.normalized;
    }

    private static float DistanceToBoundary(IReadOnlyList<Vector2> boundary, Vector2 point)
    {
        if (boundary == null || boundary.Count < 3) return 0f;
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < boundary.Count; index++)
        {
            var start = boundary[index];
            var end = boundary[(index + 1) % boundary.Count];
            var edge = end - start;
            var t = edge.sqrMagnitude > DirectionEpsilon
                ? Mathf.Clamp01(Vector2.Dot(point - start, edge) / edge.sqrMagnitude)
                : 0f;
            nearest = Mathf.Min(nearest, Vector2.Distance(point, start + edge * t));
        }
        return float.IsInfinity(nearest) ? 0f : nearest;
    }
}

/// <summary>
/// 纯 Thomas APF 规划代价估算器；APF 只提供 steering cost，不拥有 Safety 判定。
/// </summary>
public sealed class ThomasApfPlanningCostEstimator : IRdwPlanningCostEstimator
{
    private readonly IRdwPlanningDirectionEvaluator directionEvaluator;
    private readonly ThomasApfForceSettings apfSettings;
    private readonly float curvatureRadiusMeters;
    private readonly float maximumClearanceMeters;

    /// <summary>Thomas APF 规划估计器稳定 ID。</summary>
    public string EstimatorId => "thomas-apf-planning";
    /// <summary>Thomas APF 规划估计器算法版本。</summary>
    public string EstimatorVersion => "1.0.0";

    /// <summary>创建无状态 Thomas APF 规划估计器。</summary>
    public ThomasApfPlanningCostEstimator(
        IRdwPlanningDirectionEvaluator exactDirectionEvaluator,
        ThomasApfForceSettings settings,
        float curvatureRadius = 10f,
        float maximumClearance = 3f)
    {
        directionEvaluator = exactDirectionEvaluator;
        apfSettings = settings;
        curvatureRadiusMeters = Mathf.Max(0.01f, curvatureRadius);
        maximumClearanceMeters = Mathf.Max(0.01f, maximumClearance);
    }

    /// <summary>计算 APF steering 一致性、转向代价和恢复压力。</summary>
    public RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability)
    {
        if (!RdwPlanningCostEstimatorMath.TryPrepare(
                snapshot,
                traversal,
                from,
                to,
                capability,
                directionEvaluator,
                EstimatorId,
                EstimatorVersion,
                "ThomasAPF",
                out var inputs,
                out var rejectionReason))
        {
            return RdwPlanningCostEstimatorMath.Rejected(rejectionReason, traversal.PhysicalPositionXZ);
        }

        // UserRadius 是冻结 Physical Fact；InfluenceMargin 等参数仍只属于 APF tuning。
        var settings = apfSettings.WithUserRadiusMeters(snapshot.Geometry.UserRadiusMeters);
        var force = new ThomasApfForceEvaluator(settings).Evaluate(
            traversal.PhysicalPositionXZ,
            traversal.PhysicalForwardXZ,
            snapshot.Geometry.Boundary,
            snapshot.Geometry.Obstacles);
        if (!force.IsFinite)
        {
            return RdwPlanningCostEstimatorMath.Rejected(
                "apf_force_invalid",
                traversal.PhysicalPositionXZ);
        }

        var forceIntensity = settings.MaxForceMagnitude > 0.000001f
            ? Mathf.Clamp01(force.TotalForceMagnitude / settings.MaxForceMagnitude)
            : 0f;
        var neutral = force.DesiredDirectionXZ.sqrMagnitude <= 0.000001f;
        var directionalBenefit = neutral
            ? 0.5f
            : Mathf.Clamp01((Vector2.Dot(inputs.PredictedPhysicalHeading, force.DesiredDirectionXZ) + 1f) * 0.5f);
        var benefit = neutral
            ? 0.5f
            : Mathf.Lerp(0.5f, directionalBenefit, Mathf.Clamp01(0.35f + forceIntensity * 0.65f));
        var directionalConflict = neutral ? 0.15f : 1f - benefit;
        var curvatureEffort = RdwPlanningCostEstimatorMath.CurvatureEffort(
            inputs.VirtualDelta.magnitude,
            curvatureRadiusMeters,
            capability.CurvatureGainAllowed);
        var gainCost = Mathf.Clamp01(
            RdwPlanningCostEstimatorMath.YawCost(inputs.RelativeYawDegrees) * 0.35f +
            directionalConflict * 0.35f +
            curvatureEffort * 0.15f +
            forceIntensity * 0.15f);
        var clearancePressure = RdwPlanningCostEstimatorMath.ClearancePressure(
            inputs.Heading.ClearanceMeters,
            maximumClearanceMeters);
        var resetPressure = Mathf.Clamp01(
            clearancePressure * 0.5f +
            directionalConflict * 0.25f +
            forceIntensity * 0.25f);
        return RdwPlanningCostEstimatorMath.Finish(
            traversal,
            inputs,
            benefit,
            gainCost,
            resetPressure);
    }
}

/// <summary>None/关闭 Continuous RDW 时使用的明确中性规划估算器。</summary>
public sealed class NeutralRdwPlanningCostEstimator : IRdwPlanningCostEstimator
{
    private readonly IRdwPlanningDirectionEvaluator directionEvaluator;

    /// <summary>中性估算器稳定 ID。</summary>
    public string EstimatorId => "neutral-planning";
    /// <summary>中性估算器版本。</summary>
    public string EstimatorVersion => "1.0.0";

    /// <summary>创建不伪装成任一 Redirector 的中性估算器。</summary>
    public NeutralRdwPlanningCostEstimator(IRdwPlanningDirectionEvaluator exactDirectionEvaluator)
    {
        directionEvaluator = exactDirectionEvaluator;
    }

    /// <summary>只执行 Exact Safety，Continuous benefit 保持中性。</summary>
    public RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability)
    {
        if (!RdwPlanningCostEstimatorMath.TryPrepare(
                snapshot,
                traversal,
                from,
                to,
                capability,
                directionEvaluator,
                EstimatorId,
                EstimatorVersion,
                "None",
                out var inputs,
                out var rejectionReason))
        {
            return RdwPlanningCostEstimatorMath.Rejected(rejectionReason, traversal.PhysicalPositionXZ);
        }

        var clearancePressure = RdwPlanningCostEstimatorMath.ClearancePressure(
            inputs.Heading.ClearanceMeters,
            3f);
        return RdwPlanningCostEstimatorMath.Finish(
            traversal,
            inputs,
            0f,
            0f,
            clearancePressure);
    }
}

/// <summary>集中创建与 Runtime Redirector 对齐的规划估算器和 capability 类型。</summary>
public static class RdwPlanningCostEstimatorFactory
{
    /// <summary>按启动时 Continuous 模式创建匹配的纯估算器。</summary>
    public static IRdwPlanningCostEstimator Create(
        ContinuousRedirectorMode mode,
        bool enabled,
        IRdwPlanningDirectionEvaluator directionEvaluator,
        float curvatureRadiusMeters,
        float orbitRadiusMeters,
        float orbitBoundarySafetyMarginMeters,
        float sideSwitchHysteresisDegrees,
        ThomasApfForceSettings apfSettings)
    {
        if (!enabled || mode == ContinuousRedirectorMode.None)
        {
            return new NeutralRdwPlanningCostEstimator(directionEvaluator);
        }

        switch (mode)
        {
            case ContinuousRedirectorMode.S2O:
                return new S2OPlanningCostEstimator(
                    directionEvaluator,
                    curvatureRadiusMeters,
                    configuredOrbitRadius: orbitRadiusMeters,
                    orbitBoundarySafetyMargin: orbitBoundarySafetyMarginMeters,
                    sideSwitchHysteresis: sideSwitchHysteresisDegrees);
            case ContinuousRedirectorMode.ThomasAPF:
                return new ThomasApfPlanningCostEstimator(
                    directionEvaluator,
                    apfSettings,
                    curvatureRadiusMeters);
            default:
                return new S2CPlanningCostEstimator(directionEvaluator, curvatureRadiusMeters);
        }
    }

    /// <summary>返回能力中真实的 Redirector 类型；关闭时明确为 None。</summary>
    public static string RedirectorType(ContinuousRedirectorMode mode, bool enabled)
    {
        return enabled && mode != ContinuousRedirectorMode.None ? mode.ToString() : "None";
    }
}
