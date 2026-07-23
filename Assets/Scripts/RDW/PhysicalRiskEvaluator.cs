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
    [Tooltip("提供校准后的 Physical Room Pose；XR Raw Tracking Pose 不得直接写入此追踪器。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("提供风险快照关联的 calibration_id。")]
    [SerializeField] private PhysicalSpaceCalibration physicalSpaceCalibration;

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
    private int riskRevision;
    private string lastExecutionSignature = string.Empty;

    /// <summary>
    /// 每次定时或显式风险计算完成后触发，即使风险等级未变化也会重复触发。
    /// 参数为最新 PhysicalRiskState，供调试面板、可视化和 guidance 模块使用。
    /// </summary>
    public event Action<PhysicalRiskState> RiskStateChanged;

    /// <summary>
    /// 获取最近一次 deterministic 风险计算结果。
    /// </summary>
    public PhysicalRiskState CurrentRiskState => currentRiskState;
    /// <summary>当前是否仍有可用于运行期安全判断的 Tracking 输入。</summary>
    public bool IsTrackingValid => physicalSpaceCalibration != null &&
                                   physicalSpaceCalibration.HasRawTrackingPose &&
                                   physicalSpaceCalibration.State != PhysicalCalibrationState.TemporarilyTrackingLost;
    /// <summary>当前 Physical Space Calibration 是否有效。</summary>
    public bool IsCalibrationValid => physicalSpaceCalibration != null &&
                                      physicalSpaceCalibration.IsCalibrationValid;
    /// <summary>当前校准是否绑定到风险计算器正在使用的 Physical Space。</summary>
    public bool PhysicalSpaceMatches => physicalSpaceManager != null &&
                                        physicalSpaceManager.CurrentSpace != null &&
                                        physicalSpaceCalibration != null &&
                                        string.Equals(
                                            physicalSpaceCalibration.CalibratedSpaceId,
                                            physicalSpaceManager.CurrentSpaceId,
                                            StringComparison.Ordinal);

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
        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
        }
    }

    private void OnDisable()
    {
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }
        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
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
    public void Configure(
        PhysicalSpaceManager spaceManager,
        UserPoseTracker poseTracker,
        PhysicalSpaceCalibration calibration = null)
    {
        var nextSpaceManager = spaceManager != null ? spaceManager : physicalSpaceManager;
        var nextPoseTracker = poseTracker != null ? poseTracker : userPoseTracker;
        if (physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged -= HandleSpaceChanged;
        }
        if (physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged -= HandleCalibrationStateChanged;
        }

        physicalSpaceManager = nextSpaceManager;
        userPoseTracker = nextPoseTracker;
        if (calibration != null) physicalSpaceCalibration = calibration;

        if (isActiveAndEnabled && physicalSpaceManager != null)
        {
            physicalSpaceManager.CurrentSpaceChanged += HandleSpaceChanged;
        }
        if (isActiveAndEnabled && physicalSpaceCalibration != null)
        {
            physicalSpaceCalibration.StateChanged += HandleCalibrationStateChanged;
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

        if (physicalSpaceManager == null || userPoseTracker == null || physicalSpaceManager.CurrentSpace == null ||
            physicalSpaceCalibration == null ||
            !physicalSpaceCalibration.IsCalibrationValid ||
            !string.Equals(
                physicalSpaceCalibration.CalibratedSpaceId,
                physicalSpaceManager.CurrentSpaceId,
                StringComparison.Ordinal))
        {
            ClearInvalidRiskState();
            return null;
        }

        var space = physicalSpaceManager.CurrentSpace;
        var sample = Evaluate(space, userPoseTracker.PhysicalPositionXZ, userPoseTracker.PhysicalForwardXZ);
        var calibrationId = physicalSpaceCalibration.CalibrationId;
        var executionSignature = BuildExecutionSignature(sample, space.id, calibrationId);
        if (riskRevision == 0 || !string.Equals(lastExecutionSignature, executionSignature, StringComparison.Ordinal))
        {
            riskRevision++;
            lastExecutionSignature = executionSignature;
        }

        currentRiskState = CreateSnapshot(sample, space, calibrationId);
        RiskStateChanged?.Invoke(currentRiskState);
        return currentRiskState;
    }

    /// <summary>
    /// 检查风险快照是否仍与当前 Calibration 和 Physical Space 身份一致。
    /// </summary>
    public bool IsRiskIdentityCurrent(PhysicalRiskState riskState)
    {
        return riskState != null &&
               !string.IsNullOrWhiteSpace(riskState.risk_snapshot_id) &&
               IsCalibrationValid &&
               PhysicalSpaceMatches &&
               string.Equals(
                   riskState.calibration_id,
                   physicalSpaceCalibration.CalibrationId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   riskState.physical_space,
                   physicalSpaceManager.CurrentSpaceId,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// 从指定风险快照读取 forward、left 或 right 的 clearance，单位：米。
    /// </summary>
    /// <returns>方向有效且 clearance 为有限数值时返回 true。</returns>
    public bool TryGetDirectionClearance(PhysicalRiskState riskState, string direction, out float clearanceMeters)
    {
        clearanceMeters = 0f;
        if (riskState == null)
        {
            return false;
        }

        switch (direction)
        {
            case "forward":
                clearanceMeters = riskState.clearance_forward;
                break;
            case "left":
                clearanceMeters = riskState.clearance_left;
                break;
            case "right":
                clearanceMeters = riskState.clearance_right;
                break;
            default:
                return false;
        }

        return !float.IsNaN(clearanceMeters) && !float.IsInfinity(clearanceMeters);
    }

    /// <summary>判断指定 clearance 是否进入本风险计算器配置的 critical 阈值。</summary>
    public bool IsCriticalClearance(float clearanceMeters)
    {
        return !float.IsNaN(clearanceMeters) &&
               !float.IsInfinity(clearanceMeters) &&
               clearanceMeters < criticalClearanceMeters;
    }

    private RiskSample Evaluate(PhysicalSpaceConfig space, Vector2 position, Vector2 forward)
    {
        // forward 可能来自模拟输入或 XR 输入，归一化可以保证 clearance 阈值单位始终是米。
        forward = forward.sqrMagnitude > Epsilon ? forward.normalized : Vector2.up;
        var left = new Vector2(-forward.y, forward.x);
        var right = new Vector2(forward.y, -forward.x);

        var sample = new RiskSample
        {
            physicalPosition = position,
            physicalForward = forward
        };

        if (!PhysicalSpaceGeometry.ContainsPoint(space.boundary, position))
        {
            // 用户已经离开 tracking boundary 时不再尝试自然导览，直接标记所有方向危险。
            sample.clearanceForward = 0f;
            sample.clearanceLeft = 0f;
            sample.clearanceRight = 0f;
            sample.boundaryRisk = "critical";
            sample.resetRisk = "critical";
            sample.unsafeDirections = new[] {"forward", "left", "right"};
            sample.safeDirections = Array.Empty<string>();
            sample.resetRequired = true;
            return sample;
        }

        sample.clearanceForward = ComputeClearance(space, position, forward);
        sample.clearanceLeft = ComputeClearance(space, position, left);
        sample.clearanceRight = ComputeClearance(space, position, right);

        var minClearance = Mathf.Min(sample.clearanceForward, sample.clearanceLeft, sample.clearanceRight);
        sample.boundaryRisk = ClearanceToRisk(minClearance);
        sample.resetRisk = sample.boundaryRisk;

        var unsafeDirections = new List<string>();
        var safeDirections = new List<string>();
        AddDirectionStatus("forward", sample.clearanceForward, unsafeDirections, safeDirections);
        AddDirectionStatus("left", sample.clearanceLeft, unsafeDirections, safeDirections);
        AddDirectionStatus("right", sample.clearanceRight, unsafeDirections, safeDirections);

        sample.unsafeDirections = unsafeDirections.ToArray();
        sample.safeDirections = safeDirections.ToArray();
        sample.resetRequired = unsafeDirections.Count == 3;
        if (sample.resetRequired)
        {
            // 三个自然方向都不可用时，reset 风险应覆盖普通 boundary risk，驱动 pause/reset 流程。
            sample.resetRisk = "critical";
        }

        return sample;
    }

    private PhysicalRiskState CreateSnapshot(RiskSample sample, PhysicalSpaceConfig space, string calibrationId)
    {
        var obstacles = new List<PhysicalRiskObstacleSnapshot>();
        if (space.obstacles != null)
        {
            foreach (var obstacle in space.obstacles)
            {
                obstacles.Add(new PhysicalRiskObstacleSnapshot(obstacle.id, obstacle.center, obstacle.radius));
            }
        }

        var critical = string.Equals(sample.boundaryRisk, "critical", StringComparison.Ordinal) ||
                       string.Equals(sample.resetRisk, "critical", StringComparison.Ordinal);
        return new PhysicalRiskState(
            Guid.NewGuid().ToString("N"),
            riskRevision,
            calibrationId,
            DateTime.UtcNow.ToString("o"),
            GuidanceProtocolVersions.RiskPolicy,
            "unity",
            space.id,
            sample.physicalPosition,
            sample.physicalForward,
            space.boundary,
            obstacles,
            sample.clearanceForward,
            sample.clearanceLeft,
            sample.clearanceRight,
            sample.boundaryRisk,
            sample.resetRisk,
            sample.unsafeDirections,
            sample.safeDirections,
            critical,
            sample.resetRequired);
    }

    private static string BuildExecutionSignature(RiskSample sample, string physicalSpaceId, string calibrationId)
    {
        return string.Join(
            "|",
            GuidanceProtocolVersions.RiskPolicy,
            calibrationId ?? string.Empty,
            physicalSpaceId ?? string.Empty,
            sample.boundaryRisk ?? string.Empty,
            sample.resetRisk ?? string.Empty,
            sample.resetRequired ? "reset" : "no_reset",
            string.Join(",", sample.unsafeDirections ?? Array.Empty<string>()),
            string.Join(",", sample.safeDirections ?? Array.Empty<string>()));
    }

    private sealed class RiskSample
    {
        public Vector2 physicalPosition;
        public Vector2 physicalForward;
        public float clearanceForward;
        public float clearanceLeft;
        public float clearanceRight;
        public string boundaryRisk;
        public string resetRisk;
        public string[] unsafeDirections = Array.Empty<string>();
        public string[] safeDirections = Array.Empty<string>();
        public bool resetRequired;
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

    private void HandleCalibrationStateChanged(PhysicalCalibrationState _)
    {
        EvaluateNow();
    }

    private void ClearInvalidRiskState()
    {
        if (currentRiskState == null)
        {
            return;
        }

        currentRiskState = null;
        lastExecutionSignature = string.Empty;
        RiskStateChanged?.Invoke(null);
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
