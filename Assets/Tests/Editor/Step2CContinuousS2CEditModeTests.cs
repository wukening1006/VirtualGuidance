using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Step 2C Continuous S2C 的 steering、几何、组合命令与 Gain 管线契约。
/// </summary>
public sealed class Step2CContinuousS2CEditModeTests
{
    private const float Epsilon = 0.001f;
    private static readonly Vector2[] SquareBoundary =
    {
        new Vector2(-3f, -3f),
        new Vector2(3f, -3f),
        new Vector2(3f, 3f),
        new Vector2(-3f, 3f)
    };

    /// <summary>验证用户位于中心左右两侧时得到相反且确定的 steering side。</summary>
    [Test]
    public void Steering_UserOnEitherSideOfCenter_UsesCorrectSide()
    {
        var redirector = Redirector();
        var geometry = Geometry();
        var intent = Intent();

        redirector.Evaluate(Pose(1, new Vector2(-1f, 0f), Vector2.up, Vector2.zero, 0f), geometry, intent);
        Assert.That(redirector.LastDiagnostics.SteeringSide, Is.EqualTo(1));

        redirector.ResetSmoothing();
        redirector.Evaluate(Pose(2, new Vector2(1f, 0f), Vector2.up, Vector2.zero, 0f), geometry, intent);
        Assert.That(redirector.LastDiagnostics.SteeringSide, Is.EqualTo(-1));
    }

