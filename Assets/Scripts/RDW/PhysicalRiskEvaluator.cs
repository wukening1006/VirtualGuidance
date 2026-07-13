using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 根据 physical boundary、obstacle 和用户 physical pose 计算确定性风险状态。
/// </summary>
public sealed class PhysicalRiskEvaluator : MonoBehaviour
{
    private const float Epsilon = 0.0001f;

    [Header("风险计算依赖")]
    [Tooltip("提供当前 physical space 配置的管理器；未配置时自动查找。")]
    [SerializeField] private PhysicalSpaceManager physicalSpaceManager;
    [Tooltip("提供用户 physical pose 的追踪器；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;

    [Header("风险阈值")]
    [Tooltip("风险状态刷新间隔，单位：秒；避免每帧重复执行几何求交。")]
    [SerializeField] private float evaluationIntervalSeconds = 0.2f;
    [Tooltip("单个方向的最大探测距离，单位：米；超过该距离视为足够安全。")]
    [SerializeField] private float maxClearanceMeters = 3f;
    [Tooltip("小于该 clearance 时方向被标记为 unsafe，单位：米。")]
    [SerializeField] private float unsafeClearanceMeters = 0.8f;
    [Tooltip("大于等于该 clearance 时方向被标记为 safe，单位：米。")]
    [SerializeField] private float safeClearanceMeters = 1.2f;
    [Tooltip("小于该 clearance 时风险为 critical，单位：米。")]
    [SerializeField] private float criticalClearanceMeters = 0.45f;

    private float nextEvaluationTime;
    private PhysicalRiskState currentRiskState;

    /// <summary>
    /// 风险状态刷新后触发，供调试面板、可视化和后续 guidance 生成模块订阅。
    /// </summary>
    public event Action<PhysicalRiskState> RiskStateChanged;

    /// <summary>
    /// 获取最近一次 deterministic 风险计算结果。
    /// </summary>
    public PhysicalRiskState CurrentRiskState => currentRiskState;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }
    }

