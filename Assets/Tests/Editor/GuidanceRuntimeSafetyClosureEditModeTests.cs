using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Guidance 提交前物理动作闸门与运行中方向失效的恢复分流。
/// </summary>
public sealed class GuidanceRuntimeSafetyClosureEditModeTests
{
    /// <summary>验证最新风险将目标方向标记为 Unsafe 时，提交前闸门拒绝该动作。</summary>
    [Test]
    public void PhysicalActionGate_RejectsLatestUnsafeTargetDirection()
    {
        using (var harness = new CoordinatorHarness())
        {
            var risk = harness.CopyRisk(
                new[] { "forward", "left" },
                new[] { "right" });
            var response = new GuidanceResponse
            {
                selected_target = "E4",
                movement_instruction = "turn_right_slowly"
            };

            var result = InvokePhysicalActionGate(harness.Coordinator, response, risk, out var reason);

            Assert.That(result, Is.False);
            Assert.That(reason, Does.Contain("Unsafe"));
            Assert.That(harness.Task.RuntimeState.CurrentGuidanceTarget, Is.Null);
        }
    }

    /// <summary>验证动作方向与目标物理方向冲突时，提交前闸门拒绝该动作。</summary>
    [Test]
    public void PhysicalActionGate_RejectsMovementTargetDirectionMismatch()
    {
        using (var harness = new CoordinatorHarness())
        {
            var risk = harness.CopyRisk(
                new[] { "forward", "left", "right" },
                Array.Empty<string>());
            var response = new GuidanceResponse
            {
                selected_target = "E4",
                movement_instruction = "turn_left_slowly"
            };

            var result = InvokePhysicalActionGate(harness.Coordinator, response, risk, out var reason);

            Assert.That(result, Is.False);
            Assert.That(reason, Does.Contain("turn_left_slowly"));
            Assert.That(reason, Does.Contain("right"));
        }
    }

