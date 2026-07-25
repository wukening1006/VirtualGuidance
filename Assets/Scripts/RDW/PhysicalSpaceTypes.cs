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
/// 记录一次 deterministic physical risk 计算结果，字段名与 Unity/后端 JSON schema 对齐。
/// </summary>
[Serializable]
public sealed class PhysicalRiskState
{
    /// <summary>本次 deterministic risk 计算的唯一快照 ID。</summary>
    public string risk_snapshot_id;

    /// <summary>生成本风险快照时使用的 Physical Space Calibration ID。</summary>
    public string calibration_id;

    /// <summary>
    /// 当前 physical tracking space 的 ID。
    /// </summary>
    public string physical_space;

    /// <summary>
    /// 用户在 physical XZ 平面中的当前位置，单位：米。
    /// </summary>
    public Vector2 user_physical_position;

    /// <summary>
    /// 用户在 physical XZ 平面中的单位前向方向。
    /// </summary>
    public Vector2 user_physical_forward;

    /// <summary>
    /// 沿用户前方可行走到边界或障碍物前的距离，单位：米。
    /// </summary>
    public float clearance_forward;

    /// <summary>
    /// 沿用户左侧方向可行走到边界或障碍物前的距离，单位：米。
    /// </summary>
    public float clearance_left;

    /// <summary>
    /// 沿用户右侧方向可行走到边界或障碍物前的距离，单位：米。
    /// </summary>
    public float clearance_right;

    /// <summary>
    /// 由最小 clearance 映射出的边界风险标签。
    /// </summary>
    public string boundary_risk;

    /// <summary>
    /// 面向 reset 决策的风险标签，所有方向危险时会提升为 critical。
    /// </summary>
    public string reset_risk;

    /// <summary>
    /// 不应继续引导的方向集合，例如 forward、left、right。
    /// </summary>
    public string[] unsafe_directions = Array.Empty<string>();

    /// <summary>
    /// 可优先用于语义重定向的安全方向集合。
    /// </summary>
    public string[] safe_directions = Array.Empty<string>();

    /// <summary>
    /// 所有自然方向都危险时置为 true，后续流程应 pause 或 explicit reset。
    /// </summary>
    public bool reset_required;
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
