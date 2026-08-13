using System;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Step 2A 的帧事实、owner、安全限幅与唯一 Root 写入契约。
/// </summary>
public sealed class RdwInfrastructureEditModeTests
{
    private const float Epsilon = 0.001f;

    /// <summary>验证统一 yaw 约定为右转正、左转负。</summary>
    [Test]
    public void YawConvention_RightTurnIsPositiveAndLeftTurnIsNegative()
    {
        Assert.That(RdwYawConvention.ForwardToYaw(Vector2.right), Is.EqualTo(90f).Within(Epsilon));
        Assert.That(RdwYawConvention.ForwardToYaw(Vector2.left), Is.EqualTo(-90f).Within(Epsilon));
        Assert.That(RdwYawConvention.SignedDeltaYaw(Vector2.up, Vector2.right), Is.EqualTo(90f).Within(Epsilon));
        Assert.That(RdwYawConvention.SignedDeltaYaw(Vector2.up, Vector2.left), Is.EqualTo(-90f).Within(Epsilon));
    }

    /// <summary>验证跨越正负 180 度时使用最短有符号增量。</summary>
    [Test]
    public void YawConvention_WrapAroundUsesShortestSignedDelta()
    {
        Assert.That(RdwYawConvention.SignedDeltaYaw(179f, -179f), Is.EqualTo(2f).Within(Epsilon));
        Assert.That(RdwYawConvention.SignedDeltaYaw(-179f, 179f), Is.EqualTo(-2f).Within(Epsilon));
    }

    /// <summary>验证旧 MotionDetector 左转为正协议只在 RDW 边界转换一次。</summary>
    [Test]
    public void YawConvention_LegacyMotionDeltaConvertsOnce()
    {
        Assert.That(RdwYawConvention.FromLegacyMotionDelta(12f), Is.EqualTo(-12f));
        Assert.That(RdwYawConvention.FromLegacyMotionDelta(-12f), Is.EqualTo(12f));
    }

    /// <summary>验证几何快照复制边界事实并计算稳定中心。</summary>
    [Test]
    public void GeometrySnapshot_CopiesFactsAndCalculatesCenter()
    {
        var boundary = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(4f, 0f),
            new Vector2(4f, 2f),
            new Vector2(0f, 2f)
        };
        var geometry = new RdwGeometrySnapshot("room", boundary, null, "risk-1", 7);
        boundary[0] = new Vector2(100f, 100f);

