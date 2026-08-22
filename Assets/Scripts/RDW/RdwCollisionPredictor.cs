using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 基于实际 Physical 位移、边界和障碍物，在本地同步产生新的碰撞预测。
/// </summary>
public sealed class RdwCollisionPredictor
{
    private const float Epsilon = 0.0001f;
    private readonly float boundaryMarginMeters;
    private readonly float trackingErrorMeters;
    private readonly float riskEvaluationIntervalSeconds;
    private readonly float resetLaunchDelaySeconds;
    private readonly float minimumMovementSpeedMetersPerSecond;
    private readonly float directionFingerprintStepDegrees;
    private string suppressedFingerprint = string.Empty;

    /// <summary>
    /// 创建本地碰撞预测器；所有距离参数单位为米，时间参数单位为秒。
    /// </summary>
    public RdwCollisionPredictor(
        float boundaryMargin = 0.08f,
        float trackingError = 0.05f,
        float riskEvaluationInterval = 0.2f,
        float resetLaunchDelay = 0.25f,
        float minimumMovementSpeed = 0.03f,
        float directionFingerprintStep = 15f)
    {
        boundaryMarginMeters = Mathf.Max(0f, boundaryMargin);
        trackingErrorMeters = Mathf.Max(0f, trackingError);
        riskEvaluationIntervalSeconds = Mathf.Max(0f, riskEvaluationInterval);
        resetLaunchDelaySeconds = Mathf.Max(0f, resetLaunchDelay);
        minimumMovementSpeedMetersPerSecond = Mathf.Max(0.001f, minimumMovementSpeed);
        directionFingerprintStepDegrees = Mathf.Clamp(directionFingerprintStep, 1f, 90f);
    }

