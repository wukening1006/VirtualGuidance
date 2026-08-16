using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 根据 physical boundary、obstacle 和用户 physical pose 计算确定性风险状态。
/// </summary>
public sealed class PhysicalRiskEvaluator : MonoBehaviour, IPhysicalHeadingEvaluator
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
    [Tooltip("参与者水平碰撞半径，单位：米；用于扩张障碍物并构建移动走廊。")]
    [SerializeField, Min(0f)] private float userRadiusMeters = 0.22f;
    [Tooltip("用户半径之外保留的边界安全余量，单位：米。")]
    [SerializeField, Min(0f)] private float boundaryMarginMeters = 0.08f;

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
    /// <summary>获取参与者碰撞半径这一共享 Physical Fact，单位：米。</summary>
    public float UserRadiusMeters => Mathf.Max(0f, userRadiusMeters);
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

    /// <summary>判断指定 clearance 是否进入本风险计算器配置的 critical 阈值。</summary>
    public bool IsCriticalClearance(float clearanceMeters)
    {
        return !float.IsNaN(clearanceMeters) &&
               !float.IsInfinity(clearanceMeters) &&
               clearanceMeters < criticalClearanceMeters;
    }

    /// <summary>判断精确 Heading Evaluation 是否达到可移动的 Safe 等级。</summary>
    public static bool IsMovementSafe(HeadingSample sample) => sample.Safety == HeadingSafety.Safe;

    /// <summary>判断精确 Heading Evaluation 是否可作为原地转向后的恢复朝向。</summary>
    public static bool IsRecoveryHeadingAcceptable(HeadingSample sample)
    {
        return sample.Safety == HeadingSafety.Safe;
    }

    /// <summary>
    /// 基于指定风险快照的位置，对一条绝对 Physical Forward 执行精确走廊风险计算。
    /// 本方法不发布新快照，也不修改 risk revision，供冻结恢复航向和响应终验使用。
    /// </summary>
    /// <param name="riskState">提供物理位置、校准与空间身份的最新可信风险快照。</param>
    /// <param name="absolutePhysicalForward">Physical Room XZ 平面中的绝对单位朝向。</param>
    /// <param name="sample">成功时返回精确 clearance 与安全等级。</param>
    /// <returns>风险身份有效且朝向可计算时返回 true。</returns>
    public bool TryEvaluateAbsoluteHeading(
        PhysicalRiskState riskState,
        Vector2 absolutePhysicalForward,
        out HeadingSample sample)
    {
        sample = default;
        ResolveReferences();
        if (!IsRiskIdentityCurrent(riskState) ||
            absolutePhysicalForward.sqrMagnitude <= Epsilon ||
            float.IsNaN(absolutePhysicalForward.x) ||
            float.IsNaN(absolutePhysicalForward.y) ||
            float.IsInfinity(absolutePhysicalForward.x) ||
            float.IsInfinity(absolutePhysicalForward.y))
        {
            return false;
        }

        var normalizedForward = absolutePhysicalForward.normalized;
        var relativeYaw = SignedAngle(riskState.user_physical_forward, normalizedForward);
        return TryEvaluateDirectionInternal(
            riskState.user_physical_position,
            normalizedForward,
            relativeYaw,
            out sample);
    }

    /// <summary>
    /// 对任意 Physical XZ 位置和方向执行精确走廊风险计算。
    /// 本查询不读取或量化到固定方向集合，也不修改当前风险快照。
    /// </summary>
    /// <param name="physicalPositionXZ">Physical Room XZ 位置，单位：米。</param>
    /// <param name="physicalDirectionXZ">Physical Room XZ 连续方向向量。</param>
    /// <param name="sample">成功时返回精确 clearance 与安全等级。</param>
    /// <returns>空间配置和方向有效时返回 true。</returns>
    public bool TryEvaluateDirection(
        Vector2 physicalPositionXZ,
        Vector2 physicalDirectionXZ,
        out HeadingSample sample)
    {
        sample = default;
        ResolveReferences();
        if (physicalSpaceManager == null || physicalSpaceManager.CurrentSpace == null ||
            !IsFiniteDirection(physicalDirectionXZ))
        {
            return false;
        }

        var normalizedForward = physicalDirectionXZ.normalized;
        var referenceForward = userPoseTracker != null
            ? userPoseTracker.PhysicalForwardXZ
            : Vector2.up;
        return TryEvaluateDirectionInternal(
            physicalPositionXZ,
            normalizedForward,
            SignedAngle(referenceForward, normalizedForward),
            out sample);
    }

    private bool TryEvaluateDirectionInternal(
        Vector2 physicalPositionXZ,
        Vector2 normalizedForward,
        float relativeYawDegrees,
        out HeadingSample sample)
    {
        var space = physicalSpaceManager.CurrentSpace;
        if (!PhysicalSpaceGeometry.ContainsPoint(space.boundary, physicalPositionXZ))
        {
            sample = new HeadingSample(
                relativeYawDegrees,
                normalizedForward,
                0f,
                HeadingSafety.Critical);
            return true;
        }

        var clearance = ComputeCorridorClearance(space, physicalPositionXZ, normalizedForward);
        sample = new HeadingSample(
            relativeYawDegrees,
            normalizedForward,
            clearance,
            ClassifyHeadingSafety(clearance));
        return true;
    }

    private static bool IsFiniteDirection(Vector2 direction)
    {
        return direction.sqrMagnitude > Epsilon &&
               !float.IsNaN(direction.x) && !float.IsNaN(direction.y) &&
               !float.IsInfinity(direction.x) && !float.IsInfinity(direction.y);
    }

    private RiskSample Evaluate(PhysicalSpaceConfig space, Vector2 position, Vector2 forward)
    {
        // forward 可能来自模拟输入或 XR 输入，归一化可以保证 clearance 阈值单位始终是米。
        forward = forward.sqrMagnitude > Epsilon ? forward.normalized : Vector2.up;
        var sample = new RiskSample
        {
            physicalPosition = position,
            physicalForward = forward
        };

        if (!PhysicalSpaceGeometry.ContainsPoint(space.boundary, position))
        {
            // 用户已经离开 tracking boundary 时，当前真实朝向直接进入 Critical。
            sample.currentHeading = new HeadingSample(0f, forward, 0f, HeadingSafety.Critical);
            sample.boundaryRisk = "critical";
            sample.resetRisk = "critical";
            sample.resetRequired = true;
            return sample;
        }

        var currentClearance = ComputeCorridorClearance(space, position, forward);
        sample.currentHeading = new HeadingSample(
            0f, forward, currentClearance, ClassifyHeadingSafety(currentClearance));
        // boundary_risk 只描述当前真实 Physical Forward，不表达全角域可恢复性。
        sample.boundaryRisk = ClearanceToRisk(currentClearance);
        sample.resetRequired = sample.currentHeading.Safety == HeadingSafety.Critical;
        sample.resetRisk = sample.resetRequired ? "critical" : sample.boundaryRisk;

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
            riskSnapshotId: Guid.NewGuid().ToString("N"),
            riskRevision: riskRevision,
            calibrationId: calibrationId,
            sampledAtUtc: DateTime.UtcNow.ToString("o"),
            riskPolicyVersion: GuidanceProtocolVersions.RiskPolicy,
            snapshotSource: "unity",
            physicalSpace: space.id,
            physicalPosition: sample.physicalPosition,
            physicalForward: sample.physicalForward,
            boundaryContext: space.boundary,
            obstacleContext: obstacles,
            boundaryRisk: sample.boundaryRisk,
            resetRisk: sample.resetRisk,
            isCritical: critical,
            resetRequired: sample.resetRequired,
            currentHeading: sample.currentHeading,
            userRadiusMeters: UserRadiusMeters);
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
            sample.currentHeading.ClearanceMeters.ToString("0.###"),
            sample.currentHeading.Safety);
    }

    private sealed class RiskSample
    {
        public Vector2 physicalPosition;
        public Vector2 physicalForward;
        public HeadingSample currentHeading;
        public string boundaryRisk;
        public string resetRisk;
        public bool resetRequired;
    }

    private float ComputeCorridorClearance(PhysicalSpaceConfig space, Vector2 origin, Vector2 direction)
    {
        direction = direction.sqrMagnitude > Epsilon ? direction.normalized : Vector2.up;
        var corridorRadius = Mathf.Max(0f, userRadiusMeters + boundaryMarginMeters);
        var lateral = new Vector2(-direction.y, direction.x);
        var bestDistance = ComputeBoundaryClearance(space, origin, direction, corridorRadius, lateral);

        if (space.obstacles != null)
        {
            foreach (var obstacle in space.obstacles)
            {
                var expandedRadius = Mathf.Max(0f, obstacle.radius) + corridorRadius;
                if (TryRayCircleDistance(origin, direction, obstacle.center, expandedRadius, out var distance))
                {
                    bestDistance = Mathf.Min(bestDistance, distance);
                }
            }
        }

        return Mathf.Clamp(bestDistance, 0f, maxClearanceMeters);
    }

    private float ComputeBoundaryClearance(
        PhysicalSpaceConfig space,
        Vector2 origin,
        Vector2 direction,
        float corridorRadius,
        Vector2 lateral)
    {
        var bestDistance = maxClearanceMeters;
        if (space.boundary == null || space.boundary.Length < 3)
        {
            return 0f;
        }

        for (var i = 0; i < space.boundary.Length; i++)
        {
            var a = space.boundary[i];
            var b = space.boundary[(i + 1) % space.boundary.Length];
            if (DistanceToSegment(origin, a, b) <= corridorRadius)
            {
                return 0f;
            }
        }

        bestDistance = Mathf.Min(bestDistance, ComputeBoundaryRayClearance(space, origin, direction));
        if (corridorRadius <= Epsilon)
        {
            return bestDistance;
        }

        var leftOrigin = origin + lateral * corridorRadius;
        var rightOrigin = origin - lateral * corridorRadius;
        if (!PhysicalSpaceGeometry.ContainsPoint(space.boundary, leftOrigin) ||
            !PhysicalSpaceGeometry.ContainsPoint(space.boundary, rightOrigin))
        {
            return 0f;
        }

        bestDistance = Mathf.Min(bestDistance, ComputeBoundaryRayClearance(space, leftOrigin, direction));
        bestDistance = Mathf.Min(bestDistance, ComputeBoundaryRayClearance(space, rightOrigin, direction));
        return bestDistance;
    }

    private float ComputeBoundaryRayClearance(PhysicalSpaceConfig space, Vector2 origin, Vector2 direction)
    {
        var bestDistance = maxClearanceMeters;

        for (var i = 0; i < space.boundary.Length; i++)
        {
            var a = space.boundary[i];
            var b = space.boundary[(i + 1) % space.boundary.Length];
            if (TryRaySegmentDistance(origin, direction, a, b, out var distance))
            {
                bestDistance = Mathf.Min(bestDistance, distance);
            }
        }
        return bestDistance;
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

    private HeadingSafety ClassifyHeadingSafety(float clearance)
    {
        if (clearance < criticalClearanceMeters) return HeadingSafety.Critical;
        if (clearance < unsafeClearanceMeters) return HeadingSafety.Unsafe;
        if (clearance < safeClearanceMeters) return HeadingSafety.Unknown;
        return HeadingSafety.Safe;
    }

    private static float SignedAngle(Vector2 from, Vector2 to)
    {
        from = from.sqrMagnitude > Epsilon ? from.normalized : Vector2.up;
        to = to.sqrMagnitude > Epsilon ? to.normalized : Vector2.up;
        return Mathf.Atan2(
                   from.x * to.y - from.y * to.x,
                   Vector2.Dot(from, to)) * Mathf.Rad2Deg * -1f;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        if (segment.sqrMagnitude < Epsilon)
        {
            return Vector2.Distance(point, start);
        }

        var t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude);
        return Vector2.Distance(point, start + segment * t);
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
