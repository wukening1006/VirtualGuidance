using System;
using UnityEngine;

/// <summary>保存一次 RDW 帧事务的 requested、limited 与 applied 结果。</summary>
public sealed class RdwFrameEventInfo
{
    /// <summary>冻结的位姿帧。</summary>
    public RdwPoseFrame Pose { get; }
    /// <summary>当前帧解析出的唯一 owner。</summary>
    public RdwGainOwner ActiveOwner { get; }
    /// <summary>算法或旧 Guidance Gain 请求的命令。</summary>
    public RdwGainCommand RequestedCommand { get; }
    /// <summary>经过安全门和限幅后的命令。</summary>
    public RdwGainCommand LimitedCommand { get; }
    /// <summary>统一 Root 入口返回的应用结果。</summary>
    public RdwGainApplyResult ApplyResult { get; }
    /// <summary>当前帧的导航意图。</summary>
    public RdwNavigationIntent NavigationIntent { get; }
    /// <summary>当前帧冻结的 Physical Space 几何事实。</summary>
    public RdwGeometrySnapshot Geometry { get; }
    /// <summary>当前帧结束后的 Mapping Reset 累计进度。</summary>
    public RdwResetProgress ResetProgress { get; }
    /// <summary>连续 Redirector 的 steering 诊断；无诊断来源时返回空值。</summary>
    public RdwRedirectorDiagnostics RedirectorDiagnostics { get; }

    /// <summary>创建不可变 RDW 帧事件。</summary>
    public RdwFrameEventInfo(
        in RdwPoseFrame pose,
        RdwGainOwner activeOwner,
        in RdwGainCommand requestedCommand,
        in RdwGainCommand limitedCommand,
        in RdwGainApplyResult applyResult,
        in RdwNavigationIntent navigationIntent,
        in RdwGeometrySnapshot geometry,
        in RdwResetProgress resetProgress = default,
        in RdwRedirectorDiagnostics redirectorDiagnostics = default)
    {
        Pose = pose;
        ActiveOwner = activeOwner;
        RequestedCommand = requestedCommand;
        LimitedCommand = limitedCommand;
        ApplyResult = applyResult;
        NavigationIntent = navigationIntent;
        Geometry = geometry;
        ResetProgress = resetProgress;
        RedirectorDiagnostics = redirectorDiagnostics;
    }
}

/// <summary>按一次 tracking sample 执行完整且唯一的 RDW 帧事务。</summary>
public sealed class RdwFrameCoordinator : IRdwGainCommandSink
{
    private readonly UserPoseTracker userPoseTracker;
    private readonly PhysicalSpaceCalibration calibration;
    private readonly PhysicalSpaceManager physicalSpaceManager;
    private readonly PhysicalRiskEvaluator riskEvaluator;
    private readonly MotionDetector motionDetector;
    private readonly PauseResetController pauseResetController;
    private readonly RotationGainController rotationGainController;
    private readonly GuidanceCoordinator guidanceCoordinator;
    private readonly NavigationTargetManager navigationTargetManager;
    private readonly IRdwRedirector redirector;
    private readonly RdwOwnerArbiter ownerArbiter;
    private readonly RdwGainLimiter gainLimiter;
    private readonly RdwGainApplier gainApplier;
    private readonly ExperimentDashboard sessionDashboard;
    private bool continuousRdwEnabled;
    private readonly float poseJumpDistanceMeters;
    private readonly float poseJumpYawDegrees;

    private long sequence;
    private bool hasPreviousPose;
    private Vector2 previousPhysicalPosition;
    private float previousPhysicalYaw;
    private double previousTimestamp;
    private bool isProcessingFrame;
    private RdwPoseFrame processingPose;
    private RdwGainOwner processingOwner;
    private RdwGainCommand processingRequested;
    private RdwGainCommand processingLimited;
    private RdwGainApplyResult processingApplied;
    private bool commandSubmitted;

    /// <summary>每个 tracking sample 完成 RDW 事务后触发一次。</summary>
    public event Action<RdwFrameEventInfo> FrameProcessed;
    /// <summary>最近一次关闭的帧事务。</summary>
    public RdwFrameEventInfo LastFrame { get; private set; }
    /// <summary>当前单调 pose sequence。</summary>
    public long CurrentSequence => sequence;
    /// <summary>当前帧使用的 Continuous Redirector。</summary>
    public IRdwRedirector Redirector => redirector;

    /// <summary>当前会话是否允许 Continuous RDW owner 参与仲裁。</summary>
    public bool ContinuousRdwEnabled => continuousRdwEnabled;

