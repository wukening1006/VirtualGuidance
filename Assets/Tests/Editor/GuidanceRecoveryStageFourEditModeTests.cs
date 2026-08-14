using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证物理恢复状态机与语义候选解耦的阶段四规则。
/// </summary>
public sealed class GuidanceRecoveryStageFourEditModeTests
{
    /// <summary>验证全角域分层覆盖能发现两个 Unsafe 粗采样点之间的狭窄 Safe 区间。</summary>
    [Test]
    public void RecoveryHeadingPlanner_FindsNarrowSafeBandBetweenUnsafeCoarseSamples()
    {
        var planner = new RecoveryHeadingPlanner();
        var evaluator = new TestHeadingEvaluator((yaw, _) =>
            yaw >= 42.6f && yaw <= 43.5f ? HeadingSafety.Safe : HeadingSafety.Unsafe);
        var result = planner.Search(
            SearchRisk(),
            evaluator,
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 30f,
                minimumResolutionDegrees = 1f,
                refinementFactor = 3f,
                maximumEvaluations = 720
            });

        Assert.That(result.Plan.IsSuccess, Is.True);
        Assert.That(result.Plan.RelativeYawDegrees, Is.InRange(42.6f, 43.5f));
        Assert.That(result.CompletedCoverageResolutionDegrees, Is.EqualTo(1f).Within(0.001f));
        Assert.That(result.Evaluations, Has.Some.Matches<HeadingSample>(
            sample => Mathf.Abs(Mathf.DeltaAngle(sample.RelativeYawDegrees, 30f)) < 0.001f &&
                      sample.Safety == HeadingSafety.Unsafe));
        Assert.That(result.Evaluations, Has.Some.Matches<HeadingSample>(
            sample => Mathf.Abs(Mathf.DeltaAngle(sample.RelativeYawDegrees, 60f)) < 0.001f &&
                      sample.Safety == HeadingSafety.Unsafe));
    }

    /// <summary>验证预算耗尽时 NoAcceptableHeading 只表达当前分辨率和预算下未发现 Safe。</summary>
    [Test]
    public void RecoveryHeadingPlanner_NoSafeWithinBudgetReturnsResolutionScopedFailure()
    {
        var result = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            new TestHeadingEvaluator((_, __) => HeadingSafety.Unsafe),
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 30f,
                minimumResolutionDegrees = 1f,
                refinementFactor = 3f,
                maximumEvaluations = 72
            });

        Assert.That(result.Plan.IsSuccess, Is.False);
        Assert.That(result.Plan.Failure, Is.EqualTo(RecoveryHeadingFailure.NoAcceptableHeading));
        Assert.That(result.BudgetExhausted, Is.True);
        Assert.That(result.AttemptedEvaluationCount, Is.LessThanOrEqualTo(72));
        Assert.That(result.FailureReason, Is.EqualTo("no_safe_heading_at_current_resolution_budget_exhausted"));
    }

    /// <summary>验证失败的精确查询同样消耗预算，并报告评估器不可用。</summary>
    [Test]
    public void RecoveryHeadingPlanner_FailedQueriesConsumeBudgetAndReportUnavailableEvaluator()
    {
        var evaluator = new UnavailableHeadingEvaluator();
        var result = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            evaluator,
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 30f,
                minimumResolutionDegrees = 1f,
                maximumEvaluations = 8
            });

        Assert.That(result.Plan.Failure, Is.EqualTo(RecoveryHeadingFailure.EvaluationUnavailable));
        Assert.That(result.BudgetExhausted, Is.True);
        Assert.That(result.AttemptedEvaluationCount, Is.EqualTo(7));
        Assert.That(result.SuccessfulEvaluationCount, Is.Zero);
        Assert.That(result.Evaluations, Is.Empty);
        Assert.That(evaluator.CallCount, Is.EqualTo(result.AttemptedEvaluationCount));
    }

    /// <summary>验证搜索查询与独立终验合计不会超过配置预算。</summary>
    [Test]
    public void RecoveryHeadingPlanner_FinalValidationStaysWithinTotalEvaluationBudget()
    {
        var evaluator = new TestHeadingEvaluator((yaw, _) =>
            Mathf.Abs(yaw) < 0.001f ? HeadingSafety.Safe : HeadingSafety.Unsafe);
        var result = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            evaluator,
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 90f,
                minimumResolutionDegrees = 90f,
                maximumEvaluations = 5
            });

        Assert.That(result.Plan.IsSuccess, Is.True);
        Assert.That(result.FinalValidationAttempted, Is.True);
        Assert.That(result.AttemptedEvaluationCount, Is.EqualTo(5));
        Assert.That(result.SuccessfulEvaluationCount, Is.EqualTo(5));
    }

    /// <summary>验证最佳候选在独立终验时失效后不得生成 Recovery Plan。</summary>
    [Test]
    public void RecoveryHeadingPlanner_FinalValidationFailureDoesNotProducePlan()
    {
        var evaluator = new TestHeadingEvaluator((yaw, headingCallCount) =>
            Mathf.Abs(yaw) < 0.001f && headingCallCount == 1
                ? HeadingSafety.Safe
                : HeadingSafety.Unsafe);
        var result = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            evaluator,
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 90f,
                minimumResolutionDegrees = 90f,
                maximumEvaluations = 16
            });

        Assert.That(result.Plan.IsSuccess, Is.False);
        Assert.That(result.Plan.Failure, Is.EqualTo(RecoveryHeadingFailure.FinalValidationFailed));
        Assert.That(result.FinalValidationAttempted, Is.True);
        Assert.That(result.FinalValidationPassed, Is.False);
    }

    /// <summary>验证任意非正交动作方向直接交给精确评估器，不做最近样本量化。</summary>
    [TestCase(47f)]
    [TestCase(-128f)]
    public void GuidanceActionSafetyEvaluator_UsesExactArbitraryYaw(float yawDegrees)
    {
        var evaluator = new TestHeadingEvaluator((_, __) => HeadingSafety.Safe);
        var action = new GuidanceAction(
            GuidanceActionType.TurnInPlace,
            yawDegrees < 0f ? TurnSide.Left : TurnSide.Right,
            yawDegrees,
            true);
        var state = new GuidanceActionSafetyEvaluator().Evaluate(
            action,
            SearchRisk(),
            evaluator,
            out var reason);

        Assert.That(state, Is.EqualTo(CandidateExecutionState.Executable), reason);
        Assert.That(evaluator.LastYawDegrees, Is.EqualTo(yawDegrees).Within(0.001f));
    }

    /// <summary>验证不存在可接受朝向时返回明确不可恢复结果。</summary>
    [Test]
    public void RecoveryHeadingPlanner_NoAcceptableHeadingReturnsExplicitFailure()
    {
        var evaluator = new TestHeadingEvaluator((_, __) => HeadingSafety.Critical);
        var result = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            evaluator,
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 60f,
                minimumResolutionDegrees = 10f,
                maximumEvaluations = 128
            });

        Assert.That(result.Plan.IsSuccess, Is.False);
        Assert.That(result.Plan.Failure, Is.EqualTo(RecoveryHeadingFailure.NoAcceptableHeading));
    }

    private static PhysicalRiskState SearchRisk()
    {
        return new PhysicalRiskState(
            riskSnapshotId: "continuous-search-risk",
            riskRevision: 1,
            calibrationId: "continuous-search-calibration",
            physicalSpace: "continuous-search-space",
            physicalForward: Vector2.up,
            currentHeading: new HeadingSample(0f, Vector2.up, 0.5f, HeadingSafety.Unsafe));
    }

    private sealed class TestHeadingEvaluator : IPhysicalHeadingEvaluator
    {
        private readonly Func<float, int, HeadingSafety> classify;
        private readonly Dictionary<int, int> headingCallCounts = new Dictionary<int, int>();

        public float LastYawDegrees { get; private set; }

        public TestHeadingEvaluator(Func<float, int, HeadingSafety> classifyHeading)
        {
            classify = classifyHeading;
        }

        public bool TryEvaluateAbsoluteHeading(
            PhysicalRiskState riskState,
            Vector2 absolutePhysicalForward,
            out HeadingSample sample)
        {
            LastYawDegrees = Mathf.DeltaAngle(
                0f,
                Mathf.Atan2(absolutePhysicalForward.x, absolutePhysicalForward.y) * Mathf.Rad2Deg);
            var key = Mathf.RoundToInt(LastYawDegrees * 1000f);
            headingCallCounts.TryGetValue(key, out var count);
            count++;
            headingCallCounts[key] = count;
            var safety = classify(LastYawDegrees, count);
            sample = new HeadingSample(
                LastYawDegrees,
                absolutePhysicalForward,
                safety == HeadingSafety.Safe ? 2f : 0.4f,
                safety);
            return true;
        }
    }

    private sealed class UnavailableHeadingEvaluator : IPhysicalHeadingEvaluator
    {
        public int CallCount { get; private set; }

        public bool TryEvaluateAbsoluteHeading(
            PhysicalRiskState riskState,
            Vector2 absolutePhysicalForward,
            out HeadingSample sample)
        {
            CallCount++;
            sample = default;
            return false;
        }
    }

    /// <summary>验证 Unknown 航向不能作为 Reset 的安全恢复目标。</summary>
    [Test]
    public void RecoveryHeadingPlanner_UnknownHeadingIsNotAcceptable()
    {
        var plan = new RecoveryHeadingPlanner().Search(
            SearchRisk(),
            new TestHeadingEvaluator((yaw, _) =>
                Mathf.Abs(yaw) < 0.001f ? HeadingSafety.Unknown : HeadingSafety.Unsafe),
            new RecoveryHeadingSearchSettings
            {
                coarseStepDegrees = 60f,
                minimumResolutionDegrees = 10f,
                maximumEvaluations = 128
            }).Plan;

        Assert.That(plan.IsSuccess, Is.False);
        Assert.That(plan.Failure, Is.EqualTo(RecoveryHeadingFailure.NoAcceptableHeading));
    }

    /// <summary>验证绝对航向接口使用精确几何，而不是风险快照中最近的离散采样。</summary>
    [Test]
    public void PhysicalRiskEvaluator_AbsoluteHeadingIgnoresNearestDiscreteSample()
    {
        var root = new GameObject("StageFourAbsoluteHeadingTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out _, out _, out var risk, out _);
            var fabricatedProfile = CopyRisk(risk.CurrentRiskState, new[]
            {
                new HeadingSample(0f, Vector2.up, 0.1f, HeadingSafety.Critical)
            });

            Assert.That(risk.TryEvaluateAbsoluteHeading(fabricatedProfile, Vector2.up, out var exact), Is.True);
            Assert.That(exact.Safety, Is.EqualTo(HeadingSafety.Safe));
            Assert.That(exact.ClearanceMeters, Is.GreaterThanOrEqualTo(1.2f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证旧离散字段不能回填当前朝向或重新进入运行时风险事实。</summary>
    [Test]
    public void PhysicalRiskState_LegacyDirectionInputsRemainFrozenAndDoNotFallback()
    {
        var state = new PhysicalRiskState(
            riskSnapshotId: "legacy-input-risk",
            riskRevision: 1,
            calibrationId: "legacy-input-calibration",
            physicalSpace: "legacy-input-space",
            physicalForward: Vector2.up,
            clearanceForward: 9f,
            clearanceLeft: 8f,
            clearanceRight: 7f,
            unsafeDirections: new[] { "forward" },
            safeDirections: new[] { "left", "right" },
            headingProfile: new[]
            {
                new HeadingSample(0f, Vector2.up, 9f, HeadingSafety.Safe)
            });

        Assert.That(state.current_heading.Safety, Is.EqualTo(HeadingSafety.Unknown));
        Assert.That(state.current_heading.ClearanceMeters, Is.Zero);
        Assert.That(state.clearance_forward, Is.Zero);
        Assert.That(state.clearance_left, Is.Zero);
        Assert.That(state.clearance_right, Is.Zero);
        Assert.That(state.safe_directions, Is.Empty);
        Assert.That(state.unsafe_directions, Is.Empty);
        Assert.That(state.heading_profile, Is.Empty);
    }

    /// <summary>验证完成转向后先稳定等待，再按 Revalidating、Replanning 顺序恢复。</summary>
    [Test]
    public void GuidanceCoordinator_SafeRecoveryUsesRequiredStateSequence()
    {
        var root = new GameObject("StageFourSafeRecoverySequenceTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out var task, out var risk, out var reset);
            SetPrivateField(coordinator, "resetStabilizationSeconds", 10f);
            var states = new List<GuidanceRecoveryState>();
            coordinator.RecoveryStateChanged += states.Add;

            InvokePrivate(coordinator, "TriggerPauseReset", null, risk.CurrentRiskState, "planned heading unsafe");
            states.Clear();
            reset.ProcessPhysicalForward(reset.SafePhysicalForward);

            CollectionAssert.AreEqual(new[] {GuidanceRecoveryState.Stabilizing}, states);
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Completed));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(coordinator.ResumeAfterResetAndReplan(), Is.False);

            SetPrivateField(coordinator, "recoveryRevalidationAt", Time.realtimeSinceStartup);
            InvokePrivate(coordinator, "Update");

            CollectionAssert.AreEqual(
                new[]
                {
                    GuidanceRecoveryState.Stabilizing,
                    GuidanceRecoveryState.Revalidating,
                    GuidanceRecoveryState.Replanning,
                    GuidanceRecoveryState.Inactive
                },
                states);
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证最新冻结航向为 Unknown 时不会恢复导航，而是选择新的 Safe 航向继续 Reset。</summary>
    [Test]
    public void GuidanceCoordinator_UnknownRevalidationDoesNotResumeNavigation()
    {
        var root = new GameObject("StageFourUnknownRevalidationTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out var task, out var risk, out var reset);
            var pose = root.GetComponent<UserPoseTracker>();
            var states = new List<GuidanceRecoveryState>();
            coordinator.RecoveryStateChanged += states.Add;

            InvokePrivate(coordinator, "TriggerPauseReset", null, risk.CurrentRiskState, "planned heading unsafe");
            var originalForward = reset.SafePhysicalForward;
            var originalYaw = Mathf.Atan2(originalForward.x, originalForward.y) * Mathf.Rad2Deg;
            pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 4.6f), originalYaw);
            states.Clear();
            reset.ProcessPhysicalForward(pose.PhysicalForwardXZ);

            Assert.That(states[0], Is.EqualTo(GuidanceRecoveryState.Stabilizing));
            Assert.That(states[1], Is.EqualTo(GuidanceRecoveryState.Revalidating));
            Assert.That(states, Has.None.EqualTo(GuidanceRecoveryState.Replanning));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));
            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
            Assert.That(coordinator.FrozenRecoveryPlan.Safety, Is.EqualTo(HeadingSafety.Safe));
            Assert.That(Vector2.Angle(coordinator.FrozenRecoveryPlan.SafePhysicalForward, originalForward),
                Is.GreaterThan(1f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证冻结朝向在普通风险样本变化时保持不变，失效后先 Hold，再依据新风险事实重选。</summary>
    [Test]
    public void GuidanceCoordinator_FrozenHeadingInvalidEntersHoldBeforeReselecting()
    {
        var root = new GameObject("StageFourFrozenRecoveryTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out _, out var risk, out var reset);
            var initialRisk = CopyRisk(risk.CurrentRiskState, new[]
            {
                Sample(0f, 0.9f, HeadingSafety.Unknown),
                Sample(60f, 2.6f, HeadingSafety.Safe),
                Sample(-60f, 1.4f, HeadingSafety.Safe)
            });

            InvokePrivate(coordinator, "TriggerPauseReset", null, initialRisk, "planned heading unsafe");
            var frozenForward = coordinator.FrozenRecoveryPlan.SafePhysicalForward;

            var changedButSafe = CopyRisk(risk.CurrentRiskState, new[]
            {
                Sample(0f, 2.9f, HeadingSafety.Safe),
                Sample(60f, 1.3f, HeadingSafety.Unknown),
                Sample(-60f, 2.8f, HeadingSafety.Safe)
            });
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", changedButSafe);

            Assert.That(Vector2.Distance(coordinator.FrozenRecoveryPlan.SafePhysicalForward, frozenForward),
                Is.LessThan(0.001f));
            Assert.That(Vector2.Distance(reset.SafePhysicalForward, frozenForward), Is.LessThan(0.001f));

            var discreteFrozenCritical = CopyRisk(risk.CurrentRiskState, new[]
            {
                new HeadingSample(60f, frozenForward, 0.2f, HeadingSafety.Critical),
                Sample(-60f, 2.8f, HeadingSafety.Safe)
            });
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", discreteFrozenCritical);

            Assert.That(Vector2.Distance(coordinator.FrozenRecoveryPlan.SafePhysicalForward, frozenForward),
                Is.LessThan(0.001f), "离散最近采样不得覆盖冻结航向的精确几何结果");

            var pose = root.GetComponent<UserPoseTracker>();
            InvokePrivate(coordinator, "UnbindRuntimeSafety");
            pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 2.5f) + frozenForward * 2.5f, 0f);
            var latestRisk = risk.EvaluateNow();

            Assert.That(risk.TryEvaluateAbsoluteHeading(latestRisk, frozenForward, out var exactFrozen), Is.True);
            Assert.That(exactFrozen.Safety, Is.EqualTo(HeadingSafety.Critical),
                "测试位姿必须让旧冻结航向真实进入 Critical 区间");
            var replacement = new RecoveryHeadingPlanner().Search(latestRisk, risk).Plan;
            Assert.That(replacement.IsSuccess, Is.True, "最新精确几何事实必须仍存在 Safe 替代航向");
            Assert.That(Vector2.Distance(replacement.SafePhysicalForward, frozenForward), Is.GreaterThan(0.001f));

            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", latestRisk);

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));
            Assert.That(coordinator.FrozenRecoveryPlan, Is.Null);
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));

            pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 2.5f), 0f);
            var refreshedRisk = risk.EvaluateNow();
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", refreshedRisk);

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
            Assert.That(coordinator.FrozenRecoveryPlan, Is.Not.Null);
            Assert.That(Vector2.Distance(coordinator.FrozenRecoveryPlan.SafePhysicalForward, frozenForward),
                Is.GreaterThan(0.001f));
            Assert.That(Vector2.Distance(reset.SafePhysicalForward, coordinator.FrozenRecoveryPlan.SafePhysicalForward),
                Is.LessThan(0.001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证低风险但语义候选为空时任务保持导航并进入 Hold/Replan，而不是 Reset。</summary>
    [Test]
    public void GuidanceCoordinator_EmptySemanticCandidatesHoldWithoutReset()
    {
        var root = new GameObject("StageFourEmptyCandidateTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "MissingFinal", out var coordinator, out var task, out _, out var reset);
            RuntimeInterventionInfo decision = null;
            coordinator.RuntimeIntervention += info => decision = info;

            coordinator.RequestNextGuidance();

            Assert.That(decision, Is.Not.Null);
            Assert.That(decision.Decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
            Assert.That(decision.StartReset, Is.False);
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Inactive));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证物理恢复完成后即使仍无语义候选也能退出 Reset，并转入 Hold/Replan。</summary>
    [Test]
    public void GuidanceCoordinator_RecoveryCompletesWithoutSemanticCandidate()
    {
        var root = new GameObject("StageFourRecoveryWithoutCandidateTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "MissingFinal", out var coordinator, out var task, out var risk, out var reset);
            var recoveryRisk = risk.CurrentRiskState;

            InvokePrivate(coordinator, "TriggerPauseReset", null, recoveryRisk, "planned heading unsafe");
            reset.ProcessPhysicalForward(reset.SafePhysicalForward);

            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Inactive));
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证暂时没有 Safe 恢复航向时保持任务暂停，事实恢复后继续 Reset。</summary>
    [Test]
    public void GuidanceCoordinator_RecoveryHoldResumesWhenSafeHeadingAppears()
    {
        var root = new GameObject("StageFourRecoveryHoldResumeTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out var task, out var risk, out var reset);
            InvokePrivate(coordinator, "UnbindRuntimeSafety");
            var pose = root.GetComponent<UserPoseTracker>();
            pose.SetCalibratedPhysicalPose(new Vector2(0.1f, 2.5f), 0f);
            var noSafeRisk = risk.EvaluateNow();

            InvokePrivate(coordinator, "TriggerPauseReset", null, noSafeRisk, "planned heading unsafe");

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));
            Assert.That(coordinator.IsHoldingForRecovery, Is.True);
            Assert.That(coordinator.IsAutomaticRecoveryActive, Is.True);
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(task.RuntimeState.phase, Is.Not.EqualTo(GuidanceRuntimePhase.Ended));
            Assert.That(reset.IsActive, Is.False);

            pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 2.5f), 0f);
            var safeRisk = risk.EvaluateNow();
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", safeRisk);

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));
            Assert.That(coordinator.FrozenRecoveryPlan, Is.Not.Null);
            Assert.That(coordinator.FrozenRecoveryPlan.Safety, Is.EqualTo(HeadingSafety.Safe));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Recovery Hold 超过预算后才进入明确 HardStop。</summary>
    [Test]
    public void GuidanceCoordinator_RecoveryHoldHardStopsOnlyAfterBudgetExhausted()
    {
        var root = new GameObject("StageFourRecoveryHoldBudgetTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out var task, out var risk, out var reset);
            InvokePrivate(coordinator, "UnbindRuntimeSafety");
            var pose = root.GetComponent<UserPoseTracker>();
            pose.SetCalibratedPhysicalPose(new Vector2(0.1f, 2.5f), 0f);
            var noSafeRisk = risk.EvaluateNow();
            InvokePrivate(coordinator, "TriggerPauseReset", null, noSafeRisk, "planned heading unsafe");

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            SetPrivateField(coordinator, "recoveryHoldStartedAt", Time.realtimeSinceStartup - 10f);
            SetPrivateField(coordinator, "recoveryHoldBudgetSeconds", 0.1f);
            InvokePrivate(coordinator, "Update");

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Terminated));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Failed));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Hold 可由风险修订、位移、转向和重试计时分别唤醒。</summary>
    [Test]
    public void GuidanceCoordinator_ReplanHoldHasIndependentWakeSources()
    {
        var root = new GameObject("StageFourReplanHoldWakeTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "MissingFinal", out var coordinator, out _, out var risk, out _);
            var pose = root.GetComponent<UserPoseTracker>();

            coordinator.RequestNextGuidance();
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(1));

            SetPrivateField(coordinator, "holdRiskRevision", risk.CurrentRiskState.risk_revision - 1);
            coordinator.RequestNextGuidance();
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(2), "risk revision");

            InvokePrivate(coordinator, "UnbindRuntimeSafety");
            pose.SetCalibratedPhysicalPose(pose.PhysicalPositionXZ + new Vector2(0.3f, 0f), 0f);
            var movedRisk = risk.EvaluateNow();
            SetPrivateField(coordinator, "holdRiskRevision", movedRisk.risk_revision);
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", movedRisk);
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(3), "physical position");

            pose.SetCalibratedPhysicalPose(pose.PhysicalPositionXZ, 20f);
            var turnedRisk = risk.EvaluateNow();
            SetPrivateField(coordinator, "holdRiskRevision", turnedRisk.risk_revision);
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", turnedRisk);
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(4), "physical heading");

            SetPrivateField(coordinator, "nextReplanAttemptTime", Time.realtimeSinceStartup);
            InvokePrivate(coordinator, "Update");
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(5), "retry timer");
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Reset 完成会退出旧 Hold，并以最新位姿形成一次全新重规划检查。</summary>
    [Test]
    public void GuidanceCoordinator_ResetCompletionWakesExistingReplanHold()
    {
        var root = new GameObject("StageFourResetWakeHoldTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "MissingFinal", out var coordinator, out var task, out var risk, out var reset);
            coordinator.RequestNextGuidance();
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
            var holdTransitions = new List<bool>();
            coordinator.ReplanHoldChanged += info => holdTransitions.Add(info.IsHolding);

            InvokePrivate(coordinator, "TriggerPauseReset", null, risk.CurrentRiskState, "planned heading unsafe");
            reset.ProcessPhysicalForward(reset.SafePhysicalForward);

            CollectionAssert.AreEqual(new[] {false, true}, holdTransitions);
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
            Assert.That(coordinator.ReplanRetryCount, Is.EqualTo(1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证恢复期间丢失可信 tracking/calibration 事实会明确终止。</summary>
    [Test]
    public void GuidanceCoordinator_InvalidRecoveryFactsTerminateExplicitly()
    {
        var root = new GameObject("StageFourInvalidRecoveryFactsTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out _, out var risk, out var reset);
            var recoveryRisk = risk.CurrentRiskState;

            InvokePrivate(coordinator, "TriggerPauseReset", null, recoveryRisk, "planned heading unsafe");
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", (PhysicalRiskState)null);

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Terminated));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Failed));
            Assert.That(coordinator.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void CreateCoordinatorHarness(
        GameObject root,
        string finalTargetId,
        out GuidanceCoordinator coordinator,
        out GuidanceTaskController task,
        out PhysicalRiskEvaluator risk,
        out PauseResetController reset)
    {
        var virtualUser = new GameObject("StageFourVirtualUser");
        virtualUser.transform.SetParent(root.transform, false);
        var pose = root.AddComponent<UserPoseTracker>();
        pose.ConfigureVirtualUserRoot(virtualUser.transform);
        var space = root.AddComponent<PhysicalSpaceManager>();
        space.LoadSpaces();
        Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
        var calibration = root.AddComponent<PhysicalSpaceCalibration>();
        calibration.Configure(space, pose);
        calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
        calibration.UpdateRawTrackingPose(new Vector2(2.5f, 2.5f), 0f);
        Assert.That(calibration.CalibrateCurrentPose(), Is.True);
        risk = root.AddComponent<PhysicalRiskEvaluator>();
        risk.Configure(space, pose, calibration);

        task = root.AddComponent<GuidanceTaskController>();
        task.ConfigureTask(new GuidanceTaskDefinition
        {
            condition = "ours_full",
            startExhibitId = "E1",
            finalTargetId = finalTargetId,
            routeTargetIds = new[] { "E1", finalTargetId },
            maxConsecutiveDetours = 0
        });
        task.StartTask();
        coordinator = root.AddComponent<GuidanceCoordinator>();
        var targets = root.AddComponent<NavigationTargetManager>();
        targets.Configure(pose);
        targets.RebuildIndex();
        var selector = root.AddComponent<SemanticTargetSelector>();
        selector.Configure(targets);
        var validator = root.AddComponent<LocalGuidanceValidator>();
        validator.Configure(targets, risk);
        reset = root.AddComponent<PauseResetController>();
        reset.Configure(null, null, null, pose, risk);
        coordinator.Configure(
            null,
            null,
            task,
            targets,
            selector,
            null,
            risk,
            validator,
            reset,
            null,
            null);
        coordinator.ConfigureRequestClient(new NoOpRequestClient());
        SetPrivateField(coordinator, "resetStabilizationSeconds", 0f);
        root.SetActive(true);
    }

    private static PhysicalRiskState CopyRisk(PhysicalRiskState source, IReadOnlyList<HeadingSample> profile)
    {
        return new PhysicalRiskState(
            riskSnapshotId: Guid.NewGuid().ToString("N"),
            riskRevision: source.risk_revision + 1,
            calibrationId: source.calibration_id,
            sampledAtUtc: source.sampled_at_utc,
            riskPolicyVersion: source.risk_policy_version,
            snapshotSource: source.source,
            physicalSpace: source.physical_space,
            physicalPosition: source.user_physical_position,
            physicalForward: source.user_physical_forward,
            boundaryContext: source.boundary_context,
            obstacleContext: source.obstacle_context,
            clearanceForward: source.clearance_forward,
            clearanceLeft: source.clearance_left,
            clearanceRight: source.clearance_right,
            boundaryRisk: source.boundary_risk,
            resetRisk: source.reset_risk,
            unsafeDirections: source.unsafe_directions,
            safeDirections: source.safe_directions,
            isCritical: source.critical,
            resetRequired: false,
            headingProfile: profile,
            currentHeading: source.current_heading);
    }

    private static HeadingSample Sample(float yawDegrees, float clearanceMeters, HeadingSafety safety)
    {
        return new HeadingSample(yawDegrees, ForwardAt(yawDegrees), clearanceMeters, safety);
    }

    private static Vector2 ForwardAt(float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }

    private static void InvokePrivate(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, methodName);
        method.Invoke(target, arguments);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, fieldName);
        field.SetValue(target, value);
    }

    private sealed class NoOpRequestClient : IGuidanceRequestClient
    {
        public bool RequestInFlight => false;

        public void RequestGuidance(GuidanceRequestContext context)
        {
        }

        public void CancelPendingRequest()
        {
        }
    }

}
