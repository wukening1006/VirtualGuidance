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

    /// <summary>该几何快照所属的 Physical Space ID。</summary>
    public string PhysicalSpaceId { get; }
    /// <summary>边界顶点的只读副本，单位：米。</summary>
    public IReadOnlyList<Vector2> Boundary => boundary ?? Array.Empty<Vector2>();
    /// <summary>障碍物事实的只读副本。</summary>
    public IReadOnlyList<PhysicalRiskObstacleSnapshot> Obstacles =>
        obstacles ?? Array.Empty<PhysicalRiskObstacleSnapshot>();
    /// <summary>边界顶点的算术中心，单位：米；无有效边界时为零。</summary>
    public Vector2 CenterXZ { get; }
    /// <summary>关联的风险快照 ID；尚无风险快照时为空。</summary>
    public string RiskSnapshotId { get; }
    /// <summary>关联的风险修订号；尚无风险快照时为零。</summary>
    public int RiskRevision { get; }
    /// <summary>快照是否具备可供后续 Redirector 使用的最小边界事实。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(PhysicalSpaceId) && Boundary.Count >= 3;

    /// <summary>创建一份与当前 Physical Space 和风险身份绑定的不可变几何快照。</summary>
    public RdwGeometrySnapshot(
        string physicalSpaceId,
        IEnumerable<Vector2> boundaryPoints,
        IEnumerable<PhysicalRiskObstacleSnapshot> obstacleFacts,
        string riskSnapshotId = "",
        int riskRevision = 0)
    {
        PhysicalSpaceId = physicalSpaceId ?? string.Empty;
        var boundaryCopy = new List<Vector2>(boundaryPoints ?? Array.Empty<Vector2>()).ToArray();
        boundary = Array.AsReadOnly(boundaryCopy);
        obstacles = Array.AsReadOnly(new List<PhysicalRiskObstacleSnapshot>(
            obstacleFacts ?? Array.Empty<PhysicalRiskObstacleSnapshot>()).ToArray());
        CenterXZ = CalculateCenter(boundaryCopy);
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
    }

    private static Vector2 CalculateCenter(IReadOnlyList<Vector2> points)
    {
        if (points == null || points.Count == 0)
        {
            return Vector2.zero;
        }

        var sum = Vector2.zero;
        for (var i = 0; i < points.Count; i++)
        {
            sum += points[i];
        }

        return sum / points.Count;
    }
}
