using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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

/// <summary>
/// 解析第一阶段的 physical space JSON 配置，避免引入额外 JSON 依赖。
/// </summary>
public static class PhysicalSpaceConfigParser
{
    // 配置格式目前固定且较小，用正则解析可避免在第一阶段引入新的序列化依赖。
    private static readonly Regex StringFieldRegex = new Regex("\"(?<key>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex Vector2Regex = new Regex("\\[\\s*(?<x>-?\\d+(?:\\.\\d+)?)\\s*,\\s*(?<z>-?\\d+(?:\\.\\d+)?)\\s*\\]", RegexOptions.Compiled);
    private static readonly Regex ObstacleRegex = new Regex("\\{\\s*\"id\"\\s*:\\s*\"(?<id>[^\"]+)\"\\s*,\\s*\"center\"\\s*:\\s*\\[\\s*(?<x>-?\\d+(?:\\.\\d+)?)\\s*,\\s*(?<z>-?\\d+(?:\\.\\d+)?)\\s*\\]\\s*,\\s*\"radius\"\\s*:\\s*(?<radius>-?\\d+(?:\\.\\d+)?)\\s*\\}", RegexOptions.Compiled | RegexOptions.Singleline);

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

        config.id = ReadStringField(json, "id");
        config.boundary = ParseVectorArray(ExtractArraySection(json, "boundary")).ToArray();
        config.obstacles = ParseObstacles(ExtractArraySection(json, "obstacles")).ToArray();

        if (!config.IsValid)
        {
            error = $"Invalid physical space config '{config.id}'. Boundary needs at least 3 points.";
            return false;
        }

        return true;
    }

    private static string ReadStringField(string json, string key)
    {
        foreach (Match match in StringFieldRegex.Matches(json))
        {
            if (match.Groups["key"].Value == key)
            {
                return match.Groups["value"].Value;
            }
        }

        return string.Empty;
    }

    private static string ExtractArraySection(string json, string key)
    {
        var keyIndex = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return string.Empty;
        }

        var arrayStart = json.IndexOf('[', keyIndex);
        if (arrayStart < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = arrayStart; i < json.Length; i++)
        {
            // 用深度计数支持 [[x,z], ...] 这种嵌套数组，避免在第一个子数组结尾提前截断。
            if (json[i] == '[')
            {
                depth++;
            }
            else if (json[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Substring(arrayStart, i - arrayStart + 1);
                }
            }
        }

        return string.Empty;
    }

    private static List<Vector2> ParseVectorArray(string section)
    {
        var points = new List<Vector2>();
        foreach (Match match in Vector2Regex.Matches(section))
        {
            points.Add(new Vector2(ParseFloat(match.Groups["x"].Value), ParseFloat(match.Groups["z"].Value)));
        }

        return points;
    }

    private static List<PhysicalObstacle> ParseObstacles(string section)
    {
        var obstacles = new List<PhysicalObstacle>();
        foreach (Match match in ObstacleRegex.Matches(section))
        {
            obstacles.Add(new PhysicalObstacle
            {
                id = match.Groups["id"].Value,
                center = new Vector2(ParseFloat(match.Groups["x"].Value), ParseFloat(match.Groups["z"].Value)),
                radius = Mathf.Max(0f, ParseFloat(match.Groups["radius"].Value))
            });
        }

        return obstacles;
    }

    private static float ParseFloat(string value)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }
}