    private void OnDisable()
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }
    }

    private void Update()
    {
        if (Time.unscaledTime < nextEvaluationTime)
        {
            return;
        }

        // 风险状态按固定频率刷新，避免在 Update 中每帧重复做几何求交。
        nextEvaluationTime = Time.unscaledTime + Mathf.Max(0.02f, evaluationIntervalSeconds);
        EvaluateNow();
    }

    /// <summary>
    /// 注入风险计算依赖，并立即重新计算一次当前状态。
    /// </summary>
    /// <param name="spaceManager">提供当前 physical space 配置的管理器。</param>
    /// <param name="poseTracker">提供用户 physical pose 的追踪器。</param>
    public void Configure(PhysicalSpaceManager spaceManager, UserPoseTracker poseTracker)
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }

        physicalSpaceManager = spaceManager;
        userPoseTracker = poseTracker;

        if (isActiveAndEnabled && physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }

        EvaluateNow();
    }

    /// <summary>
    /// 立即执行一次风险计算，并通知订阅者刷新 UI、可视化或 guidance 状态。
    /// </summary>
    /// <returns>最新的风险状态；依赖未就绪时返回上一次状态。</returns>
    public PhysicalRiskState EvaluateNow()
    {
        ResolveReferences();

        if (physicalSpaceManager == null || userPoseTracker == null || physicalSpaceManager.CurrentSpace == null)
        {
            return currentRiskState;
        }

        currentRiskState = Evaluate(physicalSpaceManager.CurrentSpace, userPoseTracker.PhysicalPositionXZ, userPoseTracker.PhysicalForwardXZ);
        RiskStateChanged?.Invoke(currentRiskState);
        return currentRiskState;
    }

    private PhysicalRiskState Evaluate(PhysicalSpaceConfig space, Vector2 position, Vector2 forward)
    {
        // forward 可能来自模拟输入或 XR 输入，归一化可以保证 clearance 阈值单位始终是米。
        forward = forward.sqrMagnitude > Epsilon ? forward.normalized : Vector2.up;
        var left = new Vector2(-forward.y, forward.x);
        var right = new Vector2(forward.y, -forward.x);

        var state = new PhysicalRiskState
        {
            physical_space = space.id,
            user_physical_position = position,
            user_physical_forward = forward
        };

        if (!IsPointInsidePolygon(position, space.boundary))
        {
            // 用户已经离开 tracking boundary 时不再尝试自然导览，直接标记所有方向危险。
            state.clearance_forward = 0f;
            state.clearance_left = 0f;
            state.clearance_right = 0f;
            state.boundary_risk = "critical";
            state.reset_risk = "critical";
            state.unsafe_directions = new[] {"forward", "left", "right"};
            state.safe_directions = Array.Empty<string>();
            state.reset_required = true;
            return state;
        }

        state.clearance_forward = ComputeClearance(space, position, forward);
        state.clearance_left = ComputeClearance(space, position, left);
        state.clearance_right = ComputeClearance(space, position, right);

        var minClearance = Mathf.Min(state.clearance_forward, state.clearance_left, state.clearance_right);
        state.boundary_risk = ClearanceToRisk(minClearance);
        state.reset_risk = state.boundary_risk;

        var unsafeDirections = new List<string>();
        var safeDirections = new List<string>();
        AddDirectionStatus("forward", state.clearance_forward, unsafeDirections, safeDirections);
        AddDirectionStatus("left", state.clearance_left, unsafeDirections, safeDirections);
        AddDirectionStatus("right", state.clearance_right, unsafeDirections, safeDirections);

        state.unsafe_directions = unsafeDirections.ToArray();
        state.safe_directions = safeDirections.ToArray();
        state.reset_required = unsafeDirections.Count == 3;
        if (state.reset_required)
        {
            // 三个自然方向都不可用时，reset 风险应覆盖普通 boundary risk，驱动 pause/reset 流程。
            state.reset_risk = "critical";
        }

        return state;
    }

    private float ComputeClearance(PhysicalSpaceConfig space, Vector2 origin, Vector2 direction)
    {
        var bestDistance = maxClearanceMeters;

        // 边界和障碍物都以射线命中距离表达，取最近命中点作为该方向可用空间。
        for (var i = 0; i < space.boundary.Length; i++)
        {
            var a = space.boundary[i];
            var b = space.boundary[(i + 1) % space.boundary.Length];
            if (TryRaySegmentDistance(origin, direction, a, b, out var distance))
            {
                bestDistance = Mathf.Min(bestDistance, distance);
            }
        }

        if (space.obstacles != null)
        {
            foreach (var obstacle in space.obstacles)
            {
                if (TryRayCircleDistance(origin, direction, obstacle.center, obstacle.radius, out var distance))
                {
                    bestDistance = Mathf.Min(bestDistance, distance);
                }
            }
        }

        return Mathf.Clamp(bestDistance, 0f, maxClearanceMeters);
    }

    private void AddDirectionStatus(string direction, float clearance, List<string> unsafeDirections, List<string> safeDirections)
    {
        // medium 区间既不加入 safe 也不加入 unsafe，给上层策略保留谨慎选择空间。
        if (clearance < unsafeClearanceMeters)
        {
            unsafeDirections.Add(direction);
        }
        else if (clearance >= safeClearanceMeters)
        {
            safeDirections.Add(direction);
        }
    }

    private string ClearanceToRisk(float clearance)
    {
        if (clearance < criticalClearanceMeters)
        {
            return "critical";
        }

        if (clearance < unsafeClearanceMeters)
        {
            return "high";
        }

        if (clearance < safeClearanceMeters)
        {
            return "medium";
        }

        return "low";
    }

    private void ResolveReferences()
    {
        if (physicalSpaceManager == null)
        {
            physicalSpaceManager = FindObjectOfType<PhysicalSpaceManager>();
        }

        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }
    }

    private void HandleSpaceChanged(PhysicalSpaceConfig _)
    {
        EvaluateNow();
    }

    private static bool IsPointInsidePolygon(Vector2 point, Vector2[] polygon)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            // 使用射线奇偶规则判断点是否在多边形内，支持 l_shaped_room 这类非矩形空间。
            var pi = polygon[i];
            var pj = polygon[j];
            var intersects = ((pi.y > point.y) != (pj.y > point.y)) &&
                             (point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y + Epsilon) + pi.x);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool TryRaySegmentDistance(Vector2 origin, Vector2 direction, Vector2 segmentStart, Vector2 segmentEnd, out float distance)
    {
        distance = 0f;
        var segment = segmentEnd - segmentStart;
        var denominator = Cross(direction, segment);
        if (Mathf.Abs(denominator) < Epsilon)
        {
            // 平行时没有稳定交点；该边不会限制当前射线方向。
            return false;
        }

        var delta = segmentStart - origin;
        var rayT = Cross(delta, segment) / denominator;
        var segmentT = Cross(delta, direction) / denominator;
        if (rayT >= 0f && segmentT >= 0f && segmentT <= 1f)
        {
            distance = rayT;
            return true;
        }

        return false;
    }

    private static bool TryRayCircleDistance(Vector2 origin, Vector2 direction, Vector2 center, float radius, out float distance)
    {
        distance = 0f;
        if (radius <= 0f)
        {
            return false;
        }

        var toOrigin = origin - center;
        var c = Vector2.Dot(toOrigin, toOrigin) - radius * radius;
        if (c <= 0f)
        {
            // 用户已经位于障碍范围内，clearance 视为 0，触发最高风险路径。
            distance = 0f;
            return true;
        }

        var b = Vector2.Dot(toOrigin, direction);
        var discriminant = b * b - c;
        if (discriminant < 0f)
        {
            return false;
        }

        var t = -b - Mathf.Sqrt(discriminant);
        if (t >= 0f)
        {
            distance = t;
            return true;
        }

        return false;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }
}
