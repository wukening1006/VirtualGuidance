using System;
using UnityEngine;

/// <summary>将 Thomas APF force direction 接入统一 Continuous steering/gain pipeline。</summary>
public sealed class ThomasApfRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private readonly RdwContinuousSteeringController steeringController;
    private readonly ThomasApfForceEvaluator forceEvaluator;
    private string lastPhysicalSpaceId = string.Empty;
    private int lastGeometrySignature;
    private bool lastCriticalRisk;
    private bool hasGeometryIdentity;

    /// <summary>最近一次 Thomas APF 计算诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>Thomas APF 始终提供连续能力，实际帧资格由 Coordinator 门控。</summary>
    public bool IsAvailable => true;

    /// <summary>创建参数化 Thomas APF Redirector；APF 只输出 steering direction。</summary>
    public ThomasApfRedirector(
        float movementThresholdMetersPerSecond = 0.2f,
        float rotationThresholdDegreesPerSecond = 1.5f,
        float curvatureRadiusMeters = 10f,
        float rotationGainMultiplier = 0.05f,
        float steeringDampening = 0.75f,
        float smoothingFactor = 0.125f,
        ThomasApfForceSettings? apfSettings = null)
    {
        steeringController = new RdwContinuousSteeringController(
            movementThresholdMetersPerSecond,
            rotationThresholdDegreesPerSecond,
            curvatureRadiusMeters,
            rotationGainMultiplier,
            steeringDampening,
            smoothingFactor);
        forceEvaluator = new ThomasApfForceEvaluator(apfSettings ?? ThomasApfForceSettings.Default);
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>基于当前冻结 Physical Geometry 计算 APF steering 与统一 gain。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        if (!pose.IsValid || !geometry.IsValid || !intent.IsValid || !MatchesGeometryIdentity(pose, geometry))
        {
            var reason = pose.IsValid && geometry.IsValid && intent.IsValid
                ? "thomas_apf_geometry_identity_mismatch"
                : "thomas_apf_invalid_input";
            return ReturnInvalid(pose.Sequence, geometry.SteeringCenterXZ, reason);
        }

        var geometrySignature = CalculateGeometrySignature(geometry);
        if (!hasGeometryIdentity ||
            !string.Equals(lastPhysicalSpaceId, geometry.PhysicalSpaceId, System.StringComparison.Ordinal) ||
            lastGeometrySignature != geometrySignature ||
            geometry.IsCriticalRisk ||
            lastCriticalRisk != geometry.IsCriticalRisk)
        {
            steeringController.ResetSmoothing();
            lastPhysicalSpaceId = geometry.PhysicalSpaceId ?? string.Empty;
            lastGeometrySignature = geometrySignature;
            lastCriticalRisk = geometry.IsCriticalRisk;
            hasGeometryIdentity = true;
        }

        var result = forceEvaluator.Evaluate(
            pose.PhysicalPositionXZ,
            pose.PhysicalForwardXZ,
            geometry.Boundary,
            geometry.Obstacles);
        if (!result.IsFinite || result.DesiredDirectionXZ.sqrMagnitude <= 0.000001f)
        {
            var reason = result.IsFinite ? "thomas_apf_neutral" : "thomas_apf_invalid_force";
            var neutral = steeringController.ReturnNeutral(
                pose.Sequence,
                "ThomasAPF",
                pose.PhysicalPositionXZ,
                reason,
                result.NeutralReason);
            LastDiagnostics = steeringController.LastDiagnostics.WithAlgorithmData(
                boundaryForceXZ: result.BoundaryForceXZ,
                obstacleForceXZ: result.ObstacleForceXZ,
                totalForceXZ: result.TotalForceXZ,
                totalForceMagnitude: result.TotalForceMagnitude,
                apfNeutralReason: result.NeutralReason,
                apfForceClamped: result.ForceClamped);
            return neutral;
        }

        var command = steeringController.Evaluate(
            pose,
            geometry.SteeringCenterXZ,
            result.DesiredDirectionXZ,
            "ThomasAPF",
            "thomas_apf_active");
        LastDiagnostics = steeringController.LastDiagnostics.WithAlgorithmData(
            boundaryForceXZ: result.BoundaryForceXZ,
            obstacleForceXZ: result.ObstacleForceXZ,
            totalForceXZ: result.TotalForceXZ,
            totalForceMagnitude: result.TotalForceMagnitude,
            apfForceClamped: result.ForceClamped);
        return command;
    }

    /// <summary>清除 APF 的跨帧 steering 与 geometry 身份状态。</summary>
    public void ResetSmoothing()
    {
        steeringController.ResetSmoothing();
        lastPhysicalSpaceId = string.Empty;
        lastGeometrySignature = 0;
        lastCriticalRisk = false;
        hasGeometryIdentity = false;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private static int CalculateGeometrySignature(in RdwGeometrySnapshot geometry)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + (geometry.CalibrationId ?? string.Empty).GetHashCode();
            hash = hash * 31 + geometry.UserRadiusMeters.GetHashCode();
            hash = hash * 31 + geometry.Boundary.Count;
            foreach (var point in geometry.Boundary)
            {
                hash = hash * 31 + point.x.GetHashCode();
                hash = hash * 31 + point.y.GetHashCode();
            }
            hash = hash * 31 + geometry.Obstacles.Count;
            foreach (var obstacle in geometry.Obstacles)
            {
                if (obstacle == null)
                {
                    hash *= 31;
                    continue;
                }
                hash = hash * 31 + obstacle.id.GetHashCode();
                hash = hash * 31 + obstacle.center.x.GetHashCode();
                hash = hash * 31 + obstacle.center.y.GetHashCode();
                hash = hash * 31 + obstacle.radius.GetHashCode();
            }
            return hash;
        }
    }

    private static bool MatchesGeometryIdentity(
        in RdwPoseFrame pose,
        in RdwGeometrySnapshot geometry)
    {
        return (string.IsNullOrWhiteSpace(geometry.CalibrationId) ||
                string.Equals(pose.CalibrationId, geometry.CalibrationId, StringComparison.Ordinal)) &&
               (string.IsNullOrWhiteSpace(geometry.PhysicalSpaceId) ||
                string.IsNullOrWhiteSpace(pose.PhysicalSpaceId) ||
                string.Equals(pose.PhysicalSpaceId, geometry.PhysicalSpaceId, StringComparison.Ordinal));
    }

    private RdwGainCommand ReturnInvalid(long sequence, Vector2 center, string reason)
    {
        var command = steeringController.ReturnInvalid(sequence, "ThomasAPF", center, reason);
        LastDiagnostics = steeringController.LastDiagnostics;
        return command;
    }
}
