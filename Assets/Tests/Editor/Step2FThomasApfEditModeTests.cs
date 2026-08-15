using NUnit.Framework;
using UnityEngine;

/// <summary>验证 Step 2F Thomas APF 的纯 force evaluator 和 Redirector 接入契约。</summary>
public sealed class Step2FThomasApfEditModeTests
{
    private static readonly Vector2[] LargeBoundary =
    {
        new Vector2(-5f, -5f),
        new Vector2(5f, -5f),
        new Vector2(5f, 5f),
        new Vector2(-5f, 5f)
    };

    /// <summary>验证右墙斥力指向房间内部。</summary>
    [Test]
    public void Force_RightWall_PushesLeft()
    {
        var result = Evaluator().Evaluate(new Vector2(4.4f, 0f), Vector2.up, LargeBoundary, null);
        Assert.That(result.IsFinite, Is.True);
        Assert.That(result.BoundaryForceXZ.x, Is.LessThan(0f));
    }

    /// <summary>验证左墙斥力方向与右墙相反。</summary>
    [Test]
    public void Force_LeftWall_PushesRight()
    {
        var result = Evaluator().Evaluate(new Vector2(-4.4f, 0f), Vector2.up, LargeBoundary, null);
        Assert.That(result.BoundaryForceXZ.x, Is.GreaterThan(0f));
    }

    /// <summary>验证同时靠近两面墙时合力指向 corner 内部。</summary>
    [Test]
    public void Force_Corner_CombinesTwoBoundaryDirections()
    {
        var result = Evaluator().Evaluate(new Vector2(4.4f, 4.4f), Vector2.up, LargeBoundary, null);
        Assert.That(result.TotalForceXZ.x, Is.LessThan(0f));
        Assert.That(result.TotalForceXZ.y, Is.LessThan(0f));
    }

    /// <summary>验证圆形障碍物右侧的斥力远离 obstacle center。</summary>
    [Test]
    public void Force_CircleObstacle_PushesAwayFromCenter()
    {
        var obstacle = new PhysicalRiskObstacleSnapshot("center", Vector2.zero, 0.5f);
        var result = Evaluator().Evaluate(new Vector2(1f, 0f), Vector2.right, LargeBoundary, new[] { obstacle });
        Assert.That(result.ObstacleForceXZ.x, Is.GreaterThan(0f));
    }

    /// <summary>验证两个对称障碍物的相反 force 会进入 neutral，而不是声称 Safe。</summary>
    [Test]
    public void Force_SymmetricObstacles_ReturnsNeutralPreference()
    {
        var obstacles = new[]
        {
            new PhysicalRiskObstacleSnapshot("left", new Vector2(-1f, 0f), 0.25f),
            new PhysicalRiskObstacleSnapshot("right", new Vector2(1f, 0f), 0.25f)
        };
        var result = new ThomasApfForceEvaluator(new ThomasApfForceSettings(
            boundaryInfluenceRadiusMeters: 0f,
            obstacleInfluenceRadiusMeters: 2f,
            userRadiusMeters: 0f,
            safetyMarginMeters: 0f,
            maxForceMagnitude: 100f)).Evaluate(Vector2.zero, Vector2.up, LargeBoundary, obstacles);
        Assert.That(result.DesiredDirectionXZ, Is.EqualTo(Vector2.zero));
        Assert.That(result.NeutralReason, Is.EqualTo("force_neutral"));
    }

    /// <summary>验证远离边界和 obstacle 时合力接近 neutral。</summary>
    [Test]
    public void Force_FarFromGeometry_IsNeutral()
    {
        var result = Evaluator().Evaluate(Vector2.zero, Vector2.up, LargeBoundary, null);
        Assert.That(result.TotalForceMagnitude, Is.EqualTo(0f).Within(0.001f));
        Assert.That(result.DesiredDirectionXZ, Is.EqualTo(Vector2.zero));
    }