    /// <summary>按正式实验 Profile 切换 Continuous RDW；不会改变当前物理事实。</summary>
    public void SetContinuousEnabled(bool enabled)
    {
        continuousRdwEnabled = enabled;
        if (!enabled) ResetRedirectorSmoothing();
    }

    /// <summary>创建 Step 2A 的帧协调器；Redirector 缺省为 NullRedirector。</summary>
    public RdwFrameCoordinator(
        UserPoseTracker poseTracker,
        PhysicalSpaceCalibration physicalCalibration,
        PhysicalSpaceManager spaceManager,
        PhysicalRiskEvaluator evaluator,
        MotionDetector detector,
        PauseResetController resetController,
        RotationGainController guidanceGainController,
        GuidanceCoordinator coordinator,
        NavigationTargetManager targetManager,
        RdwGainApplier applier,
        IRdwRedirector continuousRedirector = null,
        RdwOwnerArbiter arbiter = null,
        RdwGainLimiter limiter = null,
        float maxPoseJumpMeters = 1f,
        float maxPoseJumpYaw = 60f,
        ExperimentDashboard dashboard = null,
        bool continuousEnabled = true)
    {
        userPoseTracker = poseTracker;
        calibration = physicalCalibration;
        physicalSpaceManager = spaceManager;
        riskEvaluator = evaluator;
        motionDetector = detector;
        pauseResetController = resetController;
        rotationGainController = guidanceGainController;
        guidanceCoordinator = coordinator;
        navigationTargetManager = targetManager;
        gainApplier = applier;
        sessionDashboard = dashboard;
        continuousRdwEnabled = continuousEnabled;
        redirector = continuousRedirector ?? new NullRedirector();
        ownerArbiter = arbiter ?? new RdwOwnerArbiter();
        gainLimiter = limiter ?? new RdwGainLimiter();
        poseJumpDistanceMeters = Mathf.Max(0.01f, maxPoseJumpMeters);
        poseJumpYawDegrees = Mathf.Clamp(maxPoseJumpYaw, 1f, 180f);
    }

    /// <summary>处理最新已校准 pose，并确保该 sequence 最多由 GainApplier 关闭一次。</summary>
    public RdwFrameEventInfo ProcessTrackingSample(float sampleDeltaTimeSeconds, double timestamp = -1d)
    {
        sequence++;
        var sampleTime = timestamp >= 0d ? timestamp : Time.unscaledTimeAsDouble;
        var frame = CaptureFrame(sampleTime, sampleDeltaTimeSeconds);
        var geometry = CaptureGeometrySnapshot();
        var intent = CaptureNavigationIntent(frame);

        if (motionDetector != null && frame.IsValid)
        {
            motionDetector.Sample(frame.PhysicalForwardXZ, frame.DeltaTime);
        }

        var hardStop = !frame.IsValid || IsImmediateHardStop();
        var resetActive = pauseResetController != null && pauseResetController.IsActive ||
                          guidanceCoordinator != null && guidanceCoordinator.IsAutomaticRecoveryActive;
        var guidanceTurnActive = rotationGainController != null && rotationGainController.IsActive;
        var continuousAvailable = RdwContinuousAvailability.IsAvailable(
            continuousRdwEnabled,
            IsRedirectorAvailable(),
            sessionDashboard != null && sessionDashboard.RunState == ExperimentRunState.Running,
            guidanceCoordinator != null &&
            guidanceCoordinator.RuntimeState != null &&
            guidanceCoordinator.RuntimeState.IsNavigating,
            frame.IsValid,
            riskEvaluator != null && riskEvaluator.IsTrackingValid,
            calibration != null && calibration.IsCalibrationValid &&
            riskEvaluator != null && riskEvaluator.IsCalibrationValid,
            geometry.IsValid && riskEvaluator != null && riskEvaluator.PhysicalSpaceMatches,
            intent.IsValid);
        var owner = ownerArbiter.Resolve(hardStop, resetActive, guidanceTurnActive, continuousAvailable);
        var requested = RdwGainCommand.Zero(frame.Sequence, OwnerReason(owner));
        var limited = requested;
        var applied = default(RdwGainApplyResult);

        BeginFrameProcessing(frame, owner, hardStop);
        if (owner != RdwGainOwner.ContinuousRedirector)
        {
            ResetRedirectorSmoothing();
        }

        if (owner == RdwGainOwner.Resetter && pauseResetController != null)
        {
            Submit(pauseResetController.EvaluateMapping(frame, geometry));
            pauseResetController.CommitMapping(processingApplied, frame.PhysicalForwardXZ);
        }
        else if (owner == RdwGainOwner.GuidanceTurnGain && rotationGainController != null && motionDetector != null)
        {
            rotationGainController.ProcessTurnSample(
                motionDetector.IsTurning,
                motionDetector.LastSignedYawDeltaDegrees,
                frame.DeltaTime,
                frame.Sequence);
        }
        else if (owner == RdwGainOwner.ContinuousRedirector)
        {
            Submit(redirector.Evaluate(frame, geometry, intent));
        }

        if (!commandSubmitted)
        {
            Submit(requested);
        }

        requested = processingRequested;
        limited = processingLimited;
        applied = processingApplied;
        isProcessingFrame = false;

        var resetProgress = pauseResetController != null
            ? pauseResetController.MappingProgress
            : default;
        LastFrame = new RdwFrameEventInfo(
            frame, owner, requested, limited, applied, intent, geometry, resetProgress, CaptureRedirectorDiagnostics());
        FrameProcessed?.Invoke(LastFrame);
        return LastFrame;
    }

