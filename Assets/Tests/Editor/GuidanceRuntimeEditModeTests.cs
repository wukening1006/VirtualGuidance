using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class GuidanceRuntimeEditModeTests
{
    [Test]
    public void ExperimentDashboard_AwakeDoesNotCreateOrStartSession()
    {
        var go = new GameObject("ExperimentDashboardLifecycleTest");
        try
        {
            var dashboard = go.AddComponent<ExperimentDashboard>();

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Preparing));
            Assert.That(dashboard.CurrentSpec, Is.Null);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ExperimentDashboard_StartSessionRejectsActiveStateReentry()
    {
        var go = new GameObject("ExperimentDashboardReentryTest");
        go.SetActive(false);
        try
        {
            var dashboard = go.AddComponent<ExperimentDashboard>();
            var originalSpec = ExperimentConditionMatrix.Build(5, 1, 1, "P-active");
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);
            SetPrivateField(dashboard, "currentSpec", originalSpec);

            dashboard.StartSession();

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Running));
            Assert.That(dashboard.CurrentSpec, Is.SameAs(originalSpec));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ExperimentDashboard_TaskResetStateEntersSafetyHold()
    {
        var go = new GameObject("ExperimentDashboardTaskResetTest");
        go.SetActive(false);
        try
        {
            var dashboard = go.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);
            var state = new GuidanceRuntimeState();
            state.Begin("T2_main_target", "E1", "E3", "E5");
            state.PauseForReset();

            InvokePrivate(dashboard, "HandleTaskStateChanged", state);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ExperimentDashboard_RuntimeInterventionEntersRecoverableSafetyHold()
    {
        var root = new GameObject("ExperimentDashboardUnsafeGuidanceTest");
        root.SetActive(false);
        try
        {
            var guidanceRoot = new GameObject("GuidancePanel");
            guidanceRoot.transform.SetParent(root.transform, false);
            var safetyRoot = new GameObject("SafetyPanel");
            safetyRoot.transform.SetParent(root.transform, false);
            var participantUi = root.AddComponent<ParticipantUiController>();
            var safetyText = CreateText(safetyRoot.transform, "SafetyText");
            participantUi.Configure(
                guidanceRoot.AddComponent<CanvasGroup>(),
                safetyRoot.AddComponent<CanvasGroup>(),
                null,
                null,
                null,
                safetyText);
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "participantUiController", participantUi);
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

            InvokePrivate(
                dashboard,
                "HandleRuntimeIntervention",
                new RuntimeInterventionInfo(
                    new GuidanceResponse
                    {
                        request_id = "request-unsafe",
                        decision_id = "decision-unsafe",
                        state_version = 1,
                        movement_instruction = "move_forward_slowly"
                    },
                    "movement_instruction uses unsafe forward"));
            var taskState = new GuidanceRuntimeState();
            taskState.Begin("T2_main_target", "E1", "E3", "E5");
            taskState.PauseForReset();
            InvokePrivate(dashboard, "HandleTaskStateChanged", taskState);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(safetyText.text, Is.EqualTo("请停止移动，等待安全引导"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_CompletionSafetyHoldPreservesCountdownAndRequiresSafeRisk()
    {
        var root = new GameObject("ExperimentDashboardCompletionSafetyTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity);
            tracking.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            task.HandleArrived("E5");

            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);
            SetPrivateField(dashboard, "stateBeforeSafetyHold", ExperimentRunState.Completing);
            SetPrivateField(dashboard, "completionSecondsRemaining", 6f);
            SetPrivateField(dashboard, "physicalSpaceManager", space);
            SetPrivateField(dashboard, "physicalSpaceCalibration", calibration);
            SetPrivateField(dashboard, "xrTrackingAdapter", tracking);

            InvokePrivate(dashboard, "ResumeSessionWithRisk", new PhysicalRiskState(
                riskSnapshotId: "risk-critical",
                calibrationId: calibration.CalibrationId,
                physicalSpace: space.CurrentSpaceId,
                boundaryRisk: "critical",
                resetRisk: "critical",
                safeDirections: new string[0],
                resetRequired: true));

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(dashboard.CompletionSecondsRemaining, Is.EqualTo(6f));

            InvokePrivate(dashboard, "ResumeSessionWithRisk", new PhysicalRiskState(
                riskSnapshotId: "risk-safe",
                calibrationId: calibration.CalibrationId,
                physicalSpace: space.CurrentSpaceId,
                boundaryRisk: "low",
                resetRisk: "low",
                safeDirections: new[] { "forward" }));

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Completing));
            Assert.That(dashboard.CompletionSecondsRemaining, Is.EqualTo(6f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_CompletionCountdownDoesNotTickOutsideCompleting()
    {
        var root = new GameObject("ExperimentDashboardCountdownFreezeTest");
        root.SetActive(false);
        try
        {
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);
            SetPrivateField(dashboard, "completionSecondsRemaining", 4f);

            var routine = (IEnumerator)InvokePrivateWithResult(dashboard, "CompletionCountdownRoutine");
            Assert.That(routine.MoveNext(), Is.False);
            Assert.That(dashboard.CompletionSecondsRemaining, Is.EqualTo(4f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_ResetCompletionPreservesProgressAndReplans()
    {
        var root = new GameObject("ExperimentDashboardResetRecoveryTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                taskId = "T2_main_target",
                participantId = "P-reset",
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            task.StartTask();
            task.HandleArrived("E3");
            Assert.That(task.TrySetGuidanceTarget(new GuidanceTarget("E5", NavigationTargetRole.FinalTarget)), Is.True);
            task.PauseForReset();
            var versionDuringReset = task.RuntimeState.stateVersion;

            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var replanningCount = 0;
            coordinator.Replanning += _ => replanningCount++;

            var pose = root.AddComponent<UserPoseTracker>();
            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, null, null, pose);
            reset.PauseForReset("critical risk", new PhysicalRiskState(
                clearanceForward: 1.5f,
                clearanceLeft: 0.4f,
                clearanceRight: 0.3f,
                resetRequired: true));
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            Assert.That(risk.CurrentRiskState.boundary_risk, Is.EqualTo("low"));
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "guidanceCoordinator", coordinator);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "riskEvaluator", risk);
            SetPrivateField(dashboard, "physicalSpaceManager", space);
            SetPrivateField(dashboard, "physicalSpaceCalibration", calibration);
            SetPrivateField(dashboard, "xrTrackingAdapter", tracking);
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);
            InvokePrivate(dashboard, "Subscribe");

            reset.ProcessPhysicalForward(Vector2.up);

            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Running));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E5"));
            Assert.That(task.RuntimeState.finalTargetId, Is.EqualTo("E5"));
            Assert.That(task.RuntimeState.stateVersion, Is.GreaterThan(versionDuringReset));
            Assert.That(replanningCount, Is.EqualTo(1));
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(requests.Contexts[0].current_exhibit, Is.EqualTo("E3"));
            Assert.That(requests.Contexts[0].preferred_task_target, Is.EqualTo("E5"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_ResetRecoveryPreservesResearcherPause()
    {
        var root = new GameObject("ExperimentDashboardResearcherPauseResetTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);

            var task = CreateRouteTask(root);
            task.PauseForResearcher();
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedByResearcher));
            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, null, null, pose, risk);
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "riskEvaluator", risk);
            SetPrivateField(dashboard, "physicalSpaceManager", space);
            SetPrivateField(dashboard, "physicalSpaceCalibration", calibration);
            SetPrivateField(dashboard, "xrTrackingAdapter", tracking);
            SetPrivateField(dashboard, "runState", ExperimentRunState.Paused);
            InvokePrivate(dashboard, "Subscribe");

            InvokePrivate(dashboard, "EnterSafetyHold", "critical risk", true);
            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedByResearcher));
            Assert.That(reset.IsActive, Is.True);

            reset.ProcessPhysicalForward(reset.SafePhysicalForward);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Paused));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedByResearcher));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(reset.IsActive, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_EndDuringResetLeavesNoActiveReset()
    {
        var root = new GameObject("ExperimentDashboardEndDuringResetTest");
        root.SetActive(false);
        try
        {
            var task = CreateRouteTask(root);
            task.PauseForReset();
            var reset = root.AddComponent<PauseResetController>();
            reset.PauseForReset("critical risk");
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);

            dashboard.EndSession("test_end_during_reset");

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Ended));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));
            Assert.That(reset.IsActive, Is.False);
            Assert.That(reset.IsPaused, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_TrackingLossPreemptsGainAndReset()
    {
        var root = new GameObject("ExperimentDashboardTrackingPreemptionTest");
        root.SetActive(false);
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, null, gain);
            reset.PauseForReset("critical risk");
            gain.BeginPendingTurn(1, 30f, "decision-tracking-loss");
            Assert.That(gain.IsActive, Is.True);
            Assert.That(reset.IsActive, Is.True);
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "rotationGainController", gain);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);

            InvokePrivate(dashboard, "HandleTrackingStateChanged", false);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("tracking_invalid"));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));
            Assert.That(reset.IsActive, Is.False);
            Assert.That(reset.IsPaused, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_InterruptedResetRestartsAfterSafeTrackingRecovery()
    {
        var root = new GameObject("ExperimentDashboardInterruptedResetRecoveryTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            tracking.ApplyTrackedPose(Vector3.zero, Quaternion.identity);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var task = CreateRouteTask(root);
            task.PauseForReset();
            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, null, null, pose, risk);
            reset.PauseForReset("critical risk");
            reset.CancelReset("tracking lost");
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Cancelled));
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "riskEvaluator", risk);
            SetPrivateField(dashboard, "physicalSpaceManager", space);
            SetPrivateField(dashboard, "physicalSpaceCalibration", calibration);
            SetPrivateField(dashboard, "xrTrackingAdapter", tracking);
            SetPrivateField(dashboard, "runState", ExperimentRunState.SafetyHold);

            InvokePrivate(dashboard, "ResumeSessionWithRisk", risk.CurrentRiskState);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));
            Assert.That(reset.IsActive, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceTaskController_AdvancesConfiguredRouteToCompleted()
    {
        var go = new GameObject("GuidanceTaskControllerTest");
        try
        {
            var controller = go.AddComponent<GuidanceTaskController>();
            controller.ConfigureTask(new GuidanceTaskDefinition
            {
                taskId = "T2_main_target",
                participantId = "P-test",
                condition = "ours_full",
                userTask = "Reach the final exhibit.",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Idle));
            controller.StartTask();

            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(controller.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(controller.RuntimeState.nextTargetId, Is.EqualTo("E3"));

            controller.HandleArrived("E2");
            Assert.That(controller.RuntimeState.nextTargetId, Is.EqualTo("E3"));

            controller.HandleArrived("E3");
            Assert.That(controller.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(controller.RuntimeState.nextTargetId, Is.EqualTo("E5"));
            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));

            controller.HandleArrived("E5");
            Assert.That(controller.RuntimeState.currentExhibitId, Is.EqualTo("E5"));
            Assert.That(controller.RuntimeState.nextTargetId, Is.Empty);
            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Completed));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void LocalGuidanceValidator_RejectsStaleOrUnsafeGuidance()
    {
        var go = new GameObject("LocalGuidanceValidatorTest");
        try
        {
            var virtualUser = new GameObject("ValidatorVirtualUser");
            virtualUser.transform.SetParent(go.transform, false);
            var pose = go.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            CreateExhibit(go.transform, "E3", Vector3.forward * 2f, "route_subgoal", null);
            CreateExhibit(go.transform, "E5", Vector3.left * 2f, "main_target", null);
            var targets = go.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var riskEvaluator = go.AddComponent<PhysicalRiskEvaluator>();
            var validator = go.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, riskEvaluator);
            var runtimeState = new GuidanceRuntimeState();
            runtimeState.Begin("T2_main_target", "E1", "E3", "E5");
            var originalVersion = runtimeState.stateVersion;

            var riskState = new PhysicalRiskState(
                riskRevision: 2,
                physicalSpace: "narrow_room",
                unsafeDirections: new[] { "forward" },
                safeDirections: new[] { "left", "right" });
            var request = new GuidanceRequest
            {
                request_id = "request-validator",
                decision_id = "decision-validator",
                state_version = originalVersion,
                physical_space_id = "narrow_room",
                current_exhibit = "E1"
            };
            ConfigureSingleCandidate(request, "E3", "navigable_detour");
            var snapshot = GuidanceRequestSnapshot.Capture(request, riskState, runtimeState);

            var unsafeResponse = new GuidanceResponse
            {
                selected_target = "E3",
                movement_instruction = "move_forward_slowly"
            };
            Assert.That(validator.Validate(unsafeResponse, snapshot, runtimeState, riskState, out var unsafeReason), Is.False);
            Assert.That(unsafeReason, Does.Contain("unsafe forward"));

            var safeAtRequest = new PhysicalRiskState(
                riskRevision: 1,
                physicalSpace: "narrow_room",
                unsafeDirections: new string[0],
                safeDirections: new[] { "forward", "left", "right" });
            var safeSnapshot = GuidanceRequestSnapshot.Capture(request, safeAtRequest, runtimeState);
            Assert.That(
                validator.Validate(unsafeResponse, safeSnapshot, runtimeState, riskState, out var worsenedReason),
                Is.False);
            Assert.That(worsenedReason, Does.Contain("changed after the request"));

            runtimeState.AdvanceTo("E3", "E5");
            Assert.That(runtimeState.stateVersion, Is.GreaterThan(originalVersion));

            var safeResponse = new GuidanceResponse
            {
                selected_target = "E5",
                movement_instruction = "turn_left_slowly"
            };
            Assert.That(validator.Validate(safeResponse, snapshot, runtimeState, riskState, out var staleReason), Is.False);
            Assert.That(staleReason, Does.Contain("changed after the request"));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void RuntimeGuidanceSafetyPolicy_UsesActiveDirectionAndFailsClosed()
    {
        var policy = new RuntimeGuidanceSafetyPolicy();
        var unsafeForward = new PhysicalRiskState(
            unsafeDirections: new[] { "forward" },
            safeDirections: new[] { "left" });

        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unsafeForward, "forward", 0.6f, 1, true)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.Continue));
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unsafeForward, "forward", 0.6f, 2, true)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unsafeForward, "forward", 0.6f, 2, false)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));

        var unrelatedCritical = new PhysicalRiskState(
            boundaryRisk: "critical",
            unsafeDirections: new[] { "left" },
            safeDirections: new[] { "forward" });
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unrelatedCritical, "forward", 2f, 0, false)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.Continue));
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unrelatedCritical, "forward", 0.3f, 0, false, true)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(unrelatedCritical, "forward", 2f, 0, false, false, false)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
        Assert.That(
            policy.Evaluate(CreateRuntimeSafetyInput(null, "forward", 0f, 0, false)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
    }

    [Test]
    public void LocalGuidanceValidator_FailsClosedForUnknownConflictAndBehind()
    {
        var root = new GameObject("LocalGuidanceFailClosedTest");
        try
        {
            var virtualUser = new GameObject("FailClosedVirtualUser");
            virtualUser.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            CreateExhibit(root.transform, "ForwardTarget", Vector3.forward * 2f, "route_subgoal", null);
            CreateExhibit(root.transform, "BehindTarget", Vector3.back * 2f, "route_subgoal", null);
            CreateExhibit(root.transform, "TurnCueTarget", Vector3.left * 2f, "turn_cue_anchor", null);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var riskEvaluator = root.AddComponent<PhysicalRiskEvaluator>();
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, riskEvaluator);
            var risk = new PhysicalRiskState(
                clearanceForward: 2f,
                clearanceLeft: 2f,
                clearanceRight: 2f,
                unsafeDirections: new string[0],
                safeDirections: new[] { "forward", "left", "right" });

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "ForwardTarget",
                        movement_instruction = "move_forward_slowly"
                    },
                    null,
                    null,
                    null,
                    out var nullRiskReason),
                Is.False);
            Assert.That(nullRiskReason, Does.Contain("no current physical risk"));

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "ForwardTarget",
                        movement_instruction = "fly_forward"
                    },
                    null,
                    null,
                    risk,
                    out var movementReason),
                Is.False);
            Assert.That(movementReason, Does.Contain("unknown movement"));

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "MissingTarget",
                        movement_instruction = "move_forward_slowly"
                    },
                    null,
                    null,
                    risk,
                    out var targetReason),
                Is.False);
            Assert.That(targetReason, Does.Contain("cannot resolve"));

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "ForwardTarget",
                        movement_instruction = "turn_left_slowly"
                    },
                    null,
                    null,
                    risk,
                    out var conflictReason),
                Is.False);
            Assert.That(conflictReason, Does.Contain("conflicting movement"));

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "BehindTarget",
                        movement_instruction = "turn_left_slowly"
                    },
                    null,
                    null,
                    risk,
                    out var behindReason),
                Is.False);
            Assert.That(behindReason, Does.Contain("cannot calculate"));

            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = "TurnCueTarget",
                        movement_instruction = "pause_and_wait"
                    },
                    null,
                    null,
                    risk,
                    out var stationaryTurnCueReason),
                Is.False);
            Assert.That(stationaryTurnCueReason, Does.Contain("stationary movement"));

            var noCueTarget = CreateExhibit(
                root.transform,
                "NoCueTarget",
                new Vector3(-0.1f, 0f, 2f),
                "turn_cue_anchor",
                null);
            targets.RebuildIndex();
            Assert.That(
                validator.Validate(
                    new GuidanceResponse
                    {
                        selected_target = noCueTarget.Id,
                        movement_instruction = "look_forward"
                    },
                    null,
                    null,
                    risk,
                    out var noCueReason),
                Is.True,
                noCueReason);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_TwoUnsafeSamplesSoftRerouteExactlyOnce()
    {
        var root = new GameObject("GuidanceRuntimeSoftRerouteTest");
        root.SetActive(false);
        try
        {
            var virtualUser = new GameObject("RuntimeSafetyVirtualUser");
            virtualUser.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var riskEvaluator = root.AddComponent<PhysicalRiskEvaluator>();
            riskEvaluator.Configure(space, pose, calibration);

            root.SetActive(true);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            AlignVirtualUserToT2Route(virtualUser.transform, targets);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, riskEvaluator);
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(
                null,
                null,
                task,
                targets,
                selector,
                null,
                riskEvaluator,
                validator,
                null,
                null,
                null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var softRerouteCount = 0;
            var replanCount = 0;
            coordinator.RuntimeIntervention += info =>
            {
                if (info.Decision == RuntimeGuidanceSafetyDecision.SoftReroute) softRerouteCount++;
            };
            coordinator.Replanning += _ => replanCount++;

            coordinator.RequestNextGuidance();
            var request = RequestForContext(requests.Contexts[0], "request-runtime-safe");
            request.physical_space_id = "wide_room";
            InvokePrivate(coordinator, "HandleRequestBuilt", request);
            coordinator.HandleGuidanceResponse(ResponseForRequest(request, "E3"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));
            var selectedDirection = CandidateDirectionForRequest(request, "E3");

            var unsafeForward = new PhysicalRiskState(
                riskSnapshotId: "runtime-risk-1",
                calibrationId: calibration.CalibrationId,
                physicalSpace: space.CurrentSpaceId,
                clearanceForward: 0.6f,
                clearanceLeft: 2f,
                clearanceRight: 2f,
                unsafeDirections: new[] { selectedDirection },
                safeDirections: SafeDirectionsExcept(selectedDirection));
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", unsafeForward);
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", unsafeForward);
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));

            var nextUnsafeForward = new PhysicalRiskState(
                riskSnapshotId: "runtime-risk-2",
                calibrationId: calibration.CalibrationId,
                physicalSpace: space.CurrentSpaceId,
                clearanceForward: 0.6f,
                clearanceLeft: 2f,
                clearanceRight: 2f,
                unsafeDirections: new[] { selectedDirection },
                safeDirections: SafeDirectionsExcept(selectedDirection));
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", nextUnsafeForward);
            InvokePrivate(coordinator, "HandleRuntimeRiskStateChanged", nextUnsafeForward);

            Assert.That(softRerouteCount, Is.EqualTo(1));
            Assert.That(replanCount, Is.EqualTo(1));
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(requests.Contexts[1].preferred_task_target, Is.EqualTo("E5"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            Assert.That(task.RuntimeState.IsNavigating, Is.True);
            Assert.That(task.RuntimeState.ActiveReservation, Is.Null);
            Assert.That(task.RuntimeState.ActiveCandidateSetReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Reserved));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_GainAbortUsesDecisionIdentityAndSoftReroutesOnce()
    {
        var root = new GameObject("GuidanceGainAbortSoftRerouteTest");
        root.SetActive(false);
        try
        {
            var virtualUser = new GameObject("GainAbortVirtualUser");
            virtualUser.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var riskEvaluator = root.AddComponent<PhysicalRiskEvaluator>();
            riskEvaluator.Configure(space, pose, calibration);

            root.SetActive(true);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            AlignVirtualUserToT2Route(virtualUser.transform, targets);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, riskEvaluator);
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(
                null,
                null,
                task,
                targets,
                selector,
                null,
                riskEvaluator,
                validator,
                null,
                gain,
                null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var softRerouteCount = 0;
            coordinator.RuntimeIntervention += info =>
            {
                if (info.Decision == RuntimeGuidanceSafetyDecision.SoftReroute)
                {
                    softRerouteCount++;
                }
            };
            RotationGainEventInfo matchedAbort = null;
            string expectedAbortDecisionId = null;
            gain.LifecycleEventOccurred += info =>
            {
                if (info.EventType == RotationGainEventType.GainCancelled &&
                    info.DecisionId == expectedAbortDecisionId)
                {
                    matchedAbort = info;
                }
            };

            coordinator.RequestNextGuidance();
            var request = RequestForContext(requests.Contexts[0], "request-gain-abort");
            request.physical_space_id = space.CurrentSpaceId;
            expectedAbortDecisionId = request.decision_id;
            InvokePrivate(coordinator, "HandleRequestBuilt", request);
            coordinator.HandleGuidanceResponse(ResponseForRequest(request, "E3"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));
            var selectedDirection = CandidateDirectionForRequest(request, "E3");

            SetPrivateField(riskEvaluator, "currentRiskState", new PhysicalRiskState(
                riskSnapshotId: "risk-gain-abort",
                calibrationId: calibration.CalibrationId,
                physicalSpace: space.CurrentSpaceId,
                clearanceForward: 0.6f,
                clearanceLeft: 2f,
                clearanceRight: 2f,
                unsafeDirections: new[] { selectedDirection },
                safeDirections: SafeDirectionsExcept(selectedDirection)));

            gain.BeginPendingTurn(1, 30f, "stale-decision");
            gain.AbortGain("gain_limit_exceeded");
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(softRerouteCount, Is.Zero);

            gain.BeginPendingTurn(1, 30f, request.decision_id);
            gain.AbortGain("gain_limit_exceeded");

            Assert.That(matchedAbort, Is.Not.Null);
            Assert.That(matchedAbort.DecisionId, Is.EqualTo(request.decision_id));
            Assert.That(matchedAbort.Reason, Is.EqualTo("gain_limit_exceeded"));
            Assert.That(softRerouteCount, Is.EqualTo(1));
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(requests.Contexts[1].preferred_task_target, Is.EqualTo("E5"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void RotationGainController_DefaultsDisabledUntilExplicitlyEnabled()
    {
        var go = new GameObject("RotationGainControllerTest");
        try
        {
            var gain = go.AddComponent<RotationGainController>();

            gain.QueueFromMovementInstruction("turn_left_slowly");

            Assert.That(gain.IsActive, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void MotionDetector_UsesTurnStartAndStopHysteresis()
    {
        var go = new GameObject("MotionDetectorTest");
        try
        {
            var detector = go.AddComponent<MotionDetector>();

            detector.Sample(Vector2.up, 0.1f);
            detector.Sample(Vector2.left, 0.1f);

            Assert.That(detector.IsTurning, Is.True);
            Assert.That(detector.LastSignedYawDeltaDegrees, Is.EqualTo(90f).Within(0.001f));
            Assert.That(detector.AngularSpeedDegreesPerSecond, Is.EqualTo(900f).Within(0.001f));

            detector.Sample(Vector2.left, 0.1f);

            Assert.That(detector.IsTurning, Is.False);
            Assert.That(detector.AngularSpeedDegreesPerSecond, Is.Zero.Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void TurnCueCompletionTracker_HandlesNoCueStableReverseAndTimeout()
    {
        var tracker = new TurnCueCompletionTracker();
        tracker.Configure(12f, 5f, 0.25f, 8f);

        Assert.That(tracker.Begin(1, 10f), Is.EqualTo(TurnCueTrackingResult.Completed));
        Assert.That(tracker.State, Is.EqualTo(TurnCueTrackingState.Completed));

        tracker.Cancel();
        Assert.That(tracker.Begin(1, 90f), Is.EqualTo(TurnCueTrackingResult.None));
        Assert.That(tracker.ProcessSample(true, 78f, 0.1f), Is.EqualTo(TurnCueTrackingResult.None));
        Assert.That(tracker.ProcessSample(false, 0f, 0.2f), Is.EqualTo(TurnCueTrackingResult.None));
        Assert.That(tracker.ProcessSample(false, 0f, 0.05f), Is.EqualTo(TurnCueTrackingResult.Completed));

        tracker.Cancel();
        tracker.Begin(-1, 90f);
        Assert.That(tracker.ProcessSample(true, 2f, 0.1f), Is.EqualTo(TurnCueTrackingResult.None));
        Assert.That(tracker.ProcessSample(true, 3f, 0.1f), Is.EqualTo(TurnCueTrackingResult.Reversed));

        tracker.Cancel();
        tracker.Begin(1, 90f);
        Assert.That(tracker.ProcessSample(false, 0f, 7.99f), Is.EqualTo(TurnCueTrackingResult.None));
        Assert.That(tracker.ProcessSample(false, 0f, 0.01f), Is.EqualTo(TurnCueTrackingResult.TimedOut));
    }

    [Test]
    public void RotationGainController_FollowsCompleteLifecycleAndEmitsRequiredEvents()
    {
        var root = new GameObject("RotationGainLifecycleTest");
        var poseSource = new GameObject("RotationGainPoseSource");
        var rotationRoot = new GameObject("RotationGainVirtualRoot");
        try
        {
            poseSource.transform.SetParent(rotationRoot.transform, false);
            poseSource.transform.localPosition = new Vector3(1f, 1.65f, 2f);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualPoseSource(poseSource.transform);
            Assert.That(pose.ConfigureVirtualRotationRoot(rotationRoot.transform), Is.True);
            var detector = root.AddComponent<MotionDetector>();
            detector.Configure(pose);
            var gain = root.AddComponent<RotationGainController>();
            gain.Configure(pose, detector);
            gain.ConfigureLimits(0.15f, 8f, 30f, 0.75f);
            var states = new List<RotationGainController.GainState>();
            var events = new List<RotationGainEventType>();
            gain.StateChanged += states.Add;
            gain.LifecycleEventOccurred += info => events.Add(info.EventType);
            gain.SetGainEnabled(true);

            gain.BeginPendingTurn(1);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));
            Assert.That(states, Is.EqualTo(new[]
            {
                RotationGainController.GainState.Prepared,
                RotationGainController.GainState.WaitingForUserTurn
            }));
            Assert.That(events, Is.EqualTo(new[] { RotationGainEventType.TurnRequested }));

            gain.ProcessTurnSample(false, 0f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));

            var posePositionBeforeGain = poseSource.transform.position;
            gain.ProcessTurnSample(true, 10f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.GainActive));
            Assert.That(gain.AccumulatedPhysicalYawDegrees, Is.EqualTo(10f).Within(0.001f));
            Assert.That(gain.AppliedExtraYawDegrees, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(rotationRoot.transform.eulerAngles.y, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(Vector3.Distance(poseSource.transform.position, posePositionBeforeGain), Is.LessThan(0.0001f));
            Assert.That(poseSource.transform.localEulerAngles.y, Is.Zero.Within(0.001f));
            Assert.That(poseSource.transform.eulerAngles.y, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(events, Is.EqualTo(new[]
            {
                RotationGainEventType.TurnRequested,
                RotationGainEventType.UserTurnStarted,
                RotationGainEventType.GainStarted
            }));

            gain.ProcessTurnSample(false, 0f, 0.2f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.GainActive));
            gain.ProcessTurnSample(true, 20f, 0.1f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Reevaluating));
            Assert.That(gain.AccumulatedPhysicalYawDegrees, Is.EqualTo(30f).Within(0.001f));
            Assert.That(gain.AppliedExtraYawDegrees, Is.EqualTo(4.5f).Within(0.001f));
            Assert.That(events, Is.EqualTo(new[]
            {
                RotationGainEventType.TurnRequested,
                RotationGainEventType.UserTurnStarted,
                RotationGainEventType.GainStarted,
                RotationGainEventType.UserTurnCompleted,
                RotationGainEventType.GainCompleted
            }));

            gain.CompleteReevaluation(true);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Inactive));
            Assert.That(states, Does.Contain(RotationGainController.GainState.GainCompleted));
            Assert.That(states, Does.Contain(RotationGainController.GainState.Reevaluating));

            gain.SetGainEnabled(false);
            Assert.That(gain.GainEnabled, Is.False);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Inactive));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(poseSource);
            Object.DestroyImmediate(rotationRoot);
        }
    }

    [Test]
    public void RotationGainController_AbortsForReverseStopTimeoutAndGainLimitWithDecisionId()
    {
        var root = new GameObject("RotationGainAbortRulesTest");
        var rotationRoot = new GameObject("RotationGainAbortRoot");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(rotationRoot.transform), Is.True);
            gain.ConfigureLimits(0.15f, 8f, 30f, 0.5f);
            gain.SetGainEnabled(true);
            var cancelledEvents = new List<RotationGainEventInfo>();
            gain.LifecycleEventOccurred += info =>
            {
                if (info.EventType == RotationGainEventType.GainCancelled)
                {
                    cancelledEvents.Add(info);
                }
            };

            gain.BeginPendingTurn(1, -1f, "decision-reverse");
            gain.AbortGain("turn_cue_reverse");
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("turn_cue_reverse"));

            gain.BeginPendingTurn(1, -1f, "decision-timeout");
            gain.ProcessTurnSample(true, 5f, 0.1f);
            gain.ProcessTurnSample(false, 0f, 0.5f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("turn_stopped_timeout"));

            gain.BeginPendingTurn(1, -1f, "decision-limit");
            gain.ProcessTurnSample(true, 60f, 0.1f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("gain_limit_exceeded"));
            Assert.That(gain.AppliedExtraYawDegrees, Is.Zero.Within(0.001f));
            Assert.That(
                cancelledEvents.Select(info => info.DecisionId),
                Is.EqualTo(new[] { "decision-reverse", "decision-timeout", "decision-limit" }));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(rotationRoot);
        }
    }

    [Test]
    public void RotationGainController_AbortsWhenTrackingBecomesInvalid()
    {
        var root = new GameObject("RotationGainTrackingLossTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            pose.ConfigureVirtualPoseSource(root.transform);
            Assert.That(pose.ConfigureVirtualRotationRoot(root.transform), Is.True);
            var detector = root.AddComponent<MotionDetector>();
            detector.Configure(pose);
            var gain = root.AddComponent<RotationGainController>();
            gain.Configure(pose, detector, null, calibration);
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(1);

            calibration.ReportTrackingLost();

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("tracking_invalid"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void RotationGainController_RejectsCameraAsRotationRoot()
    {
        var root = new GameObject("RotationGainCameraContractTest");
        var cameraObject = new GameObject("TrackedHeadCamera");
        var rotationRoot = new GameObject("SafeVirtualRotationRoot");
        try
        {
            cameraObject.AddComponent<Camera>();
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualPoseSource(cameraObject.transform);
            Assert.That(pose.ConfigureVirtualRotationRoot(rotationRoot.transform), Is.False);

            cameraObject.transform.SetParent(rotationRoot.transform, false);
            cameraObject.transform.localPosition = new Vector3(1f, 1.65f, 2f);
            Assert.That(pose.ConfigureVirtualRotationRoot(rotationRoot.transform), Is.True);
            Assert.That(pose.HasValidVirtualPoseContract, Is.True);

            Assert.That(pose.ConfigureVirtualRotationRoot(cameraObject.transform), Is.False);
            Assert.That(pose.VirtualPoseSource, Is.EqualTo(cameraObject.transform));
            Assert.That(pose.VirtualRotationRoot, Is.EqualTo(rotationRoot.transform));

            var detector = root.AddComponent<MotionDetector>();
            detector.Configure(pose);
            var gain = root.AddComponent<RotationGainController>();
            gain.Configure(pose, detector);
            Assert.That(gain.ConfigureRotationRoot(cameraObject.transform), Is.False);
            Assert.That(gain.RotationRoot, Is.EqualTo(rotationRoot.transform));

            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(1);
            var cameraPositionBeforeGain = cameraObject.transform.position;
            gain.ProcessTurnSample(true, 10f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.GainActive));
            Assert.That(Vector3.Distance(cameraObject.transform.position, cameraPositionBeforeGain), Is.LessThan(0.0001f));
            Assert.That(cameraObject.transform.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(cameraObject.transform.eulerAngles.y, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(rotationRoot.transform.eulerAngles.y, Is.EqualTo(1.5f).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(rotationRoot);
        }
    }

    [Test]
    public void SystemBootstrapper_RejectsRotationRootOutsideVirtualPoseHierarchy()
    {
        var root = new GameObject("BootstrapVirtualPoseContractTest");
        var poseSource = new GameObject("BootstrapTrackedHead");
        var unrelatedRotationRoot = new GameObject("BootstrapUnrelatedRotationRoot");
        root.SetActive(false);
        try
        {
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualPoseSource(poseSource.transform);
            SetPrivateField(pose, "virtualRotationRoot", unrelatedRotationRoot.transform);
            var bootstrapper = root.AddComponent<SystemBootstrapper>();
            SetPrivateField(bootstrapper, "userPoseTracker", pose);

            var readiness = bootstrapper.ValidateReferences();

            Assert.That(readiness.IsReady, Is.False);
            Assert.That(readiness.MissingReferences, Does.Contain("userPoseTracker.virtualPoseHierarchy"));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(poseSource);
            Object.DestroyImmediate(unrelatedRotationRoot);
        }
    }

    [Test]
    public void PauseResetController_ClearsGuidanceAndRestoresAfterReset()
    {
        var root = new GameObject("PauseResetControllerTest");
        try
        {
            var narrationText = CreateText(root.transform, "NarrationText");
            var movementText = CreateText(root.transform, "MovementText");
            var gazeText = CreateText(root.transform, "GazeText");
            var rejectionRoot = new GameObject("RejectionRoot");
            rejectionRoot.transform.SetParent(root.transform, false);
            var rejectionText = CreateText(rejectionRoot.transform, "RejectionText");

            var narration = root.AddComponent<NarrationPlayer>();
            narration.Configure(narrationText);
            var movement = root.AddComponent<MovementHintController>();
            movement.Configure(movementText);
            var gaze = root.AddComponent<GazeCueController>();
            gaze.Configure(gazeText);
            var rejection = root.AddComponent<RejectionPanel>();
            rejection.Configure(rejectionText, rejectionRoot);
            var executor = root.AddComponent<GuidanceExecutor>();
            executor.Configure(narration, gaze, movement, null, rejection, null);
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(1);
            var pose = root.AddComponent<UserPoseTracker>();
            var pauseReset = root.AddComponent<PauseResetController>();
            pauseReset.Configure(executor, rejection, gain, pose);

            executor.Apply(new GuidanceResponse
            {
                narration = "Move toward exhibit E5.",
                movement_instruction = "move_forward_slowly",
                gaze_cue = "look_forward",
                gaze_target_id = "E5_GazeTarget",
                verifier = new VerifierResult { approved = true, reason = "ok" }
            });

            Assert.That(narrationText.text, Is.EqualTo("Move toward exhibit E5."));
            Assert.That(movementText.text, Is.EqualTo("缓慢向前移动"));
            Assert.That(gazeText.text, Is.EqualTo("请看向前方"));
            Assert.That(gain.IsActive, Is.True);

            pauseReset.PauseForReset("All directions unsafe.");

            Assert.That(pauseReset.IsPaused, Is.True);
            Assert.That(narrationText.text, Is.Empty);
            Assert.That(movementText.text, Is.Empty);
            Assert.That(gazeText.text, Is.Empty);
            Assert.That(rejectionRoot.activeSelf, Is.True);
            Assert.That(rejectionText.text, Does.Contain("安全"));
            Assert.That(gain.IsActive, Is.False);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));

            pauseReset.ProcessPhysicalForward(Vector2.up);
            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.Completed));
            Assert.That(pauseReset.ResumeAfterReset(), Is.True);

            Assert.That(pauseReset.IsPaused, Is.False);
            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(rejectionRoot.activeSelf, Is.False);
            Assert.That(rejectionText.text, Is.Empty);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_CriticalRiskOwnsResetAndAbortsGainImmediately()
    {
        var root = new GameObject("RDWCriticalResetTest");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(-1);
            var pauseReset = root.AddComponent<PauseResetController>();
            pauseReset.Configure(null, null, gain);
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "pauseResetController", pauseReset);
            SetPrivateField(dashboard, "rotationGainController", gain);

            InvokePrivate(dashboard, "HandleRiskStateChanged", new PhysicalRiskState(
                boundaryRisk: "critical",
                resetRisk: "critical",
                unsafeDirections: new[] { "forward", "left", "right" },
                resetRequired: true));

            Assert.That(pauseReset.IsPaused, Is.True);
            Assert.That(pauseReset.IsActive, Is.True);
            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(gain.IsActive, Is.False);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void XRTrackingAdapter_SeparatesRawTrackingFromCalibratedPhysicalPose()
    {
        var root = new GameObject("XRTrackingAdapterTest");
        var virtualHead = new GameObject("XRTrackingAdapterVirtualHead");
        try
        {
            virtualHead.transform.SetPositionAndRotation(
                new Vector3(4f, 1.65f, 7f),
                Quaternion.Euler(0f, 70f, 0f));

            var pose = root.AddComponent<UserPoseTracker>();
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(2f, 3f), 0f);
            var adapter = root.AddComponent<XRTrackingAdapter>();
            adapter.Configure(pose, calibration);
            adapter.ApplyTrackedPose(
                new Vector3(1.25f, 1.65f, -0.5f),
                Quaternion.Euler(0f, 35f, 0f),
                virtualHead.transform);
            adapter.ApplyTrackedPose(
                new Vector3(1.25f, 1.65f, -0.5f),
                Quaternion.Euler(0f, 35f, 0f),
                virtualHead.transform);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            Assert.That(adapter.IsTrackingActive, Is.True);
            Assert.That(adapter.LastHeadLocalPosition, Is.EqualTo(new Vector3(1.25f, 1.65f, -0.5f)));
            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(new Vector2(2f, 3f)));
            Assert.That(adapter.PhysicalYawDegrees, Is.Zero.Within(0.001f));

            var snapshot = adapter.CaptureSnapshot();
            Assert.That(snapshot.virtualPosition, Is.EqualTo(virtualHead.transform.position));
            Assert.That(snapshot.virtualForward, Is.EqualTo(virtualHead.transform.forward));
            Assert.That(snapshot.physicalPosition, Is.EqualTo(new Vector2(2f, 3f)));
            Assert.That(snapshot.physicalYawDegrees, Is.Zero.Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(virtualHead);
        }
    }

    [Test]
    public void XRTrackingAdapter_RequiresStableSamplesAndDoesNotFallbackAfterLoss()
    {
        var root = new GameObject("XRTrackingStabilityTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var adapter = root.AddComponent<XRTrackingAdapter>();
            adapter.Configure(pose, calibration);

            Assert.That(adapter.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.False);
            Assert.That(adapter.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.True);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            adapter.ReportTrackingUnavailable();

            Assert.That(adapter.InputMode, Is.EqualTo(TrackingInputMode.XRDevice));
            Assert.That(adapter.IsTrackingActive, Is.False);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.TemporarilyTrackingLost));
            Assert.That(adapter.ApplyTrackedPose(new Vector3(2.6f, 1.65f, 2.5f), Quaternion.identity), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.False);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.TemporarilyTrackingLost));

            adapter.ApplyTrackedPose(new Vector3(2.6f, 1.65f, 2.5f), Quaternion.identity);

            Assert.That(adapter.IsTrackingActive, Is.True);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.Calibrated));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void XRTrackingAdapter_EditorSimulationAcceptsOnlyExplicitSimulationSamples()
    {
        var root = new GameObject("XRTrackingSimulationModeTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            var adapter = root.AddComponent<XRTrackingAdapter>();
            adapter.Configure(pose, calibration);
            adapter.SetInputMode(TrackingInputMode.EditorSimulation);

            Assert.That(adapter.ApplyTrackedPose(Vector3.zero, Quaternion.identity), Is.False);
            Assert.That(adapter.IsTrackingActive, Is.False);
            Assert.That(adapter.ApplyEditorSimulationPose(Vector3.zero, Quaternion.identity), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.False);
            Assert.That(adapter.ApplyEditorSimulationPose(Vector3.zero, Quaternion.identity), Is.True);
            Assert.That(adapter.IsTrackingActive, Is.True);
            Assert.That(adapter.InputMode, Is.EqualTo(TrackingInputMode.EditorSimulation));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalRiskEvaluator_ClearsSnapshotWhenCalibrationBecomesInvalid()
    {
        var root = new GameObject("PhysicalRiskCalibrationInvalidTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            Assert.That(risk.CurrentRiskState, Is.Not.Null);

            calibration.InvalidateCalibration();

            Assert.That(risk.CurrentRiskState, Is.Null);
            Assert.That(risk.EvaluateNow(), Is.Null);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_TrackingLossEntersSafetyHoldAndNeedsStableFreshRisk()
    {
        var root = new GameObject("ExperimentDashboardTrackingLossTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity);
            tracking.ApplyTrackedPose(new Vector3(2.5f, 1.65f, 2.5f), Quaternion.identity);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            root.SetActive(true);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var reset = root.AddComponent<PauseResetController>();
            var dashboard = root.AddComponent<ExperimentDashboard>();
            dashboard.Configure(
                space, pose, risk, tracking, null, null, null, null, null, null, null, reset, null, null, calibration);
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

            tracking.ReportTrackingUnavailable();

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(risk.CurrentRiskState, Is.Null);

            tracking.ApplyTrackedPose(new Vector3(2.6f, 1.65f, 2.5f), Quaternion.identity);
            dashboard.ResumeSession();
            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));

            tracking.ApplyTrackedPose(new Vector3(2.6f, 1.65f, 2.5f), Quaternion.identity);
            Assert.That(risk.CurrentRiskState, Is.Not.Null);
            Assert.That(risk.CurrentRiskState.calibration_id, Is.EqualTo(calibration.CalibrationId));
            dashboard.ResumeSession();

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Running));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceConfigParser_UsesStructuredDtoIndependentOfPropertyOrder()
    {
        const string json = "{\"obstacles\":[{\"radius\":0.45,\"center\":[2.0,2.2],\"id\":\"obstacle_01\"}]," +
                            "\"boundary\":[[0,0],[4,0],[4,4],[0,4]],\"id\":\"obstacle_room\"}";

        Assert.That(PhysicalSpaceConfigParser.TryParse(json, out var config, out var error), Is.True, error);
        Assert.That(config.id, Is.EqualTo("obstacle_room"));
        Assert.That(config.boundary, Has.Length.EqualTo(4));
        Assert.That(config.obstacles, Has.Length.EqualTo(1));
        Assert.That(config.obstacles[0].center, Is.EqualTo(new Vector2(2f, 2.2f)));
        Assert.That(config.obstacles[0].radius, Is.EqualTo(0.45f));
    }

    [Test]
    public void PauseResetController_SelectsLargestClearanceAndCompletesAtSafeHeading()
    {
        var root = new GameObject("TurnToSafeDirectionResetTest");
        try
        {
            var pose = root.AddComponent<UserPoseTracker>();
            pose.SetSimulatedPhysicalPose(Vector2.zero, 0f);
            var pauseReset = root.AddComponent<PauseResetController>();
            pauseReset.Configure(null, null, null, pose);
            ResetEventInfo started = null;
            ResetEventInfo completed = null;
            pauseReset.ResetStarted += info => started = info;
            pauseReset.ResetCompleted += info => completed = info;

            pauseReset.PauseForReset("critical risk", new PhysicalRiskState(
                clearanceForward: 0.2f,
                clearanceLeft: 1.4f,
                clearanceRight: 0.6f,
                boundaryRisk: "critical",
                resetRequired: true));

            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));
            Assert.That(pauseReset.SafeDirection, Is.EqualTo("left"));
            Assert.That(pauseReset.SafePhysicalForward, Is.EqualTo(Vector2.left));
            Assert.That(started, Is.Not.Null);
            pauseReset.ProcessPhysicalForward(Vector2.up);
            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.TurningToSafeDirection));

            pauseReset.ProcessPhysicalForward(Vector2.left);

            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.Completed));
            Assert.That(pauseReset.IsPaused, Is.True);
            Assert.That(completed, Is.Not.Null);
            Assert.That(completed.SafeDirection, Is.EqualTo("left"));
            Assert.That(pauseReset.ResumeAfterReset(), Is.True);
            Assert.That(pauseReset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(pauseReset.IsPaused, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_ArrivalAutomaticallyReplansUntilFinalTargetCompletes()
    {
        var root = new GameObject("GuidanceContinuousLoopTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                taskId = "T2_main_target",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var replanningCount = 0;
            var completedCount = 0;
            var selectedTargets = new List<GuidanceTarget>();
            var reachedTargets = new List<GuidanceTarget>();
            coordinator.Replanning += _ => replanningCount++;
            coordinator.TaskCompleted += _ => completedCount++;
            coordinator.GuidanceTargetSelected += selectedTargets.Add;
            coordinator.TargetReached += reachedTargets.Add;

            coordinator.RequestNextGuidance();
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(requests.Contexts[0].current_exhibit, Is.EqualTo("E1"));
            var firstRequest = RequestForContext(requests.Contexts[0], "request-arrival-e3");
            InvokePrivate(coordinator, "HandleRequestBuilt", firstRequest);
            coordinator.HandleGuidanceResponse(ResponseForRequest(firstRequest, "E3"));
            Assert.That(task.RuntimeState.CurrentGuidanceTarget.TargetId, Is.EqualTo("E3"));

            coordinator.HandleTargetArrived("E3");
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E5"));
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(requests.Contexts[1].current_exhibit, Is.EqualTo("E3"));
            Assert.That(replanningCount, Is.EqualTo(1));

            var finalRequest = RequestForContext(requests.Contexts[1], "request-arrival-e5");
            InvokePrivate(coordinator, "HandleRequestBuilt", finalRequest);
            coordinator.HandleGuidanceResponse(ResponseForRequest(finalRequest, "E5"));

            coordinator.HandleTargetArrived("E5");
            Assert.That(task.RuntimeState.IsCompleted, Is.True);
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(completedCount, Is.EqualTo(1));
            Assert.That(requests.CancelCount, Is.EqualTo(2));
            Assert.That(selectedTargets.Select(target => target.TargetId), Is.EqualTo(new[] { "E3", "E5" }));
            Assert.That(reachedTargets.Select(target => target.TargetId), Is.EqualTo(new[] { "E3", "E5" }));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceRuntimeState_PreventsRepeatedOrUnlimitedNavigableDetours()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E3", "E5", detourLimit: 2);

        Assert.That(
            state.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.NavigableDetour)),
            Is.True);
        Assert.That(state.activeDetourId, Is.EqualTo("E2"));
        Assert.That(state.consecutiveDetourCount, Is.EqualTo(1));
        Assert.That(
            state.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.NavigableDetour)),
            Is.True);
        Assert.That(state.consecutiveDetourCount, Is.EqualTo(1));
        Assert.That(state.CompleteDetour("E2"), Is.True);
        Assert.That(state.currentExhibitId, Is.EqualTo("E2"));
        Assert.That(state.nextTargetId, Is.EqualTo("E3"));
        Assert.That(state.HasVisitedDetour("E2"), Is.True);
        Assert.That(
            state.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.NavigableDetour)),
            Is.False);

        Assert.That(
            state.TrySetGuidanceTarget(new GuidanceTarget("E4", NavigationTargetRole.NavigableDetour)),
            Is.True);
        Assert.That(state.CompleteDetour("E4"), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.EqualTo(2));
        Assert.That(
            state.TrySetGuidanceTarget(new GuidanceTarget("E6", NavigationTargetRole.NavigableDetour)),
            Is.False);
    }

    [Test]
    public void GuidanceCoordinator_NavigableDetourArrivalPreservesRouteAndReplans()
    {
        var root = new GameObject("NavigableDetourFlowTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                taskId = "T2_main_target",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            Assert.That(
                task.TrySetGuidanceTarget(new GuidanceTarget("E3", NavigationTargetRole.NavigableDetour)),
                Is.True);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            GuidanceTarget reachedTarget = null;
            GuidanceTarget completedDetour = null;
            coordinator.TargetReached += target => reachedTarget = target;
            coordinator.DetourCompleted += target => completedDetour = target;

            coordinator.HandleTargetArrived("E3");

            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E5"));
            Assert.That(task.RuntimeState.HasVisitedDetour("E3"), Is.True);
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(requests.Contexts[0].current_exhibit, Is.EqualTo("E3"));
            Assert.That(requests.Contexts[0].preferred_task_target, Is.EqualTo("E5"));
            Assert.That(requests.Contexts[0].semantic_candidates[0].target_role, Is.EqualTo("final_target"));
            Assert.That(reachedTarget, Is.Not.Null);
            Assert.That(reachedTarget.TargetId, Is.EqualTo("E3"));
            Assert.That(completedDetour, Is.SameAs(reachedTarget));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_TurnCueRequiresTurnFeedbackAndDoesNotAdvanceRoute()
    {
        var root = new GameObject("TurnCueAnchorFlowTest");
        root.SetActive(false);
        try
        {
            var task = CreateRouteTask(root);
            Assert.That(
                task.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.TurnCueAnchor)),
                Is.True);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);

            coordinator.HandleTargetArrived("E2");
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(requests.Contexts, Is.Empty);

            coordinator.HandleTurnCueCompleted();

            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.HasCompletedTurnCue("E2"), Is.True);
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(requests.Contexts[0].preferred_task_target, Is.EqualTo("E3"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GuidanceCoordinator_TurnCueCompletesWithGainEnabledOrDisabled(bool gainEnabled)
    {
        var root = new GameObject($"TurnCueLifecycleTest_{gainEnabled}");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            pose.ConfigureVirtualUserRoot(root.transform);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            const string turnCueTargetId = "Stage5TurnCueTarget";
            CreateExhibit(root.transform, turnCueTargetId, new Vector3(-1f, 0f, 1f), "turn_cue_anchor", null);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var validator = root.AddComponent<LocalGuidanceValidator>();
            validator.Configure(targets, risk);
            var motion = root.AddComponent<MotionDetector>();
            motion.Configure(pose);
            var task = CreateRouteTask(root);
            Assert.That(
                task.TrySetGuidanceTarget(new GuidanceTarget(turnCueTargetId, NavigationTargetRole.TurnCueAnchor)),
                Is.True);
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.Configure(pose, motion, risk, calibration);
            gain.SetGainEnabled(gainEnabled);
            var lifecycleEvents = new List<RotationGainEventType>();
            gain.LifecycleEventOccurred += info => lifecycleEvents.Add(info.EventType);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(
                null,
                null,
                task,
                targets,
                null,
                null,
                risk,
                validator,
                null,
                gain,
                null,
                motion);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var reservation = task.RuntimeState.ActiveReservation;
            var currentRisk = risk.CurrentRiskState;
            var request = new GuidanceRequest
            {
                session_id = "session-turn-cue",
                request_id = "request-turn-cue",
                decision_id = reservation.DecisionId,
                state_version = reservation.TaskStateVersion,
                physical_space_id = space.CurrentSpaceId,
                calibration_id = calibration.CalibrationId,
                risk_snapshot_id = currentRisk.risk_snapshot_id,
                risk_snapshot = GuidanceRiskSnapshot.From(currentRisk),
                current_exhibit = task.RuntimeState.currentExhibitId
            };
            ConfigureSingleCandidate(request, turnCueTargetId, "turn_cue_anchor");
            InvokePrivate(coordinator, "HandleRequestBuilt", request);
            var response = ResponseForRequest(request, turnCueTargetId);
            response.movement_instruction = "turn_left_slowly";

            SetPrivateField(coordinator, "activeSnapshot", GuidanceRequestSnapshot.Capture(
                request,
                currentRisk,
                task.RuntimeState));
            SetPrivateField(coordinator, "activeResponse", response);
            SetPrivateField(coordinator, "requestPhase", GuidanceRequestPhase.Executing);
            InvokePrivate(coordinator, "BeginTurnCueTracking", response);

            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));
            Assert.That(task.RuntimeState.HasCompletedTurnCue(turnCueTargetId), Is.False);
            motion.Sample(Vector2.up, 0.1f);
            gain.ProcessTurnSample(false, 0f, 0.1f);
            var leftFortyFiveDegrees = new Vector2(-1f, 1f).normalized;
            motion.Sample(leftFortyFiveDegrees, 0.1f);
            gain.ProcessTurnSample(true, 45f, 0.1f);
            Assert.That(task.RuntimeState.HasCompletedTurnCue(turnCueTargetId), Is.False);

            motion.Sample(leftFortyFiveDegrees, 0.25f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Inactive));
            Assert.That(task.RuntimeState.HasCompletedTurnCue(turnCueTargetId), Is.True);
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(requests.Contexts, Is.Empty);
            if (gainEnabled)
            {
                Assert.That(lifecycleEvents, Is.EqualTo(new[]
                {
                    RotationGainEventType.TurnRequested,
                    RotationGainEventType.UserTurnStarted,
                    RotationGainEventType.GainStarted,
                    RotationGainEventType.UserTurnCompleted,
                    RotationGainEventType.GainCompleted
                }));
            }
            else
            {
                Assert.That(lifecycleEvents, Is.Empty);
            }
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceRuntimeState_CandidateReservationCommitsExactlyOnceAfterValidation()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5");
        var initialVersion = state.stateVersion;
        var finalCandidate = new NavigationCandidate(
            "E5", NavigationTargetRole.FinalTarget, "forward", "E5", "", "main_target", null, null);
        var detourCandidate = new NavigationCandidate(
            "E3", NavigationTargetRole.NavigableDetour, "left", "E3", "", "detour", null, new[] { "E1" });
        var candidateSet = new NavigationCandidateSet(
            new[] { "E5", "E3" },
            new[] { "E3" },
            new[] { "E3" },
            new[] { finalCandidate, detourCandidate },
            1);
        var risk = new PhysicalRiskState(
            riskSnapshotId: "risk-reservation",
            riskRevision: 7,
            calibrationId: "calibration-reservation");

        Assert.That(state.TryReserveCandidateSet(
            candidateSet,
            "candidate-set-reservation",
            "request-reservation",
            "decision-reservation",
            "ours_full",
            risk,
            out var reservation), Is.True);
        Assert.That(state.stateVersion, Is.EqualTo(initialVersion));
        Assert.That(state.CurrentGuidanceTarget, Is.Null);
        Assert.That(state.ActiveReservation, Is.Null);
        Assert.That(state.ActiveCandidateSetReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Reserved));

        Assert.That(state.TryValidateCandidateSelection(reservation, "E4", out _), Is.False);
        Assert.That(state.CurrentGuidanceTarget, Is.Null);
        Assert.That(state.ActiveReservation, Is.Null);

        Assert.That(state.TryValidateCandidateSelection(reservation, "E3", out var selected), Is.True);
        Assert.That(state.CommitCurrentSubgoal(reservation, selected, out var arrivalReservation), Is.True);
        Assert.That(state.CurrentGuidanceTarget.TargetId, Is.EqualTo("E3"));
        Assert.That(arrivalReservation.Phase, Is.EqualTo(TargetReservationPhase.Committed));
        Assert.That(state.ActiveCandidateSetReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Committed));
        Assert.That(state.CommitCurrentSubgoal(reservation, selected, out _), Is.False);
    }

    [Test]
    public void GuidanceCoordinator_RequestReservesCandidateSetWithoutArrivalWatch()
    {
        var root = new GameObject("CandidateRequestBeforeCommitTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                condition = "ours_full",
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var arrival = root.AddComponent<ArrivalDetector>();
            coordinator.Configure(
                null,
                null,
                task,
                root.GetComponent<NavigationTargetManager>(),
                root.GetComponent<SemanticTargetSelector>(),
                arrival,
                root.GetComponent<PhysicalRiskEvaluator>(),
                root.GetComponent<LocalGuidanceValidator>(),
                null,
                null,
                null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);

            coordinator.RequestNextGuidance();

            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            Assert.That(requests.Contexts, Has.Count.EqualTo(1));
            Assert.That(task.RuntimeState.CurrentGuidanceTarget, Is.Null);
            Assert.That(task.RuntimeState.ActiveReservation, Is.Null);
            Assert.That(task.RuntimeState.ActiveCandidateSetReservation.Phase,
                Is.EqualTo(CandidateSetReservationPhase.Reserved));
            Assert.That(GetPrivateField<TargetReservation>(arrival, "watchedReservation"), Is.Null);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_GainReevaluationWithoutCurrentRiskHardStops()
    {
        var root = new GameObject("GainReevaluationFailClosedTest");
        root.SetActive(false);
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, null, null, null, null, null, null, null, gain, null);
            SetPrivateField(coordinator, "requestPhase", GuidanceRequestPhase.Executing);
            RuntimeInterventionInfo intervention = null;
            coordinator.RuntimeIntervention += info => intervention = info;

            gain.BeginPendingTurn(1, 30f, "decision-no-risk");
            gain.ProcessTurnSample(true, 30f, 0.1f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("risk_snapshot_unavailable"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Failed));
            Assert.That(intervention, Is.Not.Null);
            Assert.That(intervention.Decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
            Assert.That(intervention.StartReset, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_CancelActiveGuidanceCancelsPendingGain()
    {
        var root = new GameObject("GuidanceCancelsGainTest");
        root.SetActive(false);
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(-1);
            RotationGainEventInfo cancelledEvent = null;
            gain.LifecycleEventOccurred += info =>
            {
                if (info.EventType == RotationGainEventType.GainCancelled)
                {
                    cancelledEvent = info;
                }
            };
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, null, null, null, null, null, null, null, gain, null);

            coordinator.CancelActiveGuidance();

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Cancelled));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("guidance_cancelled"));
            Assert.That(cancelledEvent, Is.Not.Null);
            Assert.That(cancelledEvent.Reason, Is.EqualTo("guidance_cancelled"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_TracksRequestLifecycleAcrossTwoGuidanceDecisions()
    {
        var root = new GameObject("GuidanceRequestLifecycleTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var phases = new System.Collections.Generic.List<GuidanceRequestPhase>();
            coordinator.RequestPhaseChanged += phases.Add;

            coordinator.RequestNextGuidance();
            var firstRequest = RequestForContext(requests.Contexts[0], "request-1");
            InvokePrivate(coordinator, "HandleRequestBuilt", firstRequest);
            coordinator.HandleGuidanceResponse(ResponseForRequest(firstRequest, "E3"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Executing));

            coordinator.HandleTargetArrived("E3");
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            var secondRequest = RequestForContext(requests.Contexts[1], "request-2");
            InvokePrivate(coordinator, "HandleRequestBuilt", secondRequest);
            coordinator.HandleGuidanceResponse(ResponseForRequest(secondRequest, "E5"));
            coordinator.HandleTargetArrived("E5");

            Assert.That(task.RuntimeState.IsCompleted, Is.True);
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Completed));
            Assert.That(phases, Does.Contain(GuidanceRequestPhase.Requested));
            Assert.That(phases, Does.Contain(GuidanceRequestPhase.Received));
            Assert.That(phases, Does.Contain(GuidanceRequestPhase.Verified));
            Assert.That(phases, Does.Contain(GuidanceRequestPhase.Executing));
            Assert.That(phases, Does.Contain(GuidanceRequestPhase.Completed));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceRequestSnapshot_RejectsMismatchedRequestDecisionOrStateIdentity()
    {
        var runtime = new GuidanceRuntimeState();
        runtime.Begin("T2_main_target", "E1", "E3", "E5");
        var request = new GuidanceRequest
        {
            session_id = "session-current",
            calibration_id = "calibration-current",
            request_id = "request-current",
            decision_id = "decision-current",
            state_version = runtime.stateVersion,
            risk_snapshot_id = "risk-current",
            risk_snapshot = new GuidanceRiskSnapshot { risk_revision = 1 },
            current_exhibit = "E1",
            physical_space_id = "narrow_room"
        };
        ConfigureSingleCandidate(request, "E3", "navigable_detour");
        var snapshot = GuidanceRequestSnapshot.Capture(request, null, runtime);

        Assert.That(snapshot.MatchesResponse(ResponseForRequest(request, "E3")), Is.True);

        var wrongRequest = ResponseForRequest(request, "E3");
        wrongRequest.request_id = "request-old";
        Assert.That(snapshot.MatchesResponse(wrongRequest), Is.False);

        var wrongDecision = ResponseForRequest(request, "E3");
        wrongDecision.decision_id = "decision-old";
        Assert.That(snapshot.MatchesResponse(wrongDecision), Is.False);

        var wrongCandidateSet = ResponseForRequest(request, "E3");
        wrongCandidateSet.candidate_set_id = "candidate-set-old";
        Assert.That(snapshot.MatchesResponse(wrongCandidateSet), Is.False);

        var wrongState = ResponseForRequest(request, "E3");
        wrongState.state_version++;
        Assert.That(snapshot.MatchesResponse(wrongState), Is.False);

        var wrongCalibration = ResponseForRequest(request, "E3");
        wrongCalibration.calibration_id = "calibration-old";
        Assert.That(snapshot.MatchesResponse(wrongCalibration), Is.False);

        var wrongRisk = ResponseForRequest(request, "E3");
        wrongRisk.risk_snapshot_id = "risk-old";
        Assert.That(snapshot.MatchesResponse(wrongRisk), Is.False);

        var wrongRiskRevision = ResponseForRequest(request, "E3");
        wrongRiskRevision.risk_revision++;
        Assert.That(snapshot.MatchesResponse(wrongRiskRevision), Is.False);
    }

    [Test]
    public void GuidanceCoordinator_StaleResponseDoesNotCancelNewActiveRequest()
    {
        var root = new GameObject("GuidanceStaleResponseTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);

            coordinator.RequestNextGuidance();
            var firstRequest = RequestForContext(requests.Contexts[0], "request-old");
            InvokePrivate(coordinator, "HandleRequestBuilt", firstRequest);
            coordinator.HandleGuidanceResponse(ResponseForRequest(firstRequest, "E3"));
            coordinator.HandleTargetArrived("E3");
            var secondRequest = RequestForContext(requests.Contexts[1], "request-current");
            InvokePrivate(coordinator, "HandleRequestBuilt", secondRequest);

            coordinator.HandleGuidanceResponse(ResponseForRequest(firstRequest, "E3"));

            Assert.That(coordinator.ActiveRequestId, Is.EqualTo("request-current"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Requested));
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E5"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_OldReservationCannotAdvanceNewGeneration()
    {
        var root = new GameObject("GuidanceReservationGenerationTest");
        root.SetActive(false);
        try
        {
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E5" }
            });
            task.StartTask();
            Assert.That(task.TryReserveGuidanceTarget(
                new GuidanceTarget("E5", NavigationTargetRole.FinalTarget), "decision-old"), Is.True);
            var oldReservation = task.RuntimeState.ActiveReservation;
            Assert.That(task.AbandonReservation(), Is.True);
            Assert.That(task.TryReserveGuidanceTarget(
                new GuidanceTarget("E5", NavigationTargetRole.FinalTarget), "decision-current"), Is.True);
            var currentReservation = task.RuntimeState.ActiveReservation;
            Assert.That(task.CommitReservation(currentReservation), Is.True);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);

            coordinator.HandleTargetArrived(oldReservation);
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.ActiveReservation, Is.SameAs(currentReservation));

            coordinator.HandleTargetArrived(currentReservation);
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E5"));
            Assert.That(task.RuntimeState.IsCompleted, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void AgentClient_BuildRequestPreservesReservedRequestAndDecisionIdentity()
    {
        var root = new GameObject("AgentClientRequestIdentityTest");
        root.SetActive(false);
        try
        {
            var pose = root.AddComponent<UserPoseTracker>();
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.UpdateRawTrackingPose(new Vector2(2.5f, 2.5f), 0f);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var client = root.AddComponent<AgentClient>();
            client.Configure(space, pose, null, risk, calibration);
            var context = new GuidanceRequestContext
            {
                session_id = "session-stable",
                decision_id = "decision-stable",
                state_version = 9,
                current_exhibit = "E1"
            };
            ConfigureSingleCandidateContext(context, "E3", "navigable_detour");
            context.request_id = "request-stable";

            var first = (GuidanceRequest)InvokePrivateWithResult(client, "BuildRequest", context);
            var second = (GuidanceRequest)InvokePrivateWithResult(client, "BuildRequest", context);

            Assert.That(first.request_id, Is.EqualTo("request-stable"));
            Assert.That(second.request_id, Is.EqualTo(first.request_id));
            Assert.That(first.decision_id, Is.EqualTo("decision-stable"));
            Assert.That(first.state_version, Is.EqualTo(9));
            Assert.That(first.session_id, Is.EqualTo("session-stable"));
            Assert.That(first.calibration_id, Is.EqualTo(calibration.CalibrationId));
            Assert.That(first.risk_snapshot_id, Is.EqualTo(risk.CurrentRiskState.risk_snapshot_id));
            Assert.That(first.risk_snapshot, Is.Not.Null);
            Assert.That(first.risk_snapshot.source, Is.EqualTo("unity"));
            Assert.That(first.risk_snapshot.risk_snapshot_id, Is.EqualTo(first.risk_snapshot_id));
            Assert.That(first.risk_snapshot.risk_revision, Is.GreaterThan(0));
            Assert.That(first.risk_snapshot.boundary_context.points, Is.Not.Empty);
            Assert.That(second.risk_snapshot.risk_revision, Is.EqualTo(first.risk_snapshot.risk_revision));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void AgentClient_ResponseContractFailsClosedForMalformedIdentityAndTargetMismatch()
    {
        var request = new GuidanceRequest
        {
            session_id = "session-contract",
            calibration_id = "calibration-contract",
            request_id = "request-contract",
            decision_id = "decision-contract",
            state_version = 5,
            risk_snapshot_id = "risk-contract",
            risk_snapshot = new GuidanceRiskSnapshot { risk_revision = 1 }
        };
        ConfigureSingleCandidate(request, "E3", "navigable_detour");
        var response = new GuidanceResponse
        {
            task_definition_version = request.task_definition_version,
            guidance_schema_version = request.guidance_schema_version,
            candidate_policy_version = request.candidate_policy_version,
            risk_policy_version = request.risk_policy_version,
            session_id = request.session_id,
            calibration_id = request.calibration_id,
            request_id = request.request_id,
            decision_id = request.decision_id,
            candidate_set_id = request.candidate_set_id,
            state_version = request.state_version,
            risk_snapshot_id = request.risk_snapshot_id,
            risk_revision = request.risk_snapshot.risk_revision,
            target_role = "navigable_detour",
            selected_target = "E3",
            route_decision = "visit_route_subgoal",
            narration = "请看向左侧目标。",
            gaze_cue = "look_left",
            gaze_target_id = "E3_GazeTarget",
            movement_instruction = "turn_left_slowly",
            physical_intent = "continue_semantic_route",
            verifier = new VerifierResult { approved = true, reason = "safe" }
        };

        Assert.That(
            AgentClient.TryParseGuidanceResponse("{}", request, out _, out var missingReason),
            Is.False);
        Assert.That(missingReason, Does.Contain("required response field"));

        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                JsonUtility.ToJson(response),
                request,
                out var parsed,
                out var validReason),
            Is.True,
            validReason);
            Assert.That(parsed.selected_target, Is.EqualTo("E3"));

        var legacyAvatarResponse = JObject.Parse(JsonUtility.ToJson(response));
        legacyAvatarResponse["avatar_action"] = "point_left";
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                legacyAvatarResponse.ToString(),
                request,
                out _,
                out var legacyAvatarReason),
            Is.False);
        Assert.That(legacyAvatarReason, Does.Contain("unexpected response field: avatar_action"));

        var missingStateVersion = JObject.Parse(JsonUtility.ToJson(response));
        missingStateVersion.Remove("state_version");
        request.state_version = 0;
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                missingStateVersion.ToString(),
                request,
                out _,
                out var missingStateVersionReason),
            Is.False);
        Assert.That(missingStateVersionReason, Does.Contain("state_version"));
        request.state_version = response.state_version;

        var missingVerifierApproved = JObject.Parse(JsonUtility.ToJson(response));
        ((JObject)missingVerifierApproved["verifier"]).Remove("approved");
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                missingVerifierApproved.ToString(),
                request,
                out _,
                out var missingVerifierReason),
            Is.False);
        Assert.That(missingVerifierReason, Does.Contain("approved"));

        var unexpectedField = JObject.Parse(JsonUtility.ToJson(response));
        unexpectedField["unexpected_field"] = "not-in-contract";
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                unexpectedField.ToString(),
                request,
                out _,
                out var unexpectedFieldReason),
            Is.False);
        Assert.That(unexpectedFieldReason, Does.Contain("unexpected response field"));

        response.selected_target = "E4";
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                JsonUtility.ToJson(response),
                request,
                out _,
                out var targetReason),
            Is.False);
        Assert.That(targetReason, Does.Contain("selected_target"));

        response.selected_target = "E3";
        response.request_id = "request-stale";
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                JsonUtility.ToJson(response),
                request,
                out _,
                out var identityReason),
            Is.False);
        Assert.That(identityReason, Does.Contain("identity"));

        response.request_id = request.request_id;
        response.movement_instruction = "unknown_movement";
        Assert.That(
            AgentClient.TryParseGuidanceResponse(
                JsonUtility.ToJson(response),
                request,
                out _,
                out var enumReason),
            Is.False);
        Assert.That(enumReason, Does.Contain("unknown protocol enum"));
    }

    [Test]
    public void AgentClient_RejectsStaleContextAndRetryAfterCalibrationInvalidation()
    {
        var root = new GameObject("AgentClientFailClosedIdentityTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var client = root.AddComponent<AgentClient>();
            client.Configure(space, pose, null, risk, calibration);

            var currentContext = new GuidanceRequestContext
            {
                calibration_id = calibration.CalibrationId,
                risk_snapshot_id = risk.CurrentRiskState.risk_snapshot_id
            };
            ConfigureSingleCandidateContext(currentContext, "E3", "navigable_detour");
            currentContext.request_id = "request-current";
            currentContext.decision_id = "decision-current";
            var currentRequest = (GuidanceRequest)InvokePrivateWithResult(client, "BuildRequest", currentContext);
            Assert.That(currentRequest, Is.Not.Null);

            var staleContext = new GuidanceRequestContext
            {
                calibration_id = "calibration-stale",
                risk_snapshot_id = currentContext.risk_snapshot_id
            };
            ConfigureSingleCandidateContext(staleContext, "E3", "navigable_detour");
            staleContext.request_id = "request-stale";
            staleContext.decision_id = "decision-stale";
            Assert.That(InvokePrivateWithResult(client, "BuildRequest", staleContext), Is.Null);

            staleContext.calibration_id = currentContext.calibration_id;
            staleContext.risk_snapshot_id = "risk-stale";
            Assert.That(InvokePrivateWithResult(client, "BuildRequest", staleContext), Is.Null);

            string failure = null;
            client.GuidanceRequestFailed.AddListener(message => failure = message);
            SetPrivateField(client, "lastRequest", currentRequest);
            calibration.InvalidateCalibration();

            client.RetryLastRequest();

            Assert.That(client.RequestInFlight, Is.False);
            Assert.That(failure, Does.Contain("Direct retry is disabled"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_ZeroOffsetMapsRawPoseDirectly()
    {
        var root = new GameObject("PhysicalCalibrationZeroTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.UpdateRawTrackingPose(new Vector2(1f, 2f), 25f);
            calibration.SetCalibrationPoint(new Vector2(1f, 2f), 25f);

            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.Calibrated));
            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(new Vector2(1f, 2f)));
            Assert.That(YawOf(pose.PhysicalForwardXZ), Is.EqualTo(25f).Within(0.001f));

            calibration.UpdateRawTrackingPose(new Vector2(2f, 4f), 40f);
            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(new Vector2(2f, 4f)));
            Assert.That(YawOf(pose.PhysicalForwardXZ), Is.EqualTo(40f).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_TranslationAlignsRawOriginToRoomPoint()
    {
        var root = new GameObject("PhysicalCalibrationTranslationTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(1f, 2f), 0f);
            calibration.UpdateRawTrackingPose(new Vector2(10f, 20f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            calibration.UpdateRawTrackingPose(new Vector2(11f, 22f), 0f);

            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(new Vector2(2f, 4f)));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_NinetyDegreeYawRotatesTrackingDeltaIntoRoom()
    {
        var root = new GameObject("PhysicalCalibrationYaw90Test");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.SetCalibrationPoint(Vector2.zero, 90f);
            calibration.UpdateRawTrackingPose(new Vector2(5f, 5f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            calibration.UpdateRawTrackingPose(new Vector2(5f, 6f), 0f);

            Assert.That(pose.PhysicalPositionXZ.x, Is.EqualTo(1f).Within(0.001f));
            Assert.That(pose.PhysicalPositionXZ.y, Is.Zero.Within(0.001f));
            Assert.That(YawOf(pose.PhysicalForwardXZ), Is.EqualTo(90f).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_OneHundredEightyDegreeYawReversesTrackingDelta()
    {
        var root = new GameObject("PhysicalCalibrationYaw180Test");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.SetCalibrationPoint(Vector2.zero, 180f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            calibration.UpdateRawTrackingPose(Vector2.up, 0f);

            Assert.That(pose.PhysicalPositionXZ.x, Is.Zero.Within(0.001f));
            Assert.That(pose.PhysicalPositionXZ.y, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(Mathf.Abs(YawOf(pose.PhysicalForwardXZ)), Is.EqualTo(180f).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_RecalibrationCreatesNewIdAndRealignsPose()
    {
        var root = new GameObject("PhysicalCalibrationRepeatTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(1f, 2f), 0f);
            calibration.UpdateRawTrackingPose(new Vector2(5f, 5f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var firstId = calibration.CalibrationId;

            calibration.SetCalibrationPoint(new Vector2(3f, 4f), 90f);
            calibration.UpdateRawTrackingPose(new Vector2(8f, 9f), 20f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            Assert.That(calibration.CalibrationId, Is.Not.EqualTo(firstId));
            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(new Vector2(3f, 4f)));
            Assert.That(YawOf(pose.PhysicalForwardXZ), Is.EqualTo(90f).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_TrackingLossRetainsCalibrationUntilRawPoseReturns()
    {
        var root = new GameObject("PhysicalCalibrationTrackingLossTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out _, out var calibration);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var calibrationId = calibration.CalibrationId;

            calibration.ReportTrackingLost();
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.TemporarilyTrackingLost));
            Assert.That(calibration.CalibrationId, Is.EqualTo(calibrationId));

            calibration.UpdateRawTrackingPose(Vector2.right, 0f);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.TemporarilyTrackingLost));
            Assert.That(calibration.ReportTrackingRecovered(), Is.True);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.Calibrated));
            Assert.That(pose.PhysicalPositionXZ, Is.EqualTo(Vector2.right));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalSpaceCalibration_SpaceChangeInvalidatesCalibration()
    {
        var root = new GameObject("PhysicalCalibrationSpaceChangeTest");
        try
        {
            CreateCalibrationRig(root, out _, out var space, out var calibration);
            Assert.That(space.CurrentSpaceId, Is.EqualTo("wide_room"));
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.Calibrated));

            Assert.That(space.SetPhysicalSpace("narrow_room"), Is.True);
            Assert.That(calibration.State, Is.EqualTo(PhysicalCalibrationState.Invalid));
            Assert.That(calibration.IsCalibrationValid, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalRiskEvaluator_UsesCalibratedRoomPoseInsteadOfRawTrackingPose()
    {
        var root = new GameObject("PhysicalRiskCalibratedPoseTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(1f, 2f), 0f);
            calibration.UpdateRawTrackingPose(new Vector2(10f, 20f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            calibration.UpdateRawTrackingPose(new Vector2(11f, 20f), 0f);

            var evaluator = root.AddComponent<PhysicalRiskEvaluator>();
            evaluator.Configure(space, pose, calibration);
            var risk = evaluator.EvaluateNow();

            Assert.That(risk, Is.Not.Null);
            Assert.That(risk.risk_snapshot_id, Is.Not.Empty);
            Assert.That(risk.calibration_id, Is.EqualTo(calibration.CalibrationId));
            Assert.That(risk.user_physical_position, Is.EqualTo(new Vector2(2f, 2f)));
            Assert.That(risk.user_physical_position, Is.Not.EqualTo(new Vector2(11f, 20f)));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PhysicalRiskEvaluator_AdvancesRevisionOnlyForExecutionRelevantRiskChanges()
    {
        var root = new GameObject("PhysicalRiskRevisionTest");
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
            calibration.UpdateRawTrackingPose(Vector2.zero, 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var evaluator = root.AddComponent<PhysicalRiskEvaluator>();
            evaluator.Configure(space, pose, calibration);
            var first = evaluator.EvaluateNow();

            calibration.UpdateRawTrackingPose(new Vector2(0.01f, 0.01f), 0f);
            var jittered = evaluator.EvaluateNow();

            Assert.That(jittered.risk_snapshot_id, Is.Not.EqualTo(first.risk_snapshot_id));
            Assert.That(jittered.risk_revision, Is.EqualTo(first.risk_revision));
            Assert.That(jittered.safe_directions, Is.EqualTo(first.safe_directions));
            Assert.That(jittered.unsafe_directions, Is.EqualTo(first.unsafe_directions));

            calibration.UpdateRawTrackingPose(new Vector2(0f, 2.2f), 0f);
            var changed = evaluator.EvaluateNow();

            Assert.That(changed.risk_revision, Is.GreaterThan(jittered.risk_revision));
            Assert.That(changed.critical, Is.True);
            Assert.That(changed.source, Is.EqualTo("unity"));
            Assert.That(changed.risk_policy_version, Is.EqualTo(GuidanceProtocolVersions.RiskPolicy));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_RequiresCalibrationBeforeSessionCanStart()
    {
        var root = new GameObject("ExperimentDashboardCalibrationGateTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
            tracking.ApplyTrackedPose(new Vector3(1f, 1.65f, 2f), Quaternion.identity);
            tracking.ApplyTrackedPose(new Vector3(1f, 1.65f, 2f), Quaternion.identity);
            var task = root.AddComponent<GuidanceTaskController>();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            var client = root.AddComponent<AgentClient>();
            client.SetBackendOnlineForTesting(true);
            var logger = root.AddComponent<ExperimentLogger>();
            var arrival = root.AddComponent<ArrivalDetector>();
            var gain = root.AddComponent<RotationGainController>();
            var executor = root.AddComponent<GuidanceExecutor>();
            var reset = root.AddComponent<PauseResetController>();
            var dashboard = root.AddComponent<ExperimentDashboard>();
            dashboard.Configure(
                space,
                pose,
                risk,
                tracking,
                task,
                coordinator,
                client,
                logger,
                arrival,
                gain,
                executor,
                reset,
                null,
                null,
                calibration);

            Assert.That(dashboard.CanStartSession(out var missingBefore), Is.False);
            Assert.That(missingBefore, Is.EqualTo("physical calibration"));

            Assert.That(dashboard.CalibratePhysicalSpace(), Is.True);
            Assert.That(space.CurrentSpaceId, Is.EqualTo("narrow_room"));
            Assert.That(calibration.IsCalibrationValid, Is.True);
            Assert.That(dashboard.CanStartSession(out var missingAfter), Is.True, missingAfter);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void BoundaryVisualizer_KeepsCalibrationPointFixedWhilePhysicalUserMarkerMoves()
    {
        var root = new GameObject("BoundaryCalibrationMarkersTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            calibration.SetCalibrationPoint(new Vector2(1f, 2f), 0f);
            calibration.UpdateRawTrackingPose(new Vector2(5f, 5f), 0f);
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose, calibration);
            var visualizer = root.AddComponent<BoundaryVisualizer>();
            visualizer.Configure(space, pose, risk, calibration);

            var calibrationPoint = root.transform.Find("PhysicalSpaceDebugView/CalibrationPoint");
            var userMarker = root.transform.Find("PhysicalSpaceDebugView/PhysicalUserMarker");
            Assert.That(calibrationPoint, Is.Not.Null);
            Assert.That(userMarker, Is.Not.Null);
            Assert.That(userMarker.gameObject.activeSelf, Is.True);
            Assert.That(userMarker.position.x, Is.EqualTo(calibrationPoint.position.x).Within(0.001f));
            Assert.That(userMarker.position.z, Is.EqualTo(calibrationPoint.position.z).Within(0.001f));
            var fixedCalibrationPosition = calibrationPoint.position;

            calibration.UpdateRawTrackingPose(new Vector2(6f, 5f), 0f);
            InvokePrivate(visualizer, "RefreshPoseAndDirections");

            Assert.That(calibrationPoint.position, Is.EqualTo(fixedCalibrationPosition));
            Assert.That(userMarker.position.x, Is.EqualTo(fixedCalibrationPosition.x + 1f).Within(0.001f));
            Assert.That(userMarker.position.z, Is.EqualTo(fixedCalibrationPosition.z).Within(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentConditionMatrix_CoversExpectedExperimentGrid()
    {
        Assert.That(ExperimentConditionMatrix.TotalCombinations, Is.EqualTo(24));
        Assert.That(ExperimentConditionMatrix.Conditions, Does.Contain("ours_full"));
        Assert.That(ExperimentConditionMatrix.TaskIds, Is.EqualTo(new[] { "T2_main_target" }));
        Assert.That(ExperimentConditionMatrix.FrozenTaskIds, Does.Contain("T4_next_step"));
        Assert.That(ExperimentConditionMatrix.PhysicalSpaces, Does.Contain("l_shaped_room"));

        var spec = ExperimentConditionMatrix.Build(5, 1, 3, "P042");

        Assert.That(spec.participantId, Is.EqualTo("P042"));
        Assert.That(spec.condition, Is.EqualTo("ours_full"));
        Assert.That(spec.taskId, Is.EqualTo("T2_main_target"));
        Assert.That(spec.physicalSpaceId, Is.EqualTo("l_shaped_room"));
        Assert.That(spec.routeTargetIds, Is.EqualTo(new[] { "E1", "E5" }));

        var task = spec.ToTaskDefinition();
        Assert.That(task.routeTargetIds, Is.EqualTo(spec.routeTargetIds));
        Assert.That(ReferenceEquals(task.routeTargetIds, spec.routeTargetIds), Is.False);

        var b1 = ExperimentConditionMatrix.GetPolicy("B1_mllm_only");
        var b4 = ExperimentConditionMatrix.GetPolicy("B4_scripted_reset");
        var b5 = ExperimentConditionMatrix.GetPolicy("B5_ours_without_verifier");
        var full = ExperimentConditionMatrix.GetPolicy("ours_full");
        Assert.That(b1.UseSafeCandidateFilter, Is.EqualTo(ConditionCapabilityMode.Disabled));
        Assert.That(b4.UseRdwAffordance, Is.EqualTo(ConditionCapabilityMode.Scripted));
        Assert.That(b5.UseRotationGain, Is.EqualTo(ConditionCapabilityMode.Enabled));
        Assert.That(b5.UseModelVerifier, Is.EqualTo(ConditionCapabilityMode.Disabled));
        Assert.That(full.UseModelVerifier, Is.EqualTo(ConditionCapabilityMode.Enabled));
        Assert.That(ExperimentConditionPolicy.CurrentVersion, Is.EqualTo("condition-policy-v1"));
    }

    [Test]
    public void SemanticTargetSelector_AppliesSafeFilterOnlyWhenConditionPolicyEnablesIt()
    {
        var root = new GameObject("ConditionPolicyTargetSelectionTest");
        try
        {
            var virtualUser = new GameObject("PolicyVirtualUser");
            virtualUser.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Assert.That(targets.TryGetTarget("E1", out var current), Is.True);
            Assert.That(targets.TryGetTarget("E3", out var detour), Is.True);
            Assert.That(targets.TryGetTarget("E5", out var routeTarget), Is.True);
            var currentPosition = current.ViewPoint != null ? current.ViewPoint.position : current.transform.position;
            var finalPosition = routeTarget.ViewPoint != null
                ? routeTarget.ViewPoint.position
                : routeTarget.transform.position;
            virtualUser.transform.position = currentPosition;
            virtualUser.transform.forward = Vector3.ProjectOnPlane(finalPosition - currentPosition, Vector3.up).normalized;
            Assert.That(targets.TryGetTargetDirection("E3", out var detourDirection), Is.True);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E5", "E5");
            var risk = new PhysicalRiskState(
                unsafeDirections: new[] { "forward" },
                safeDirections: new[] { detourDirection });

            var baselineTarget = selector.SelectNextTarget(
                runtime,
                risk,
                ExperimentConditionMatrix.GetPolicy("B1_mllm_only"));
            var fullTarget = selector.SelectNextTarget(
                runtime,
                risk,
                ExperimentConditionMatrix.GetPolicy("ours_full"));

            Assert.That(baselineTarget, Is.EqualTo("E5"));
            Assert.That(fullTarget, Is.EqualTo("E3"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void NavigationTargetManager_ClassifiesDirectionRelativeToCurrentVirtualForward()
    {
        var root = new GameObject("RelativeVirtualDirectionTest");
        try
        {
            var targets = root.AddComponent<NavigationTargetManager>();
            var position = Vector3.zero;
            var target = Vector3.forward * 2f;

            Assert.That(
                targets.ClassifyRelativeDirection(position, Vector3.forward, target),
                Is.EqualTo("forward"));
            Assert.That(
                targets.ClassifyRelativeDirection(position, Vector3.right, target),
                Is.EqualTo("left"));
            Assert.That(
                targets.ClassifyRelativeDirection(position, Vector3.left, target),
                Is.EqualTo("right"));
            Assert.That(
                targets.ClassifyRelativeDirection(position, Vector3.back, target),
                Is.EqualTo("behind"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void SemanticTargetSelector_DistinguishesDetourAndFinalRoles()
    {
        var root = new GameObject("GuidanceTargetRoleSelectionTest");
        try
        {
            var userRoot = new GameObject("RoleUser");
            userRoot.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(userRoot.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            Assert.That(targets.TryGetTarget("E1", out var current), Is.True);
            Assert.That(targets.TryGetTarget("E5", out var finalMetadata), Is.True);
            var currentPosition = current.ViewPoint != null ? current.ViewPoint.position : current.transform.position;
            var finalPosition = finalMetadata.ViewPoint != null
                ? finalMetadata.ViewPoint.position
                : finalMetadata.transform.position;
            userRoot.transform.position = currentPosition;
            userRoot.transform.forward = Vector3.ProjectOnPlane(finalPosition - currentPosition, Vector3.up).normalized;
            Assert.That(targets.TryGetTargetDirection("E3", out var detourDirection), Is.True);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", current.Id, "E5", "E5");
            var unsafeForward = new PhysicalRiskState(
                unsafeDirections: new[] { "forward" },
                safeDirections: new[] { detourDirection });

            var detour = selector.SelectNextGuidanceTarget(
                runtime,
                unsafeForward,
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            Assert.That(detour.TargetId, Is.EqualTo("E3"));
            Assert.That(detour.TargetRole, Is.EqualTo(NavigationTargetRole.NavigableDetour));
            Assert.That(targets.GetSafetyTargetRole("E4"), Is.EqualTo(NavigationTargetRole.NavigableDetour));

            var finalTarget = selector.SelectNextGuidanceTarget(
                runtime,
                new PhysicalRiskState(safeDirections: new[] { "forward" }),
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            Assert.That(finalTarget.TargetRole, Is.EqualTo(NavigationTargetRole.FinalTarget));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_VerifierRejectionTriggersRuntimeInterventionWithoutExperimentError()
    {
        var root = new GameObject("GuidanceCoordinatorVerifierTest");
        root.SetActive(false);
        try
        {
            var narrationText = CreateText(root.transform, "NarrationText");
            var rejectionRoot = new GameObject("RejectionRoot");
            rejectionRoot.transform.SetParent(root.transform, false);
            var rejectionText = CreateText(rejectionRoot.transform, "RejectionText");
            var narration = root.AddComponent<NarrationPlayer>();
            narration.Configure(narrationText);
            var rejection = root.AddComponent<RejectionPanel>();
            rejection.Configure(rejectionText, rejectionRoot);
            var executor = root.AddComponent<GuidanceExecutor>();
            executor.Configure(narration, null, null, null, rejection, null);
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            ConfigureT2CoordinatorHarness(root, coordinator, task, executor, rejection);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            coordinator.RequestNextGuidance();
            var request = RequestForContext(requests.Contexts[0], "request-verifier-rejected");
            InvokePrivate(coordinator, "HandleRequestBuilt", request);
            RuntimeInterventionInfo intervention = null;
            coordinator.RuntimeIntervention += value => intervention = value;

            var response = ResponseForRequest(request, "E3");
            response.narration = "不应显示";
            response.movement_instruction = "turn_left_slowly";
            response.verifier = new VerifierResult { approved = false, reason = "verifier blocked" };
            coordinator.HandleGuidanceResponse(response);

            Assert.That(intervention, Is.Not.Null);
            Assert.That(intervention.Reason, Is.EqualTo("verifier blocked"));
            Assert.That(narrationText.text, Is.Empty);
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Failed));
            coordinator.CancelActiveGuidance();
            Assert.That(coordinator.RequestPhase, Is.EqualTo(GuidanceRequestPhase.Failed));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceTaskController_ResearcherPauseBlocksArrivalAndIncrementsVersion()
    {
        var go = new GameObject("GuidanceTaskPauseTest");
        try
        {
            var controller = go.AddComponent<GuidanceTaskController>();
            controller.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            controller.StartTask();
            var runningVersion = controller.RuntimeState.stateVersion;

            controller.PauseForResearcher();
            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedByResearcher));
            Assert.That(controller.RuntimeState.stateVersion, Is.GreaterThan(runningVersion));

            controller.HandleArrived("E3");
            Assert.That(controller.RuntimeState.currentExhibitId, Is.EqualTo("E1"));

            controller.ResumeAfterResearcherPause();
            Assert.That(controller.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
            controller.HandleArrived("E3");
            Assert.That(controller.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ExperimentSession_TracksPauseAndExplicitEnd()
    {
        var session = ExperimentSession.Start(ExperimentConditionMatrix.Build(5, 1, 1, "P099"));
        Assert.That(session.IsActive, Is.True);

        session.BeginPause();
        session.BeginPause();
        Assert.That(session.pauseCount, Is.EqualTo(1));
        session.EndPause();
        session.End("researcher_ended");

        Assert.That(session.IsActive, Is.False);
        Assert.That(session.endReason, Is.EqualTo("researcher_ended"));
        Assert.That(session.endedAtUtc, Is.Not.Empty);
        Assert.That(session.totalPausedSeconds, Is.GreaterThanOrEqualTo(0d));
    }

    [Test]
    public void ExperimentSession_GeneratesUniqueIdsAndMonotonicElapsedTime()
    {
        var spec = ExperimentConditionMatrix.Build(5, 1, 1, "P-unique");
        var first = ExperimentSession.Start(spec);
        var elapsedBefore = first.ElapsedMilliseconds;
        System.Threading.Thread.Sleep(5);
        var elapsedAfter = first.ElapsedMilliseconds;
        var second = ExperimentSession.Start(spec);

        Assert.That(first.sessionId, Is.Not.EqualTo(second.sessionId));
        Assert.That(elapsedAfter, Is.GreaterThanOrEqualTo(elapsedBefore));
    }

    [Test]
    public void ExperimentLogger_WritesMetaAndTelemetryFilesWithoutSchemaVersion()
    {
        var root = new GameObject("ExperimentLoggerSchemaTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-schema"));
            logPath = logger.LogPath;
            logger.LogRiskSnapshot(LogTraceContext.Empty, new PhysicalRiskState(
                riskSnapshotId: "risk-schema",
                calibrationId: "calibration-schema",
                physicalSpace: "narrow_room",
                physicalPosition: new Vector2(1.2f, 2.4f),
                physicalForward: Vector2.up,
                clearanceForward: 0.7f,
                clearanceLeft: 1f,
                clearanceRight: 1.3f,
                boundaryRisk: "high",
                resetRisk: "high",
                unsafeDirections: new[] { "forward" },
                safeDirections: new[] { "right" }), null, true, true, new Vector3(1f, 1.65f, 2f), Vector3.forward);
            logger.Flush();

            var eventJson = ReadAllLinesShared(logger.LogPath)[0];
            var telemetryJson = ReadAllLinesShared(logger.TelemetryLogPath)[0];
            var eventProbe = JObject.Parse(eventJson);
            var telemetryProbe = JObject.Parse(telemetryJson);
            Assert.That(eventJson, Does.Not.Contain("\"schema_version\""));
            Assert.That((string)eventProbe["record_type"], Is.EqualTo("event"));
            Assert.That((string)eventProbe["event_name"], Is.EqualTo("session.started"));
            Assert.That((string)eventProbe["category"], Is.EqualTo("Session"));
            Assert.That((string)eventProbe["session_id"], Is.EqualTo(logger.Session.sessionId));
            Assert.That((long)eventProbe["elapsed_ms"], Is.GreaterThanOrEqualTo(0));
            Assert.That(eventProbe["trace"], Is.TypeOf<JObject>());
            Assert.That(eventProbe["data"], Is.TypeOf<JObject>());
            Assert.That(eventProbe.Property("event_type"), Is.Null);
            Assert.That(eventProbe.Property("request_id"), Is.Null);
            Assert.That(telemetryJson, Does.Not.Contain("\"schema_version\""));
            Assert.That((string)telemetryProbe["record_type"], Is.EqualTo("telemetry"));
            Assert.That((string)telemetryProbe["sample_name"], Is.EqualTo("risk.snapshot"));
            Assert.That(telemetryProbe.Property("event_name"), Is.Null);
            Assert.That(telemetryProbe.Property("event_type"), Is.Null);
            Assert.That((bool)telemetryProbe["data"]["tracking_valid"], Is.True);
            Assert.That((bool)telemetryProbe["data"]["calibration_valid"], Is.True);
            Assert.That((float)telemetryProbe["data"]["virtual_position_m"]["y"], Is.EqualTo(1.65f));
            Assert.That(File.Exists(logger.SessionMetaPath), Is.True);
            var meta = File.ReadAllText(logger.SessionMetaPath);
            Assert.That(meta, Does.Not.Contain("\"schema_version\""));
            Assert.That(meta, Does.Contain("\"position_unit\": \"meter\""));
            Assert.That(meta, Does.Contain("\"telemetry_sampling_hz\": 5.0"));
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_EscapesJsonControlCharacters()
    {
        var root = new GameObject("ExperimentLoggerEscapeTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            SetPrivateField(logger, "mirrorToUnityConsole", false);
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-escape"));
            logPath = logger.LogPath;
            var message = "quote=\" slash=\\ tab=\t control=\u0001 newline=\n";
            logger.LogStatus("task_failed", LogTraceContext.Empty, null, null, message);

            var lines = ReadAllEventLines(logger);
            var probe = JsonUtility.FromJson<LogEnvelopeProbe>(lines.Last());
            Assert.That(probe.message, Is.EqualTo(message));
            Assert.That(lines.Last(), Does.Contain("\\t"));
            Assert.That(lines.Last(), Does.Contain("\\u0001"));
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_InvalidPathDoesNotBreakSession()
    {
        var root = new GameObject("ExperimentLoggerInvalidPathTest");
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "\0");
            LogAssert.Expect(LogType.Error, new Regex(@"\[VirtualGuidance\]\[ERROR\]\[storage_write_failed\]\[存储\].*日志写入失败.*LOG_WRITE_001"));

            Assert.DoesNotThrow(() => logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-invalid-path")));
            Assert.That(logger.Session.IsActive, Is.True);
            Assert.That(logger.LogPath, Is.Empty);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_WritesSessionEndedBeforeClosingSession()
    {
        var root = new GameObject("ExperimentLoggerEndSessionTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-log"));
            logPath = logger.LogPath;

            logger.EndSession("completed_auto", LogTraceContext.Empty);

            Assert.That(logger.Session.IsActive, Is.False);
            Assert.That(File.Exists(logPath), Is.True);
            var lines = ReadAllEventLines(logger);
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(lines[0], Does.Contain("\"event_name\":\"session.started\""));
            Assert.That(lines[1], Does.Contain("\"event_name\":\"session.ended\""));
            Assert.That(lines.All(line => !line.Contains("\"event_type\"")), Is.True);
            Assert.That(lines[1], Does.Contain("\"message\":\"completed_auto\""));
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_RecordsRawGuidanceRuntimeInterventionAndExecutedOutcome()
    {
        var root = new GameObject("ExperimentLoggerRuntimeInterventionTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-runtime-log"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");
            var response = new GuidanceResponse
            {
                request_id = "request-runtime-log",
                decision_id = "decision-runtime-log",
                state_version = runtime.stateVersion,
                selected_target = "E5",
                movement_instruction = "move_forward_slowly",
                verifier = new VerifierResult { approved = true, reason = "baseline pass-through" }
            };

            var trace = logger.LogResponse(response, null);
            logger.LogRuntimeIntervention(
                trace,
                response,
                runtime,
                null,
                "unsafe forward",
                RuntimeGuidanceSafetyDecision.HardStop,
                true);
            logger.LogGuidanceExecuted(trace, response, runtime, null);

            var lines = ReadAllEventLines(logger);
            Assert.That(lines.Length, Is.EqualTo(4));
            var received = JObject.Parse(lines[1]);
            var intervention = JObject.Parse(lines[2]);
            var executedRecord = JObject.Parse(lines[3]);
            Assert.That((string)received["event_name"], Is.EqualTo("guidance.received"));
            Assert.That(received["data"]["raw_guidance"], Is.TypeOf<JObject>());
            Assert.That((string)received["data"]["runtime_validation_result"], Is.EqualTo("pending"));
            Assert.That((string)intervention["event_name"], Is.EqualTo("safety.runtime_intervention"));
            Assert.That((bool)intervention["data"]["runtime_intervention"], Is.True);
            Assert.That((string)intervention["data"]["intervention_type"], Is.EqualTo("hard_stop"));
            Assert.That(intervention["data"]["executed_guidance"], Is.Null);
            Assert.That((string)executedRecord["event_name"], Is.EqualTo("guidance.executed"));
            Assert.That((bool)executedRecord["data"]["runtime_intervention"], Is.False);
            Assert.That(executedRecord["data"]["executed_guidance"], Is.TypeOf<JObject>());
            Assert.That(lines.All(line => !line.Contains("\"event_type\"")), Is.True);
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_PreservesActiveTraceAcrossDownstreamEvents()
    {
        var root = new GameObject("ExperimentLoggerTraceContextTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-trace-log"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");
            var request = new GuidanceRequest
            {
                calibration_id = "calibration-trace",
                request_id = "request-trace",
                decision_id = "decision-trace",
                state_version = runtime.stateVersion,
                risk_snapshot_id = "risk-request",
                condition = "ours_full",
                task_id = "T2_main_target",
                current_exhibit = "E1"
            };
            ConfigureSingleCandidate(request, "E3", "navigable_detour");
            var response = ResponseForRequest(request, "E3");

            var trace = logger.LogRequest(request, new PhysicalRiskState(
                riskSnapshotId: "risk-request",
                calibrationId: "calibration-trace"));
            trace = logger.LogResponse(response, null);
            logger.LogGuidanceVerified(trace, runtime, null);
            runtime.AdvanceTo("E3", "E5");
            logger.LogStatus("gain_started", trace, runtime, new PhysicalRiskState(
                riskSnapshotId: "risk-runtime",
                calibrationId: "calibration-trace"), "gain started");

            var lines = ReadAllEventLines(logger);
            Assert.That(lines.Length, Is.EqualTo(5));
            Assert.That(lines[1], Does.Contain("\"event_name\":\"guidance.requested\""));
            Assert.That(lines[2], Does.Contain("\"event_name\":\"guidance.received\""));
            Assert.That(lines[3], Does.Contain("\"event_name\":\"guidance.verified\""));
            Assert.That(lines[4], Does.Contain("\"event_name\":\"rdw.gain_started\""));
            var gainRecord = JObject.Parse(lines[4]);
            Assert.That((string)gainRecord["trace"]["calibration_id"], Is.EqualTo("calibration-trace"));
            Assert.That((string)gainRecord["trace"]["request_id"], Is.EqualTo("request-trace"));
            Assert.That((string)gainRecord["trace"]["decision_id"], Is.EqualTo("decision-trace"));
            Assert.That((string)gainRecord["trace"]["risk_snapshot_id"], Is.EqualTo("risk-runtime"));
            Assert.That((int)gainRecord["trace"]["state_version"], Is.EqualTo(request.state_version));
            Assert.That(gainRecord["data"]["raw_guidance"], Is.Null);
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_FlushesPreSessionCalibrationEvents()
    {
        var root = new GameObject("ExperimentLoggerCalibrationQueueTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.LogCalibrationStarted();
            logger.LogCalibrationCompleted("calibration-before-session");

            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-calibration-log"));
            logPath = logger.LogPath;
            logger.LogRiskSnapshot(LogTraceContext.Empty, new PhysicalRiskState(
                riskSnapshotId: "risk-after-calibration",
                calibrationId: "calibration-before-session",
                boundaryRisk: "high"), null, true, true, Vector3.zero, Vector3.forward);

            var lines = ReadAllEventLines(logger);
            Assert.That(lines.Length, Is.EqualTo(4));
            Assert.That(lines[1], Does.Contain("\"event_name\":\"tracking.calibration_started\""));
            Assert.That(lines[2], Does.Contain("\"event_name\":\"tracking.calibration_completed\""));
            Assert.That((string)JObject.Parse(lines[2])["trace"]["calibration_id"], Is.EqualTo("calibration-before-session"));
            Assert.That((string)JObject.Parse(lines[3])["trace"]["calibration_id"], Is.EqualTo("calibration-before-session"));
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_MirrorsTraceIdsAndSeverityToUnityConsole()
    {
        var root = new GameObject("ExperimentLoggerUnityConsoleTest");
        string logPath = null;
        ExperimentLogger logger = null;
        var messages = new List<string>();
        var levels = new List<LogType>();
        Application.LogCallback callback = (condition, _, type) =>
        {
            if (!condition.Contains("[VirtualGuidance]")) return;
            messages.Add(condition);
            levels.Add(type);
        };
        Application.logMessageReceived += callback;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-console-log"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");
            var risk = new PhysicalRiskState(
                riskSnapshotId: "risk-console",
                calibrationId: "calibration-console",
                boundaryRisk: "safe",
                safeDirections: new[] { "forward" });
            var request = new GuidanceRequest
            {
                calibration_id = risk.calibration_id,
                request_id = "request-console",
                decision_id = "decision-console",
                state_version = runtime.stateVersion,
                risk_snapshot_id = risk.risk_snapshot_id,
                condition = "ours_full",
                task_id = runtime.taskId,
                current_exhibit = runtime.currentExhibitId
            };
            ConfigureSingleCandidate(request, runtime.nextTargetId, "route_subgoal");
            var response = ResponseForRequest(request, "E3");

            var trace = logger.LogRequest(request, risk);
            logger.LogRuntimeIntervention(
                trace,
                response,
                runtime,
                risk,
                "unsafe forward",
                RuntimeGuidanceSafetyDecision.HardStop,
                false);
            LogAssert.Expect(
                LogType.Error,
                new Regex(@"\[VirtualGuidance\]\[ERROR\]\[task_failed\]\[任务\].*任务执行失败.*request_id=request-console.*decision_id=decision-console.*risk_snapshot_id=risk-console"));
            logger.LogStatus("task_failed", trace, runtime, risk, "backend unavailable");

            var requestIndex = messages.FindIndex(message => message.Contains("[guidance_requested]"));
            var interventionIndex = messages.FindIndex(message => message.Contains("[runtime_intervention]"));
            var failureIndex = messages.FindIndex(message => message.Contains("[task_failed]"));
            Assert.That(requestIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(interventionIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(failureIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(messages[requestIndex], Does.Contain("[引导] 已发送引导请求"));
            Assert.That(messages[requestIndex], Does.Contain("event_name=guidance.requested"));
            Assert.That(messages[requestIndex], Does.Contain("session_id=" + logger.Session.sessionId));
            Assert.That(messages[requestIndex], Does.Contain("calibration_id=calibration-console"));
            Assert.That(messages[requestIndex], Does.Contain("request_id=request-console"));
            Assert.That(messages[requestIndex], Does.Contain("decision_id=decision-console"));
            Assert.That(messages[requestIndex], Does.Contain($"state_version={runtime.stateVersion}"));
            Assert.That(messages[requestIndex], Does.Contain("risk_snapshot_id=risk-console"));
            Assert.That(messages[requestIndex], Does.Not.Contain("detail=guidance requested"));
            Assert.That(messages[interventionIndex], Does.Contain("[安全] 安全系统已介入"));
            Assert.That(messages[interventionIndex], Does.Contain("detail=unsafe forward"));
            Assert.That(messages[failureIndex], Does.Contain("[任务] 任务执行失败"));
            Assert.That(messages[failureIndex], Does.Contain("detail=backend unavailable"));
            Assert.That(levels[requestIndex], Is.EqualTo(LogType.Log));
            Assert.That(levels[interventionIndex], Is.EqualTo(LogType.Warning));
            Assert.That(levels[failureIndex], Is.EqualTo(LogType.Error));
        }
        finally
        {
            Application.logMessageReceived -= callback;
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentLogger_RoutesRiskSnapshotsToTelemetryAndDeduplicatesEvents()
    {
        var root = new GameObject("ExperimentLoggerRiskConsoleTest");
        string logPath = null;
        ExperimentLogger logger = null;
        var riskConsoleCount = 0;
        Application.LogCallback callback = (condition, _, _) =>
        {
            if (condition.Contains("[VirtualGuidance]") && condition.Contains("[risk_changed]"))
            {
                riskConsoleCount++;
            }
        };
        Application.logMessageReceived += callback;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-risk-console"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");

            logger.LogRiskSnapshot(LogTraceContext.Empty, new PhysicalRiskState(
                riskSnapshotId: "risk-1",
                boundaryRisk: "safe",
                safeDirections: new[] { "forward" }), runtime, true, false, Vector3.zero, Vector3.forward);
            logger.LogRiskSnapshot(LogTraceContext.Empty, new PhysicalRiskState(
                riskSnapshotId: "risk-2",
                boundaryRisk: "safe",
                safeDirections: new[] { "forward" }), runtime, true, false, Vector3.zero, Vector3.forward);
            logger.LogRiskSnapshot(LogTraceContext.Empty, new PhysicalRiskState(
                riskSnapshotId: "risk-3",
                boundaryRisk: "critical",
                unsafeDirections: new[] { "forward" },
                resetRequired: true), runtime, true, false, Vector3.zero, Vector3.forward);

            Assert.That(riskConsoleCount, Is.EqualTo(2));
            var lines = ReadAllEventLines(logger);
            Assert.That(lines.Count(line => line.Contains("\"event_name\":\"safety.risk_changed\"")), Is.EqualTo(2));
            Assert.That(lines.Any(line => line.Contains("\"risk_snapshot_id\":\"risk-2\"")), Is.False);
            var telemetryLines = ReadAllLinesShared(logger.TelemetryLogPath);
            Assert.That(telemetryLines.Length, Is.EqualTo(3));
            Assert.That(telemetryLines.Any(line => line.Contains("\"risk_snapshot_id\":\"risk-2\"")), Is.True);
        }
        finally
        {
            Application.logMessageReceived -= callback;
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_ProducesRequiredTraceabilityEvents()
    {
        var root = new GameObject("ExperimentRequiredEventsTest");
        string logPath = null;
        ExperimentLogger logger = null;
        try
        {
            logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-required-events"));
            logPath = logger.LogPath;
            var request = new GuidanceRequest
            {
                calibration_id = "calibration-required-events",
                request_id = "request-required-events",
                decision_id = "decision-required-events",
                state_version = 1,
                risk_snapshot_id = "risk-required-events",
                condition = "ours_full",
                task_id = "T2_main_target",
                current_exhibit = "E1"
            };
            ConfigureSingleCandidate(request, "E2", "navigable_detour");
            var response = ResponseForRequest(request, "E2");
            var risk = new PhysicalRiskState(
                riskSnapshotId: request.risk_snapshot_id,
                calibrationId: request.calibration_id,
                boundaryRisk: "safe",
                safeDirections: new[] { "forward" });
            logger.LogCalibrationStarted();
            logger.LogCalibrationCompleted(request.calibration_id);
            logger.LogRequest(request, risk);
            var trace = logger.LogResponse(response, risk);

            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "experimentLogger", logger);
            SetPrivateField(dashboard, "activeGuidanceTrace", trace);
            InvokePrivate(dashboard, "HandleRequestPhaseChanged", GuidanceRequestPhase.Verified);
            InvokePrivate(dashboard, "HandleRequestPhaseChanged", GuidanceRequestPhase.Cancelled);
            InvokePrivate(dashboard, "HandleGuidanceRejected", "protocol rejected");
            InvokePrivate(dashboard, "HandleGuidanceExpired", "state changed");
            InvokePrivate(dashboard, "HandleGuidanceExecuted", response);
            InvokePrivate(dashboard, "HandleRuntimeIntervention", new RuntimeInterventionInfo(response, "unsafe forward"));
            InvokePrivate(dashboard, "HandleRiskStateChanged", risk);
            logger.LogStatus("tracking_lost", trace, null, risk, "tracking lost");
            logger.LogStatus("tracking_recovered", trace, null, risk, "tracking recovered");

            foreach (var gainEventType in new[]
                     {
                         RotationGainEventType.TurnRequested,
                         RotationGainEventType.UserTurnStarted,
                         RotationGainEventType.GainStarted,
                         RotationGainEventType.GainCompleted,
                         RotationGainEventType.GainCancelled
                     })
            {
                InvokePrivate(
                    dashboard,
                    "HandleRotationGainEvent",
                    new RotationGainEventInfo(
                        gainEventType,
                        RotationGainController.GainState.GainActive,
                        1,
                        string.Empty,
                        10f,
                        1.5f));
            }

            var detour = new GuidanceTarget("E2", NavigationTargetRole.NavigableDetour);
            SetPrivateField(dashboard, "activeGuidanceTrace", trace);
            InvokePrivate(dashboard, "HandleGuidanceTargetSelected", detour);
            InvokePrivate(dashboard, "HandleTargetReached", detour);
            InvokePrivate(dashboard, "HandleDetourCompleted", detour);
            InvokePrivate(dashboard, "HandleResetStarted", new ResetEventInfo("left", Vector2.left, "critical risk"));
            InvokePrivate(dashboard, "HandleResetCompleted", new ResetEventInfo("left", Vector2.left, "critical risk"));
            InvokePrivate(dashboard, "HandleResetStateChanged", ResetLifecycleState.Cancelled);
            InvokePrivate(dashboard, "HandleTaskCompleted", new GuidanceRuntimeState());
            logger.LogCalibrationInvalidated(request.calibration_id, "space changed");
            LogAssert.Expect(LogType.Error, new Regex(@"\[VirtualGuidance\]\[ERROR\]\[task_failed\]\[任务\].*任务执行失败.*detail=backend unavailable"));
            InvokePrivate(dashboard, "EnterError", "backend unavailable");

            var lines = ReadAllEventLines(logger);
            foreach (var requiredEventName in new[]
            {
                "tracking.calibration_started",
                "tracking.calibration_completed",
                "tracking.calibration_invalidated",
                "tracking.lost",
                "tracking.recovered",
                "guidance.requested",
                "guidance.received",
                "guidance.verified",
                "guidance.rejected",
                "guidance.expired",
                "guidance.cancelled",
                "guidance.executed",
                "safety.risk_changed",
                "safety.runtime_intervention",
                "rdw.turn_requested",
                "rdw.user_turn_started",
                "rdw.gain_started",
                "rdw.gain_completed",
                "rdw.gain_aborted",
                "task.target_reserved",
                "task.target_completed",
                "task.detour_selected",
                "task.detour_completed",
                "safety.reset_started",
                "safety.reset_completed",
                "safety.reset_cancelled",
                "task.completed",
                "task.failed"
            })
            {
                Assert.That(
                    lines.Any(line => line.Contains($"\"event_name\":\"{requiredEventName}\"")),
                    Is.True,
                    requiredEventName);
            }
            var targetRecord = JObject.Parse(
                lines.Single(line => line.Contains("\"event_name\":\"task.target_completed\"")));
            Assert.That((string)targetRecord["data"]["selected_target"], Is.EqualTo("E2"));
            Assert.That((string)targetRecord["data"]["target_role"], Is.EqualTo("navigable_detour"));
            Assert.That((string)targetRecord["trace"]["request_id"], Is.EqualTo("request-required-events"));
            Assert.That(lines.All(line => !line.Contains("\"event_type\"")), Is.True);
        }
        finally
        {
            CloseLoggerAndDeleteSession(logger, logPath);
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ParticipantUiController_SafetyPanelExcludesGuidancePanel()
    {
        var root = new GameObject("ParticipantUiControllerTest");
        try
        {
            var guidanceRoot = new GameObject("GuidancePanel");
            guidanceRoot.transform.SetParent(root.transform, false);
            var safetyRoot = new GameObject("SafetyPanel");
            safetyRoot.transform.SetParent(root.transform, false);
            var guidanceGroup = guidanceRoot.AddComponent<CanvasGroup>();
            var safetyGroup = safetyRoot.AddComponent<CanvasGroup>();
            var safetyText = CreateText(safetyRoot.transform, "SafetyText");
            var controller = root.AddComponent<ParticipantUiController>();
            controller.Configure(guidanceGroup, safetyGroup, null, null, null, safetyText);

            controller.ShowGuidance();
            Assert.That(guidanceGroup.alpha, Is.EqualTo(1f));
            Assert.That(safetyGroup.alpha, Is.EqualTo(0f));

            controller.ShowCriticalSafety();
            Assert.That(guidanceGroup.alpha, Is.EqualTo(0f));
            Assert.That(safetyGroup.alpha, Is.EqualTo(1f));
            Assert.That(safetyText.text, Is.EqualTo("请立即停止移动，等待研究员指引"));
            Assert.That(controller.SafetyVisible, Is.True);

            controller.ShowRuntimeIntervention();
            Assert.That(safetyText.text, Is.EqualTo("请停止移动，等待安全引导"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceDtos_KeepBackendJsonContractFieldNames()
    {
        AssertPublicFieldNames<GuidanceRequest>(
            "task_definition_version",
            "guidance_schema_version",
            "candidate_policy_version",
            "risk_policy_version",
            "session_id",
            "calibration_id",
            "request_id",
            "decision_id",
            "candidate_set_id",
            "state_version",
            "risk_snapshot_id",
            "condition",
            "task_id",
            "physical_space_id",
            "current_exhibit",
            "risk_snapshot",
            "user_physical_position",
            "user_physical_forward",
            "participant_id",
            "user_task",
            "final_target",
            "preferred_task_target",
            "candidate_ids",
            "model_candidate_ids",
            "allowed_candidate_ids",
            "semantic_candidates",
            "user_virtual_position",
            "user_virtual_forward");

        AssertPublicFieldNames<GuidanceResponse>(
            "task_definition_version",
            "guidance_schema_version",
            "candidate_policy_version",
            "risk_policy_version",
            "session_id",
            "calibration_id",
            "request_id",
            "decision_id",
            "candidate_set_id",
            "state_version",
            "risk_snapshot_id",
            "risk_revision",
            "target_role",
            "selected_target",
            "route_decision",
            "narration",
            "gaze_cue",
            "gaze_target_id",
            "movement_instruction",
            "physical_intent",
            "verifier");

        AssertPublicFieldNames<VerifierResult>(
            "approved",
            "reason");
    }

    private static RuntimeGuidanceSafetyInput CreateRuntimeSafetyInput(
        PhysicalRiskState risk,
        string direction,
        float clearanceMeters,
        int unsafeSamples,
        bool safeCandidateAvailable,
        bool plannedDirectionCritical = false,
        bool trackingValid = true,
        bool calibrationValid = true,
        bool physicalSpaceMatches = true,
        bool plannedDirectionAvailable = true)
    {
        return new RuntimeGuidanceSafetyInput(
            "decision-runtime-safety",
            "E3",
            "move_forward_slowly",
            risk,
            trackingValid,
            calibrationValid,
            physicalSpaceMatches,
            direction,
            clearanceMeters,
            plannedDirectionAvailable,
            plannedDirectionCritical,
            ResetLifecycleState.Inactive,
            unsafeSamples,
            safeCandidateAvailable);
    }

    [Test]
    public void NavigationTargetManager_RelatedExhibitsDoNotAdvanceMembershipRevision()
    {
        var root = new GameObject("CandidateMembershipMetadataTest");
        try
        {
            var targetObject = new GameObject("MembershipOnlyTarget");
            targetObject.transform.SetParent(root.transform, false);
            var metadata = targetObject.AddComponent<ExhibitMetadata>();
            metadata.Configure(
                "MembershipOnlyTarget",
                "Membership",
                null,
                string.Empty,
                "navigable_detour",
                new[] { "E1" },
                targetObject.transform,
                targetObject.transform);
            var manager = root.AddComponent<NavigationTargetManager>();
            manager.RebuildIndex();
            var membershipRevision = manager.RegistryRevision;

            metadata.Configure(
                "MembershipOnlyTarget",
                "Membership changed",
                null,
                string.Empty,
                "navigable_detour",
                new[] { "E2", "E5" },
                targetObject.transform,
                targetObject.transform);
            manager.RebuildIndex();

            Assert.That(manager.RegistryRevision, Is.EqualTo(membershipRevision));
            Assert.That(manager.GetRelatedTargetIds("MembershipOnlyTarget"), Is.EqualTo(new[] { "E2", "E5" }));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void SemanticTargetSelector_ReservationUsesRiskRevisionInsteadOfSampleId()
    {
        var root = new GameObject("CandidateReservationRiskRevisionTest");
        try
        {
            var virtualUser = new GameObject("ReservationRiskUser");
            virtualUser.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(virtualUser.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            AlignVirtualUserToT2Route(virtualUser.transform, targets);
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E5", "E5");
            var policy = ExperimentConditionMatrix.GetPolicy("ours_full");
            var initialRisk = new PhysicalRiskState(
                riskSnapshotId: "risk-sample-1",
                riskRevision: 4,
                calibrationId: "calibration-current",
                safeDirections: new[] { "forward", "left", "right" });
            var candidateSet = selector.BuildCandidateSet(runtime, initialRisk, policy);

            Assert.That(runtime.TryReserveCandidateSet(
                candidateSet,
                "candidate-set-risk",
                "request-risk",
                "decision-risk",
                policy.Condition,
                initialRisk,
                out var reservation), Is.True);

            var sameRevisionNewSample = new PhysicalRiskState(
                riskSnapshotId: "risk-sample-2",
                riskRevision: 4,
                calibrationId: "calibration-current",
                safeDirections: new[] { "forward", "left", "right" });
            Assert.That(
                selector.IsReservationCurrent(reservation, runtime, sameRevisionNewSample, policy),
                Is.True);

            var changedRevision = new PhysicalRiskState(
                riskSnapshotId: "risk-sample-3",
                riskRevision: 5,
                calibrationId: "calibration-current",
                safeDirections: new[] { "forward", "left", "right" });
            Assert.That(
                selector.IsReservationCurrent(reservation, runtime, changedRevision, policy),
                Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static TMP_Text CreateText(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<TextMeshProUGUI>();
    }

    private static void AssertPublicFieldNames<T>(params string[] expectedNames)
    {
        var actualNames = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .OrderBy(field => field.MetadataToken)
            .Select(field => field.Name)
            .ToArray();

        Assert.That(actualNames, Is.EqualTo(expectedNames), typeof(T).Name);
    }

    private static void CreateCalibrationRig(
        GameObject root,
        out UserPoseTracker pose,
        out PhysicalSpaceManager space,
        out PhysicalSpaceCalibration calibration)
    {
        pose = root.AddComponent<UserPoseTracker>();
        space = root.AddComponent<PhysicalSpaceManager>();
        space.LoadSpaces();
        Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
        calibration = root.AddComponent<PhysicalSpaceCalibration>();
        calibration.Configure(space, pose);
    }

    private static float YawOf(Vector2 forward)
    {
        return Mathf.Atan2(forward.x, forward.y) * Mathf.Rad2Deg;
    }

    private static ExhibitMetadata CreateExhibit(
        Transform parent,
        string id,
        Vector3 position,
        string semanticRole,
        string[] related)
    {
        var exhibitObject = new GameObject(id);
        exhibitObject.transform.SetParent(parent, false);
        exhibitObject.transform.position = position;
        var metadata = exhibitObject.AddComponent<ExhibitMetadata>();
        metadata.Configure(
            id,
            id,
            null,
            string.Empty,
            semanticRole,
            related,
            exhibitObject.transform,
            exhibitObject.transform);
        return metadata;
    }

    private static GuidanceRequest RequestForContext(GuidanceRequestContext context, string requestId)
    {
        var preferredTarget = !string.IsNullOrWhiteSpace(context.preferred_task_target)
            ? context.preferred_task_target
            : "E5";
        var candidateIds = context.candidate_ids != null && context.candidate_ids.Length > 0
            ? context.candidate_ids
            : new[] { preferredTarget };
        var modelCandidateIds = context.model_candidate_ids != null && context.model_candidate_ids.Length > 0
            ? context.model_candidate_ids
            : candidateIds;
        var semanticCandidates = context.semantic_candidates != null && context.semantic_candidates.Length > 0
            ? context.semantic_candidates
            : new[]
            {
                new GuidanceSemanticCandidate
                {
                    id = preferredTarget,
                    target_role = "final_target",
                    relative_direction = "forward"
                }
            };
        return new GuidanceRequest
        {
            session_id = context.session_id,
            calibration_id = context.calibration_id,
            request_id = requestId,
            decision_id = context.decision_id,
            candidate_set_id = string.IsNullOrWhiteSpace(context.candidate_set_id)
                ? "candidate-set-test"
                : context.candidate_set_id,
            state_version = context.state_version,
            risk_snapshot_id = context.risk_snapshot_id,
            risk_snapshot = new GuidanceRiskSnapshot { risk_revision = 1 },
            current_exhibit = context.current_exhibit,
            final_target = string.IsNullOrWhiteSpace(context.final_target) ? preferredTarget : context.final_target,
            preferred_task_target = preferredTarget,
            candidate_ids = candidateIds,
            model_candidate_ids = modelCandidateIds,
            allowed_candidate_ids = context.allowed_candidate_ids ?? new string[0],
            semantic_candidates = semanticCandidates,
            physical_space_id = "narrow_room"
        };
    }

    private static GuidanceResponse ResponseForRequest(GuidanceRequest request, string selectedTarget)
    {
        var direction = CandidateDirectionForRequest(request, selectedTarget);
        return new GuidanceResponse
        {
            session_id = request.session_id,
            calibration_id = request.calibration_id,
            request_id = request.request_id,
            decision_id = request.decision_id,
            candidate_set_id = request.candidate_set_id,
            state_version = request.state_version,
            risk_snapshot_id = request.risk_snapshot_id,
            risk_revision = request.risk_snapshot != null ? request.risk_snapshot.risk_revision : 1,
            target_role = CandidateRoleForRequest(request, selectedTarget),
            selected_target = selectedTarget,
            movement_instruction = direction == "left"
                ? "turn_left_slowly"
                : direction == "right" ? "turn_right_slowly" : "move_forward_slowly",
            verifier = new VerifierResult { approved = true, reason = "ok" }
        };
    }

    private static string CandidateDirectionForRequest(GuidanceRequest request, string selectedTarget)
    {
        if (request != null && request.semantic_candidates != null)
        {
            foreach (var candidate in request.semantic_candidates)
            {
                if (candidate != null && candidate.id == selectedTarget)
                {
                    return candidate.relative_direction;
                }
            }
        }
        return selectedTarget == "E3" ? "left" : selectedTarget == "E4" ? "right" : "forward";
    }

    private static string[] SafeDirectionsExcept(string unsafeDirection)
    {
        return new[] { "forward", "left", "right" }
            .Where(direction => direction != unsafeDirection)
            .ToArray();
    }

    private static void ConfigureT2CoordinatorHarness(
        GameObject root,
        GuidanceCoordinator coordinator,
        GuidanceTaskController task,
        GuidanceExecutor executor = null,
        RejectionPanel rejection = null)
    {
        var virtualUser = new GameObject("T2HarnessUser");
        virtualUser.transform.SetParent(root.transform, false);
        var pose = root.AddComponent<UserPoseTracker>();
        pose.ConfigureVirtualUserRoot(virtualUser.transform);
        var space = root.AddComponent<PhysicalSpaceManager>();
        space.LoadSpaces();
        space.SetPhysicalSpace("wide_room");
        var calibration = root.AddComponent<PhysicalSpaceCalibration>();
        calibration.Configure(space, pose);
        calibration.SetCalibrationPoint(new Vector2(2.5f, 2.5f), 0f);
        calibration.UpdateRawTrackingPose(new Vector2(2.5f, 2.5f), 0f);
        calibration.CalibrateCurrentPose();
        var risk = root.AddComponent<PhysicalRiskEvaluator>();
        risk.Configure(space, pose, calibration);

        root.SetActive(true);
        var targets = root.AddComponent<NavigationTargetManager>();
        targets.Configure(pose);
        targets.RebuildIndex();
        AlignVirtualUserToT2Route(virtualUser.transform, targets);
        var selector = root.AddComponent<SemanticTargetSelector>();
        selector.Configure(targets);
        var validator = root.AddComponent<LocalGuidanceValidator>();
        validator.Configure(targets, risk);
        coordinator.Configure(null, executor, task, targets, selector, null, risk, validator, null, null, rejection);
    }

    private static void AlignVirtualUserToT2Route(Transform virtualUser, NavigationTargetManager targets)
    {
        if (virtualUser == null || targets == null ||
            !targets.TryGetTarget("E1", out var start) || !targets.TryGetTarget("E5", out var finalTarget))
        {
            return;
        }

        var startPosition = start.ViewPoint != null ? start.ViewPoint.position : start.transform.position;
        var finalPosition = finalTarget.ViewPoint != null
            ? finalTarget.ViewPoint.position
            : finalTarget.transform.position;
        virtualUser.position = startPosition;
        var forward = Vector3.ProjectOnPlane(finalPosition - startPosition, Vector3.up);
        if (forward.sqrMagnitude > 0.0001f)
        {
            virtualUser.forward = forward.normalized;
        }
    }

    private static void ConfigureSingleCandidate(
        GuidanceRequest request,
        string targetId,
        string targetRole)
    {
        request.candidate_set_id = string.IsNullOrWhiteSpace(request.candidate_set_id)
            ? $"candidate-set-{request.request_id ?? "test"}"
            : request.candidate_set_id;
        request.final_target = "E5";
        request.preferred_task_target = targetId;
        request.candidate_ids = targetId == "E5" ? new[] { "E5" } : new[] { "E5", targetId };
        request.model_candidate_ids = new[] { targetId };
        request.allowed_candidate_ids = new[] { targetId };
        request.semantic_candidates = new[]
        {
            new GuidanceSemanticCandidate
            {
                id = targetId,
                target_role = targetRole,
                relative_direction = targetId == "E3" ? "left" : targetId == "E4" ? "right" : "forward"
            }
        };
    }

    private static void ConfigureSingleCandidateContext(
        GuidanceRequestContext context,
        string targetId,
        string targetRole)
    {
        context.candidate_set_id = "candidate-set-context-test";
        context.final_target = "E5";
        context.preferred_task_target = targetId;
        context.candidate_ids = targetId == "E5" ? new[] { "E5" } : new[] { "E5", targetId };
        context.model_candidate_ids = new[] { targetId };
        context.allowed_candidate_ids = new[] { targetId };
        context.semantic_candidates = new[]
        {
            new GuidanceSemanticCandidate
            {
                id = targetId,
                target_role = targetRole,
                relative_direction = targetId == "E3" ? "left" : targetId == "E4" ? "right" : "forward"
            }
        };
    }

    private static string CandidateRoleForRequest(GuidanceRequest request, string selectedTarget)
    {
        if (request != null && request.semantic_candidates != null)
        {
            foreach (var candidate in request.semantic_candidates)
            {
                if (candidate != null && candidate.id == selectedTarget)
                {
                    return candidate.target_role;
                }
            }
        }
        return selectedTarget == "E5" ? "final_target" : "navigable_detour";
    }

    private static string[] ReadAllEventLines(ExperimentLogger logger)
    {
        logger.Flush();
        Assert.That(logger.LogPath, Is.Not.Empty);
        Assert.That(File.Exists(logger.LogPath), Is.True);
        return ReadAllLinesShared(logger.LogPath);
    }

    private static string[] ReadAllLinesShared(string path)
    {
        var lines = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
        }

        return lines.ToArray();
    }

    private static void CloseLoggerAndDeleteSession(ExperimentLogger logger, string logPath)
    {
        if (logger != null)
        {
            logger.EndSession("test_cleanup", LogTraceContext.Empty);
        }

        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException exception)
            {
                Assert.Fail("DeleteLogSession failed: " + exception);
            }
        }
    }

    [System.Serializable]
    private sealed class LogEnvelopeProbe
    {
        /// <summary>相对 Session 开始的毫秒时间。</summary>
        public long elapsed_ms;
        /// <summary>event 或 telemetry。</summary>
        public string record_type;
        /// <summary>固定日志分类。</summary>
        public string category;
        /// <summary>稳定的 domain.action 事件名。</summary>
        public string event_name;
        /// <summary>供人阅读的消息。</summary>
        public string message;
        /// <summary>当前实验 Session ID。</summary>
        public string session_id;
    }

    [Test]
    public void AgentClient_SerializesNestedBoundaryPointsForBackendTransport()
    {
        var request = new GuidanceRequest
        {
            risk_snapshot = new GuidanceRiskSnapshot
            {
                boundary_context = new GuidanceRiskBoundaryContext
                {
                    points = new[]
                    {
                        new[] { 0f, 0f },
                        new[] { 2f, 0f }
                    }
                }
            }
        };

        var json = AgentClient.SerializeGuidanceRequest(request);
        var points = (JArray)JObject.Parse(json)["risk_snapshot"]["boundary_context"]["points"];

        Assert.That(points.Count, Is.EqualTo(2));
        Assert.That(points[0], Is.TypeOf<JArray>());
        Assert.That(points[1], Is.TypeOf<JArray>());
        Assert.That((float)points[0][0], Is.EqualTo(0f));
        Assert.That((float)points[1][0], Is.EqualTo(2f));
    }

    [Test]
    public void AgentClient_FormatsBackendValidationDetailsWithoutUnboundedBody()
    {
        var detail = new string('x', 700);
        var formatted = AgentClient.FormatGuidanceRequestFailure(
            "HTTP/1.1 422 Unprocessable Entity",
            "{\"detail\":\"" + detail + "\"}");

        Assert.That(formatted, Does.Contain("HTTP/1.1 422 Unprocessable Entity"));
        Assert.That(formatted, Does.Contain("backend detail:"));
        Assert.That(formatted.Length, Is.LessThan(900));
        Assert.That(formatted, Does.EndWith("..."));
    }

    [Test]
    public void AgentClient_FormatsNonJsonBackendErrorBody()
    {
        var formatted = AgentClient.FormatGuidanceRequestFailure(
            "HTTP/1.1 422 Unprocessable Entity",
            "plain backend failure\r\nline two");

        Assert.That(formatted, Does.Contain("plain backend failure line two"));
        Assert.That(formatted, Does.Not.Contain("\r"));
        Assert.That(formatted, Does.Not.Contain("\n"));
    }

    private sealed class RecordingGuidanceRequestClient : IGuidanceRequestClient
    {
        public readonly System.Collections.Generic.List<GuidanceRequestContext> Contexts =
            new System.Collections.Generic.List<GuidanceRequestContext>();

        public bool RequestInFlight { get; set; }
        public int CancelCount { get; private set; }

        public void RequestGuidance(GuidanceRequestContext context)
        {
            Contexts.Add(context);
        }

        public void CancelPendingRequest()
        {
            CancelCount++;
            RequestInFlight = false;
        }
    }

    private static GuidanceTaskController CreateRouteTask(GameObject root)
    {
        var task = root.AddComponent<GuidanceTaskController>();
        task.ConfigureTask(new GuidanceTaskDefinition
        {
            startExhibitId = "E1",
            finalTargetId = "E5",
            routeTargetIds = new[] { "E1", "E3", "E5" },
            maxConsecutiveDetours = 2
        });
        task.StartTask();
        return task;
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, fieldName);
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, fieldName);
        return (T)field.GetValue(target);
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        InvokePrivateWithResult(target, methodName, args);
    }

    private static object InvokePrivateWithResult(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, methodName);
        return method.Invoke(target, args);
    }
}
