using NUnit.Framework;
using UnityEngine;

/// <summary>验证 Step 2E S2O 的 orbit geometry、侧向稳定和统一 gain contract。</summary>
public sealed class Step2EContinuousS2OEditModeTests
{
    private static readonly Vector2[] Boundary =
    {
        new Vector2(-3f, -3f),
        new Vector2(3f, -3f),
        new Vector2(3f, 3f),
        new Vector2(-3f, 3f)
    };

    /// <summary>验证用户位于 center 外侧时生成正值 orbit 半径和合法目标。</summary>
    [Test]
    public void Orbit_OutsideCenter_GeneratesTargetOnConfiguredRadius()
    {
        var redirector = Redirector(1f);
        var command = redirector.Evaluate(
            Pose(1, new Vector2(2f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            Geometry(),
            Intent());

        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(redirector.LastDiagnostics.OrbitRadiusMeters, Is.EqualTo(1f).Within(0.001f));
        Assert.That(redirector.LastDiagnostics.OrbitTargetXZ.magnitude, Is.EqualTo(1f).Within(0.001f));
        Assert.That(redirector.ActiveOrbitSide, Is.Not.Zero);
    }

    /// <summary>验证内部、近轨道和外部位置均能产生稳定 orbit target。</summary>
    [Test]
    public void Orbit_InsideOnAndOutsideRadius_RemainsWellFormed()
    {
        var redirector = Redirector(1f);
        foreach (var position in new[] { new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(2f, 0f) })
        {
            redirector.ResetSmoothing();
            var command = redirector.Evaluate(Pose(1, position, Vector2.up, Vector2.zero, 0f), Geometry(), Intent());
            Assert.That(command.Reason, Is.EqualTo("s2o_active"));
            Assert.That(redirector.LastDiagnostics.OrbitTargetXZ.magnitude, Is.EqualTo(1f).Within(0.001f));
        }
    }

    /// <summary>验证小幅 Physical Forward 噪声不会造成 CW/CCW 高频切换。</summary>
    [Test]
    public void Orbit_SidePersistence_ResistsSmallForwardNoise()
    {
        var redirector = Redirector(1f, hysteresis: 20f);
        redirector.Evaluate(Pose(1, new Vector2(2f, 0f), Vector2.up, Vector2.zero, 0f), Geometry(), Intent());
        var firstSide = redirector.ActiveOrbitSide;
        redirector.Evaluate(Pose(2, new Vector2(2f, 0f), RdwYawConvention.YawToForward(4f), Vector2.zero, 0f), Geometry(), Intent());
        Assert.That(redirector.ActiveOrbitSide, Is.EqualTo(firstSide));
    }

    /// <summary>验证配置半径超过房间可用半径时只裁剪到 boundary safety margin 内。</summary>
    [Test]
    public void Orbit_LargeConfiguredRadius_IsClampedInsideBoundary()
    {
        var redirector = Redirector(10f, margin: 0.5f);
        redirector.Evaluate(Pose(1, new Vector2(1f, 0f), Vector2.up, Vector2.zero, 0f), Geometry(), Intent());
        Assert.That(redirector.LastDiagnostics.OrbitRadiusMeters, Is.EqualTo(2.5f).Within(0.001f));
        Assert.That(redirector.LastDiagnostics.OrbitTargetXZ.x, Is.InRange(-2.5f, 2.5f));
        Assert.That(redirector.LastDiagnostics.OrbitTargetXZ.y, Is.InRange(-2.5f, 2.5f));
    }

    /// <summary>验证无效 Physical Space 只能返回 neutral，不保留前一帧 side。</summary>
    [Test]
    public void Orbit_InvalidGeometry_ReturnsNeutral()
    {
        var redirector = Redirector(1f);
        var invalid = new RdwGeometrySnapshot("invalid", new[] { Vector2.zero, Vector2.right }, null);
        var command = redirector.Evaluate(Pose(1, Vector2.right, Vector2.up, Vector2.zero, 0f), invalid, Intent());
        Assert.That(command.HasControl, Is.False);
        Assert.That(redirector.ActiveOrbitSide, Is.Zero);
        Assert.That(redirector.LastDiagnostics.RedirectorType, Is.EqualTo("S2O"));
    }

    /// <summary>验证 S2O 通过 CompositeContinuousRedirector 仍只提交一个 Continuous 命令。</summary>
    [Test]
    public void Pipeline_CompositeS2O_UsesSingleContinuousOwner()
    {
        var steering = Redirector(1f);
        var composite = new CompositeContinuousRedirector(new NullRedirector(), steering);
        var command = composite.Evaluate(
            Pose(1, new Vector2(2f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f),
            Geometry(),
            Intent());
        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(command.TranslationDeltaXZ, Is.EqualTo(Vector2.zero));
        Assert.That(composite.LastDiagnostics.RedirectorType, Is.EqualTo("S2O"));
    }

    /// <summary>验证 Reset 生命周期清除 S2O 的 side 与平滑增益。</summary>
    [Test]
    public void Pipeline_ResetS2O_ClearsStateBeforeFreshPose()
    {
        var redirector = Redirector(1f);
        redirector.Evaluate(Pose(1, new Vector2(2f, 0f), Vector2.up, new Vector2(0f, 0.1f), 0f), Geometry(), Intent());
        Assert.That(redirector.ActiveOrbitSide, Is.Not.Zero);
        redirector.ResetSmoothing();
        var command = redirector.Evaluate(Pose(2, new Vector2(2f, 0f), Vector2.up, Vector2.zero, 0f), Geometry(), Intent());
        Assert.That(command.HasControl, Is.False);
        Assert.That(redirector.ActiveOrbitSide, Is.Not.Zero);
    }

    private static SteerToOrbitRedirector Redirector(float radius, float margin = 0.25f, float hysteresis = 12f)
    {
        return new SteerToOrbitRedirector(
            movementThresholdMetersPerSecond: 0.2f,
            rotationThresholdDegreesPerSecond: 1.5f,
            curvatureRadiusMeters: 10f,
            rotationGainMultiplier: 0.1f,
            steeringDampening: 0f,
            smoothingFactor: 1f,
            orbitRadiusMeters: radius,
            orbitBoundarySafetyMarginMeters: margin,
            sideSwitchHysteresisDegrees: hysteresis);
    }

    private static RdwGeometrySnapshot Geometry()
    {
        return new RdwGeometrySnapshot("room", Boundary, null);
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