    /// <summary>验证近零 clearance 不产生 NaN/Infinity，并通过 force clamp。</summary>
    [Test]
    public void Force_NearZeroDistance_IsFiniteAndClamped()
    {
        var settings = new ThomasApfForceSettings(
            boundaryInfluenceRadiusMeters: 0f,
            obstacleInfluenceRadiusMeters: 2f,
            userRadiusMeters: 0f,
            safetyMarginMeters: 0f,
            maxForceMagnitude: 5f,
            distanceEpsilonMeters: 0.01f);
        var result = new ThomasApfForceEvaluator(settings).Evaluate(
            Vector2.zero,
            Vector2.right,
            LargeBoundary,
            new[] { new PhysicalRiskObstacleSnapshot("center", Vector2.zero, 0.5f) });
        Assert.That(result.IsFinite, Is.True);
        Assert.That(result.ForceClamped, Is.True);
        Assert.That(result.TotalForceMagnitude, Is.EqualTo(5f).Within(0.001f));
    }

    /// <summary>验证 APF 只输出 gain command，不绕过统一 owner 和 Apply 管线。</summary>
    [Test]
    public void Redirector_ActiveForce_UsesContinuousOwnerAndDiagnostics()
    {
        var redirector = new ThomasApfRedirector(
            movementThresholdMetersPerSecond: 0.2f,
            rotationThresholdDegreesPerSecond: 1.5f,
            curvatureRadiusMeters: 10f,
            rotationGainMultiplier: 0.1f,
            steeringDampening: 0f,
            smoothingFactor: 1f,
            apfSettings: new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 2f,
                obstacleInfluenceRadiusMeters: 0f,
                userRadiusMeters: 0f,
                safetyMarginMeters: 0f,
                maxForceMagnitude: 100f));
        var command = redirector.Evaluate(
            Pose(1, new Vector2(4.4f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            new RdwGeometrySnapshot("room", LargeBoundary, null),
            Intent());
        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(redirector.LastDiagnostics.RedirectorType, Is.EqualTo("ThomasAPF"));
        Assert.That(redirector.LastDiagnostics.BoundaryForceXZ.x, Is.LessThan(0f));
    }

    /// <summary>验证 invalid tracking geometry 时 APF 返回零命令。</summary>
    [Test]
    public void Redirector_InvalidGeometry_ReturnsZeroCommand()
    {
        var redirector = new ThomasApfRedirector(apfSettings: ThomasApfForceSettings.Default);
        var invalidGeometry = new RdwGeometrySnapshot("invalid", new[] { Vector2.zero, Vector2.right }, null);
        var command = redirector.Evaluate(Pose(1, Vector2.zero, Vector2.up, Vector2.zero, 0f), invalidGeometry, Intent());
        Assert.That(command.HasControl, Is.False);
    }

    /// <summary>验证 Thomas APF 经 CompositeContinuousRedirector 仍只输出统一 Continuous owner。</summary>
    [Test]
    public void Pipeline_CompositeThomasApf_UsesSingleContinuousOwner()
    {
        var redirector = new ThomasApfRedirector(
            movementThresholdMetersPerSecond: 0.2f,
            rotationThresholdDegreesPerSecond: 1.5f,
            curvatureRadiusMeters: 10f,
            rotationGainMultiplier: 0.1f,
            steeringDampening: 0f,
            smoothingFactor: 1f,
            apfSettings: new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 2f,
                obstacleInfluenceRadiusMeters: 0f,
                userRadiusMeters: 0f,
                safetyMarginMeters: 0f,
                maxForceMagnitude: 100f));
        var composite = new CompositeContinuousRedirector(new NullRedirector(), redirector);
        var command = composite.Evaluate(
            Pose(1, new Vector2(4.4f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            new RdwGeometrySnapshot("room", LargeBoundary, null),
            Intent());
        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(composite.LastDiagnostics.RedirectorType, Is.EqualTo("ThomasAPF"));
    }

    /// <summary>验证 Reset 生命周期清除 APF 的平滑方向，避免旧 force 支配 Fresh Pose。</summary>
    [Test]
    public void Pipeline_ResetThomasApf_ClearsSmoothing()
    {
        var redirector = new ThomasApfRedirector(apfSettings: ThomasApfForceSettings.Default);
        var geometry = new RdwGeometrySnapshot("room", LargeBoundary, null);
        redirector.Evaluate(Pose(1, new Vector2(4.4f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f), geometry, Intent());
        redirector.ResetSmoothing();
        var command = redirector.Evaluate(Pose(2, new Vector2(4.4f, 0f), Vector2.up, Vector2.zero, 0f), geometry, Intent());
        Assert.That(command.HasControl, Is.False);
    }

    /// <summary>验证普通 risk revision 不打断平滑，而 Critical 出现时立即清除旧方向历史。</summary>
    [Test]
    public void Pipeline_CriticalRiskClearsHistoryWithoutRevisionChurnReset()
    {
        var redirector = new ThomasApfRedirector(
            movementThresholdMetersPerSecond: 0.2f,
            rotationThresholdDegreesPerSecond: 1.5f,
            curvatureRadiusMeters: 10f,
            rotationGainMultiplier: 0.1f,
            steeringDampening: 0f,
            smoothingFactor: 0.5f,
            apfSettings: new ThomasApfForceSettings(
                boundaryInfluenceRadiusMeters: 2f,
                obstacleInfluenceRadiusMeters: 0f,
                userRadiusMeters: 0f,
                safetyMarginMeters: 0f,
                maxForceMagnitude: 100f));
        var firstGeometry = new RdwGeometrySnapshot("room", LargeBoundary, null, "risk-1", 1);
        var revisedGeometry = new RdwGeometrySnapshot("room", LargeBoundary, null, "risk-2", 2);
        var criticalGeometry = new RdwGeometrySnapshot("room", LargeBoundary, null, "risk-3", 3, true);
        var pose = Pose(1, new Vector2(4.4f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f);
        var first = redirector.Evaluate(pose, firstGeometry, Intent());
        var revised = redirector.Evaluate(Pose(2, pose.PhysicalPositionXZ, pose.PhysicalForwardXZ, pose.DeltaPhysicalPositionXZ, 0f), revisedGeometry, Intent());
        var critical = redirector.Evaluate(Pose(3, pose.PhysicalPositionXZ, pose.PhysicalForwardXZ, pose.DeltaPhysicalPositionXZ, 0f), criticalGeometry, Intent());
        Assert.That(Mathf.Abs(revised.CurvatureYawDeltaDeg), Is.GreaterThan(Mathf.Abs(first.CurvatureYawDeltaDeg)));
        Assert.That(Mathf.Abs(critical.CurvatureYawDeltaDeg), Is.EqualTo(Mathf.Abs(first.CurvatureYawDeltaDeg)).Within(0.001f));
    }

    private static ThomasApfForceEvaluator Evaluator()
    {
        return new ThomasApfForceEvaluator(new ThomasApfForceSettings(
            boundaryInfluenceRadiusMeters: 1.5f,
            obstacleInfluenceRadiusMeters: 1.5f,
            userRadiusMeters: 0.25f,
            safetyMarginMeters: 0.15f,
            maxForceMagnitude: 100f));
    }

    private static RdwNavigationIntent Intent()
    {
        return new RdwNavigationIntent("target", new Vector2(0f, 5f), Vector2.up, NavigationTargetRole.FinalTarget);
    }

    private static RdwPoseFrame Pose(long sequence, Vector2 position, Vector2 forward, Vector2 deltaPosition, float deltaYaw)
    {
        return new RdwPoseFrame(
            sequence,
            sequence,
            position,
            forward,
            Vector2.zero,
            Vector2.up,
            deltaPosition,
            deltaYaw,
            0.1f,
            "calibration",
            "room",
            true);
    }
}
