using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 描述 physical tracking space 中的圆形障碍物，字段名与配置 JSON 保持一致。
/// </summary>
[Serializable]
public sealed class PhysicalObstacle
{
    /// <summary>
    /// 障碍物唯一标识，用于日志和调试可视化命名。
    /// </summary>
    public string id;

    /// <summary>
    /// 障碍物在 physical XZ 平面中的圆心坐标，单位：米。
    /// </summary>
    public Vector2 center;

    /// <summary>
    /// 障碍物半径，单位：米；风险计算会将其作为射线-圆形碰撞距离。
    /// </summary>
    public float radius;
}

/// <summary>
/// 描述一个可切换的 physical tracking space 配置，供风险计算和可视化共享。
/// </summary>
[Serializable]
public sealed class PhysicalSpaceConfig
{
    /// <summary>
    /// 空间 ID，例如 wide_room、narrow_room，用于实验条件切换和日志记录。
    /// </summary>
    public string id;

    /// <summary>
    /// physical XZ 平面上的边界多边形顶点，单位：米；按顺序连接成闭合区域。
    /// </summary>
    public Vector2[] boundary = Array.Empty<Vector2>();

    /// <summary>
    /// 空间内的圆形障碍物列表，用于任务运行时判断候选方向是否被阻塞。
    /// </summary>
    public PhysicalObstacle[] obstacles = Array.Empty<PhysicalObstacle>();

    /// <summary>
    /// 判断配置是否具备最基本的可计算条件，避免无效边界进入风险计算。
    /// </summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(id) && boundary != null && boundary.Length >= 3;
}

/// <summary>
/// 保存障碍物的只读风险上下文，避免发布快照后被场景配置修改。
/// </summary>
public sealed class PhysicalRiskObstacleSnapshot
{
    /// <summary>障碍物 ID。</summary>
    public string id { get; }
    /// <summary>障碍物在 Physical XZ 平面中的圆心，单位：米。</summary>
    public Vector2 center { get; }
    /// <summary>障碍物半径，单位：米。</summary>
    public float radius { get; }

    /// <summary>创建一份障碍物上下文快照。</summary>
    public PhysicalRiskObstacleSnapshot(string obstacleId, Vector2 obstacleCenter, float obstacleRadius)
    {
        id = obstacleId ?? string.Empty;
        center = obstacleCenter;
        radius = obstacleRadius;
    }
}

/// <summary>
/// 表示某个 Physical Heading 的移动安全等级。
/// </summary>
public enum HeadingSafety
{
    /// <summary>净空低于 Critical 阈值，禁止转入或移动。</summary>
    Critical,
    /// <summary>净空低于最低恢复阈值，禁止转入或移动。</summary>
    Unsafe,
    /// <summary>净空处于滞回区间，禁止移动但允许继续规划。</summary>
    Unknown,
    /// <summary>净空达到安全阈值，允许作为移动方向。</summary>
    Safe
}

/// <summary>
/// 保存相对当前 Physical Forward 的单个朝向采样事实。
/// </summary>
public readonly struct HeadingSample
{
    /// <summary>相对当前 Physical Forward 的有符号偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>该采样在 Physical XZ 平面中的单位前向。</summary>
    public Vector2 PhysicalForward { get; }
    /// <summary>考虑用户半径和边界余量后的走廊净空，单位：米。</summary>
    public float ClearanceMeters { get; }
    /// <summary>该朝向的安全等级。</summary>
    public HeadingSafety Safety { get; }

    /// <summary>创建一项不可变 Heading Profile 采样。</summary>
    public HeadingSample(
        float relativeYawDegrees,
        Vector2 physicalForward,
        float clearanceMeters,
        HeadingSafety safety)
    {
        RelativeYawDegrees = relativeYawDegrees;
        PhysicalForward = physicalForward.sqrMagnitude > 0.0001f
            ? physicalForward.normalized
            : Vector2.up;
        ClearanceMeters = Math.Max(0f, clearanceMeters);
        Safety = safety;
    }
}

