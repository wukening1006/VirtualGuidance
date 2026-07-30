using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证物理恢复状态机与语义候选解耦的阶段四规则。
/// </summary>
public sealed class GuidanceRecoveryStageFourEditModeTests
{
    /// <summary>验证恢复规划器可以选择多角度 Profile 中的非正交安全朝向。</summary>
    [Test]
    public void RecoveryHeadingPlanner_SelectsNonOrthogonalSafeHeading()
    {
        var planner = new RecoveryHeadingPlanner();
        var plan = planner.Plan(new[]
        {
            Sample(0f, 0.9f, HeadingSafety.Unknown),
            Sample(30f, 1.1f, HeadingSafety.Unknown),
            Sample(60f, 2.6f, HeadingSafety.Safe),
            Sample(90f, 1.4f, HeadingSafety.Safe)
        });

        Assert.That(plan.IsSuccess, Is.True);
        Assert.That(plan.RelativeYawDegrees, Is.EqualTo(60f).Within(0.01f));
        Assert.That(Vector2.Distance(plan.SafePhysicalForward, ForwardAt(60f)), Is.LessThan(0.001f));
    }

    /// <summary>验证不存在可接受朝向时返回明确不可恢复结果。</summary>
    [Test]
    public void RecoveryHeadingPlanner_NoAcceptableHeadingReturnsExplicitFailure()
    {
        var planner = new RecoveryHeadingPlanner();
        var plan = planner.Plan(new[]
        {
            Sample(0f, 0.2f, HeadingSafety.Critical),
            Sample(30f, 0.5f, HeadingSafety.Unsafe),
            Sample(-60f, 0.3f, HeadingSafety.Critical)
        });

        Assert.That(plan.IsSuccess, Is.False);
        Assert.That(plan.Failure, Is.EqualTo(RecoveryHeadingFailure.NoAcceptableHeading));
    }

    /// <summary>验证冻结朝向在普通风险样本变化时保持不变，变为 Critical 后才重新选择。</summary>
    [Test]
    public void GuidanceCoordinator_FrozenHeadingReselectsOnlyAfterCritical()
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

            var frozenCritical = CopyRisk(risk.CurrentRiskState, new[]
            {
                new HeadingSample(60f, frozenForward, 0.2f, HeadingSafety.Critical),
                Sample(-60f, 2.8f, HeadingSafety.Safe)
            });
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", frozenCritical);

            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
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
            var recoveryRisk = CopyRisk(risk.CurrentRiskState, new[]
            {
                Sample(0f, 2.4f, HeadingSafety.Safe),
                Sample(60f, 1.8f, HeadingSafety.Safe)
            });

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

    /// <summary>验证恢复期间丢失可信 tracking/calibration 事实会明确终止。</summary>
    [Test]
    public void GuidanceCoordinator_InvalidRecoveryFactsTerminateExplicitly()
    {
        var root = new GameObject("StageFourInvalidRecoveryFactsTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, "E5", out var coordinator, out _, out var risk, out var reset);
            var recoveryRisk = CopyRisk(risk.CurrentRiskState, new[]
            {
                Sample(30f, 2f, HeadingSafety.Safe)
            });

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
        root.SetActive(true);
    }

    private static PhysicalRiskState CopyRisk(PhysicalRiskState source, HeadingSample[] profile)
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
            headingProfile: profile);
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
