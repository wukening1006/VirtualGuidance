using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// 验证 Step 1 从物理风险恢复到最终目标完成的基础闭环。
/// </summary>
public sealed class Step1ClosedLoopEditModeTests
{
    /// <summary>
    /// 验证 HMD 高度差不会阻塞同一 XZ 位置的到达。
    /// </summary>
    [Test]
    public void ArrivalDetector_HmdHeightDoesNotBlockArrival()
    {
        using (var harness = new ArrivalHarness("Step1ArrivalHeightTarget", new Vector3(2f, 0f, 3f)))
        {
            harness.User.position = new Vector3(2f, 1.7f, 3f);

            Assert.That(harness.DetectArrival(), Is.True);
        }
    }

    /// <summary>
    /// 验证用户水平距离超过到达半径时不会触发到达。
    /// </summary>
    [Test]
    public void ArrivalDetector_XzOutsideRadiusDoesNotArrive()
    {
        using (var harness = new ArrivalHarness("Step1ArrivalOutsideTarget", Vector3.zero))
        {
            harness.User.position = new Vector3(0.76f, 0f, 0f);

            Assert.That(harness.DetectArrival(), Is.False);
        }
    }

    /// <summary>
    /// 验证有效 E5 预留到达后任务进入 Completed。
    /// </summary>
    [Test]
    public void ArrivalDetector_FinalArrivalCompletesTask()
    {
        using (var harness = new TaskArrivalHarness())
        {
            Assert.That(harness.Task.TryReserveGuidanceTarget(
                new GuidanceTarget(TaskArrivalHarness.FinalTargetId, NavigationTargetRole.FinalTarget),
                "decision-final"), Is.True);
            var reservation = harness.Task.RuntimeState.ActiveReservation;
            Assert.That(harness.Task.CommitReservation(reservation), Is.True);

            harness.User.position = new Vector3(2f, 1.7f, 0f);
            harness.Detector.WatchReservation(reservation);
            harness.InvokeDetectorUpdate();

            Assert.That(harness.Task.RuntimeState.currentExhibitId, Is.EqualTo(TaskArrivalHarness.FinalTargetId));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Completed));
        }
    }

    /// <summary>
    /// 验证真实风险计算器产生的前方危险、无语义候选和恢复航向进入 PauseReset。
    /// </summary>
    [Test]
    public void RuntimeSafety_UnsafeWithoutSemanticCandidateButWithRecoveryHeadingPausesForReset()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(2.5f, 4.5f), 0f);
            var hasRecoveryHeading = harness.RecoveryPlanner.Search(risk, harness.Evaluator).Plan.IsSuccess;
            var input = harness.CreateInput(risk, true, false, hasRecoveryHeading);

            Assert.That(risk.current_heading.Safety,
                Is.EqualTo(HeadingSafety.Unsafe).Or.EqualTo(HeadingSafety.Critical));
            Assert.That(risk.reset_required, Is.False);
            Assert.That(hasRecoveryHeading, Is.True);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
        }
    }

    /// <summary>
    /// 验证真实风险计算器在边界外且没有恢复航向时进入 HardStop。
    /// </summary>
    [Test]
    public void RuntimeSafety_NoRecoveryHeadingHardStops()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(5.2f, 5.2f), 0f);
            var hasRecoveryHeading = harness.RecoveryPlanner.Search(risk, harness.Evaluator).Plan.IsSuccess;
            var input = harness.CreateInput(risk, true, false, hasRecoveryHeading);

            Assert.That(risk.reset_required, Is.True);
            Assert.That(hasRecoveryHeading, Is.False);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
        }
    }

    /// <summary>
    /// 验证用户仍在可信边界内但暂时没有 Safe 航向时进入恢复等待，而不是立即 HardStop。
    /// </summary>
    [Test]
    public void RuntimeSafety_NoRecoveryHeadingInsideBoundaryPausesForRecoveryHold()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(0.2f, 2.5f), 0f);
            var hasRecoveryHeading = harness.RecoveryPlanner.Search(risk, harness.Evaluator).Plan.IsSuccess;
            var input = harness.CreateInput(risk, true, false, hasRecoveryHeading);

            Assert.That(hasRecoveryHeading, Is.False);
            Assert.That(risk.boundary_context, Has.Count.GreaterThanOrEqualTo(3));
            Assert.That(risk.user_physical_position.x, Is.GreaterThan(0f).And.LessThan(5f));
            Assert.That(risk.user_physical_position.y, Is.GreaterThan(0f).And.LessThan(5f));
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
        }
    }

    /// <summary>
    /// 验证 ours_full + wide_room 下 E1 到 E5 可跨越一次真实物理风险 Reset 完成闭环。
    /// </summary>
    [UnityTest]
    public IEnumerator ClosedLoop_E1ToE5CompletesAfterRecoveryHoldResetAndFreshReplan()
    {
        using (var harness = new ClosedLoopHarness())
        {
            var recoveryStates = new List<GuidanceRecoveryState>();
            harness.Coordinator.RecoveryStateChanged += recoveryStates.Add;

            harness.Coordinator.RequestNextGuidance();
            var firstRequest = harness.Backend.BuildLatestRequest(harness.Coordinator, harness.Risk.CurrentRiskState);
            var firstResponse = harness.Backend.CreateApprovedResponse(firstRequest, "E5");
            harness.Backend.Respond(harness.Coordinator, firstResponse);

            Assert.That(harness.Coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));
            Assert.That(harness.Task.RuntimeState.CurrentGuidanceTarget.TargetId, Is.EqualTo("E5"));
            Assert.That(harness.Targets.GetTargetDirection("E5"), Is.EqualTo("forward"));

            // 边界内但起点侵入安全余量时，生产风险事件应进入受预算控制的 Recovery Hold。
            harness.Pose.SetCalibratedPhysicalPose(new Vector2(0.2f, 2.5f), 0f);
            var firstUnsafeRisk = harness.Risk.EvaluateNow();
            var secondUnsafeRisk = harness.Risk.EvaluateNow();

            Assert.That(firstUnsafeRisk.current_heading.Safety,
                Is.EqualTo(HeadingSafety.Unsafe).Or.EqualTo(HeadingSafety.Critical));
            Assert.That(secondUnsafeRisk.reset_required, Is.True);
            Assert.That(harness.Coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.HoldingForSafeHeading));
            Assert.That(harness.Coordinator.IsHoldingForRecovery, Is.True);
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.Inactive));

            // 位姿事实恢复后由真实 RiskStateChanged 唤醒 Hold，并选择一条精确验证为 Safe 的航向。
            harness.Pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 4.3f), 0f);
            var safeRecoveryRisk = harness.Risk.EvaluateNow();
            Assert.That(new RecoveryHeadingPlanner().Search(safeRecoveryRisk, harness.Risk).Plan.IsSuccess, Is.True);
            Assert.That(harness.Coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));

            // Reset 转向期间继续推进生产的 0.2 秒风险刷新，冻结航向不得被 Safe/Unknown 抖动打断。
            var turningRiskEventCount = harness.RiskEventCount;
            var turningRefreshDeadline = Time.realtimeSinceStartup + 0.25f;
            while (Time.realtimeSinceStartup < turningRefreshDeadline)
            {
                harness.TickRuntime();
                yield return null;
            }

            Assert.That(harness.RiskEventCount, Is.GreaterThan(turningRiskEventCount));
            Assert.That(harness.Coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.WaitingForUserTurn));

            var safeForward = harness.Reset.SafePhysicalForward;
            var safeYaw = Mathf.Atan2(safeForward.x, safeForward.y) * Mathf.Rad2Deg;
            harness.Pose.SetCalibratedPhysicalPose(harness.Pose.PhysicalPositionXZ, safeYaw);
            var stabilizationStartedAt = -1f;
            var recoveryDeadline = Time.realtimeSinceStartup + 2f;
            while (harness.Backend.Contexts.Count < 2 && Time.realtimeSinceStartup < recoveryDeadline)
            {
                harness.TickRuntime();
                if (stabilizationStartedAt < 0f &&
                    harness.Coordinator.RecoveryState == GuidanceRecoveryState.Stabilizing)
                {
                    stabilizationStartedAt = Time.realtimeSinceStartup;
                }
                yield return null;
            }

            Assert.That(stabilizationStartedAt, Is.GreaterThanOrEqualTo(0f));
            Assert.That(recoveryStates, Does.Contain(GuidanceRecoveryState.Stabilizing));
            Assert.That(recoveryStates, Does.Contain(GuidanceRecoveryState.Revalidating));
            Assert.That(recoveryStates, Does.Contain(GuidanceRecoveryState.Replanning));
            Assert.That(Time.realtimeSinceStartup - stabilizationStartedAt, Is.GreaterThanOrEqualTo(0.1f));
            Assert.That(harness.Reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(harness.Backend.Contexts, Has.Count.EqualTo(2));

            // Fresh Replan 在途期间继续刷新风险；Safe 快照不得每 0.2 秒取消并重建请求。
            var secondRequest = harness.Backend.BuildLatestRequest(
                harness.Coordinator,
                harness.Risk.CurrentRiskState);
            Assert.That(secondRequest.request_id, Is.Not.EqualTo(firstRequest.request_id));
            Assert.That(secondRequest.decision_id, Is.Not.EqualTo(firstRequest.decision_id));
            Assert.That(secondRequest.risk_snapshot_id, Is.Not.EqualTo(firstRequest.risk_snapshot_id));
            var cancelCountAfterFreshReplan = harness.Backend.CancelCount;
            var pendingRefreshDeadline = Time.realtimeSinceStartup + 0.25f;
            while (Time.realtimeSinceStartup < pendingRefreshDeadline)
            {
                harness.TickRuntime();
                yield return null;
            }

            Assert.That(harness.Coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            Assert.That(harness.Backend.Contexts, Has.Count.EqualTo(2));
            Assert.That(harness.Backend.CancelCount, Is.EqualTo(cancelCountAfterFreshReplan));

            harness.Coordinator.HandleGuidanceResponse(firstResponse);
            Assert.That(harness.Coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            Assert.That(harness.Task.RuntimeState.CurrentGuidanceTarget, Is.Null);

            var secondResponse = harness.Backend.CreateApprovedResponse(secondRequest, "E5");
            harness.Backend.Respond(harness.Coordinator, secondResponse);
            Assert.That(harness.Coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));

            harness.User.position = harness.FinalTarget.position + Vector3.up * 1.7f;
            harness.InvokeArrivalUpdate();

            Assert.That(harness.Task.RuntimeState.currentExhibitId, Is.EqualTo("E5"));
            Assert.That(harness.Task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Completed));
        }
    }

    private sealed class ArrivalHarness : IDisposable
    {
        private readonly GameObject root;
        private readonly TargetReservation reservation;
        private bool arrived;

        public Transform User { get; }
        public ArrivalDetector Detector { get; }

        public ArrivalHarness(string targetId, Vector3 targetPosition)
        {
            root = new GameObject("Step1ArrivalHarness");
            User = CreateChild(root.transform, "VirtualUser", Vector3.zero);
            CreateExhibit(root.transform, targetId, targetPosition);

            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(User);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Detector = root.AddComponent<ArrivalDetector>();
            Detector.Configure(pose, targets);
            Detector.TargetArrived += _ => arrived = true;
            reservation = new TargetReservation(
                targetId, NavigationTargetRole.FinalTarget, "arrival-test", 1, 1);
        }

        public bool DetectArrival()
        {
            arrived = false;
            Detector.WatchReservation(reservation);
            InvokeUpdate(Detector);
            return arrived;
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private sealed class TaskArrivalHarness : IDisposable
    {
        public const string FinalTargetId = "Step1FinalArrivalTarget";

        private readonly GameObject root;

        public Transform User { get; }
        public GuidanceTaskController Task { get; }
        public ArrivalDetector Detector { get; }

        public TaskArrivalHarness()
        {
            root = new GameObject("Step1TaskArrivalHarness");
            User = CreateChild(root.transform, "VirtualUser", Vector3.zero);
            CreateExhibit(root.transform, FinalTargetId, new Vector3(2f, 0f, 0f));

            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(User);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Detector = root.AddComponent<ArrivalDetector>();
            Detector.Configure(pose, targets);

            Task = root.AddComponent<GuidanceTaskController>();
            Task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = FinalTargetId,
                routeTargetIds = new[] {"E1", FinalTargetId},
                maxConsecutiveDetours = 2
            });
            Task.StartTask();

            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, Task, targets, null, Detector, null, null, null, null, null);
        }

        public void InvokeDetectorUpdate()
        {
            InvokeUpdate(Detector);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private sealed class RiskHarness : IDisposable
    {
        private readonly GameObject root;
        private readonly UserPoseTracker pose;
        private readonly PhysicalRiskEvaluator evaluator;

        public RuntimeGuidanceSafetyPolicy Policy { get; } = new RuntimeGuidanceSafetyPolicy();
        public RecoveryHeadingPlanner RecoveryPlanner { get; } = new RecoveryHeadingPlanner();
        /// <summary>提供任意角度精确物理评估的生产计算器。</summary>
        public PhysicalRiskEvaluator Evaluator => evaluator;

        public RiskHarness()
        {
            root = new GameObject("Step1RiskHarness");
            pose = root.AddComponent<UserPoseTracker>();
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);

            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            evaluator = root.AddComponent<PhysicalRiskEvaluator>();
            evaluator.Configure(space, pose, calibration);
        }

        public PhysicalRiskState Evaluate(Vector2 position, float yawDegrees)
        {
            pose.SetCalibratedPhysicalPose(position, yawDegrees);
            var risk = evaluator.EvaluateNow();
            Assert.That(risk, Is.Not.Null);
            Assert.That(evaluator.IsRiskIdentityCurrent(risk), Is.True);
            return risk;
        }

        public RuntimeGuidanceSafetyInput CreateInput(
            PhysicalRiskState risk,
            bool requiresSafetyIntervention,
            bool hasSafeSemanticCandidate,
            bool hasRecoveryHeading)
        {
            return new RuntimeGuidanceSafetyInput(
                "step1-decision",
                "E5",
                "move_forward_slowly",
                risk,
                evaluator.IsTrackingValid,
                evaluator.IsCalibrationValid,
                evaluator.IsRiskIdentityCurrent(risk),
                "forward",
                risk.current_heading.ClearanceMeters,
                true,
                risk.current_heading.Safety == HeadingSafety.Critical,
                ResetLifecycleState.Inactive,
                requiresSafetyIntervention ? 2 : 0,
                requiresSafetyIntervention,
                hasSafeSemanticCandidate,
                hasRecoveryHeading);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private sealed class ClosedLoopHarness : IDisposable
    {
        private readonly GameObject root;

        public Transform User { get; }
        public Transform FinalTarget { get; private set; }
        public UserPoseTracker Pose { get; }
        public PhysicalRiskEvaluator Risk { get; }
        public NavigationTargetManager Targets { get; }
        public GuidanceTaskController Task { get; }
        public ArrivalDetector Arrival { get; }
        public PauseResetController Reset { get; }
        public GuidanceCoordinator Coordinator { get; }
        public DeterministicGuidanceBackend Backend { get; } = new DeterministicGuidanceBackend();
        public int RiskEventCount { get; private set; }

        public ClosedLoopHarness()
        {
            root = new GameObject("Step1ClosedLoopHarness");
            root.SetActive(false);
            User = CreateChild(root.transform, "VirtualUser", Vector3.zero);
            CreateExhibit(root.transform, "E1", Vector3.zero);
            FinalTarget = CreateExhibit(root.transform, "E5", new Vector3(0f, 0f, 5f)).transform;

            Pose = root.AddComponent<UserPoseTracker>();
            Pose.ConfigureVirtualUserRoot(User);
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, Pose);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            Pose.SetCalibratedPhysicalPose(new Vector2(2.5f, 2.5f), 0f);

            Risk = root.AddComponent<PhysicalRiskEvaluator>();
            Risk.Configure(space, Pose, calibration);
            Risk.RiskStateChanged += _ => RiskEventCount++;
            Targets = root.AddComponent<NavigationTargetManager>();
            Targets.Configure(Pose);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(Targets);
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(Targets, Risk);
            Arrival = root.AddComponent<ArrivalDetector>();
            Arrival.Configure(Pose, Targets);
            Reset = root.AddComponent<PauseResetController>();
            Reset.Configure(null, null, null, Pose, Risk);

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
                Targets,
                selector,
                Arrival,
                Risk,
                validator,
                Reset,
                null,
                null);
            Coordinator.ConfigureRequestClient(Backend);
            SetPrivateField(Coordinator, "resetStabilizationSeconds", 0.15f);

            root.SetActive(true);
            // EditMode 不保证激活对象时执行与 PlayerLoop 完全一致的 OnEnable 时序；
            // 显式调用生产绑定入口，后续风险仍只通过真实 RiskStateChanged 事件投递。
            InvokePrivate(Coordinator, "BindRuntimeSafety");
            Targets.RebuildIndex();
            Assert.That(Targets.TryGetTarget("E1", out var start), Is.True);
            Assert.That(Targets.TryGetTarget("E5", out var final), Is.True);
            var startAnchor = start.ViewPoint != null ? start.ViewPoint : start.transform;
            FinalTarget = final.ViewPoint != null ? final.ViewPoint : final.transform;
            User.position = startAnchor.position;
            User.forward = Vector3.ProjectOnPlane(
                FinalTarget.position - startAnchor.position,
                Vector3.up).normalized;
            Risk.EvaluateNow();
        }

        public void TickRuntime()
        {
            InvokePrivate(Risk, "Update");
            InvokePrivate(Reset, "Update");
            InvokePrivate(Coordinator, "Update");
        }

        public void InvokeArrivalUpdate()
        {
            InvokeUpdate(Arrival);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private sealed class DeterministicGuidanceBackend : IGuidanceRequestClient
    {
        private bool requestInFlight;

        public List<GuidanceRequestContext> Contexts { get; } = new List<GuidanceRequestContext>();
        public bool RequestInFlight => requestInFlight;
        public int CancelCount { get; private set; }

        public void RequestGuidance(GuidanceRequestContext context)
        {
            Contexts.Add(context);
            requestInFlight = true;
        }

        public void CancelPendingRequest()
        {
            CancelCount++;
            requestInFlight = false;
        }

        public GuidanceRequest BuildLatestRequest(
            GuidanceCoordinator coordinator,
            PhysicalRiskState riskState)
        {
            Assert.That(Contexts, Is.Not.Empty);
            var context = Contexts[Contexts.Count - 1];
            var riskSnapshot = GuidanceRiskSnapshot.From(riskState);
            var request = new GuidanceRequest
            {
                session_id = context.session_id,
                calibration_id = context.calibration_id,
                request_id = context.request_id,
                decision_id = context.decision_id,
                candidate_set_id = context.candidate_set_id,
                state_version = context.state_version,
                risk_snapshot_id = context.risk_snapshot_id,
                condition = context.condition,
                task_id = context.task_id,
                physical_space_id = riskState.physical_space,
                current_exhibit = context.current_exhibit,
                risk_snapshot = riskSnapshot,
                final_target = context.final_target,
                preferred_task_target = context.preferred_task_target,
                candidate_ids = context.candidate_ids,
                model_candidate_ids = context.model_candidate_ids,
                allowed_candidate_ids = context.allowed_candidate_ids,
                semantic_candidates = context.semantic_candidates
            };
            InvokePrivate(coordinator, "HandleRequestBuilt", request);
            return request;
        }

        public GuidanceResponse CreateApprovedResponse(GuidanceRequest request, string targetId)
        {
            var targetRole = "final_target";
            if (request.semantic_candidates != null)
            {
                foreach (var candidate in request.semantic_candidates)
                {
                    if (candidate != null && string.Equals(candidate.id, targetId, StringComparison.Ordinal))
                    {
                        targetRole = candidate.target_role;
                        break;
                    }
                }
            }

            return new GuidanceResponse
            {
                session_id = request.session_id,
                calibration_id = request.calibration_id,
                request_id = request.request_id,
                decision_id = request.decision_id,
                candidate_set_id = request.candidate_set_id,
                state_version = request.state_version,
                risk_snapshot_id = request.risk_snapshot_id,
                risk_revision = request.risk_snapshot.risk_revision,
                target_role = targetRole,
                selected_target = targetId,
                movement_instruction = "move_forward_slowly",
                verifier = new VerifierResult {approved = true, reason = "deterministic"}
            };
        }

        public void Respond(GuidanceCoordinator coordinator, GuidanceResponse response)
        {
            requestInFlight = false;
            coordinator.HandleGuidanceResponse(response);
        }
    }

    private static Transform CreateChild(Transform parent, string name, Vector3 position)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.position = position;
        return child;
    }

    private static ExhibitMetadata CreateExhibit(Transform parent, string id, Vector3 position)
    {
        var exhibitObject = new GameObject(id);
        exhibitObject.transform.SetParent(parent, false);
        exhibitObject.transform.position = position;
        var metadata = exhibitObject.AddComponent<ExhibitMetadata>();
        metadata.Configure(
            id, id, null, string.Empty, "main_target", null,
            exhibitObject.transform, exhibitObject.transform);
        return metadata;
    }

    private static void InvokeUpdate(ArrivalDetector detector)
    {
        var update = typeof(ArrivalDetector).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(update, Is.Not.Null);
        update.Invoke(detector, null);
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
}
