using UnityEngine;

/// <summary>保存一次 Gain 限幅所需的安全上下文。</summary>
public readonly struct RdwGainLimitContext
{
    /// <summary>当前帧仲裁出的唯一 Root owner。</summary>
    public RdwGainOwner ActiveOwner { get; }
    /// <summary>Tracking 是否有效。</summary>
    public bool TrackingValid { get; }
    /// <summary>Calibration 是否有效。</summary>
    public bool CalibrationValid { get; }
    /// <summary>风险、校准与 Physical Space identity 是否一致。</summary>
    public bool PhysicalSpaceMatches { get; }
    /// <summary>当前帧是否由快速安全门禁止注入。</summary>
    public bool HardStop { get; }
    /// <summary>用户 Physical 位移是否超过 curvature movement threshold。</summary>
    public bool UserMoving { get; }
    /// <summary>当前 pose 帧时长，单位：秒。</summary>
    public float DeltaTime { get; }

    /// <summary>创建不可变 Gain 限幅上下文。</summary>
    public RdwGainLimitContext(RdwGainOwner activeOwner, bool trackingValid, bool calibrationValid, bool physicalSpaceMatches, bool hardStop, bool userMoving, float deltaTime)
    {
        ActiveOwner = activeOwner;
        TrackingValid = trackingValid;
        CalibrationValid = calibrationValid;
        PhysicalSpaceMatches = physicalSpaceMatches;
        HardStop = hardStop;
        UserMoving = userMoving;
        DeltaTime = Mathf.Max(0f, deltaTime);
    }
}

/// <summary>集中校验并限制所有 RDW gain 命令。</summary>
public sealed class RdwGainLimiter
{
    private readonly float maxYawPerFrameDegrees;
    private readonly float maxYawPerSecondDegrees;
    private readonly float movementThresholdMeters;
    private readonly float maxContinuousRotationDegreesPerSecond;
    private readonly float maxContinuousCurvatureDegreesPerSecond;

    /// <summary>创建统一 Gain 限幅器，并集中配置 Continuous rotation/curvature 安全上限。</summary>
    public RdwGainLimiter(
        float maxYawPerFrame = 8f,
        float maxYawPerSecond = float.PositiveInfinity,
        float movementThreshold = 0.001f,
        float maxContinuousRotationDegreesPerSecond = 30f,
        float maxContinuousCurvatureDegreesPerSecond = 15f)
    {
        maxYawPerFrameDegrees = Mathf.Max(0f, maxYawPerFrame);
        maxYawPerSecondDegrees = Mathf.Max(0f, maxYawPerSecond);
        movementThresholdMeters = Mathf.Max(0f, movementThreshold);
        this.maxContinuousRotationDegreesPerSecond = Mathf.Max(0f, maxContinuousRotationDegreesPerSecond);
        this.maxContinuousCurvatureDegreesPerSecond = Mathf.Max(0f, maxContinuousCurvatureDegreesPerSecond);
    }

    /// <summary>对命令执行身份、安全、有限值和幅值检查。</summary>
    public RdwGainCommand Limit(in RdwGainCommand command, in RdwGainLimitContext context)
    {
        if (context.HardStop || !context.TrackingValid || !context.CalibrationValid || !context.PhysicalSpaceMatches)
        {
            return RdwGainCommand.Zero(command.PoseSequence, "safety_veto");
        }

        if (!IsOwnerAllowed(command.Source, context.ActiveOwner, command.HasControl))
        {
            return RdwGainCommand.Zero(command.PoseSequence, "owner_mismatch");
        }

        if (!RdwYawConvention.IsFinite(command.RotationYawDeltaDeg) ||
            !RdwYawConvention.IsFinite(command.CurvatureYawDeltaDeg) ||
            !RdwYawConvention.IsFinite(command.TranslationDeltaXZ.x) ||
            !RdwYawConvention.IsFinite(command.TranslationDeltaXZ.y))
        {
            return RdwGainCommand.Zero(command.PoseSequence, "invalid_command");
        }

        var rotationYaw = command.RotationYawDeltaDeg;
        var curvatureYaw = context.UserMoving ? command.CurvatureYawDeltaDeg : 0f;
        if (command.Source == RdwGainOwner.ContinuousRedirector)
        {
            // Continuous proposal 的最终分量安全上限只在统一 Limiter 中执行。
            var rotationCap = maxContinuousRotationDegreesPerSecond * context.DeltaTime;
            var curvatureCap = maxContinuousCurvatureDegreesPerSecond * context.DeltaTime;
            rotationYaw = Mathf.Clamp(rotationYaw, -rotationCap, rotationCap);
            curvatureYaw = Mathf.Clamp(curvatureYaw, -curvatureCap, curvatureCap);
        }

        var requestedYaw = rotationYaw + curvatureYaw;
        var cap = maxYawPerFrameDegrees;
        if (context.DeltaTime > 0.0001f)
        {
            cap = Mathf.Min(cap, maxYawPerSecondDegrees * context.DeltaTime);
        }

        var limitedYaw = Mathf.Clamp(requestedYaw, -cap, cap);
        var yawScale = Mathf.Abs(requestedYaw) > 0.0001f
            ? Mathf.Abs(limitedYaw / requestedYaw)
            : 1f;
        var limitedRotationYaw = rotationYaw * yawScale;
        var limitedCurvatureYaw = curvatureYaw * yawScale;
        // 虚拟平移只开放给 ContinuousRedirector；Reset 与 Rotation Gain 仍只拥有 yaw。
        var limitedTranslation = command.Source == RdwGainOwner.ContinuousRedirector
            ? command.TranslationDeltaXZ
            : Vector2.zero;
        return new RdwGainCommand(
            limitedRotationYaw,
            limitedCurvatureYaw,
            limitedTranslation,
            command.Source,
            command.Reason,
            command.PoseSequence);
    }

    /// <summary>判断位移是否达到 curvature 计算所需的最小阈值。</summary>
    public bool IsMoving(Vector2 deltaPositionXZ)
    {
        return deltaPositionXZ.sqrMagnitude >= movementThresholdMeters * movementThresholdMeters;
    }

    private static bool IsOwnerAllowed(RdwGainOwner source, RdwGainOwner owner, bool hasControl)
    {
        if (!hasControl) return true;
        return source == owner && source != RdwGainOwner.None && source != RdwGainOwner.HardStop;
    }
}
