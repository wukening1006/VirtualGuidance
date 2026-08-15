using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 冻结一次 RDW 帧使用的 Physical Space 几何事实，避免算法读取可变场景配置。
/// </summary>
public readonly struct RdwGeometrySnapshot
{
    private readonly IReadOnlyList<Vector2> boundary;
    private readonly IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacles;
    private readonly bool steeringCenterValid;

    /// <summary>该几何快照所属的 Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>边界顶点的只读副本，单位：米。</summary>
    public IReadOnlyList<Vector2> Boundary => boundary ?? Array.Empty<Vector2>();
    /// <summary>障碍物事实的只读副本。</summary>
    public IReadOnlyList<PhysicalRiskObstacleSnapshot> Obstacles =>
        obstacles ?? Array.Empty<PhysicalRiskObstacleSnapshot>();
    /// <summary>用于连续 steering 的稳定内部中心，单位：米。</summary>
    public Vector2 SteeringCenterXZ { get; }
    /// <summary>兼容旧调用的中心别名；新代码应使用 SteeringCenterXZ。</summary>
    public Vector2 CenterXZ => SteeringCenterXZ;
    /// <summary>关联的风险快照 ID；尚无风险快照时为空。</summary>
    public string RiskSnapshotId { get; }
    /// <summary>关联的风险修订号；尚无风险快照时为零。</summary>
    public int RiskRevision { get; }
    /// <summary>同源 PhysicalRiskEvaluator 是否报告 Critical；仅用于清理算法历史。</summary>
    public bool IsCriticalRisk { get; }
    /// <summary>快照是否具备可供后续 Redirector 使用的最小边界事实。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(PhysicalSpaceId) && Boundary.Count >= 3 && steeringCenterValid;

    /// <summary>创建一份与当前 Physical Space 和风险身份绑定的不可变几何快照。</summary>
    public RdwGeometrySnapshot(
        string physicalSpaceId,
        IEnumerable<Vector2> boundaryPoints,
        IEnumerable<PhysicalRiskObstacleSnapshot> obstacleFacts,
        string riskSnapshotId = "",
        int riskRevision = 0,
        bool isCriticalRisk = false)
    {
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        var boundaryCopy = new List<Vector2>(boundaryPoints ?? Array.Empty<Vector2>()).ToArray();
        boundary = Array.AsReadOnly(boundaryCopy);
        obstacles = Array.AsReadOnly(new List<PhysicalRiskObstacleSnapshot>(
            obstacleFacts ?? Array.Empty<PhysicalRiskObstacleSnapshot>()).ToArray());
        SteeringCenterXZ = CalculateSteeringCenter(boundaryCopy, obstacles, out var centerValid);
        steeringCenterValid = centerValid;
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        IsCriticalRisk = isCriticalRisk;
    }

    private static Vector2 CalculateSteeringCenter(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacleFacts,
        out bool isValid)
    {
        isValid = false;
        if (points == null || points.Count == 0)
        {
            return Vector2.zero;
        }

        var twiceArea = 0f;
        var centroid = Vector2.zero;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            var cross = current.x * next.y - next.x * current.y;
            twiceArea += cross;
            centroid += (current + next) * cross;
        }

        if (Mathf.Abs(twiceArea) > 0.000001f)
        {
            var areaWeighted = centroid / (3f * twiceArea);
            if (IsValidSteeringCenter(points, obstacleFacts, areaWeighted))
            {
                isValid = true;
                return areaWeighted;
            }
        }

        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Count; i++)
        {
            min = Vector2.Min(min, points[i]);
            max = Vector2.Max(max, points[i]);
        }

        var candidates = new[]
        {
            (min + max) * 0.5f,
            (min + max) * 0.25f + points[0] * 0.5f,
            (min + max) * 0.5f + new Vector2(-0.25f, -0.25f),
            (min + max) * 0.5f + new Vector2(0.25f, -0.25f),
            (min + max) * 0.5f + new Vector2(-0.25f, 0.25f),
            (min + max) * 0.5f + new Vector2(0.25f, 0.25f),
            points[0]
        };
        foreach (var candidate in candidates)
        {
            if (IsValidSteeringCenter(points, obstacleFacts, candidate))
            {
                isValid = true;
                return candidate;
            }
        }

        // 质心不可用时，在包围盒内部做确定性搜索，并选择净空最大的合法点。
        const int gridResolution = 24;
        var bestCandidate = Vector2.zero;
        var bestClearance = float.NegativeInfinity;
        for (var y = 1; y <= gridResolution; y++)
        {
            for (var x = 1; x <= gridResolution; x++)
            {
                var candidate = new Vector2(
                    Mathf.Lerp(min.x, max.x, x / (gridResolution + 1f)),
                    Mathf.Lerp(min.y, max.y, y / (gridResolution + 1f)));
                if (!IsValidSteeringCenter(points, obstacleFacts, candidate))
                {
                    continue;
                }

                var clearance = CalculateClearance(points, obstacleFacts, candidate);
                if (clearance > bestClearance)
                {
                    bestCandidate = candidate;
                    bestClearance = clearance;
                    isValid = true;
                }
            }
        }

        return bestCandidate;
    }

    private static bool IsValidSteeringCenter(IReadOnlyList<Vector2> points, IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacleFacts, Vector2 candidate)
    {
        if (!PhysicalSpaceGeometry.ContainsPoint(points, candidate))
        {
            return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            if (DistanceToSegment(candidate, points[index], points[(index + 1) % points.Count]) <= 0.001f)
            {
                return false;
            }
        }

        if (obstacleFacts == null)
        {
            return true;
        }

        foreach (var obstacle in obstacleFacts)
        {
            if (obstacle != null && Vector2.Distance(candidate, obstacle.center) <= obstacle.radius + 0.1f)
            {
                return false;
            }
        }

        return true;
    }

    private static float CalculateClearance(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacleFacts,
        Vector2 candidate)
    {
        var clearance = float.PositiveInfinity;
        for (var index = 0; index < points.Count; index++)
        {
            clearance = Mathf.Min(
                clearance,
                DistanceToSegment(candidate, points[index], points[(index + 1) % points.Count]));
        }

        if (obstacleFacts == null)
        {
            return clearance;
        }

        foreach (var obstacle in obstacleFacts)
        {
            if (obstacle != null)
            {
                clearance = Mathf.Min(clearance, Vector2.Distance(candidate, obstacle.center) - obstacle.radius);
            }
        }
        return clearance;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        if (segment.sqrMagnitude <= 0.000001f)
        {
            return Vector2.Distance(point, start);
        }

        var t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude);
        return Vector2.Distance(point, start + segment * t);
    }
}
