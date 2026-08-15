using UnityEngine;

/// <summary>
/// 统一计算 Continuous Redirector 的 rotation/curvature proposal 和跨帧平滑。
/// </summary>
public sealed class RdwContinuousSteeringController
{
    private const float DirectionEpsilon = 0.000001f;

    private readonly float movementThresholdMetersPerSecond;
    private readonly float rotationThresholdDegreesPerSecond;
    private readonly float curvatureRadiusMeters;
    private readonly float maxCurvatureDegreesPerSecond;
    private readonly float maxRotationDegreesPerSecond;
    private readonly float rotationGainMultiplier;
    private readonly float steeringDampening;
    private readonly float smoothingFactor;
    private float smoothedRotation;
    private float smoothedCurvature;

    /// <summary>最近一次统一 steering 计算的诊断。</summary>
    public RdwRedirectorDiagnostics LastDiagnostics { get; private set; }

    /// <summary>创建统一 steering/gain 计算器；距离、速度和角度使用 SI/度单位。</summary>
    public RdwContinuousSteeringController(
        float movementThresholdMetersPerSecond = 0.2f,
        float rotationThresholdDegreesPerSecond = 1.5f,
        float curvatureRadiusMeters = 10f,
        float maxCurvatureDegreesPerSecond = 15f,
        float maxRotationDegreesPerSecond = 30f,
        float rotationGainMultiplier = 0.05f,
        float steeringDampening = 0.75f,
        float smoothingFactor = 0.125f)
    {
        this.movementThresholdMetersPerSecond = Mathf.Max(0f, movementThresholdMetersPerSecond);
        this.rotationThresholdDegreesPerSecond = Mathf.Max(0f, rotationThresholdDegreesPerSecond);
        this.curvatureRadiusMeters = Mathf.Max(0.01f, curvatureRadiusMeters);
        this.maxCurvatureDegreesPerSecond = Mathf.Max(0f, maxCurvatureDegreesPerSecond);
        this.maxRotationDegreesPerSecond = Mathf.Max(0f, maxRotationDegreesPerSecond);
        this.rotationGainMultiplier = Mathf.Max(0f, rotationGainMultiplier);
        this.steeringDampening = Mathf.Clamp01(steeringDampening);
        this.smoothingFactor = Mathf.Clamp01(smoothingFactor);
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>把期望 Physical Steering Direction 转换为统一 gain command。</summary>
    public RdwGainCommand Evaluate(
        in RdwPoseFrame pose,
        Vector2 steeringTargetXZ,
        Vector2 desiredSteeringDirectionXZ,
        string redirectorType,
        string reason,
        int steeringSideOverride = 0)
    {
        if (!pose.IsValid || desiredSteeringDirectionXZ.sqrMagnitude <= DirectionEpsilon)
        {
            return ReturnInvalid(pose.Sequence, redirectorType, steeringTargetXZ, reason);
        }

        var direction = desiredSteeringDirectionXZ.normalized;
        var steeringAngle = SignedAngle(pose.PhysicalForwardXZ, direction);
        var steeringSide = steeringSideOverride == 0
            ? steeringAngle > 0.01f ? 1 : steeringAngle < -0.01f ? -1 : 0
            : steeringSideOverride > 0 ? 1 : -1;
        var deltaTime = pose.DeltaTime;
        var speed = deltaTime > 0.0001f
            ? pose.DeltaPhysicalPositionXZ.magnitude / deltaTime
            : 0f;
        var rotationSpeed = deltaTime > 0.0001f
            ? Mathf.Abs(pose.DeltaPhysicalYaw) / deltaTime
            : 0f;
        var injectionSign = -steeringSide;
        var rawCurvature = speed >= movementThresholdMetersPerSecond && steeringSide != 0
            ? Mathf.Min(maxCurvatureDegreesPerSecond, speed / curvatureRadiusMeters * Mathf.Rad2Deg) * deltaTime * injectionSign
            : 0f;
        var turningWithSteering = steeringSide != 0 && Mathf.Sign(pose.DeltaPhysicalYaw) == steeringSide;
        var rawRotation = rotationSpeed >= rotationThresholdDegreesPerSecond && steeringSide != 0
            ? Mathf.Min(maxRotationDegreesPerSecond, rotationSpeed * rotationGainMultiplier * (turningWithSteering ? 1f : 1.5f)) * deltaTime * injectionSign
            : 0f;
        var bearingDampening = Mathf.Sin(Mathf.Clamp01(Mathf.Abs(steeringAngle) / 180f) * Mathf.PI);
        var dampening = Mathf.Lerp(1f, bearingDampening, steeringDampening);
        rawRotation *= dampening;
        rawCurvature *= dampening;
        smoothedRotation = Mathf.Lerp(smoothedRotation, rawRotation, smoothingFactor);
        smoothedCurvature = Mathf.Lerp(smoothedCurvature, rawCurvature, smoothingFactor);
        LastDiagnostics = new RdwRedirectorDiagnostics(
            redirectorType,
            steeringTargetXZ,
            direction,
            steeringAngle,
            steeringSide,
            rawRotation,
            rawCurvature,
            smoothedRotation,
            smoothedCurvature,
            reason);
        return new RdwGainCommand(
            smoothedRotation,
            smoothedCurvature,
            Vector2.zero,
            RdwGainOwner.ContinuousRedirector,
            reason,
            pose.Sequence);
    }

    /// <summary>清除跨帧 gain，供 Reset、暂停和 geometry 变化使用。</summary>
    public void ResetSmoothing()
    {
        smoothedRotation = 0f;
        smoothedCurvature = 0f;
        LastDiagnostics = RdwRedirectorDiagnostics.Empty;
    }

    /// <summary>返回不保留旧 gain 的 neutral command。</summary>
    public RdwGainCommand ReturnNeutral(
        long poseSequence,
        string redirectorType,
        Vector2 steeringTargetXZ,
        string reason,
        string apfNeutralReason = "")
    {
        smoothedRotation = 0f;
        smoothedCurvature = 0f;
        LastDiagnostics = new RdwRedirectorDiagnostics(
            redirectorType,
            steeringTargetXZ,
            Vector2.zero,
            0f,
            0,
            0f,
            0f,
            0f,
            0f,
            reason,
            apfNeutralReason: apfNeutralReason);
        return new RdwGainCommand(0f, 0f, Vector2.zero, RdwGainOwner.ContinuousRedirector, reason, poseSequence);
    }

    /// <summary>返回无效输入的零命令，并避免残留平滑状态。</summary>
    public RdwGainCommand ReturnInvalid(
        long poseSequence,
        string redirectorType,
        Vector2 steeringTargetXZ,
        string reason)
    {
        ResetSmoothing();
        LastDiagnostics = new RdwRedirectorDiagnostics(
            redirectorType,
            steeringTargetXZ,
            Vector2.zero,
            0f,
            0,
            0f,
            0f,
            0f,
            0f,
            reason);
        return new RdwGainCommand(
            0f,
            0f,
            Vector2.zero,
            RdwGainOwner.ContinuousRedirector,
            reason,
            poseSequence);
    }

    private static float SignedAngle(Vector2 from, Vector2 to)
    {
        return Mathf.DeltaAngle(RdwYawConvention.ForwardToYaw(from), RdwYawConvention.ForwardToYaw(to));
    }
}
