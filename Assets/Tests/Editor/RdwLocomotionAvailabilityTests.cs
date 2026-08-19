using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证基础 locomotion 与可选 Continuous Steering 的资格彼此独立，同时共享物理安全门。
/// </summary>
public sealed class RdwLocomotionAvailabilityTests
{
    /// <summary>B1 关闭 Continuous RDW 且尚无导航意图时，基础手柄平移仍可获得 Root owner。</summary>
    [Test]
    public void B1_AllowsLocomotionWithoutContinuousSteeringOrNavigationIntent()
    {
        Assert.That(RdwContinuousAvailability.IsAvailable(
            locomotionAvailable: true,
            continuousEnabled: false,
            steeringAvailable: true,
            sessionRunning: true,
            runtimeNavigating: true,
            poseValid: true,
            trackingValid: true,
            calibrationValid: true,
            physicalSpaceValid: true,
            navigationIntentValid: false), Is.True);
    }

    /// <summary>B1 关闭 Continuous RDW 时，单独的 Steering 不能获得 Root owner。</summary>
    [Test]
    public void B1_DoesNotAllowSteeringWithoutLocomotion()
    {
        Assert.That(RdwContinuousAvailability.IsAvailable(
            locomotionAvailable: false,
            continuousEnabled: false,
            steeringAvailable: true,
            sessionRunning: true,
            runtimeNavigating: true,
            poseValid: true,
            trackingValid: true,
            calibrationValid: true,
            physicalSpaceValid: true,
            navigationIntentValid: true), Is.False);
    }

    /// <summary>B2 开启 Continuous RDW 且导航意图有效时，Steering 可以获得 Root owner。</summary>
    [Test]
    public void B2_AllowsSteeringWithValidNavigationIntent()
    {
        Assert.That(RdwContinuousAvailability.IsAvailable(
            locomotionAvailable: false,
            continuousEnabled: true,
            steeringAvailable: true,
            sessionRunning: true,
            runtimeNavigating: true,
            poseValid: true,
            trackingValid: true,
            calibrationValid: true,
            physicalSpaceValid: true,
            navigationIntentValid: true), Is.True);
    }

    /// <summary>关闭 Steering 后组合命令仍保留基础平移，但不会注入 yaw 或 curvature。</summary>
    [Test]
    public void CompositeRedirector_PreservesLocomotionWhenSteeringDisabled()
    {
        var locomotion = new FixedRedirector(new RdwGainCommand(
            0f,
            0f,
            new Vector2(0.25f, 0.5f),
            RdwGainOwner.ContinuousRedirector,
            "locomotion",
            1));
        var steering = new FixedRedirector(new RdwGainCommand(
            2f,
            1f,
            Vector2.zero,
            RdwGainOwner.ContinuousRedirector,
            "s2c",
            1));
        var redirector = new CompositeContinuousRedirector(locomotion, steering);
        redirector.SetSteeringEnabled(false);

        var command = redirector.Evaluate(CreatePose(), default, default);

        Assert.That(command.TranslationDeltaXZ, Is.EqualTo(new Vector2(0.25f, 0.5f)));
        Assert.That(command.RotationYawDeltaDeg, Is.Zero);
        Assert.That(command.CurvatureYawDeltaDeg, Is.Zero);
        Assert.That(redirector.LocomotionAvailable, Is.True);
        Assert.That(redirector.SteeringAvailable, Is.False);
    }

    /// <summary>Tracking 失效时统一 Limiter 仍会否决基础平移，保证碰撞与 Reset 安全链路优先。</summary>
    [Test]
    public void GainLimiter_VetoesLocomotionWhenTrackingIsInvalid()
    {
        var command = new RdwGainCommand(
            0f,
            0f,
            new Vector2(0.25f, 0.5f),
            RdwGainOwner.ContinuousRedirector,
            "locomotion",
            1);
        var context = new RdwGainLimitContext(
            RdwGainOwner.ContinuousRedirector,
            trackingValid: false,
            calibrationValid: true,
            physicalSpaceMatches: true,
            hardStop: false,
            userMoving: true,
            deltaTime: 0.02f);

        var limited = new RdwGainLimiter().Limit(command, context);

        Assert.That(limited.HasControl, Is.False);
        Assert.That(limited.Reason, Is.EqualTo("safety_veto"));
    }

    private static RdwPoseFrame CreatePose()
    {
        return new RdwPoseFrame(
            1,
            0d,
            Vector2.zero,
            Vector2.up,
            Vector2.zero,
            Vector2.up,
            Vector2.zero,
            0f,
            0.02f,
            "calibration",
            "wide_room",
            true);
    }

    private sealed class FixedRedirector : IRdwRedirector, IRdwRedirectorAvailability
    {
        private readonly RdwGainCommand command;

        public FixedRedirector(RdwGainCommand fixedCommand)
        {
            command = fixedCommand;
        }

        public bool IsAvailable => command.HasControl;

        public RdwGainCommand Evaluate(
            in RdwPoseFrame pose,
            in RdwGeometrySnapshot geometry,
            in RdwNavigationIntent intent)
        {
            return new RdwGainCommand(
                command.RotationYawDeltaDeg,
                command.CurvatureYawDeltaDeg,
                command.TranslationDeltaXZ,
                command.Source,
                command.Reason,
                pose.Sequence);
        }
    }
}
