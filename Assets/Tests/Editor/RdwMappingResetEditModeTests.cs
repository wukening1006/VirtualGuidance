using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Step 2B Mapping Reset 的算法、帧事务、生命周期与恢复链。
/// </summary>
public sealed class RdwMappingResetEditModeTests
{
    private const float Epsilon = 0.001f;

    /// <summary>验证 TwoOneTurnLike 会逐帧把 Physical turn 转成同符号 mapping injection 并累计完成。</summary>
    [Test]
    public void TwoOneTurnLike_PhysicalTurnProgressesAppliedMapping()
    {
        var root = new GameObject("TwoOneTurnLikeRoot");
        try
        {
            var resetter = new TwoOneTurnLikeResetter();
            var plan = ResetPlan(
                RdwResetMappingStrategy.TwoOneTurnLike,
                targetPhysicalTurn: 10f,
                injectionRatio: 1f,
                targetInjectedYaw: 10f);
            resetter.Initialize(plan, Pose(1, 1d, 0f, 0f));
            var applier = new RdwGainApplier(root.transform, root.transform);
            var limiter = new RdwGainLimiter();

            ApplyResetFrame(resetter, limiter, applier, Pose(2, 1.02d, 4f, 4f));

            Assert.That(resetter.AccumulatedPhysicalTurn, Is.EqualTo(4f).Within(Epsilon));
            Assert.That(resetter.AccumulatedInjectedYaw, Is.EqualTo(4f).Within(Epsilon));
            Assert.That(resetter.IsMappingComplete, Is.False);

            ApplyResetFrame(resetter, limiter, applier, Pose(3, 1.04d, 10f, 6f));

            Assert.That(resetter.AccumulatedPhysicalTurn, Is.EqualTo(10f).Within(Epsilon));
            Assert.That(resetter.AccumulatedInjectedYaw, Is.EqualTo(10f).Within(Epsilon));
            Assert.That(resetter.IsMappingComplete, Is.True);
            Assert.That(RdwYawConvention.ForwardToYaw(
                    new Vector2(root.transform.forward.x, root.transform.forward.z)),
                Is.EqualTo(10f).Within(Epsilon));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 SafeHeading plan 冻结全部起点字段、配置倍率，并保持左转负号。</summary>
    [Test]
    public void SafeHeadingMapping_PlanFreezesConfiguredRatioAndYawSign()
    {
        var start = Pose(41, 5d, 10f, 0f, 25f);
        var recovery = RecoveryPlan(10f, -30f);
        var plan = RdwResetPlan.Create(
            recovery,
            start,
            RdwResetMappingStrategy.SafeHeadingMapping,
            0.5f,
            180f,
            0.25f,
            9f);

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan.StartPoseSequence, Is.EqualTo(41));
        Assert.That(plan.StartPhysicalYaw, Is.EqualTo(10f).Within(Epsilon));
        Assert.That(plan.StartVirtualYaw, Is.EqualTo(25f).Within(Epsilon));
        Assert.That(plan.TargetPhysicalHeading, Is.EqualTo(-30f).Within(Epsilon));
        Assert.That(plan.TargetPhysicalTurnDelta, Is.EqualTo(-40f).Within(Epsilon));
        Assert.That(plan.InjectionRatio, Is.EqualTo(0.5f).Within(Epsilon));
        Assert.That(plan.TargetInjectedYawDelta, Is.EqualTo(-20f).Within(Epsilon));
        Assert.That(plan.MappingStrategy, Is.EqualTo(RdwResetMappingStrategy.SafeHeadingMapping));
        Assert.That(plan.MaxDuration, Is.EqualTo(9f).Within(Epsilon));

        var resetter = new SafeHeadingMappingResetter();
        resetter.Initialize(plan, start);
        var command = resetter.Evaluate(Pose(42, 5.02d, 0f, -10f), default);

        Assert.That(command.Source, Is.EqualTo(RdwGainOwner.Resetter));
        Assert.That(command.RotationYawDeltaDeg, Is.EqualTo(-5f).Within(Epsilon));
    }

    /// <summary>验证 Resetter 只累计 GainApplier 实际应用的 yaw，而不是 limiter 前的 requested yaw。</summary>
    [Test]
    public void Resetter_AccumulatesAppliedYawAfterLimiter()
    {
        var root = new GameObject("AppliedYawLedgerRoot");
        try
        {
            var resetter = new SafeHeadingMappingResetter();
            resetter.Initialize(
                ResetPlan(
                    RdwResetMappingStrategy.SafeHeadingMapping,
                    targetPhysicalTurn: 10f,
                    injectionRatio: 1f,
                    targetInjectedYaw: 10f),
                Pose(1, 1d, 0f, 0f));
            var requested = resetter.Evaluate(Pose(2, 1.02d, 10f, 10f), default);
            var limiter = new RdwGainLimiter(maxYawPerFrame: 3f);
            var limited = limiter.Limit(requested, ResetLimitContext());
            var result = new RdwGainApplier(root.transform, root.transform).Apply(limited);

            resetter.Commit(result);

            Assert.That(requested.RotationYawDeltaDeg, Is.EqualTo(10f).Within(Epsilon));
            Assert.That(limited.RotationYawDeltaDeg, Is.EqualTo(3f).Within(Epsilon));
            Assert.That(resetter.AccumulatedPhysicalTurn, Is.EqualTo(10f).Within(Epsilon));
            Assert.That(resetter.AccumulatedInjectedYaw, Is.EqualTo(3f).Within(Epsilon));
            Assert.That(resetter.IsMappingComplete, Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证反向 physical turn 不会向错误方向注入 mapping yaw。</summary>
    [Test]
    public void Resetter_OppositePhysicalTurnProducesZeroMappingCommand()
    {
        var resetter = new SafeHeadingMappingResetter();
        resetter.Initialize(
            ResetPlan(
                RdwResetMappingStrategy.SafeHeadingMapping,
                targetPhysicalTurn: 20f,
                injectionRatio: 0.5f,
                targetInjectedYaw: 10f),
            Pose(1, 1d, 0f, 0f));

        var command = resetter.Evaluate(Pose(2, 1.02d, -5f, -5f), default);

        Assert.That(command.RotationYawDeltaDeg, Is.Zero.Within(Epsilon));
        Assert.That(resetter.AccumulatedPhysicalTurn, Is.EqualTo(-5f).Within(Epsilon));
        Assert.That(resetter.AccumulatedInjectedYaw, Is.Zero.Within(Epsilon));
        Assert.That(resetter.IsMappingComplete, Is.False);
    }

    /// <summary>验证 GainApplier 拒绝 Reset 命令后立即报告 mapping failure，而不是继续保持 active。</summary>
    [Test]
    public void Resetter_ApplyRejectionCancelsMapping()
    {
        var resetter = new SafeHeadingMappingResetter();
        resetter.Initialize(
            ResetPlan(
                RdwResetMappingStrategy.SafeHeadingMapping,
                targetPhysicalTurn: 20f,
                injectionRatio: 0.5f,
                targetInjectedYaw: 10f),
            Pose(1, 1d, 0f, 0f));

        var command = resetter.Evaluate(Pose(2, 1.02d, 5f, 5f), default);
        var rejected = new RdwGainApplyResult(
            RdwGainApplyStatus.InvalidRoot,
            command,
            0f,
            Vector2.zero);

        resetter.Commit(rejected);

        Assert.That(resetter.IsActive, Is.False);
        Assert.That(resetter.IsMappingComplete, Is.False);
        Assert.That(resetter.StatusReason, Is.EqualTo("reset_gain_not_applied"));
    }

    /// <summary>验证 physical heading 到达与 mapping target 完成是两个独立完成门。</summary>
    [Test]
    public void PauseReset_RequiresPhysicalAndMappingCompletion()
    {
        var host = new GameObject("ResetDualCompletionGate");
        try
        {
            var controller = host.AddComponent<PauseResetController>();
            controller.ConfigureMappingReset(new SafeHeadingMappingResetter());
            Assert.That(controller.PauseForReset("test", RecoveryPlan(0f, 5f)), Is.True);

            var start = Pose(1, 1d, 0f, 0f);
            Assert.That(controller.TryInitializeMapping(start), Is.True);
            controller.ProcessPhysicalForward(RdwYawConvention.YawToForward(5f));

            Assert.That(controller.PhysicalHeadingReached, Is.True);
            Assert.That(controller.IsMappingComplete, Is.False);
            Assert.That(controller.State, Is.EqualTo(ResetLifecycleState.TurningToSafeHeading));

            var command = controller.EvaluateMapping(Pose(2, 1.02d, 5f, 5f), default);
            var result = new RdwGainApplier(host.transform, host.transform).Apply(command);
            controller.CommitMapping(result, RdwYawConvention.YawToForward(5f));

            Assert.That(controller.IsMappingComplete, Is.True);
            Assert.That(controller.State, Is.EqualTo(ResetLifecycleState.Completed));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    /// <summary>验证 Reset owner 同时排除 GuidanceTurn 和 ContinuousRedirector 的非零命令。</summary>
    [Test]
    public void ResetOwner_ExcludesGuidanceAndContinuousCommands()
    {
        var owner = new RdwOwnerArbiter().Resolve(
            hardStop: false,
            resetActive: true,
            guidanceTurnActive: true,
            continuousRedirectorAvailable: true);
        var limiter = new RdwGainLimiter();
        var context = ResetLimitContext();
        var guidance = new RdwGainCommand(
            3f, 0f, Vector2.zero, RdwGainOwner.GuidanceTurnGain, "guidance", 1);
        var continuous = new RdwGainCommand(
            3f, 0f, Vector2.zero, RdwGainOwner.ContinuousRedirector, "continuous", 2);
        var reset = new RdwGainCommand(
            3f, 0f, Vector2.zero, RdwGainOwner.Resetter, "reset", 3);

        Assert.That(owner, Is.EqualTo(RdwGainOwner.Resetter));
        Assert.That(limiter.Limit(guidance, context).HasControl, Is.False);
        Assert.That(limiter.Limit(continuous, context).HasControl, Is.False);
        Assert.That(limiter.Limit(reset, context).RotationYawDeltaDeg, Is.EqualTo(3f).Within(Epsilon));
    }

    /// <summary>验证同一 Reset PoseSequence 只修改 Root 一次，且以 HMD 为 pivot 不产生位置跳变。</summary>
    [Test]
    public void ResetGainApplier_AppliesSequenceOnceAroundHmdPivot()
    {
        var root = new GameObject("ResetPivotRoot");
        var hmd = new GameObject("ResetPivotHmd");
        try
        {
            hmd.transform.SetParent(root.transform, false);
            hmd.AddComponent<Camera>();
            hmd.transform.localPosition = new Vector3(0.35f, 1.65f, 0.25f);
            var initialHmdPosition = hmd.transform.position;
            var initialLocalRotation = hmd.transform.localRotation;
            var applier = new RdwGainApplier(root.transform, hmd.transform);
            var command = new RdwGainCommand(
                6f, 0f, Vector2.zero, RdwGainOwner.Resetter, "reset", 12);

            var first = applier.Apply(command);
            var duplicate = applier.Apply(command);

            Assert.That(first.Status, Is.EqualTo(RdwGainApplyStatus.Applied));
            Assert.That(duplicate.Status, Is.EqualTo(RdwGainApplyStatus.DuplicateSequence));
            Assert.That(Vector3.Distance(hmd.transform.position, initialHmdPosition), Is.LessThan(Epsilon));
            Assert.That(Quaternion.Angle(hmd.transform.localRotation, initialLocalRotation), Is.LessThan(Epsilon));
            Assert.That(RdwYawConvention.ForwardToYaw(
                    new Vector2(root.transform.forward.x, root.transform.forward.z)),
                Is.EqualTo(6f).Within(Epsilon));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证真实帧事务中 Reset 独占 owner，并逐帧驱动 Root 到明确 mapping completion。</summary>
    [Test]
    public void FrameCoordinator_PhysicalTurnDrivesExclusiveResetMapping()
    {
        using (var harness = new MappingHarness(includeRecoveryCoordinator: false))
        {
            harness.BeginReset(startYaw: 0f, targetYaw: 10f);
            var initialHmdPosition = harness.Hmd.position;

            var start = harness.ProcessPose(0f, 1d);
            var firstTurn = harness.ProcessPose(5f, 1.02d);
            var completed = harness.ProcessPose(10f, 1.04d);

            Assert.That(start.ActiveOwner, Is.EqualTo(RdwGainOwner.Resetter));
            Assert.That(start.ApplyResult.Status, Is.EqualTo(RdwGainApplyStatus.ZeroCommand));
            Assert.That(firstTurn.ActiveOwner, Is.EqualTo(RdwGainOwner.Resetter));
            Assert.That(firstTurn.RequestedCommand.RotationYawDeltaDeg, Is.EqualTo(5f).Within(Epsilon));
            Assert.That(firstTurn.ApplyResult.AppliedYawDeltaDeg, Is.EqualTo(5f).Within(Epsilon));
            Assert.That(firstTurn.ResetProgress.AccumulatedPhysicalTurn, Is.EqualTo(5f).Within(Epsilon));
            Assert.That(firstTurn.ResetProgress.AccumulatedInjectedYaw, Is.EqualTo(5f).Within(Epsilon));
            Assert.That(completed.ResetProgress.IsMappingComplete, Is.True);
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.Completed));
            Assert.That(harness.Redirector.EvaluateCount, Is.Zero);
            Assert.That(Vector3.Distance(harness.Hmd.position, initialHmdPosition), Is.LessThan(Epsilon));
        }
    }

    /// <summary>验证 tracking loss、calibration invalid 与 pose jump 都产生零注入并重建 ResetPlan delta chain。</summary>
    [Test]
    public void FrameCoordinator_InvalidPoseStopsInjectionAndInvalidatesResetChain()
    {
        using (var harness = new MappingHarness(includeRecoveryCoordinator: false))
        {
            harness.BeginReset(startYaw: 0f, targetYaw: 20f);
            harness.ProcessPose(0f, 1d);
            harness.ProcessPose(5f, 1.02d);
            var firstResetId = harness.Reset.ActiveMappingPlan.ResetId;
            var rotationBeforeInvalid = harness.RotationRoot.rotation;

            harness.Calibration.ReportTrackingLost();
            var trackingLost = harness.FrameCoordinator.ProcessTrackingSample(0.02f, 1.04d);

            Assert.That(trackingLost.ActiveOwner, Is.EqualTo(RdwGainOwner.HardStop));
            Assert.That(trackingLost.ApplyResult.AppliedYawDeltaDeg, Is.Zero.Within(Epsilon));
            Assert.That(Quaternion.Angle(harness.RotationRoot.rotation, rotationBeforeInvalid), Is.LessThan(Epsilon));
            Assert.That(harness.Reset.ActiveMappingPlan, Is.Null);

            Assert.That(harness.Calibration.ReportTrackingRecovered(), Is.True);
            var restarted = harness.FrameCoordinator.ProcessTrackingSample(0.02f, 1.06d);
            Assert.That(restarted.ActiveOwner, Is.EqualTo(RdwGainOwner.Resetter));
            Assert.That(restarted.ApplyResult.AppliedYawDeltaDeg, Is.Zero.Within(Epsilon));
            Assert.That(harness.Reset.ActiveMappingPlan.ResetId, Is.Not.EqualTo(firstResetId));

            var rotationBeforeJump = harness.RotationRoot.rotation;
            harness.Pose.SetCalibratedPhysicalPose(harness.Pose.PhysicalPositionXZ, 80f);
            var poseJump = harness.FrameCoordinator.ProcessTrackingSample(0.02f, 1.08d);
            Assert.That(poseJump.ActiveOwner, Is.EqualTo(RdwGainOwner.HardStop));
            Assert.That(poseJump.ApplyResult.AppliedYawDeltaDeg, Is.Zero.Within(Epsilon));
            Assert.That(Quaternion.Angle(harness.RotationRoot.rotation, rotationBeforeJump), Is.LessThan(Epsilon));
            Assert.That(harness.Reset.ActiveMappingPlan, Is.Null);

            harness.Calibration.MarkCalibrationSuspect();
            var calibrationInvalid = harness.FrameCoordinator.ProcessTrackingSample(0.02f, 1.10d);
            Assert.That(calibrationInvalid.ActiveOwner, Is.EqualTo(RdwGainOwner.HardStop));
            Assert.That(calibrationInvalid.ApplyResult.AppliedYawDeltaDeg, Is.Zero.Within(Epsilon));
        }
    }

    /// <summary>验证 mapping completion 继续进入 Stabilizing、Safe Revalidation、Fresh Replan。</summary>
    [Test]
    public void MappingCompletion_UsesStabilizeRevalidateFreshReplanChain()
    {
        using (var harness = new MappingHarness(includeRecoveryCoordinator: true))
        {
            var states = new List<GuidanceRecoveryState>();
            var replanCount = 0;
            harness.Coordinator.RecoveryStateChanged += states.Add;
            harness.Coordinator.Replanning += _ => replanCount++;
            harness.BeginReset(startYaw: 0f, targetYaw: 10f);

            harness.ProcessPose(0f, 1d);
            harness.ProcessPose(5f, 1.02d);
            harness.ProcessPose(10f, 1.04d);

            CollectionAssert.AreEqual(
                new[]
                {
                    GuidanceRecoveryState.Stabilizing,
                    GuidanceRecoveryState.Revalidating,
                    GuidanceRecoveryState.Replanning,
                    GuidanceRecoveryState.Inactive
                },
                states);
            Assert.That(replanCount, Is.EqualTo(1));
            Assert.That(harness.Backend.RequestCount, Is.EqualTo(1));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
        }
    }

    /// <summary>验证 frozen heading 失效会取消 mapping、进入 Hold，并在新风险事实后创建新 ResetPlan。</summary>
    [Test]
    public void FrozenHeadingInvalid_EntersHoldAndCreatesNewResetPlanFromNewFacts()
    {
        using (var harness = new MappingHarness(includeRecoveryCoordinator: true))
        {
            harness.SetPhysicalPose(Vector2.zero, -10f);
            harness.BeginReset(startYaw: -10f, targetYaw: 0f);
            harness.ProcessCurrentPose(1d);
            harness.ProcessPose(-5f, 1.02d);
            var oldResetId = harness.Reset.ActiveMappingPlan.ResetId;

            harness.SetPhysicalPose(new Vector2(0f, 2.3f), -5f);
            var invalidRisk = harness.Risk.EvaluateNow();

            Assert.That(invalidRisk, Is.Not.Null);
            Assert.That(harness.Coordinator.RecoveryState,
                Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));
            Assert.That(harness.Reset.MappingProgress.IsActive, Is.False);

            harness.SetPhysicalPose(Vector2.zero, 0f);
            var safeRisk = harness.Risk.EvaluateNow();
            Assert.That(safeRisk, Is.Not.Null);
            Assert.That(harness.Coordinator.RecoveryState,
                Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeHeading));

            harness.ProcessCurrentPose(1.04d);
            Assert.That(harness.Reset.ActiveMappingPlan, Is.Not.Null);
            Assert.That(harness.Reset.ActiveMappingPlan.ResetId, Is.Not.EqualTo(oldResetId));
        }
    }

    /// <summary>验证 Recovery Hold 在持续无新可恢复事实且预算耗尽后才进入 HardStop。</summary>
    [Test]
    public void PersistentRecoveryHold_EndsInHardStopAfterBudget()
    {
        using (var harness = new MappingHarness(includeRecoveryCoordinator: true))
        {
            harness.SetPhysicalPose(Vector2.zero, -10f);
            harness.BeginReset(startYaw: -10f, targetYaw: 0f);
            harness.ProcessCurrentPose(1d);
            harness.SetPhysicalPose(new Vector2(0f, 2.3f), -10f);
            harness.Risk.EvaluateNow();
            Assert.That(harness.Coordinator.RecoveryState,
                Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));

            SetPrivateField(
                harness.Coordinator,
                "recoveryHoldStartedAt",
                Time.realtimeSinceStartup - 10f);
            SetPrivateField(harness.Coordinator, "recoveryHoldBudgetSeconds", 0.1f);
            InvokePrivate(harness.Coordinator, "Update");

            Assert.That(harness.Coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Terminated));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
        }
    }

    private static void ApplyResetFrame(
        IRdwResetter resetter,
        RdwGainLimiter limiter,
        RdwGainApplier applier,
        in RdwPoseFrame pose)
    {
        var requested = resetter.Evaluate(pose, default);
        var limited = limiter.Limit(requested, ResetLimitContext());
        resetter.Commit(applier.Apply(limited));
    }

    private static RdwGainLimitContext ResetLimitContext()
    {
        return new RdwGainLimitContext(
            RdwGainOwner.Resetter,
            trackingValid: true,
            calibrationValid: true,
            physicalSpaceMatches: true,
            hardStop: false,
            userMoving: false,
            deltaTime: 0.02f);
    }

    private static RdwResetPlan ResetPlan(
        RdwResetMappingStrategy strategy,
        float targetPhysicalTurn,
        float injectionRatio,
        float targetInjectedYaw)
    {
        return new RdwResetPlan(
            "reset-test",
            1,
            1d,
            0f,
            0f,
            targetPhysicalTurn,
            targetPhysicalTurn,
            strategy,
            injectionRatio,
            targetInjectedYaw,
            0.01f,
            10f);
    }

    private static RecoveryHeadingPlan RecoveryPlan(float startYaw, float targetYaw)
    {
        var turn = RdwYawConvention.SignedDeltaYaw(startYaw, targetYaw);
        return new RecoveryHeadingPlan(
            new HeadingSample(
                turn,
                RdwYawConvention.YawToForward(targetYaw),
                2f,
                HeadingSafety.Safe),
            1f);
    }

    private static RdwPoseFrame Pose(
        long sequence,
        double timestamp,
        float physicalYaw,
        float physicalDeltaYaw,
        float virtualYaw = 0f)
    {
        return new RdwPoseFrame(
            sequence,
            timestamp,
            Vector2.zero,
            RdwYawConvention.YawToForward(physicalYaw),
            Vector2.zero,
            RdwYawConvention.YawToForward(virtualYaw),
            Vector2.zero,
            physicalDeltaYaw,
            0.02f,
            "calibration",
            "space",
            true);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field: {fieldName}");
        field.SetValue(target, value);
    }

    private static object InvokePrivate(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing private method: {methodName}");
        return method.Invoke(target, arguments);
    }

    private sealed class MappingHarness : IDisposable
    {
        private readonly GameObject root;
        private readonly GameObject world;

        public Transform RotationRoot => root.transform;
        public Transform Hmd { get; }
        public UserPoseTracker Pose { get; }
        public PhysicalSpaceCalibration Calibration { get; }
        public PhysicalRiskEvaluator Risk { get; }
        public PauseResetController Reset { get; }
        public RdwFrameCoordinator FrameCoordinator { get; }
        public CountingRedirector Redirector { get; } = new CountingRedirector();
        public GuidanceTaskController Task { get; }
        public GuidanceCoordinator Coordinator { get; }
        public RecordingGuidanceClient Backend { get; }

        public MappingHarness(bool includeRecoveryCoordinator)
        {
            root = new GameObject("Step2BMappingHarness");
            world = new GameObject("Step2BMappingWorld");
            Hmd = new GameObject("Hmd").transform;
            Hmd.SetParent(root.transform, false);
            Hmd.localPosition = new Vector3(0.3f, 1.65f, 0.2f);
            Hmd.gameObject.AddComponent<Camera>();

            Pose = root.AddComponent<UserPoseTracker>();
            Pose.ConfigureVirtualPoseSource(Hmd);
            Assert.That(Pose.ConfigureVirtualRotationRoot(root.transform), Is.True);

            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            Calibration = root.AddComponent<PhysicalSpaceCalibration>();
            Calibration.Configure(space, Pose);
            Calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            Calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(Calibration.CalibrateCurrentPose(), Is.True);

            Risk = root.AddComponent<PhysicalRiskEvaluator>();
            Risk.Configure(space, Pose, Calibration);
            Assert.That(Risk.EvaluateNow(), Is.Not.Null);

            var motion = root.AddComponent<MotionDetector>();
            motion.Configure(Pose);
            motion.SetExternallyDriven(true);
            Reset = root.AddComponent<PauseResetController>();
            Reset.Configure(null, null, null, Pose, Risk);
            Reset.ConfigureMappingReset(new SafeHeadingMappingResetter());

            NavigationTargetManager targets = null;
            SemanticTargetSelector selector = null;
            if (includeRecoveryCoordinator)
            {
                CreateExhibit(world.transform, "E1", Vector3.zero);
                CreateExhibit(world.transform, "E5", new Vector3(0f, 0f, 5f));
                targets = root.AddComponent<NavigationTargetManager>();
                targets.Configure(Pose);
                targets.RebuildIndex();
                selector = root.AddComponent<SemanticTargetSelector>();
                selector.Configure(targets);

                Task = root.AddComponent<GuidanceTaskController>();
                Task.ConfigureTask(new GuidanceTaskDefinition
                {
                    taskId = "T2_main_target",
                    condition = "ours_full",
                    startExhibitId = "E1",
                    finalTargetId = "E5",
                    routeTargetIds = new[] {"E1", "E5"},
                    maxConsecutiveDetours = 0
                });
                Task.StartTask();
                Coordinator = root.AddComponent<GuidanceCoordinator>();
                Coordinator.Configure(
                    null,
                    null,
                    Task,
                    targets,
                    selector,
                    null,
                    Risk,
                    null,
                    Reset,
                    null,
                    null,
                    motion);
                Backend = new RecordingGuidanceClient();
                Coordinator.ConfigureRequestClient(Backend);
                SetPrivateField(Coordinator, "resetStabilizationSeconds", 0f);
            }

            var applier = new RdwGainApplier(root.transform, Hmd);
            FrameCoordinator = new RdwFrameCoordinator(
                Pose,
                Calibration,
                space,
                Risk,
                motion,
                Reset,
                null,
                Coordinator,
                targets,
                applier,
                Redirector);
        }

        public void BeginReset(float startYaw, float targetYaw)
        {
            SetPhysicalPose(Vector2.zero, startYaw);
            var plan = RecoveryPlan(startYaw, targetYaw);
            if (Coordinator != null)
            {
                Task.PauseForReset();
                SetPrivateField(Coordinator, "frozenRecoveryPlan", plan);
                SetPrivateField(Coordinator, "recoveryState", GuidanceRecoveryState.WaitingForUserTurn);
            }

            Assert.That(Reset.PauseForReset("mapping reset test", plan), Is.True);
        }

        public RdwFrameEventInfo ProcessPose(float yaw, double timestamp)
        {
            SetPhysicalPose(Vector2.zero, yaw);
            return ProcessCurrentPose(timestamp);
        }

        public RdwFrameEventInfo ProcessCurrentPose(double timestamp)
        {
            return FrameCoordinator.ProcessTrackingSample(0.02f, timestamp);
        }

        public void SetPhysicalPose(Vector2 rawPosition, float rawYaw)
        {
            Calibration.UpdateRawTrackingPose(rawPosition, rawYaw);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(world);
        }

        private static ExhibitMetadata CreateExhibit(Transform parent, string id, Vector3 position)
        {
            var exhibit = new GameObject(id);
            exhibit.transform.SetParent(parent, false);
            exhibit.transform.position = position;
            var metadata = exhibit.AddComponent<ExhibitMetadata>();
            metadata.Configure(
                id,
                id,
                null,
                string.Empty,
                "main_target",
                null,
                exhibit.transform,
                exhibit.transform);
            return metadata;
        }
    }

    private sealed class CountingRedirector : IRdwRedirector
    {
        public int EvaluateCount { get; private set; }

        public RdwGainCommand Evaluate(
            in RdwPoseFrame pose,
            in RdwGeometrySnapshot geometry,
            in RdwNavigationIntent intent)
        {
            EvaluateCount++;
            return new RdwGainCommand(
                2f,
                0f,
                Vector2.zero,
                RdwGainOwner.ContinuousRedirector,
                "counting_redirector",
                pose.Sequence);
        }
    }

    private sealed class RecordingGuidanceClient : IGuidanceRequestClient
    {
        public bool RequestInFlight { get; private set; }
        public int RequestCount { get; private set; }

        public void RequestGuidance(GuidanceRequestContext context)
        {
            RequestCount++;
            RequestInFlight = true;
        }

        public void CancelPendingRequest()
        {
            RequestInFlight = false;
        }
    }
}
