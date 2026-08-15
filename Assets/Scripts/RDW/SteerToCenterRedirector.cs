using UnityEngine;

/// <summary>连续 steering 的诊断快照，可用于解释每个 PoseSequence 的方向与增益。</summary>
public readonly struct RdwRedirectorDiagnostics
{
    /// <summary>实现类型名称。</summary>
    public string RedirectorType { get; }
    /// <summary>本帧使用的 steering center，单位：米。</summary>
    public Vector2 SteeringCenterXZ { get; }
    /// <summary>从用户位置指向 steering center 的方向。</summary>
    public Vector2 CenterDirectionXZ { get; }
    /// <summary>当前算法建议的 Physical Steering Direction。</summary>
    public Vector2 DesiredSteeringDirectionXZ => CenterDirectionXZ;
    /// <summary>Physical Forward 到 steering direction 的有符号角，右转为正，单位：度。</summary>
    public float SteeringAngleDegrees { get; }
    /// <summary>确定性的 steering side，左为 -1，右为 1。</summary>
    public int SteeringSide { get; }
    /// <summary>原始 rotation proposal，单位：度。</summary>
    public float RawRotationProposalDegrees { get; }
    /// <summary>原始 curvature proposal，单位：度。</summary>
    public float RawCurvatureProposalDegrees { get; }
    /// <summary>平滑后的 rotation proposal，单位：度。</summary>
    public float SmoothedRotationDegrees { get; }
    /// <summary>平滑后的 curvature proposal，单位：度。</summary>
    public float SmoothedCurvatureDegrees { get; }
    /// <summary>诊断原因或 veto 原因。</summary>
    public string Reason { get; }
    /// <summary>S2O 实际使用的轨道半径，单位：米。</summary>
    public float OrbitRadiusMeters { get; }
    /// <summary>S2O 当前选中的轨道目标，单位：米。</summary>
    public Vector2 OrbitTargetXZ { get; }
    /// <summary>S2O 当前稳定选择的轨道侧，-1/1 表示两个候选方向。</summary>
    public int OrbitSide { get; }
    /// <summary>Thomas APF 的边界斥力。</summary>
    public Vector2 BoundaryForceXZ { get; }
    /// <summary>Thomas APF 的障碍物斥力。</summary>
    public Vector2 ObstacleForceXZ { get; }
    /// <summary>Thomas APF 的合力。</summary>
    public Vector2 TotalForceXZ { get; }
    /// <summary>Thomas APF 合力的模长。</summary>
    public float TotalForceMagnitude { get; }
    /// <summary>Thomas APF 进入 neutral 的原因。</summary>
    public string ApfNeutralReason { get; }
    /// <summary>Thomas APF 是否发生了 force clamp。</summary>
    public bool ApfForceClamped { get; }

    /// <summary>创建诊断快照。</summary>
    public RdwRedirectorDiagnostics(
        string redirectorType,
        Vector2 center,
        Vector2 direction,
        float steeringAngle,
        int side,
        float rawRotation,
        float rawCurvature,
        float smoothedRotation,
        float smoothedCurvature,
        string reason,
        float orbitRadiusMeters = 0f,
        Vector2 orbitTargetXZ = default(Vector2),
        int orbitSide = 0,
        Vector2 boundaryForceXZ = default(Vector2),
        Vector2 obstacleForceXZ = default(Vector2),
        Vector2 totalForceXZ = default(Vector2),
        float totalForceMagnitude = 0f,
        string apfNeutralReason = "",
        bool apfForceClamped = false)
    {
        RedirectorType = redirectorType ?? string.Empty;
        SteeringCenterXZ = center;
        CenterDirectionXZ = direction;
        SteeringAngleDegrees = steeringAngle;
        SteeringSide = side;
        RawRotationProposalDegrees = rawRotation;
        RawCurvatureProposalDegrees = rawCurvature;
        SmoothedRotationDegrees = smoothedRotation;
        SmoothedCurvatureDegrees = smoothedCurvature;
        Reason = reason ?? string.Empty;
        OrbitRadiusMeters = orbitRadiusMeters;
        OrbitTargetXZ = orbitTargetXZ;
        OrbitSide = orbitSide;
        BoundaryForceXZ = boundaryForceXZ;
        ObstacleForceXZ = obstacleForceXZ;
        TotalForceXZ = totalForceXZ;
        TotalForceMagnitude = totalForceMagnitude;
        ApfNeutralReason = apfNeutralReason ?? string.Empty;
        ApfForceClamped = apfForceClamped;
    }

    /// <summary>返回空诊断。</summary>
    public static RdwRedirectorDiagnostics Empty => new RdwRedirectorDiagnostics("None", Vector2.zero, Vector2.zero, 0f, 0, 0f, 0f, 0f, 0f, "none");

    /// <summary>复制基础 steering 诊断并附加 S2O/APF 算法数据。</summary>
    public RdwRedirectorDiagnostics WithAlgorithmData(
        float orbitRadiusMeters = 0f,
        Vector2 orbitTargetXZ = default(Vector2),
        int orbitSide = 0,
        Vector2 boundaryForceXZ = default(Vector2),
        Vector2 obstacleForceXZ = default(Vector2),
        Vector2 totalForceXZ = default(Vector2),
        float totalForceMagnitude = 0f,
        string apfNeutralReason = "",
        bool apfForceClamped = false)
    {
        return new RdwRedirectorDiagnostics(
            RedirectorType,
            SteeringCenterXZ,
            CenterDirectionXZ,
            SteeringAngleDegrees,
            SteeringSide,
            RawRotationProposalDegrees,
            RawCurvatureProposalDegrees,
            SmoothedRotationDegrees,
            SmoothedCurvatureDegrees,
            Reason,
            orbitRadiusMeters,
            orbitTargetXZ,
            orbitSide,
            boundaryForceXZ,
            obstacleForceXZ,
            totalForceXZ,
            totalForceMagnitude,
            apfNeutralReason,
            apfForceClamped);
    }
}