    /// <summary>验证用户已处于 Steering Center 时不产生多余的 rotation 或 curvature。</summary>
    [Test]
    public void Steering_AtCenter_ReturnsZeroGain()
    {
        var command = Redirector().Evaluate(
            Pose(1, Vector2.zero, Vector2.up, new Vector2(0.2f, 0f), 5f),
            Geometry(),
            Intent());

        Assert.That(command.RotationYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(command.CurvatureYawDeltaDeg, Is.Zero.Within(Epsilon));
    }

    /// <summary>验证移动产生 curvature、静止不产生 curvature，真实转动产生 rotation proposal。</summary>
    [Test]
    public void MovementAndTurn_ProduceExpectedProposalTypes()
    {
        var geometry = Geometry();
        var intent = Intent();
        var stationary = Redirector().Evaluate(
            Pose(1, new Vector2(-1f, 0f), Vector2.up, Vector2.zero, 0f), geometry, intent);
        var moving = Redirector().Evaluate(
            Pose(2, new Vector2(-1f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f), geometry, intent);
        var turning = Redirector().Evaluate(
            Pose(3, new Vector2(-1f, 0f), Vector2.up, Vector2.zero, 5f), geometry, intent);

        Assert.That(stationary.CurvatureYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(Mathf.Abs(moving.CurvatureYawDeltaDeg), Is.GreaterThan(Epsilon));
        Assert.That(Mathf.Abs(turning.RotationYawDeltaDeg), Is.GreaterThan(Epsilon));
    }

    /// <summary>验证接近正负 180 度时保持首次选择的 steering side，不发生符号跳变。</summary>
    [Test]
    public void NearOpposite_WrapAround_PreservesDeterministicSide()
    {
        var redirector = Redirector();
        var geometry = Geometry();
        var intent = Intent();
        var forward179 = RdwYawConvention.YawToForward(179f);
        var forwardMinus179 = RdwYawConvention.YawToForward(-179f);

        redirector.Evaluate(Pose(1, new Vector2(0f, -1f), forward179, Vector2.zero, 0f), geometry, intent);
        var firstSide = redirector.LastDiagnostics.SteeringSide;
        redirector.Evaluate(Pose(2, new Vector2(0f, -1f), forwardMinus179, Vector2.zero, 0f), geometry, intent);

        Assert.That(firstSide, Is.Not.Zero);
        Assert.That(redirector.LastDiagnostics.SteeringSide, Is.EqualTo(firstSide));
    }

    /// <summary>验证矩形与 L 形凹多边形均生成严格位于内部的 Steering Center。</summary>
    [Test]
    public void Geometry_RectangleAndConcaveRoom_ProduceInteriorCenter()
    {
        var rectangle = Geometry();
        var concaveBoundary = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(4f, 0f),
            new Vector2(4f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 4f),
            new Vector2(0f, 4f)
        };
        var concave = new RdwGeometrySnapshot("l_room", concaveBoundary, null);

        Assert.That(rectangle.IsValid, Is.True);
        Assert.That(IsInside(rectangle.Boundary, rectangle.SteeringCenterXZ), Is.True);
        Assert.That(concave.IsValid, Is.True);
        Assert.That(IsInside(concave.Boundary, concave.SteeringCenterXZ), Is.True);
        Assert.That(concave.SteeringCenterXZ.x < 1f || concave.SteeringCenterXZ.y < 1f, Is.True);
    }

    /// <summary>验证障碍覆盖质心时使用确定性合法回退点。</summary>
    [Test]
    public void Geometry_ObstacleAtCentroid_SelectsClearFallback()
    {
        var obstacle = new PhysicalRiskObstacleSnapshot("center", Vector2.zero, 0.75f);
        var first = new RdwGeometrySnapshot("room", SquareBoundary, new[] { obstacle });
        var second = new RdwGeometrySnapshot("room", SquareBoundary, new[] { obstacle });

        Assert.That(first.IsValid, Is.True);
        Assert.That(Vector2.Distance(first.SteeringCenterXZ, obstacle.center), Is.GreaterThan(0.85f));
        Assert.That(first.SteeringCenterXZ, Is.EqualTo(second.SteeringCenterXZ));
    }

    /// <summary>验证 ControllerSimulation translation 与 S2C yaw 合并为同一 Continuous 命令。</summary>
    [Test]
    public void CompositeRedirector_MergesIntoSingleContinuousCommand()
    {
        var locomotion = new FixedRedirector(new RdwGainCommand(
            0f, 0f, new Vector2(0.1f, 0.2f), RdwGainOwner.ContinuousRedirector, "simulation", 1));
        var s2c = new FixedRedirector(new RdwGainCommand(
            -1f, -0.5f, Vector2.zero, RdwGainOwner.ContinuousRedirector, "s2c", 1));
        var composite = new CompositeContinuousRedirector(locomotion, s2c);

        var command = composite.Evaluate(
            Pose(7, new Vector2(-1f, 0f), Vector2.up, Vector2.zero, 0f), Geometry(), Intent());

        Assert.That(command.PoseSequence, Is.EqualTo(7));
        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(command.RotationYawDeltaDeg, Is.EqualTo(-1f).Within(Epsilon));
        Assert.That(command.CurvatureYawDeltaDeg, Is.EqualTo(-0.5f).Within(Epsilon));
        Assert.That(command.TranslationDeltaXZ, Is.EqualTo(new Vector2(0.1f, 0.2f)));
        Assert.That(locomotion.EvaluateCount, Is.EqualTo(1));
        Assert.That(s2c.EvaluateCount, Is.EqualTo(1));
    }

    /// <summary>验证 PhysicalWalking 不注入模拟平移，但仍由同一 Continuous S2C 生成曲率。</summary>
    [Test]
    public void PhysicalWalking_UsesS2cWithoutSimulatedTranslation()
    {
        var composite = new CompositeContinuousRedirector(new NullRedirector(), Redirector());

        var command = composite.Evaluate(
            Pose(1, new Vector2(-1f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            Geometry(),
            Intent());

        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(command.TranslationDeltaXZ, Is.EqualTo(Vector2.zero));
        Assert.That(Mathf.Abs(command.CurvatureYawDeltaDeg), Is.GreaterThan(Epsilon));
    }

    /// <summary>验证目标改变只向同一个 S2C 实例提供新的 NavigationIntent。</summary>
    [Test]
    public void GuidanceTargetChange_ReusesSameS2cWithNewIntent()
    {
        var recordingS2c = new IntentRecordingRedirector(Redirector());
        var composite = new CompositeContinuousRedirector(new NullRedirector(), recordingS2c);
        var pose = Pose(1, new Vector2(-1f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f);
        var firstIntent = new RdwNavigationIntent(
            "E5", new Vector2(0f, 5f), Vector2.up, NavigationTargetRole.FinalTarget);
        var rerouteIntent = new RdwNavigationIntent(
            "E3", new Vector2(-2f, 2f), new Vector2(-1f, 1f), NavigationTargetRole.NavigableDetour);

        composite.Evaluate(pose, Geometry(), firstIntent);
        composite.Evaluate(Pose(2, pose.PhysicalPositionXZ, pose.PhysicalForwardXZ, pose.DeltaPhysicalPositionXZ, 0f), Geometry(), rerouteIntent);

        Assert.That(recordingS2c.EvaluateCount, Is.EqualTo(2));
        Assert.That(recordingS2c.TargetIds, Is.EqualTo(new[] { "E5", "E3" }));
    }

    /// <summary>验证 Reset/暂停清除 S2C 跨帧状态，Fresh Replan 后可重新产生增益。</summary>
    [Test]
    public void RedirectorLifecycle_ResetStopsStaleGainAndAllowsResume()
    {
        var redirector = Redirector();
        var active = redirector.Evaluate(
            Pose(1, new Vector2(-1f, 0f), Vector2.up, new Vector2(0f, 0.1f), 5f),
            Geometry(),
            Intent());
        Assert.That(active.HasControl, Is.True);

        redirector.ResetSmoothing();
        var paused = redirector.Evaluate(
            Pose(2, new Vector2(-1f, 0f), Vector2.up, Vector2.zero, 0f),
            Geometry(),
            Intent());
        Assert.That(paused.HasControl, Is.False);

        var resumed = redirector.Evaluate(
            Pose(3, new Vector2(-1f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            Geometry(),
            Intent());
        Assert.That(Mathf.Abs(resumed.CurvatureYawDeltaDeg), Is.GreaterThan(Epsilon));
    }

    /// <summary>验证 Idle、Completed、无 Intent、安全无效与 None 模式均不能授予 Continuous owner。</summary>
    [Test]
    public void Availability_RequiresCompleteNavigatingLifecycleFacts()
    {
        Assert.That(Availability(), Is.True);
        Assert.That(Availability(sessionRunning: false), Is.False);
        Assert.That(Availability(runtimeNavigating: false), Is.False);
        Assert.That(Availability(poseValid: false), Is.False);
        Assert.That(Availability(trackingValid: false), Is.False);
        Assert.That(Availability(calibrationValid: false), Is.False);
        Assert.That(Availability(physicalSpaceValid: false), Is.False);
        Assert.That(Availability(navigationIntentValid: false), Is.False);
        Assert.That(Availability(redirectorAvailable: false), Is.False);
        Assert.That(Availability(continuousEnabled: false), Is.False);
    }

    /// <summary>验证 Limiter 与 Applier 保留 rotation/curvature 分量且只应用一次总 yaw。</summary>
    [Test]
    public void GainPipeline_PreservesComponentsAndAppliesTheirSumOnce()
    {
        var root = new GameObject("Step2CGainRoot");
        try
        {
            var limiter = new RdwGainLimiter(8f, 20f);
            var requested = new RdwGainCommand(
                3f, 3f, Vector2.zero, RdwGainOwner.ContinuousRedirector, "s2c", 1);
            var limited = limiter.Limit(requested, new RdwGainLimitContext(
                RdwGainOwner.ContinuousRedirector, true, true, true, false, true, 0.1f));
            var result = new RdwGainApplier(root.transform, root.transform).Apply(limited);

            Assert.That(limited.RotationYawDeltaDeg, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(limited.CurvatureYawDeltaDeg, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(result.AppliedRotationYawDeltaDeg, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(result.AppliedCurvatureYawDeltaDeg, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(result.AppliedYawDeltaDeg, Is.EqualTo(2f).Within(Epsilon));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static SteerToCenterRedirector Redirector()
    {
        return new SteerToCenterRedirector(
            movementThresholdMetersPerSecond: 0.2f,
            rotationThresholdDegreesPerSecond: 1.5f,
            curvatureRadiusMeters: 10f,
            maxCurvatureDegreesPerSecond: 15f,
            maxRotationDegreesPerSecond: 30f,
            rotationGainMultiplier: 0.1f,
            steeringDampening: 0f,
            smoothingFactor: 1f,
            centerToleranceMeters: 0.1f);
    }

    private static bool Availability(
        bool continuousEnabled = true,
        bool redirectorAvailable = true,
        bool sessionRunning = true,
        bool runtimeNavigating = true,
        bool poseValid = true,
        bool trackingValid = true,
        bool calibrationValid = true,
        bool physicalSpaceValid = true,
        bool navigationIntentValid = true)
    {
        return RdwContinuousAvailability.IsAvailable(
            continuousEnabled,
            redirectorAvailable,
            sessionRunning,
            runtimeNavigating,
            poseValid,
            trackingValid,
            calibrationValid,
            physicalSpaceValid,
            navigationIntentValid);
    }

    private static bool IsInside(System.Collections.Generic.IReadOnlyList<Vector2> boundary, Vector2 point)
    {
        var inside = false;
        for (var i = 0; i < boundary.Count; i++)
        {
            var j = (i + boundary.Count - 1) % boundary.Count;
            var current = boundary[i];
            var previous = boundary[j];
            if (((current.y > point.y) != (previous.y > point.y)) &&
                point.x < (previous.x - current.x) * (point.y - current.y) / (previous.y - current.y) + current.x)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static RdwGeometrySnapshot Geometry()
    {
        return new RdwGeometrySnapshot("room", SquareBoundary, null);
    }

    private static RdwNavigationIntent Intent()
    {
        return new RdwNavigationIntent(
            "E5", new Vector2(0f, 5f), Vector2.up, NavigationTargetRole.FinalTarget);
    }

    private static RdwPoseFrame Pose(
        long sequence,
        Vector2 physicalPosition,
        Vector2 physicalForward,
        Vector2 deltaPosition,
        float deltaYaw)
    {
        return new RdwPoseFrame(
            sequence,
            sequence,
            physicalPosition,
            physicalForward,
            Vector2.zero,
            Vector2.up,
            deltaPosition,
            deltaYaw,
            0.1f,
            "calibration",
            "room",
            true);
    }

    private sealed class FixedRedirector : IRdwRedirector
    {
        private readonly RdwGainCommand command;

        public int EvaluateCount { get; private set; }

        public FixedRedirector(in RdwGainCommand value)
        {
            command = value;
        }

        public RdwGainCommand Evaluate(
            in RdwPoseFrame pose,
            in RdwGeometrySnapshot geometry,
            in RdwNavigationIntent intent)
        {
            EvaluateCount++;
            return new RdwGainCommand(
                command.RotationYawDeltaDeg,
                command.CurvatureYawDeltaDeg,
                command.TranslationDeltaXZ,
                command.Source,
                command.Reason,
                pose.Sequence);
        }
    }

    private sealed class IntentRecordingRedirector : IRdwRedirector, IRdwRedirectorAvailability
    {
        private readonly IRdwRedirector inner;

        public System.Collections.Generic.List<string> TargetIds { get; } =
            new System.Collections.Generic.List<string>();
        public int EvaluateCount { get; private set; }
        public bool IsAvailable => true;

        public IntentRecordingRedirector(IRdwRedirector redirector)
        {
            inner = redirector;
        }

        public RdwGainCommand Evaluate(
            in RdwPoseFrame pose,
            in RdwGeometrySnapshot geometry,
            in RdwNavigationIntent intent)
        {
            EvaluateCount++;
            TargetIds.Add(intent.TargetId);
            return inner.Evaluate(pose, geometry, intent);
        }
    }
}
