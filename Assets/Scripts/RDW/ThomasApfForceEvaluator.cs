using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Thomas APF 纯数学参数；不包含 Safety 或 Gain 责任。</summary>
public readonly struct ThomasApfForceSettings
{
    /// <summary>Boundary 斥力影响半径，单位：米。</summary>
    public float BoundaryInfluenceRadiusMeters { get; }
    /// <summary>Obstacle 斥力影响半径，单位：米。</summary>
    public float ObstacleInfluenceRadiusMeters { get; }
    /// <summary>用户半径估计，单位：米。</summary>
    public float UserRadiusMeters { get; }
    /// <summary>额外 geometry 安全余量，单位：米。</summary>
    public float SafetyMarginMeters { get; }
    /// <summary>Boundary 斥力权重。</summary>
    public float BoundaryStrength { get; }
    /// <summary>Obstacle 斥力权重。</summary>
    public float ObstacleStrength { get; }
    /// <summary>合力最大模长。</summary>
    public float MaxForceMagnitude { get; }
    /// <summary>合力低于该值时视为 APF neutral。</summary>
    public float NeutralForceEpsilon { get; }
    /// <summary>避免反平方项爆炸的距离下限，单位：米。</summary>
    public float DistanceEpsilonMeters { get; }

    /// <summary>创建 Thomas APF 参数并规范化不安全值。</summary>
    public ThomasApfForceSettings(
        float boundaryInfluenceRadiusMeters = 1.5f,
        float obstacleInfluenceRadiusMeters = 1.5f,
        float userRadiusMeters = 0.25f,
        float safetyMarginMeters = 0.15f,
        float boundaryStrength = 1f,
        float obstacleStrength = 1f,
        float maxForceMagnitude = 100f,
        float neutralForceEpsilon = 0.001f,
        float distanceEpsilonMeters = 0.01f)
    {
        BoundaryInfluenceRadiusMeters = Mathf.Max(0f, boundaryInfluenceRadiusMeters);
        ObstacleInfluenceRadiusMeters = Mathf.Max(0f, obstacleInfluenceRadiusMeters);
        UserRadiusMeters = Mathf.Max(0f, userRadiusMeters);
        SafetyMarginMeters = Mathf.Max(0f, safetyMarginMeters);
        BoundaryStrength = Mathf.Max(0f, boundaryStrength);
        ObstacleStrength = Mathf.Max(0f, obstacleStrength);
        MaxForceMagnitude = Mathf.Max(0f, maxForceMagnitude);
        NeutralForceEpsilon = Mathf.Max(0f, neutralForceEpsilon);
        DistanceEpsilonMeters = Mathf.Max(0.000001f, distanceEpsilonMeters);
    }
    
    /// <summary>返回默认 Thomas APF 参数。</summary>
    public static ThomasApfForceSettings Default => new ThomasApfForceSettings();
}

/// <summary>Thomas APF 的可记录纯计算结果。</summary>
public readonly struct ThomasApfForceResult
{
    /// <summary>Boundary 斥力向量。</summary>
    public Vector2 BoundaryForceXZ { get; }
    /// <summary>Obstacle 斥力向量。</summary>
    public Vector2 ObstacleForceXZ { get; }
    /// <summary>经过 finite check 和 clamp 后的合力。</summary>
    public Vector2 TotalForceXZ { get; }
    /// <summary>合力模长。</summary>
    public float TotalForceMagnitude { get; }
    /// <summary>归一化后的 steering direction；neutral 时为零。</summary>
    public Vector2 DesiredDirectionXZ { get; }
    /// <summary>APF 是否遇到无效输入或数值。</summary>
    public bool IsFinite { get; }
    /// <summary>是否对合力做过上限裁剪。</summary>
    public bool ForceClamped { get; }
    /// <summary>neutral 或 invalid 的解释。</summary>
    public string NeutralReason { get; }

    /// <summary>创建计算结果。</summary>
    public ThomasApfForceResult(
        Vector2 boundaryForceXZ,
        Vector2 obstacleForceXZ,
        Vector2 totalForceXZ,
        float totalForceMagnitude,
        Vector2 desiredDirectionXZ,
        bool isFinite,
        bool forceClamped,
        string neutralReason)
    {
        BoundaryForceXZ = boundaryForceXZ;
        ObstacleForceXZ = obstacleForceXZ;
        TotalForceXZ = totalForceXZ;
        TotalForceMagnitude = totalForceMagnitude;
        DesiredDirectionXZ = desiredDirectionXZ;
        IsFinite = isFinite;
        ForceClamped = forceClamped;
        NeutralReason = neutralReason ?? string.Empty;
    }

    /// <summary>返回没有 steering preference 的结果。</summary>
    public static ThomasApfForceResult Neutral(string reason)
    {
        return new ThomasApfForceResult(
            Vector2.zero,
            Vector2.zero,
            Vector2.zero,
            0f,
            Vector2.zero,
            true,
            false,
            reason);
    }
}