/// <summary>可选的连续 Redirector 诊断来源。</summary>
public interface IRdwRedirectorDiagnosticsSource
{
    /// <summary>最近一次 Evaluate 生成的诊断。</summary>
    RdwRedirectorDiagnostics LastDiagnostics { get; }
}

/// <summary>支持跨帧状态清理的连续 Redirector 生命周期接口。</summary>
public interface IRdwRedirectorLifecycle
{
    /// <summary>清除 tracking/recenter/reset 后的跨帧平滑状态。</summary>
    void ResetSmoothing();
}

/// <summary>参考 OpenRDW SteerToRedirector 思想的中心导向连续重定向器。</summary>
public sealed class SteerToCenterRedirector : IRdwRedirector, IRdwRedirectorAvailability, IRdwRedirectorDiagnosticsSource, IRdwRedirectorLifecycle
{
    private readonly RdwContinuousSteeringController steeringController;
    private readonly float centerToleranceMeters;
    private readonly float nearOppositeAngleDegrees;
    private readonly float oppositeTemporaryBearingDegrees;
    private int lastSteeringSide;

    /// <summary>最近一次 S2C 计算诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }
    /// <summary>S2C 模式始终提供连续能力，实际帧资格由 Coordinator 门控。</summary>
    public bool IsAvailable => true;