        Assert.That(geometry.IsValid, Is.True);
        Assert.That(geometry.CenterXZ, Is.EqualTo(new Vector2(2f, 1f)));
        Assert.That(geometry.Boundary[0], Is.EqualTo(Vector2.zero));
        Assert.That(geometry.RiskSnapshotId, Is.EqualTo("risk-1"));
        Assert.That(geometry.RiskRevision, Is.EqualTo(7));
    }

    /// <summary>验证 owner 固定优先级以及正常帧的 ContinuousRedirector owner。</summary>
    [Test]
    public void OwnerArbiter_UsesDocumentedPriority()
    {
        var arbiter = new RdwOwnerArbiter();

        Assert.That(arbiter.Resolve(true, true, true, true), Is.EqualTo(RdwGainOwner.HardStop));
        Assert.That(arbiter.Resolve(false, true, true, true), Is.EqualTo(RdwGainOwner.Resetter));
        Assert.That(arbiter.Resolve(false, false, true, true), Is.EqualTo(RdwGainOwner.GuidanceTurnGain));
        Assert.That(arbiter.Resolve(false, false, false, true), Is.EqualTo(RdwGainOwner.ContinuousRedirector));
        Assert.That(arbiter.Resolve(false, false, false, false), Is.EqualTo(RdwGainOwner.None));
    }

    /// <summary>验证 tracking、calibration、space identity 与 owner 不匹配都会 veto。</summary>
    [Test]
    public void GainLimiter_InvalidSafetyOrOwnerReturnsZero()
    {
        var limiter = new RdwGainLimiter(8f, 180f);
        var command = Command(4f, 0f, Vector2.zero, RdwGainOwner.ContinuousRedirector, 1);

        var trackingVeto = limiter.Limit(command, Context(
            RdwGainOwner.ContinuousRedirector, false, true, true, false, false, 0.02f));
        var calibrationVeto = limiter.Limit(command, Context(
            RdwGainOwner.ContinuousRedirector, true, false, true, false, false, 0.02f));
        var identityVeto = limiter.Limit(command, Context(
            RdwGainOwner.ContinuousRedirector, true, true, false, false, false, 0.02f));
        var ownerVeto = limiter.Limit(command, Context(
            RdwGainOwner.GuidanceTurnGain, true, true, true, false, false, 0.02f));

        AssertZero(trackingVeto, "safety_veto");
        AssertZero(calibrationVeto, "safety_veto");
        AssertZero(identityVeto, "safety_veto");
        AssertZero(ownerVeto, "owner_mismatch");
    }

    /// <summary>验证非有限命令被拒绝，translation 只对 Continuous owner 开放。</summary>
    [Test]
    public void GainLimiter_RejectsNonFiniteAndDisablesTranslation()
    {
        var limiter = new RdwGainLimiter();
        var context = Context(
            RdwGainOwner.ContinuousRedirector, true, true, true, false, true, 0.02f);
        var invalid = Command(float.NaN, 0f, Vector2.zero, RdwGainOwner.ContinuousRedirector, 1);
        var translation = Command(0f, 0f, new Vector2(1f, 2f), RdwGainOwner.ContinuousRedirector, 2);

        AssertZero(limiter.Limit(invalid, context), "invalid_command");
        Assert.That(limiter.Limit(translation, context).TranslationDeltaXZ, Is.EqualTo(new Vector2(1f, 2f)));
        var guidanceContext = Context(
            RdwGainOwner.GuidanceTurnGain, true, true, true, false, true, 0.02f);
        var guidanceTranslation = Command(0f, 0f, new Vector2(1f, 2f), RdwGainOwner.GuidanceTurnGain, 3);
        Assert.That(limiter.Limit(guidanceTranslation, guidanceContext).TranslationDeltaXZ, Is.EqualTo(Vector2.zero));
    }

    /// <summary>验证每帧、每秒上限以及静止时 curvature 为零。</summary>
    [Test]
    public void GainLimiter_AppliesCapsAndMovementThreshold()
    {
        var limiter = new RdwGainLimiter(8f, 20f, 0.01f);
        var moving = Context(
            RdwGainOwner.ContinuousRedirector, true, true, true, false, true, 0.1f);
        var stationary = Context(
            RdwGainOwner.ContinuousRedirector, true, true, true, false, false, 0.1f);
        var command = Command(0f, 5f, Vector2.zero, RdwGainOwner.ContinuousRedirector, 1);

        var movingResult = limiter.Limit(command, moving);
        var stationaryResult = limiter.Limit(command, stationary);
        Assert.That(movingResult.RotationYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(movingResult.CurvatureYawDeltaDeg, Is.EqualTo(2f).Within(Epsilon));
        Assert.That(stationaryResult.RotationYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(stationaryResult.CurvatureYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(limiter.IsMoving(new Vector2(0.009f, 0f)), Is.False);
        Assert.That(limiter.IsMoving(new Vector2(0.01f, 0f)), Is.True);
    }

    /// <summary>验证默认限幅器不二次收紧 Step 1 已有的单次 8 度 Guidance Gain 预算。</summary>
    [Test]
    public void GainLimiter_DefaultPreservesExistingGuidanceGainBudget()
    {
        var limiter = new RdwGainLimiter();
        var command = Command(8f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 1);
        var context = Context(
            RdwGainOwner.GuidanceTurnGain, true, true, true, false, false, 0.001f);

        Assert.That(limiter.Limit(command, context).RotationYawDeltaDeg, Is.EqualTo(8f).Within(Epsilon));
    }

    /// <summary>验证 NullRedirector 对相同 pose sequence 始终输出零命令。</summary>
    [Test]
    public void NullRedirector_ReturnsZeroForCurrentPoseSequence()
    {
        var pose = Pose(42);
        var command = new NullRedirector().Evaluate(pose, default, default);

        Assert.That(command.PoseSequence, Is.EqualTo(42));
        Assert.That(command.HasControl, Is.False);
        Assert.That(command.Reason, Is.EqualTo("null_redirector"));
    }

    /// <summary>验证零命令正常关闭 sequence 且不改变 Virtual Rotation Root。</summary>
    [Test]
    public void GainApplier_NullCommandLeavesRootUnchanged()
    {
        var root = new GameObject("NullRedirectorRoot");
        try
        {
            root.transform.SetPositionAndRotation(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 25f, 0f));
            var initialPosition = root.transform.position;
            var initialRotation = root.transform.rotation;
            var applier = new RdwGainApplier(root.transform, root.transform);

            var result = applier.Apply(RdwGainCommand.Zero(1, "null_redirector"));

            Assert.That(result.Status, Is.EqualTo(RdwGainApplyStatus.ZeroCommand));
            Assert.That(root.transform.position, Is.EqualTo(initialPosition));
            Assert.That(Quaternion.Angle(root.transform.rotation, initialRotation), Is.LessThan(Epsilon));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 yaw 围绕 HMD pivot 注入，不直接改 Camera local rotation 或 world position。</summary>
    [Test]
    public void GainApplier_RotatesRootAroundHmdPivotOnly()
    {
        var root = new GameObject("XROrigin");
        var cameraObject = new GameObject("Main Camera");
        try
        {
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.AddComponent<Camera>();
            cameraObject.transform.localPosition = new Vector3(0.4f, 1.65f, 0.2f);
            cameraObject.transform.localRotation = Quaternion.Euler(5f, 15f, 0f);
            var initialHmdWorldPosition = cameraObject.transform.position;
            var initialCameraLocalRotation = cameraObject.transform.localRotation;
            var initialRootRotation = root.transform.rotation;
            var applier = new RdwGainApplier(root.transform, cameraObject.transform);

            var result = applier.Apply(Command(12f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 1));

            Assert.That(result.Status, Is.EqualTo(RdwGainApplyStatus.Applied));
            Assert.That(Vector3.Distance(cameraObject.transform.position, initialHmdWorldPosition), Is.LessThan(Epsilon));
            Assert.That(Quaternion.Angle(cameraObject.transform.localRotation, initialCameraLocalRotation), Is.LessThan(Epsilon));
            Assert.That(Quaternion.Angle(root.transform.rotation, initialRootRotation), Is.EqualTo(12f).Within(Epsilon));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Camera 不能成为 Virtual Rotation Root。</summary>
    [Test]
    public void GainApplier_RejectsCameraAsRotationRoot()
    {
        var cameraObject = new GameObject("IllegalCameraRoot");
        try
        {
            cameraObject.AddComponent<Camera>();
            var applier = new RdwGainApplier(cameraObject.transform, cameraObject.transform);

            Assert.That(applier.RotationRoot, Is.Null);
            Assert.That(applier.Apply(Command(1f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 1)).Status,
                Is.EqualTo(RdwGainApplyStatus.InvalidRoot));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }

    /// <summary>验证重复和过期 pose sequence 均不能再次修改 Root。</summary>
    [Test]
    public void GainApplier_RejectsDuplicateAndStaleSequences()
    {
        var root = new GameObject("SequenceRoot");
        try
        {
            var applier = new RdwGainApplier(root.transform, root.transform);
            Assert.That(applier.Apply(Command(1f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 2)).WasAccepted, Is.True);
            var rotationAfterFirst = root.transform.rotation;

            var duplicate = applier.Apply(Command(1f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 2));
            var stale = applier.Apply(Command(1f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, 1));

            Assert.That(duplicate.Status, Is.EqualTo(RdwGainApplyStatus.DuplicateSequence));
            Assert.That(stale.Status, Is.EqualTo(RdwGainApplyStatus.StaleSequence));
            Assert.That(Quaternion.Angle(root.transform.rotation, rotationAfterFirst), Is.LessThan(Epsilon));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证应用器拒绝带非有限 translation 的命令。</summary>
    [Test]
    public void GainApplier_RejectsNonFiniteTranslation()
    {
        var root = new GameObject("InvalidTranslationRoot");
        try
        {
            var applier = new RdwGainApplier(root.transform, root.transform);
            var command = Command(0f, 0f, new Vector2(float.PositiveInfinity, 0f),
                RdwGainOwner.ContinuousRedirector, 1);

            Assert.That(applier.Apply(command).Status, Is.EqualTo(RdwGainApplyStatus.InvalidCommand));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static RdwPoseFrame Pose(long sequence)
    {
        return new RdwPoseFrame(
            sequence,
            1d,
            Vector2.zero,
            Vector2.up,
            Vector2.zero,
            Vector2.up,
            Vector2.zero,
            0f,
            0.02f,
            "calibration",
            "space",
            true);
    }

    private static RdwGainCommand Command(
        float rotationYaw,
        float curvatureYaw,
        Vector2 translation,
        RdwGainOwner owner,
        long sequence)
    {
        return new RdwGainCommand(
            rotationYaw, curvatureYaw, translation, owner, "test", sequence);
    }

    private static RdwGainLimitContext Context(
        RdwGainOwner owner,
        bool tracking,
        bool calibration,
        bool spaceMatches,
        bool hardStop,
        bool moving,
        float deltaTime)
    {
        return new RdwGainLimitContext(
            owner, tracking, calibration, spaceMatches, hardStop, moving, deltaTime);
    }

    private static void AssertZero(in RdwGainCommand command, string reason)
    {
        Assert.That(command.HasControl, Is.False);
        Assert.That(command.Reason, Is.EqualTo(reason));
    }
}