/// <summary>根据当前 Physical Geometry 计算 Thomas APF 斥力。</summary>
public sealed class ThomasApfForceEvaluator
{
    private readonly ThomasApfForceSettings settings;

    /// <summary>创建纯 APF force evaluator。</summary>
    public ThomasApfForceEvaluator(ThomasApfForceSettings apfSettings)
    {
        settings = apfSettings;
    }

    /// <summary>计算 Boundary、Obstacle 和 Total Force；不会生成 Gain 或操作 Transform。</summary>
    public ThomasApfForceResult Evaluate(
        Vector2 physicalPositionXZ,
        Vector2 physicalForwardXZ,
        IReadOnlyList<Vector2> boundary,
        IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacles)
    {
        if (!IsFinite(physicalPositionXZ) || boundary == null || boundary.Count < 3)
        {
            return new ThomasApfForceResult(
                Vector2.zero,
                Vector2.zero,
                Vector2.zero,
                0f,
                Vector2.zero,
                false,
                false,
                "invalid_geometry");
        }

        var boundaryForce = CalculateBoundaryForce(physicalPositionXZ, boundary, out var boundaryValid);
        if (!boundaryValid)
        {
            return new ThomasApfForceResult(
                Vector2.zero,
                Vector2.zero,
                Vector2.zero,
                0f,
                Vector2.zero,
                false,
                false,
                "non_finite_boundary_force");
        }

        var obstacleForce = CalculateObstacleForce(physicalPositionXZ, physicalForwardXZ, obstacles, out var obstacleValid);
        if (!obstacleValid)
        {
            return new ThomasApfForceResult(
                boundaryForce,
                Vector2.zero,
                Vector2.zero,
                0f,
                Vector2.zero,
                false,
                false,
                "non_finite_obstacle_force");
        }

        var total = boundaryForce + obstacleForce;
        if (!IsFinite(total))
        {
            return new ThomasApfForceResult(
                boundaryForce,
                obstacleForce,
                Vector2.zero,
                0f,
                Vector2.zero,
                false,
                false,
                "non_finite_total_force");
        }

        var forceClamped = false;
        var magnitude = total.magnitude;
        if (settings.MaxForceMagnitude <= settings.DistanceEpsilonMeters)
        {
            total = Vector2.zero;
            magnitude = 0f;
            forceClamped = true;
        }
        else if (magnitude > settings.MaxForceMagnitude)
        {
            total = total / magnitude * settings.MaxForceMagnitude;
            magnitude = settings.MaxForceMagnitude;
            forceClamped = true;
        }

        if (magnitude <= settings.NeutralForceEpsilon)
        {
            return new ThomasApfForceResult(
                boundaryForce,
                obstacleForce,
                total,
                magnitude,
                Vector2.zero,
                true,
                forceClamped,
                "force_neutral");
        }

        return new ThomasApfForceResult(
            boundaryForce,
            obstacleForce,
            total,
            magnitude,
            total / magnitude,
            true,
            forceClamped,
            string.Empty);
    }