/// <summary>
/// Unity 确定性 RDW 层发布的不可变 Physical Risk 快照。
/// </summary>
public sealed class PhysicalRiskState
{
    /// <summary>本次采样的唯一快照 ID。</summary>
    public string risk_snapshot_id { get; }
    /// <summary>执行相关风险发生实质变化时才递增的单调修订号。</summary>
    public int risk_revision { get; }
    /// <summary>生成快照时使用的 Physical Space Calibration ID。</summary>
    public string calibration_id { get; }
    /// <summary>ISO 8601 UTC 采样时间。</summary>
    public string sampled_at_utc { get; }
    /// <summary>生成快照的风险策略版本。</summary>
    public string risk_policy_version { get; }
    /// <summary>风险事实生产者，正式运行固定为 unity。</summary>
    public string source { get; }
    /// <summary>当前 Physical Tracking Space ID。</summary>
    public string physical_space { get; }
    /// <summary>用户在 Physical XZ 平面中的位置，单位：米。</summary>
    public Vector2 user_physical_position { get; }
    /// <summary>用户在 Physical XZ 平面中的单位前向方向。</summary>
    public Vector2 user_physical_forward { get; }
    /// <summary>Physical Boundary 顶点的只读副本，单位：米。</summary>
    public IReadOnlyList<Vector2> boundary_context { get; }
    /// <summary>Physical Obstacles 的只读副本。</summary>
    public IReadOnlyList<PhysicalRiskObstacleSnapshot> obstacle_context { get; }
    /// <summary>前方 clearance，单位：米。</summary>
    public float clearance_forward { get; }
    /// <summary>左侧 clearance，单位：米。</summary>
    public float clearance_left { get; }
    /// <summary>右侧 clearance，单位：米。</summary>
    public float clearance_right { get; }
    /// <summary>由最小 clearance 映射出的风险标签。</summary>
    public string boundary_risk { get; }
    /// <summary>面向 Reset 决策的风险标签。</summary>
    public string reset_risk { get; }
    /// <summary>不可执行方向的只读集合。</summary>
    public IReadOnlyList<string> unsafe_directions { get; }
    /// <summary>可执行方向的只读集合。</summary>
    public IReadOnlyList<string> safe_directions { get; }
    /// <summary>快照是否达到 Critical 风险。</summary>
    public bool critical { get; }
    /// <summary>确定性安全链是否要求 Reset。</summary>
    public bool reset_required { get; }
    /// <summary>多角度 Physical Heading Profile 的只读副本。</summary>
    public IReadOnlyList<HeadingSample> heading_profile { get; }

    /// <summary>创建完整、深拷贝且发布后不可修改的风险快照。</summary>
    public PhysicalRiskState(
        string riskSnapshotId = "",
        int riskRevision = 0,
        string calibrationId = "",
        string sampledAtUtc = "",
        string riskPolicyVersion = "",
        string snapshotSource = "",
        string physicalSpace = "",
        Vector2 physicalPosition = default(Vector2),
        Vector2 physicalForward = default(Vector2),
        IEnumerable<Vector2> boundaryContext = null,
        IEnumerable<PhysicalRiskObstacleSnapshot> obstacleContext = null,
        float clearanceForward = 0f,
        float clearanceLeft = 0f,
        float clearanceRight = 0f,
        string boundaryRisk = "",
        string resetRisk = "",
        IEnumerable<string> unsafeDirections = null,
        IEnumerable<string> safeDirections = null,
        bool isCritical = false,
        bool resetRequired = false,
        IEnumerable<HeadingSample> headingProfile = null)
    {
        risk_snapshot_id = riskSnapshotId ?? string.Empty;
        risk_revision = Math.Max(0, riskRevision);
        calibration_id = calibrationId ?? string.Empty;
        sampled_at_utc = sampledAtUtc ?? string.Empty;
        risk_policy_version = riskPolicyVersion ?? string.Empty;
        source = snapshotSource ?? string.Empty;
        physical_space = physicalSpace ?? string.Empty;
        user_physical_position = physicalPosition;
        user_physical_forward = physicalForward;
        boundary_context = Array.AsReadOnly(new List<Vector2>(boundaryContext ?? Array.Empty<Vector2>()).ToArray());
        obstacle_context = Array.AsReadOnly(new List<PhysicalRiskObstacleSnapshot>(
            obstacleContext ?? Array.Empty<PhysicalRiskObstacleSnapshot>()).ToArray());
        clearance_forward = clearanceForward;
        clearance_left = clearanceLeft;
        clearance_right = clearanceRight;
        boundary_risk = boundaryRisk ?? string.Empty;
        reset_risk = resetRisk ?? string.Empty;
        unsafe_directions = Array.AsReadOnly(new List<string>(unsafeDirections ?? Array.Empty<string>()).ToArray());
        safe_directions = Array.AsReadOnly(new List<string>(safeDirections ?? Array.Empty<string>()).ToArray());
        critical = isCritical;
        reset_required = resetRequired;
        heading_profile = Array.AsReadOnly(new List<HeadingSample>(
            headingProfile ?? Array.Empty<HeadingSample>()).ToArray());
    }