    /// <summary>创建参数化 S2C；所有距离、时间和角度均使用 SI/度单位。</summary>
    public SteerToCenterRedirector(
        float movementThresholdMetersPerSecond = 0.2f,
        float rotationThresholdDegreesPerSecond = 1.5f,
        float curvatureRadiusMeters = 10f,
        float maxCurvatureDegreesPerSecond = 15f,
        float maxRotationDegreesPerSecond = 30f,
        float rotationGainMultiplier = 0.05f,
        float steeringDampening = 0.75f,
        float smoothingFactor = 0.125f,
        float centerToleranceMeters = 0.15f,
        float nearOppositeAngleDegrees = 160f,
        float oppositeTemporaryBearingDegrees = 90f)
    {
        steeringController = new RdwContinuousSteeringController(
            movementThresholdMetersPerSecond,
            rotationThresholdDegreesPerSecond,
            curvatureRadiusMeters,
            maxCurvatureDegreesPerSecond,
            maxRotationDegreesPerSecond,
            rotationGainMultiplier,
            steeringDampening,
            smoothingFactor);
        this.centerToleranceMeters = Mathf.Max(0f, centerToleranceMeters);
        this.nearOppositeAngleDegrees = Mathf.Clamp(nearOppositeAngleDegrees, 90f, 179f);
        this.oppositeTemporaryBearingDegrees = Mathf.Clamp(oppositeTemporaryBearingDegrees, 1f, 90f);
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>根据当前 Physical Pose、空间中心和 Navigation Intent 计算单帧 gain。</summary>
    public RdwGainCommand Evaluate(in RdwPoseFrame pose, in RdwGeometrySnapshot geometry, in RdwNavigationIntent intent)
    {
        if (!pose.IsValid || !geometry.IsValid || !intent.IsValid)
        {
            var invalid = steeringController.ReturnInvalid(pose.Sequence, "S2C", geometry.SteeringCenterXZ, "s2c_invalid_input");
            LastDiagnostics = steeringController.LastDiagnostics;
            return invalid;
        }

        var toCenter = geometry.SteeringCenterXZ - pose.PhysicalPositionXZ;
        var distance = toCenter.magnitude;
        if (distance <= centerToleranceMeters)
        {
            lastSteeringSide = 0;
            var neutral = steeringController.ReturnNeutral(pose.Sequence, "S2C", geometry.SteeringCenterXZ, "s2c_center_tolerance");
            LastDiagnostics = steeringController.LastDiagnostics;
            return neutral;
        }

        var direction = toCenter / distance;
        var steeringAngle = SignedAngle(pose.PhysicalForwardXZ, direction);
        var steeringSide = steeringAngle > 0.01f ? 1 : steeringAngle < -0.01f ? -1 : 0;
        if (Mathf.Abs(steeringAngle) >= nearOppositeAngleDegrees)
        {
            steeringSide = lastSteeringSide != 0
                ? lastSteeringSide
                : ResolveOppositeTurnSign(pose.PhysicalForwardXZ, direction, steeringAngle);
            direction = RdwYawConvention.YawToForward(
                pose.PhysicalYaw + steeringSide * oppositeTemporaryBearingDegrees);
            steeringAngle = SignedAngle(pose.PhysicalForwardXZ, direction);
        }

        if (steeringSide != 0)
        {
            lastSteeringSide = steeringSide;
        }

        var reason = steeringSide == 0 ? "s2c_no_steering_side" : "s2c_active";
        var command = steeringController.Evaluate(
            pose,
            geometry.SteeringCenterXZ,
            direction,
            "S2C",
            reason,
            steeringSide);
        LastDiagnostics = steeringController.LastDiagnostics;
        return command;
    }

    /// <summary>重置跨帧平滑状态，供 tracking/recenter/reset 生命周期使用。</summary>
    public void ResetSmoothing()
    {
        steeringController.ResetSmoothing();
        lastSteeringSide = 0;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    private static float SignedAngle(Vector2 from, Vector2 to)
    {
        return Mathf.DeltaAngle(RdwYawConvention.ForwardToYaw(from), RdwYawConvention.ForwardToYaw(to));
    }

    private static int ResolveOppositeTurnSign(Vector2 forward, Vector2 direction, float signedAngle)
    {
        if (Mathf.Abs(Mathf.Abs(signedAngle) - 180f) > 0.01f) return signedAngle >= 0f ? 1 : -1;
        var cross = forward.x * direction.y - forward.y * direction.x;
        if (Mathf.Abs(cross) > 0.0001f) return cross >= 0f ? 1 : -1;
        return direction.x >= 0f ? 1 : -1;
    }

}