    private Vector2 CalculateBoundaryForce(Vector2 position, IReadOnlyList<Vector2> boundary, out bool isFinite)
    {
        isFinite = true;
        if (settings.BoundaryInfluenceRadiusMeters <= settings.DistanceEpsilonMeters || settings.BoundaryStrength <= 0f)
        {
            return Vector2.zero;
        }

        var signedArea = SignedArea(boundary);
        var force = Vector2.zero;
        for (var index = 0; index < boundary.Count; index++)
        {
            var start = boundary[index];
            var end = boundary[(index + 1) % boundary.Count];
            var nearest = ClosestPointOnSegment(position, start, end);
            var offset = position - nearest;
            var distance = offset.magnitude;
            if (!IsFinite(distance))
            {
                isFinite = false;
                return Vector2.zero;
            }
            if (distance > settings.BoundaryInfluenceRadiusMeters)
            {
                continue;
            }

            var direction = distance > settings.DistanceEpsilonMeters
                ? offset / distance
                : InwardNormal(end - start, signedArea);
            var effectiveDistance = Mathf.Max(distance - settings.SafetyMarginMeters, settings.DistanceEpsilonMeters);
            var falloff = 1f - Mathf.Clamp01(distance / settings.BoundaryInfluenceRadiusMeters);
            var magnitude = settings.BoundaryStrength * falloff / (effectiveDistance * effectiveDistance);
            var contribution = direction * magnitude;
            if (!IsFinite(contribution))
            {
                isFinite = false;
                return Vector2.zero;
            }
            force += contribution;
        }
        isFinite = IsFinite(force);
        return isFinite ? force : Vector2.zero;
    }

    private Vector2 CalculateObstacleForce(
        Vector2 position,
        Vector2 forward,
        IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacles,
        out bool isFinite)
    {
        isFinite = true;
        if (obstacles == null || obstacles.Count == 0 || settings.ObstacleInfluenceRadiusMeters <= settings.DistanceEpsilonMeters || settings.ObstacleStrength <= 0f)
        {
            return Vector2.zero;
        }

        var force = Vector2.zero;
        var fallback = forward.sqrMagnitude > settings.DistanceEpsilonMeters * settings.DistanceEpsilonMeters
            ? -forward.normalized
            : Vector2.up;
        foreach (var obstacle in obstacles)
        {
            if (obstacle == null || !IsFinite(obstacle.center) || float.IsNaN(obstacle.radius) || float.IsInfinity(obstacle.radius))
            {
                continue;
            }

            var fromObstacle = position - obstacle.center;
            var centerDistance = fromObstacle.magnitude;
            if (!IsFinite(centerDistance))
            {
                isFinite = false;
                return Vector2.zero;
            }
            var clearance = centerDistance - Mathf.Max(0f, obstacle.radius) - settings.UserRadiusMeters - settings.SafetyMarginMeters;
            if (clearance >= settings.ObstacleInfluenceRadiusMeters)
            {
                continue;
            }

            var direction = centerDistance > settings.DistanceEpsilonMeters ? fromObstacle / centerDistance : fallback;
            var effectiveDistance = Mathf.Max(clearance, settings.DistanceEpsilonMeters);
            var falloff = 1f - Mathf.Clamp01(clearance / settings.ObstacleInfluenceRadiusMeters);
            var magnitude = settings.ObstacleStrength * falloff / (effectiveDistance * effectiveDistance);
            var contribution = direction * magnitude;
            if (!IsFinite(contribution))
            {
                isFinite = false;
                return Vector2.zero;
            }
            force += contribution;
        }
        isFinite = IsFinite(force);
        return isFinite ? force : Vector2.zero;
    }

    private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var edge = end - start;
        if (edge.sqrMagnitude <= 0.000001f) return start;
        var t = Mathf.Clamp01(Vector2.Dot(point - start, edge) / edge.sqrMagnitude);
        return start + edge * t;
    }

    private static Vector2 InwardNormal(Vector2 edge, float signedArea)
    {
        if (edge.sqrMagnitude <= 0.000001f) return Vector2.zero;
        var left = new Vector2(-edge.y, edge.x).normalized;
        return signedArea >= 0f ? left : -left;
    }

    private static float SignedArea(IReadOnlyList<Vector2> boundary)
    {
        var area = 0f;
        for (var index = 0; index < boundary.Count; index++)
        {
            var current = boundary[index];
            var next = boundary[(index + 1) % boundary.Count];
            area += current.x * next.y - next.x * current.y;
        }
        return area;
    }

    private static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
