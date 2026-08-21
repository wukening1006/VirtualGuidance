using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证固定 2:1 Turn 的 Physical 180 度到 Virtual 360 度映射。
/// </summary>
public sealed class RdwFixedTwoOneTurnTests
{
    /// <summary>固定模式忽略普通倍率，并按恢复方向生成 Physical 与注入各 180 度的计划。</summary>
    [TestCase(90f, 180f)]
    [TestCase(-90f, -180f)]
    public void Plan_UsesFixedPhysicalAndInjectedHalfTurns(float recoveryYaw, float expectedTurn)
    {
        var plan = CreatePlan(recoveryYaw, injectionRatio: 0.25f);

        Assert.That(plan.TargetPhysicalTurnDelta, Is.EqualTo(expectedTurn).Within(0.001f));
        Assert.That(plan.TargetInjectedYawDelta, Is.EqualTo(expectedTurn).Within(0.001f));
        Assert.That(plan.InjectionRatio, Is.EqualTo(1f));
    }

    /// <summary>用户完成 180 度 Physical 转身时，同方向注入 180 度并形成 Virtual 360 度。</summary>
    [Test]
    public void Resetter_MapsPhysical180ToVirtual360()
    {
        var plan = CreatePlan(90f, injectionRatio: 5f);
        var resetter = new TwoOneTurnLikeResetter();
        resetter.Initialize(plan, CreatePose(1, 0d, 0f, 0f));

        CommitTurn(resetter, CreatePose(2, 0.1d, 90f, 90f));
        CommitTurn(resetter, CreatePose(3, 0.2d, 180f, 90f));

        Assert.That(resetter.IsMappingComplete, Is.True);
        Assert.That(resetter.AccumulatedPhysicalTurn, Is.EqualTo(180f).Within(0.001f));
        Assert.That(resetter.AccumulatedInjectedYaw, Is.EqualTo(180f).Within(0.001f));
        Assert.That(
            resetter.AccumulatedPhysicalTurn + resetter.AccumulatedInjectedYaw,
            Is.EqualTo(360f).Within(0.001f));
    }

    private static RdwResetPlan CreatePlan(float recoveryYaw, float injectionRatio)
    {
        var recoverySample = new HeadingSample(
            recoveryYaw,
            RdwYawConvention.YawToForward(recoveryYaw),
            2f,
            HeadingSafety.Safe);
        return RdwResetPlan.Create(
            new RecoveryHeadingPlan(recoverySample, 1f),
            CreatePose(1, 0d, 0f, 0f),
            RdwResetMappingStrategy.FixedTwoOneTurn,
            injectionRatio,
            0.25f,
            12f);
    }

    private static void CommitTurn(TwoOneTurnLikeResetter resetter, RdwPoseFrame pose)
    {
        var command = resetter.Evaluate(pose, default);
        resetter.Commit(new RdwGainApplyResult(
            RdwGainApplyStatus.Applied,
            command,
            command.RotationYawDeltaDeg,
            Vector2.zero));
    }

    private static RdwPoseFrame CreatePose(
        long sequence,
        double timestamp,
        float physicalYaw,
        float deltaPhysicalYaw)
    {
        return new RdwPoseFrame(
            sequence,
            timestamp,
            Vector2.zero,
            RdwYawConvention.YawToForward(physicalYaw),
            Vector2.zero,
            Vector2.up,
            Vector2.zero,
            deltaPhysicalYaw,
            0.1f,
            "calibration",
            "room",
            true);
    }
}