    /// <summary>验证计划方向失效但存在安全语义候选时优先执行软重规划。</summary>
    [Test]
    public void UnavailablePlannedDirection_WithSafeCandidate_UsesSoftReroute()
    {
        using (var harness = new CoordinatorHarness())
        {
            var decisions = new List<RuntimeGuidanceSafetyDecision>();
            harness.Coordinator.RuntimeIntervention += info => decisions.Add(info.Decision);
            harness.PrepareExecutingResponse();
            var risk = harness.Risk.CurrentRiskState;
            var input = harness.CreateUnavailableDirectionInput(risk, true);

            var routed = InvokeUnavailableDirectionRoute(
                harness.Coordinator,
                input,
                new GuidanceTarget("E3", NavigationTargetRole.NavigableDetour),
                risk);

            Assert.That(routed, Is.True);
            Assert.That(decisions, Has.Some.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
            Assert.That(decisions, Has.None.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
            Assert.That(harness.Task.RuntimeState.IsNavigating, Is.True);
        }
    }

    /// <summary>验证没有语义候选但存在可信 Safe Heading 时进入 Pause/Reset。</summary>
    [Test]
    public void UnavailablePlannedDirection_WithTrustedRecoveryHeading_UsesPauseReset()
    {
        using (var harness = new CoordinatorHarness())
        {
            var decisions = new List<RuntimeGuidanceSafetyDecision>();
            harness.Coordinator.RuntimeIntervention += info => decisions.Add(info.Decision);
            harness.PrepareExecutingResponse();
            var risk = harness.Risk.CurrentRiskState;
            var input = harness.CreateUnavailableDirectionInput(risk, true);

            var routed = InvokeUnavailableDirectionRoute(harness.Coordinator, input, null, risk);

            Assert.That(routed, Is.True);
            Assert.That(decisions, Has.Some.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
            Assert.That(decisions, Has.None.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));
        }
    }

    /// <summary>验证基础安全身份无效时恢复分流不接管，原 fail-closed 策略仍返回 HardStop。</summary>
    [Test]
    public void UnavailablePlannedDirection_InvalidTracking_RemainsHardStop()
    {
        using (var harness = new CoordinatorHarness())
        {
            harness.PrepareExecutingResponse();
            var risk = harness.Risk.CurrentRiskState;
            var input = harness.CreateUnavailableDirectionInput(risk, false);

            var routed = InvokeUnavailableDirectionRoute(
                harness.Coordinator,
                input,
                new GuidanceTarget("E3", NavigationTargetRole.NavigableDetour),
                risk);
            var decision = new RuntimeGuidanceSafetyPolicy().Evaluate(input);

            Assert.That(routed, Is.False);
            Assert.That(decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
        }
    }

    private static bool InvokePhysicalActionGate(
        GuidanceCoordinator coordinator,
        GuidanceResponse response,
        PhysicalRiskState risk,
        out string reason)
    {
        var arguments = new object[] { response, risk, string.Empty, 0f, string.Empty };
        var result = (bool)InvokePrivate(coordinator, "TryValidateExecutablePhysicalAction", arguments);
        reason = (string)arguments[4];
        return result;
    }

    private static bool InvokeUnavailableDirectionRoute(
        GuidanceCoordinator coordinator,
        RuntimeGuidanceSafetyInput input,
        GuidanceTarget safeCandidate,
        PhysicalRiskState risk)
    {
        return (bool)InvokePrivate(
            coordinator,
            "TryRouteUnavailablePlannedDirection",
            input,
            safeCandidate,
            risk);
    }

    private static object InvokePrivate(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, methodName);
        return method.Invoke(target, arguments);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, fieldName);
        field.SetValue(target, value);
    }

    private sealed class CoordinatorHarness : IDisposable
    {
        private readonly GameObject root;

        public GuidanceCoordinator Coordinator { get; }
        public GuidanceTaskController Task { get; }
        public PhysicalRiskEvaluator Risk { get; }
        public PauseResetController Reset { get; }

        public CoordinatorHarness()
        {
            root = new GameObject("GuidanceRuntimeSafetyClosureHarness");
            root.SetActive(false);
            var virtualUser = new GameObject("VirtualUser");
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

            Risk = root.AddComponent<PhysicalRiskEvaluator>();
            Risk.Configure(space, pose, calibration);
            CreateExhibit("E1", Vector3.zero, "introduction");
            CreateExhibit("E3", Vector3.left * 2f, "navigable_detour");
            CreateExhibit("E4", Vector3.right * 2f, "navigable_detour");
            CreateExhibit("E5", Vector3.forward * 2f, "main_target");

            root.SetActive(true);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, Risk);

            Task = root.AddComponent<GuidanceTaskController>();
            Task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            Task.StartTask();

            Reset = root.AddComponent<PauseResetController>();
            Reset.Configure(null, null, null, pose, Risk);
            Coordinator = root.AddComponent<GuidanceCoordinator>();
            Coordinator.Configure(
                null,
                null,
                Task,
                targets,
                selector,
                null,
                Risk,
                validator,
                Reset,
                null,
                null);
        }

        public PhysicalRiskState CopyRisk(string[] safeDirections, string[] unsafeDirections)
        {
            var source = Risk.CurrentRiskState;
            return new PhysicalRiskState(
                riskSnapshotId: "risk-closure-copy",
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
                clearanceForward: 2f,
                clearanceLeft: 2f,
                clearanceRight: 2f,
                boundaryRisk: "low",
                resetRisk: "none",
                unsafeDirections: unsafeDirections,
                safeDirections: safeDirections);
        }

        public RuntimeGuidanceSafetyInput CreateUnavailableDirectionInput(
            PhysicalRiskState risk,
            bool trackingValid)
        {
            return new RuntimeGuidanceSafetyInput(
                "decision-closure",
                "missing-target",
                "move_forward_slowly",
                risk,
                trackingValid,
                true,
                true,
                string.Empty,
                0f,
                false,
                false,
                ResetLifecycleState.Inactive,
                0,
                true,
                true,
                true);
        }

        public void PrepareExecutingResponse()
        {
            SetPrivateField(Coordinator, "activeResponse", new GuidanceResponse
            {
                selected_target = "missing-target",
                movement_instruction = "move_forward_slowly"
            });
            SetPrivateField(Coordinator, "requestPhase", GuidanceRequestPhase.Executing);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        private void CreateExhibit(string id, Vector3 position, string role)
        {
            var exhibitObject = new GameObject(id);
            exhibitObject.transform.SetParent(root.transform, false);
            exhibitObject.transform.position = position;
            var metadata = exhibitObject.AddComponent<ExhibitMetadata>();
            metadata.Configure(
                id,
                id,
                role == "navigable_detour" ? new[] { "navigable_detour" } : null,
                string.Empty,
                role,
                null,
                exhibitObject.transform,
                exhibitObject.transform);
        }
    }
}