    /// <summary>接收当前 owner 的命令，并在同一调用栈内完成限幅和应用。</summary>
    public RdwGainApplyResult Submit(in RdwGainCommand command)
    {
        if (!isProcessingFrame || commandSubmitted || command.PoseSequence != processingPose.Sequence)
        {
            return new RdwGainApplyResult(
                RdwGainApplyStatus.InvalidCommand,
                command,
                0f,
                Vector2.zero);
        }

        processingRequested = command;
        var context = new RdwGainLimitContext(
            processingOwner,
            processingPose.IsValid,
            calibration != null && calibration.IsCalibrationValid,
            riskEvaluator != null && riskEvaluator.PhysicalSpaceMatches,
            processingOwner == RdwGainOwner.HardStop,
            gainLimiter.IsMoving(processingPose.DeltaPhysicalPositionXZ),
            processingPose.DeltaTime);
        processingLimited = gainLimiter.Limit(command, context);
        processingApplied = gainApplier != null
            ? gainApplier.Apply(processingLimited)
            : new RdwGainApplyResult(
                RdwGainApplyStatus.InvalidRoot,
                processingLimited,
                0f,
                Vector2.zero);
        commandSubmitted = true;
        return processingApplied;
    }

    /// <summary>tracking loss、recenter 或 calibration identity 变化时断开 delta chain。</summary>
    public void InvalidatePoseChain(string reason = "tracking_invalid")
    {
        hasPreviousPose = false;
        previousPhysicalPosition = Vector2.zero;
        previousPhysicalYaw = 0f;
        previousTimestamp = 0d;
        motionDetector?.ResetSamplingState();
        pauseResetController?.InvalidateMappingChain(reason);
        ResetRedirectorSmoothing();
    }

    private void BeginFrameProcessing(in RdwPoseFrame pose, RdwGainOwner owner, bool hardStop)
    {
        isProcessingFrame = true;
        processingPose = pose;
        processingOwner = hardStop ? RdwGainOwner.HardStop : owner;
        processingRequested = RdwGainCommand.Zero(pose.Sequence, OwnerReason(processingOwner));
        processingLimited = processingRequested;
        processingApplied = default;
        commandSubmitted = false;
    }

    private RdwPoseFrame CaptureFrame(double timestamp, float fallbackDeltaTime)
    {
        if (userPoseTracker == null || calibration == null || physicalSpaceManager == null ||
            !calibration.IsCalibrationValid || string.IsNullOrWhiteSpace(calibration.CalibrationId) ||
            string.IsNullOrWhiteSpace(physicalSpaceManager.CurrentSpaceId) ||
            !string.Equals(calibration.CalibratedSpaceId, physicalSpaceManager.CurrentSpaceId, StringComparison.Ordinal))
        {
            InvalidatePoseChain();
            return RdwPoseFrame.Invalid(sequence, timestamp);
        }

        var physicalPosition = userPoseTracker.PhysicalPositionXZ;
        var physicalForward = userPoseTracker.PhysicalForwardXZ;
        var virtualPosition3 = userPoseTracker.UserVirtualPosition;
        var virtualForward3 = Vector3.ProjectOnPlane(userPoseTracker.UserVirtualForward, Vector3.up);
        var physicalYaw = RdwYawConvention.ForwardToYaw(physicalForward);
        var deltaTime = hasPreviousPose
            ? Mathf.Max(0f, (float)(timestamp - previousTimestamp))
            : Mathf.Max(0f, fallbackDeltaTime);
        var deltaPosition = hasPreviousPose ? physicalPosition - previousPhysicalPosition : Vector2.zero;
        var deltaYaw = hasPreviousPose ? RdwYawConvention.SignedDeltaYaw(previousPhysicalYaw, physicalYaw) : 0f;
        var finite = RdwYawConvention.IsFinite(physicalPosition.x) && RdwYawConvention.IsFinite(physicalPosition.y) &&
                     RdwYawConvention.IsFinite(physicalForward) && RdwYawConvention.IsFinite(virtualForward3.x) &&
                     RdwYawConvention.IsFinite(virtualForward3.z) && RdwYawConvention.IsFinite(deltaTime);
        var jumped = hasPreviousPose &&
                     (deltaPosition.magnitude > poseJumpDistanceMeters || Mathf.Abs(deltaYaw) > poseJumpYawDegrees);
        if (!finite || jumped)
        {
            InvalidatePoseChain(jumped ? "pose_jump" : "invalid_pose");
            return RdwPoseFrame.Invalid(sequence, timestamp);
        }

        previousPhysicalPosition = physicalPosition;
        previousPhysicalYaw = physicalYaw;
        previousTimestamp = timestamp;
        hasPreviousPose = true;
        return new RdwPoseFrame(
            sequence,
            timestamp,
            physicalPosition,
            physicalForward,
            new Vector2(virtualPosition3.x, virtualPosition3.z),
            new Vector2(virtualForward3.x, virtualForward3.z),
            deltaPosition,
            deltaYaw,
            deltaTime,
            calibration.CalibrationId,
            physicalSpaceManager.CurrentSpaceId,
            true);
    }

