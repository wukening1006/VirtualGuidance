using System.Collections.Generic;
using UnityEngine;

/// <summary>将 Physical Path 导向 Physical Space 中心附近 orbit 的 S2O Redirector。</summary>
public sealed class SteerToOrbitRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private const float DirectionEpsilon = 0.000001f;
    private readonly RdwContinuousSteeringController steeringController;
    private readonly float configuredOrbitRadiusMeters;
    private readonly float orbitBoundarySafetyMarginMeters;
    private readonly float sideSwitchHysteresisDegrees;
    private int activeOrbitSide;

    /// <summary>最近一次 S2O 计算诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>S2O 始终提供连续能力，实际帧资格由 Coordinator 门控。</summary>
    public bool IsAvailable => true;
    /// <summary>当前稳定选择的 orbit side，-1/1 分别代表两个切向候选。</summary>
    public int ActiveOrbitSide => activeOrbitSide;

    /// <summary>创建参数化 S2O；轨道半径和安全余量单位为米。</summary>
    public SteerToOrbitRedirector(
        float movementThresholdMetersPerSecond = 0.2f,
        float rotationThresholdDegreesPerSecond = 1.5f,
        float curvatureRadiusMeters = 10f,
        float rotationGainMultiplier = 0.05f,
        float steeringDampening = 0.75f,
        float smoothingFactor = 0.125f,
        float orbitRadiusMeters = 1.5f,
        float orbitBoundarySafetyMarginMeters = 0.25f,
        float sideSwitchHysteresisDegrees = 12f)
    {
        steeringController = new RdwContinuousSteeringController(
            movementThresholdMetersPerSecond,
            rotationThresholdDegreesPerSecond,
            curvatureRadiusMeters,
            rotationGainMultiplier,
            steeringDampening,
            smoothingFactor);
        configuredOrbitRadiusMeters = Mathf.Max(0f, orbitRadiusMeters);
        this.orbitBoundarySafetyMarginMeters = Mathf.Max(0f, orbitBoundarySafetyMarginMeters);
        this.sideSwitchHysteresisDegrees = Mathf.Max(0f, sideSwitchHysteresisDegrees);
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>根据中心、轨道半径和 Physical Forward 计算单帧 S2O gain。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        if (!pose.IsValid || !geometry.IsValid || !intent.IsValid)
        {
            return ReturnInvalid(pose.Sequence, geometry.SteeringCenterXZ, "s2o_invalid_input");
        }

        var availableRadius = DistanceToBoundary(geometry.Boundary, geometry.SteeringCenterXZ) - orbitBoundarySafetyMarginMeters;
        var orbitRadius = Mathf.Min(configuredOrbitRadiusMeters, availableRadius);
        if (orbitRadius <= DirectionEpsilon || !IsInsideBoundary(geometry.Boundary, geometry.SteeringCenterXZ))
        {
            return ReturnNeutral(pose.Sequence, geometry.SteeringCenterXZ, "s2o_invalid_orbit_geometry");
        }

        var radial = pose.PhysicalPositionXZ - geometry.SteeringCenterXZ;
        var radialDistance = radial.magnitude;
        var baseDirection = radialDistance > DirectionEpsilon
            ? -radial / radialDistance
            : -NormalizeOrDefault(pose.PhysicalForwardXZ, Vector2.up);
        var alpha = radialDistance < orbitRadius
            ? 60f
            : Mathf.Acos(Mathf.Clamp(orbitRadius / Mathf.Max(radialDistance, DirectionEpsilon), -1f, 1f)) * Mathf.Rad2Deg;
        var candidatePositive = geometry.SteeringCenterXZ + Rotate(baseDirection, alpha) * orbitRadius;
        var candidateNegative = geometry.SteeringCenterXZ + Rotate(baseDirection, -alpha) * orbitRadius;
        var positiveDirection = candidatePositive - pose.PhysicalPositionXZ;
        var negativeDirection = candidateNegative - pose.PhysicalPositionXZ;
        var positiveAngle = CandidateAngle(pose.PhysicalForwardXZ, positiveDirection);
        var negativeAngle = CandidateAngle(pose.PhysicalForwardXZ, negativeDirection);
        var selectedSide = SelectOrbitSide(positiveAngle, negativeAngle);
        var target = selectedSide > 0 ? candidatePositive : candidateNegative;
        var direction = target - pose.PhysicalPositionXZ;
        if (direction.sqrMagnitude <= DirectionEpsilon)
        {
            return ReturnNeutral(pose.Sequence, geometry.SteeringCenterXZ, "s2o_degenerate_target", orbitRadius, target, selectedSide);
        }

        var command = steeringController.Evaluate(
            pose,
            geometry.SteeringCenterXZ,
            direction,
            "S2O",
            "s2o_active");
        LastDiagnostics = steeringController.LastDiagnostics.WithAlgorithmData(
            orbitRadiusMeters: orbitRadius,
            orbitTargetXZ: target,
            orbitSide: selectedSide);
        return command;
    }

    /// <summary>清除 orbit side 与统一 gain 平滑状态。</summary>
    public void ResetSmoothing()
    {
        steeringController.ResetSmoothing();
        activeOrbitSide = 0;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private int SelectOrbitSide(float positiveAngle, float negativeAngle)
    {
        if (activeOrbitSide == 0)
        {
            activeOrbitSide = positiveAngle <= negativeAngle ? 1 : -1;
            return activeOrbitSide;
        }

        var currentAngle = activeOrbitSide > 0 ? positiveAngle : negativeAngle;
        var otherAngle = activeOrbitSide > 0 ? negativeAngle : positiveAngle;
        var currentValid = currentAngle < 179.99f;
        if (!currentValid || otherAngle + sideSwitchHysteresisDegrees < currentAngle)
        {
            activeOrbitSide = -activeOrbitSide;
        }
        return activeOrbitSide;
    }

    private RdwGainCommand ReturnInvalid(long sequence, Vector2 center, string reason)
    {
        activeOrbitSide = 0;
        var command = steeringController.ReturnInvalid(sequence, "S2O", center, reason);
        LastDiagnostics = steeringController.LastDiagnostics;
        return command;
    }

    private RdwGainCommand ReturnNeutral(
        long sequence,
        Vector2 center,
        string reason,
        float orbitRadius = 0f,
        Vector2 orbitTarget = default(Vector2),
        int orbitSide = 0)
    {
        var command = steeringController.ReturnNeutral(sequence, "S2O", center, reason);
        LastDiagnostics = steeringController.LastDiagnostics.WithAlgorithmData(
            orbitRadiusMeters: orbitRadius,
            orbitTargetXZ: orbitTarget,
            orbitSide: orbitSide);
        activeOrbitSide = orbitSide;
        return command;
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

    private static bool IsInsideBoundary(IReadOnlyList<Vector2> boundary, Vector2 point)
    {
        return PhysicalSpaceGeometry.ContainsPoint(boundary, point);
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