    /// <summary>
    /// 按相对偏航查询最近的 Heading Profile 采样。
    /// </summary>
    /// <returns>Profile 包含至少一项时返回 true。</returns>
    public bool TryGetNearestHeading(float relativeYawDegrees, out HeadingSample sample)
    {
        sample = default;
        var bestDelta = float.PositiveInfinity;
        var found = false;
        foreach (var item in heading_profile)
        {
            var delta = Mathf.Abs(Mathf.DeltaAngle(relativeYawDegrees, item.RelativeYawDegrees));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                sample = item;
                found = true;
            }
        }

        return found;
    }
}

internal static class PhysicalSpaceGeometry
{
    private const float Epsilon = 0.000001f;

    internal static bool ContainsPoint(Vector2[] boundary, Vector2 point)
    {
        if (boundary == null || boundary.Length < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
        {
            var current = boundary[i];
            var previous = boundary[j];
            var intersects = ((current.y > point.y) != (previous.y > point.y)) &&
                             (point.x < (previous.x - current.x) * (point.y - current.y) /
                              (previous.y - current.y + Epsilon) + current.x);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

/// <summary>
/// 使用项目现有 Newtonsoft.Json 能力解析 physical space 结构化配置。
/// </summary>
public static class PhysicalSpaceConfigParser
{
    /// <summary>
    /// 将 JSON 文本解析为 physical space 配置，并返回结构化错误原因。
    /// </summary>
    /// <param name="json">配置文件的原始 JSON 文本。</param>
    /// <param name="config">解析成功后的 physical space 配置。</param>
    /// <param name="error">解析失败时返回给调用方和日志的错误说明。</param>
    /// <returns>如果配置可用于风险计算则返回 true。</returns>
    public static bool TryParse(string json, out PhysicalSpaceConfig config, out string error)
    {
        config = new PhysicalSpaceConfig();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Physical space JSON is empty.";
            return false;
        }

        PhysicalSpaceConfigDto dto;
        try
        {
            dto = JsonConvert.DeserializeObject<PhysicalSpaceConfigDto>(json);
        }
        catch (JsonException exception)
        {
            error = $"Physical space JSON is invalid: {exception.Message}";
            return false;
        }

        if (dto == null)
        {
            error = "Physical space JSON does not contain an object.";
            return false;
        }

        config.id = dto.id != null ? dto.id.Trim() : string.Empty;
        if (!TryConvertBoundary(dto.boundary, out config.boundary, out error) ||
            !TryConvertObstacles(dto.obstacles, out config.obstacles, out error))
        {
            return false;
        }

        if (!config.IsValid)
        {
            error = $"Invalid physical space config '{config.id}'. Boundary needs at least 3 points.";
            return false;
        }

        return true;
    }

    private static bool TryConvertBoundary(float[][] source, out Vector2[] boundary, out string error)
    {
        boundary = Array.Empty<Vector2>();
        error = null;
        if (source == null)
        {
            error = "Physical space boundary is missing.";
            return false;
        }

        var converted = new List<Vector2>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (!TryConvertPoint(source[i], out var point))
            {
                error = $"Physical space boundary point {i} must contain exactly two finite numbers.";
                return false;
            }

            converted.Add(point);
        }

        boundary = converted.ToArray();
        return true;
    }

    private static bool TryConvertObstacles(
        PhysicalObstacleDto[] source,
        out PhysicalObstacle[] obstacles,
        out string error)
    {
        error = null;
        if (source == null || source.Length == 0)
        {
            obstacles = Array.Empty<PhysicalObstacle>();
            return true;
        }

        var converted = new List<PhysicalObstacle>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var item = source[i];
            if (item == null || string.IsNullOrWhiteSpace(item.id) || !TryConvertPoint(item.center, out var center) ||
                float.IsNaN(item.radius) || float.IsInfinity(item.radius) || item.radius < 0f)
            {
                obstacles = Array.Empty<PhysicalObstacle>();
                error = $"Physical space obstacle {i} has an invalid id, center, or radius.";
                return false;
            }

            converted.Add(new PhysicalObstacle
            {
                id = item.id.Trim(),
                center = center,
                radius = item.radius
            });
        }

        obstacles = converted.ToArray();
        return true;
    }

    private static bool TryConvertPoint(float[] source, out Vector2 point)
    {
        point = default;
        if (source == null || source.Length != 2 ||
            float.IsNaN(source[0]) || float.IsInfinity(source[0]) ||
            float.IsNaN(source[1]) || float.IsInfinity(source[1]))
        {
            return false;
        }

        point = new Vector2(source[0], source[1]);
        return true;
    }

    private sealed class PhysicalSpaceConfigDto
    {
        public string id;
        public float[][] boundary;
        public PhysicalObstacleDto[] obstacles;
    }

    private sealed class PhysicalObstacleDto
    {
        public string id;
        public float[] center;
        public float radius;
    }
}