    /// <summary>
    /// 尝试从当前 Tracking Sample 产生一条尚未处理的新预测。
    /// </summary>
    public bool TryPredict(
        in RdwPoseFrame frame,
        in RdwGeometrySnapshot geometry,
        out CollisionPrediction prediction)
    {
        prediction = null;
        if (!TryBuildCandidate(frame, geometry, out var candidate))
        {
            // 轨迹已离开原危险走廊后，允许未来再次产生同源预测。
            suppressedFingerprint = string.Empty;
            return false;
        }

        if (string.Equals(candidate.Fingerprint, suppressedFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        prediction = candidate;
        return true;
    }

    /// <summary>在 Coordinator 接受预测后抑制同一轨迹的重复发布。</summary>
    public void MarkTriggered(CollisionPrediction prediction)
    {
        if (prediction != null && prediction.IsValid)
        {
            suppressedFingerprint = prediction.Fingerprint;
        }
    }

    /// <summary>
    /// Reset 完成后用最新物理事实建立新基线，避免旧轨迹立即重复触发。
    /// </summary>
    public void Rearm(in RdwPoseFrame frame, in RdwGeometrySnapshot geometry)
    {
        suppressedFingerprint = TryBuildCandidate(frame, geometry, out var candidate)
            ? candidate.Fingerprint
            : string.Empty;
    }

    /// <summary>Tracking、Calibration 或 Physical Space 身份变化时清除预测历史。</summary>
    public void Reset()
    {
        suppressedFingerprint = string.Empty;
    }

    private bool TryBuildCandidate(
        in RdwPoseFrame frame,
        in RdwGeometrySnapshot geometry,
        out CollisionPrediction prediction)
    {
        prediction = null;
        if (!frame.IsValid || !geometry.IsValid ||
            !string.Equals(frame.PhysicalSpaceId, geometry.PhysicalSpaceId, StringComparison.Ordinal))
        {
            return false;
        }

        var deltaTime = Mathf.Max(Epsilon, frame.DeltaTime);
        var speed = frame.DeltaPhysicalPositionXZ.magnitude / deltaTime;
        var outsideBoundary = !PhysicalSpaceGeometry.ContainsPoint(
            geometry.Boundary,
            frame.PhysicalPositionXZ);
        if (!outsideBoundary && speed < minimumMovementSpeedMetersPerSecond)
        {
            return false;
        }

        var direction = frame.DeltaPhysicalPositionXZ.sqrMagnitude > Epsilon * Epsilon
            ? frame.DeltaPhysicalPositionXZ.normalized
            : frame.PhysicalForwardXZ;
        var frameTravel = frame.DeltaPhysicalPositionXZ.magnitude;
        var launchTravel = speed * (riskEvaluationIntervalSeconds + resetLaunchDelaySeconds);
        var triggerTravel = frameTravel + launchTravel;
        var sharedClearance = Mathf.Max(0f, geometry.UserRadiusMeters) +
                              boundaryMarginMeters + trackingErrorMeters;

        var found = outsideBoundary;
        var sourceType = CollisionPredictionSourceType.Boundary;
        var sourceId = outsideBoundary ? "boundary_breach" : "physical_boundary";
        var predictedDistance = outsideBoundary ? 0f : float.PositiveInfinity;
        var collisionPoint = frame.PhysicalPositionXZ;

        if (!outsideBoundary && TryBoundaryDistance(
                geometry.Boundary,
                frame.PhysicalPositionXZ,
                direction,
                out var boundaryDistance))
        {
            var corridorDistance = Mathf.Max(0f, boundaryDistance - sharedClearance);
            if (corridorDistance <= triggerTravel)
            {
                found = true;
                predictedDistance = corridorDistance;
                collisionPoint = frame.PhysicalPositionXZ + direction * boundaryDistance;
            }
        }

        foreach (var obstacle in geometry.Obstacles)
        {
            if (obstacle == null || !TryRayCircleDistance(
                    frame.PhysicalPositionXZ,
                    direction,
                    obstacle.center,
                    Mathf.Max(0f, obstacle.radius) + sharedClearance,
                    out var obstacleDistance) ||
                obstacleDistance > triggerTravel || obstacleDistance >= predictedDistance)
            {
                continue;
            }

            found = true;
            sourceType = CollisionPredictionSourceType.StaticObstacle;
            sourceId = string.IsNullOrWhiteSpace(obstacle.id) ? "unnamed_obstacle" : obstacle.id;
            predictedDistance = obstacleDistance;
            collisionPoint = frame.PhysicalPositionXZ + direction * obstacleDistance;
        }

        if (!found)
        {
            return false;
        }

        var fingerprint = BuildFingerprint(
            geometry.PhysicalSpaceId,
            sourceType,
            sourceId,
            direction);
        prediction = new CollisionPrediction(
            Guid.NewGuid().ToString("N"),
            fingerprint,
            geometry.RiskRevision,
            geometry.PhysicalSpaceId,
            sourceType,
            sourceId,
            predictedDistance,
            speed >= minimumMovementSpeedMetersPerSecond ? predictedDistance / speed : 0f,
            collisionPoint,
            frame.PhysicalPositionXZ,
            direction,
            true);
        return true;
    }

    private string BuildFingerprint(
        string physicalSpaceId,
        CollisionPredictionSourceType sourceType,
        string sourceId,
        Vector2 direction)
    {
        var yaw = Mathf.Repeat(RdwYawConvention.ForwardToYaw(direction), 360f);
        var directionBucket = Mathf.RoundToInt(yaw / directionFingerprintStepDegrees);
        return string.Join(
            "|",
            physicalSpaceId ?? string.Empty,
            sourceType.ToString(),
            sourceId ?? string.Empty,
            directionBucket.ToString());
    }

    private static bool TryBoundaryDistance(
        IReadOnlyList<Vector2> boundary,
        Vector2 origin,
        Vector2 direction,
        out float distance)
    {
        distance = float.PositiveInfinity;
        if (boundary == null || boundary.Count < 3)
        {
            return false;
        }

        var found = false;
        for (var index = 0; index < boundary.Count; index++)
        {
            if (!TryRaySegmentDistance(
                    origin,
                    direction,
                    boundary[index],
                    boundary[(index + 1) % boundary.Count],
                    out var candidate))
            {
                continue;
            }

            distance = Mathf.Min(distance, candidate);
            found = true;
        }

        return found;
    }

    private static bool TryRaySegmentDistance(
        Vector2 origin,
        Vector2 direction,
        Vector2 segmentStart,
        Vector2 segmentEnd,
        out float distance)
    {
        distance = 0f;
        var segment = segmentEnd - segmentStart;
        var denominator = Cross(direction, segment);
        if (Mathf.Abs(denominator) < Epsilon)
        {
            return false;
        }

        var delta = segmentStart - origin;
        var rayT = Cross(delta, segment) / denominator;
        var segmentT = Cross(delta, direction) / denominator;
        if (rayT < 0f || segmentT < 0f || segmentT > 1f)
        {
            return false;
        }

        distance = rayT;
        return true;
    }

    private static bool TryRayCircleDistance(
        Vector2 origin,
        Vector2 direction,
        Vector2 center,
        float radius,
        out float distance)
    {
        distance = 0f;
        var toOrigin = origin - center;
        var c = Vector2.Dot(toOrigin, toOrigin) - radius * radius;
        if (c <= 0f)
        {
            return true;
        }

        var b = Vector2.Dot(toOrigin, direction);
        var discriminant = b * b - c;
        if (discriminant < 0f)
        {
            return false;
        }

        var rayT = -b - Mathf.Sqrt(discriminant);
        if (rayT < 0f)
        {
            return false;
        }

        distance = rayT;
        return true;
    }

    private static float Cross(Vector2 left, Vector2 right)
    {
        return left.x * right.y - left.y * right.x;
    }
}