    private RdwNavigationIntent CaptureNavigationIntent(in RdwPoseFrame frame)
    {
        var target = guidanceCoordinator != null ? guidanceCoordinator.CurrentGuidanceTarget : null;
        if (target == null || navigationTargetManager == null ||
            !navigationTargetManager.TryGetTarget(target.TargetId, out var metadata) || metadata == null)
        {
            return default;
        }

        var anchor = metadata.ViewPoint != null ? metadata.ViewPoint : metadata.transform;
        var targetPosition = new Vector2(anchor.position.x, anchor.position.z);
        return new RdwNavigationIntent(
            target.TargetId,
            targetPosition,
            targetPosition - frame.VirtualPositionXZ,
            target.TargetRole);
    }

    private RdwGeometrySnapshot CaptureGeometrySnapshot()
    {
        var space = physicalSpaceManager != null ? physicalSpaceManager.CurrentSpace : null;
        var risk = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        if (space == null)
        {
            return default;
        }

        var riskMatchesSpace = risk != null &&
                               string.Equals(risk.physical_space, space.id, StringComparison.Ordinal);
        var boundaryFacts = riskMatchesSpace ? risk.boundary_context : space.boundary;
        var obstacleFacts = riskMatchesSpace
            ? risk.obstacle_context
            : Array.Empty<PhysicalRiskObstacleSnapshot>();
        return new RdwGeometrySnapshot(
            space.id,
            boundaryFacts,
            obstacleFacts,
            risk != null ? risk.risk_snapshot_id : string.Empty,
            risk != null ? risk.risk_revision : 0,
            risk != null && risk.critical,
            risk != null ? risk.calibration_id : (calibration != null ? calibration.CalibrationId : string.Empty),
            riskMatchesSpace
                ? risk.user_radius_meters
                : (riskEvaluator != null ? riskEvaluator.UserRadiusMeters : 0f));
    }

    private bool IsImmediateHardStop()
    {
        var risk = riskEvaluator != null ? riskEvaluator.CurrentRiskState : null;
        return riskEvaluator == null || !riskEvaluator.IsTrackingValid || !riskEvaluator.IsCalibrationValid ||
               !riskEvaluator.PhysicalSpaceMatches ||
               (risk != null && userPoseTracker != null && risk.boundary_context != null && risk.boundary_context.Count >= 3 &&
                !PhysicalSpaceGeometry.ContainsPoint(risk.boundary_context, userPoseTracker.PhysicalPositionXZ));
    }

    private static string OwnerReason(RdwGainOwner owner)
    {
        switch (owner)
        {
            case RdwGainOwner.HardStop: return "hard_stop";
            case RdwGainOwner.Resetter: return "resetter_reserved";
            case RdwGainOwner.GuidanceTurnGain: return "guidance_turn_gain";
            case RdwGainOwner.ContinuousRedirector: return "continuous_redirector";
            default: return "none";
        }
    }

    private void ResetRedirectorSmoothing()
    {
        if (redirector is IRdwRedirectorLifecycle lifecycle)
        {
            lifecycle.ResetSmoothing();
        }
    }

    private bool IsRedirectorAvailable()
    {
        return !(redirector is IRdwRedirectorAvailability availability) || availability.IsAvailable;
    }

    private RdwRedirectorDiagnostics CaptureRedirectorDiagnostics()
    {
        return redirector is IRdwRedirectorDiagnosticsSource diagnostics
            ? diagnostics.LastDiagnostics
            : RdwRedirectorDiagnostics.Empty;
    }
}
