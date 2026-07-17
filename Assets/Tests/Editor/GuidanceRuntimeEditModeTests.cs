using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

            InvokePrivate(dashboard, "ResumeSessionWithRisk", new PhysicalRiskState
            {
                boundary_risk = "critical",
                reset_risk = "critical",
                reset_required = true,
                safe_directions = new string[0]
            });

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(dashboard.CompletionSecondsRemaining, Is.EqualTo(6f));

            InvokePrivate(dashboard, "ResumeSessionWithRisk", new PhysicalRiskState
            {
                boundary_risk = "low",
                reset_risk = "low",
                reset_required = false,
                safe_directions = new[] { "forward" }
            });

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
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            var replanningCount = 0;
            coordinator.Replanning += _ => replanningCount++;

            var pose = root.AddComponent<UserPoseTracker>();
            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, null, null, pose);
            reset.PauseForReset("critical risk", new PhysicalRiskState
            {
                clearance_forward = 1.5f,
                clearance_left = 0.4f,
                clearance_right = 0.3f,
                reset_required = true
            });
            pose.SetSimulatedPhysicalPose(new Vector2(2.5f, 2.5f), 0f);
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("wide_room"), Is.True);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose);
            Assert.That(risk.CurrentRiskState.boundary_risk, Is.EqualTo("low"));
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "guidanceCoordinator", coordinator);
            SetPrivateField(dashboard, "pauseResetController", reset);
            SetPrivateField(dashboard, "riskEvaluator", risk);
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
            Assert.That(requests.Contexts[0].semantic_next_target, Is.EqualTo("E5"));
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
            var validator = go.AddComponent<LocalGuidanceValidator>();
            var runtimeState = new GuidanceRuntimeState();
            runtimeState.Begin("T2_main_target", "E1", "E3", "E5");
            var originalVersion = runtimeState.stateVersion;

            var riskState = new PhysicalRiskState
            {
                physical_space = "narrow_room",
                reset_required = false,
                unsafe_directions = new[] { "forward" },
                safe_directions = new[] { "left", "right" }
            };
            var request = new GuidanceRequest
            {
                request_id = "request-validator",
                decision_id = "decision-validator",
                state_version = originalVersion,
                physical_space_id = "narrow_room",
                current_exhibit = "E1",
                semantic_next_target = "E3"
            };
            var snapshot = GuidanceRequestSnapshot.Capture(request, riskState, runtimeState);

            var unsafeResponse = new GuidanceResponse
            {
                selected_target = "E3",
                movement_instruction = "move_forward_slowly"
            };
            Assert.That(validator.Validate(unsafeResponse, snapshot, runtimeState, riskState, out var unsafeReason), Is.False);
            Assert.That(unsafeReason, Does.Contain("unsafe forward"));

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
    public void RotationGainController_FollowsCompleteLifecycleAndEmitsRequiredEvents()
    {
        var root = new GameObject("RotationGainLifecycleTest");
        var poseSource = new GameObject("RotationGainPoseSource");
        var rotationRoot = new GameObject("RotationGainVirtualRoot");
        try
        {
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

            gain.ProcessTurnSample(true, 10f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.GainActive));
            Assert.That(gain.AccumulatedPhysicalYawDegrees, Is.EqualTo(10f).Within(0.001f));
            Assert.That(gain.AppliedExtraYawDegrees, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(rotationRoot.transform.eulerAngles.y, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(poseSource.transform.eulerAngles.y, Is.Zero.Within(0.001f));
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
    public void RotationGainController_AbortsForReverseStopTimeoutAndGainLimit()
    {
        var root = new GameObject("RotationGainAbortRulesTest");
        var rotationRoot = new GameObject("RotationGainAbortRoot");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(rotationRoot.transform), Is.True);
            gain.ConfigureLimits(0.15f, 8f, 30f, 0.5f);
            gain.SetGainEnabled(true);

            gain.BeginPendingTurn(1);
            gain.ProcessTurnSample(true, -5f, 0.1f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("reverse_turn"));

            gain.BeginPendingTurn(1);
            gain.ProcessTurnSample(true, 5f, 0.1f);
            gain.ProcessTurnSample(false, 0f, 0.5f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("turn_stopped_timeout"));

            gain.BeginPendingTurn(1);
            gain.ProcessTurnSample(true, 60f, 0.1f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("gain_limit_exceeded"));
            Assert.That(gain.AppliedExtraYawDegrees, Is.Zero.Within(0.001f));
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
            Assert.That(pose.ConfigureVirtualRotationRoot(rotationRoot.transform), Is.True);

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
            gain.ProcessTurnSample(true, 10f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.GainActive));
            Assert.That(cameraObject.transform.rotation, Is.EqualTo(Quaternion.identity));
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
            executor.Configure(narration, gaze, movement, null, null, rejection, null);
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
    public void RDWController_CriticalRiskPausesAndCancelsGainImmediately()
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
            var rdw = root.AddComponent<RDWController>();
            rdw.Configure(null, pauseReset);

            rdw.ApplyRiskState(new PhysicalRiskState
            {
                boundary_risk = "critical",
                reset_risk = "critical",
                reset_required = true,
                unsafe_directions = new[] { "forward", "left", "right" }
            });

            Assert.That(pauseReset.IsPaused, Is.True);
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
            Assert.That(calibration.CalibrateCurrentPose(), Is.True);

            Assert.That(adapter.IsHeadsetTrackingActive, Is.True);
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

            pauseReset.PauseForReset("critical risk", new PhysicalRiskState
            {
                clearance_forward = 0.2f,
                clearance_left = 1.4f,
                clearance_right = 0.6f,
                boundary_risk = "critical",
                reset_required = true
            });

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
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);
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
            Assert.That(requests.Contexts[0].semantic_next_target, Is.EqualTo("E3"));

            coordinator.HandleTargetArrived("E3");
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E5"));
            Assert.That(requests.Contexts.Count, Is.EqualTo(2));
            Assert.That(requests.Contexts[1].current_exhibit, Is.EqualTo("E3"));
            Assert.That(requests.Contexts[1].semantic_next_target, Is.EqualTo("E5"));
            Assert.That(replanningCount, Is.EqualTo(1));

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
            var task = CreateRouteTask(root);
            Assert.That(
                task.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.NavigableDetour)),
                Is.True);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);
            GuidanceTarget reachedTarget = null;
            GuidanceTarget completedDetour = null;
            coordinator.TargetReached += target => reachedTarget = target;
            coordinator.DetourCompleted += target => completedDetour = target;

            coordinator.HandleTargetArrived("E2");

            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E2"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(task.RuntimeState.HasVisitedDetour("E2"), Is.True);
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(requests.Contexts[0].current_exhibit, Is.EqualTo("E2"));
            Assert.That(requests.Contexts[0].semantic_next_target, Is.EqualTo("E3"));
            Assert.That(requests.Contexts[0].target_role, Is.EqualTo("route_subgoal"));
            Assert.That(reachedTarget, Is.Not.Null);
            Assert.That(reachedTarget.TargetId, Is.EqualTo("E2"));
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
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);
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
            Assert.That(requests.Contexts[0].semantic_next_target, Is.EqualTo("E3"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GuidanceCoordinator_GainReevaluationCompletesTurnCueAndReplans()
    {
        var root = new GameObject("TurnCueGainLifecycleTest");
        root.SetActive(false);
        try
        {
            var task = CreateRouteTask(root);
            Assert.That(
                task.TrySetGuidanceTarget(new GuidanceTarget("E2", NavigationTargetRole.TurnCueAnchor)),
                Is.True);
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            var lifecycleEvents = new List<RotationGainEventType>();
            gain.LifecycleEventOccurred += info => lifecycleEvents.Add(info.EventType);
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, task, null, null, null, null, null, null, gain, null);
            var requests = new RecordingGuidanceRequestClient();
            coordinator.ConfigureRequestClient(requests);

            gain.BeginPendingTurn(1);
            Assert.That(task.RuntimeState.HasCompletedTurnCue("E2"), Is.False);
            Assert.That(requests.Contexts, Is.Empty);

            gain.ProcessTurnSample(true, 30f, 0.1f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Inactive));
            Assert.That(task.RuntimeState.HasCompletedTurnCue("E2"), Is.True);
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
            Assert.That(requests.Contexts.Count, Is.EqualTo(1));
            Assert.That(lifecycleEvents, Is.EqualTo(new[]
            {
                RotationGainEventType.TurnRequested,
                RotationGainEventType.UserTurnStarted,
                RotationGainEventType.GainStarted,
                RotationGainEventType.UserTurnCompleted,
                RotationGainEventType.GainCompleted
            }));
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
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, null, task, null, null, null, null, null, null, null, null);
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
            current_exhibit = "E1",
            semantic_next_target = "E3",
            physical_space_id = "narrow_room"
        };
        var snapshot = GuidanceRequestSnapshot.Capture(request, null, runtime);

        Assert.That(snapshot.MatchesResponse(ResponseForRequest(request, "E3")), Is.True);

        var wrongRequest = ResponseForRequest(request, "E3");
        wrongRequest.request_id = "request-old";
        Assert.That(snapshot.MatchesResponse(wrongRequest), Is.False);

        var wrongDecision = ResponseForRequest(request, "E3");
        wrongDecision.decision_id = "decision-old";
        Assert.That(snapshot.MatchesResponse(wrongDecision), Is.False);

        var wrongState = ResponseForRequest(request, "E3");
        wrongState.state_version++;
        Assert.That(snapshot.MatchesResponse(wrongState), Is.False);

        var wrongCalibration = ResponseForRequest(request, "E3");
        wrongCalibration.calibration_id = "calibration-old";
        Assert.That(snapshot.MatchesResponse(wrongCalibration), Is.False);

        var wrongRisk = ResponseForRequest(request, "E3");
        wrongRisk.risk_snapshot_id = "risk-old";
        Assert.That(snapshot.MatchesResponse(wrongRisk), Is.False);
    }

    [Test]
    public void AgentClient_BuildRequestCreatesUniqueRequestIdentityAndPreservesDecisionState()
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
                current_exhibit = "E1",
                semantic_next_target = "E3"
            };

            var first = (GuidanceRequest)InvokePrivateWithResult(client, "BuildRequest", context);
            var second = (GuidanceRequest)InvokePrivateWithResult(client, "BuildRequest", context);

            Assert.That(first.request_id, Is.Not.Empty);
            Assert.That(second.request_id, Is.Not.EqualTo(first.request_id));
            Assert.That(first.decision_id, Is.EqualTo("decision-stable"));
            Assert.That(first.state_version, Is.EqualTo(9));
            Assert.That(first.session_id, Is.EqualTo("session-stable"));
            Assert.That(first.calibration_id, Is.EqualTo(calibration.CalibrationId));
            Assert.That(first.risk_snapshot_id, Is.EqualTo(risk.CurrentRiskState.risk_snapshot_id));
            Assert.That(second.risk_snapshot_id, Is.EqualTo(first.risk_snapshot_id));
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
    public void ExperimentDashboard_RequiresCalibrationBeforeSessionCanStart()
    {
        var root = new GameObject("ExperimentDashboardCalibrationGateTest");
        root.SetActive(false);
        try
        {
            CreateCalibrationRig(root, out var pose, out var space, out var calibration);
            var risk = root.AddComponent<PhysicalRiskEvaluator>();
            risk.Configure(space, pose);
            var tracking = root.AddComponent<XRTrackingAdapter>();
            tracking.Configure(pose, calibration);
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
            risk.Configure(space, pose);
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
        Assert.That(ExperimentConditionMatrix.TotalCombinations, Is.EqualTo(96));
        Assert.That(ExperimentConditionMatrix.Conditions, Does.Contain("ours_full"));
        Assert.That(ExperimentConditionMatrix.TaskIds, Does.Contain("T4_next_step"));
        Assert.That(ExperimentConditionMatrix.PhysicalSpaces, Does.Contain("l_shaped_room"));

        var spec = ExperimentConditionMatrix.Build(5, 1, 3, "P042");

        Assert.That(spec.participantId, Is.EqualTo("P042"));
        Assert.That(spec.condition, Is.EqualTo("ours_full"));
        Assert.That(spec.taskId, Is.EqualTo("T2_main_target"));
        Assert.That(spec.physicalSpaceId, Is.EqualTo("l_shaped_room"));
        Assert.That(spec.routeTargetIds, Is.EqualTo(new[] { "E1", "E3", "E5" }));

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
            var current = new GameObject("PolicyCurrent");
            current.transform.SetParent(root.transform, false);
            current.AddComponent<ExhibitMetadata>().Configure(
                "PolicyE1", "Current", null, null, null, new[] { "PolicyE3" }, current.transform, current.transform);
            var detour = new GameObject("PolicyLeftCandidate");
            detour.transform.SetParent(root.transform, false);
            detour.transform.position = new Vector3(-2f, 0f, 0f);
            detour.AddComponent<ExhibitMetadata>().Configure(
                "PolicyE3", "Left", null, null, null, null, detour.transform, detour.transform);
            var routeTarget = new GameObject("PolicyRouteTarget");
            routeTarget.transform.SetParent(root.transform, false);
            routeTarget.transform.position = new Vector3(0f, 0f, 2f);
            routeTarget.AddComponent<ExhibitMetadata>().Configure(
                "PolicyE5", "Forward", null, null, null, null, routeTarget.transform, routeTarget.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.RebuildIndex();
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "PolicyE1", "PolicyE5", "PolicyE5");
            var risk = new PhysicalRiskState
            {
                unsafe_directions = new[] { "forward" },
                safe_directions = new[] { "left" }
            };

            var baselineTarget = selector.SelectNextTarget(
                runtime,
                risk,
                ExperimentConditionMatrix.GetPolicy("B1_mllm_only"));
            var fullTarget = selector.SelectNextTarget(
                runtime,
                risk,
                ExperimentConditionMatrix.GetPolicy("ours_full"));

            Assert.That(baselineTarget, Is.EqualTo("PolicyE5"));
            Assert.That(fullTarget, Is.EqualTo("PolicyE3"));
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
    public void SemanticTargetSelector_DistinguishesRouteTurnCueAndNavigableDetourRoles()
    {
        var root = new GameObject("GuidanceTargetRoleSelectionTest");
        try
        {
            var userRoot = new GameObject("RoleUser");
            userRoot.transform.SetParent(root.transform, false);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(userRoot.transform);
            var current = CreateExhibit(
                root.transform,
                "RoleE1",
                Vector3.zero,
                "introduction",
                new[] { "RoleE2", "RoleE3" });
            CreateExhibit(
                root.transform,
                "RoleE2",
                new Vector3(-2f, 0f, 1f),
                "turn_cue_anchor",
                null);
            CreateExhibit(
                root.transform,
                "RoleE3",
                new Vector3(-3f, 0f, 1f),
                "left_related_anchor",
                null);
            CreateExhibit(
                root.transform,
                "RoleE5",
                new Vector3(0f, 0f, 3f),
                "route_subgoal",
                null);
            CreateExhibit(
                root.transform,
                "RoleE6",
                new Vector3(0f, 0f, 5f),
                "main_target",
                null);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", current.Id, "RoleE5", "RoleE6");
            var unsafeForward = new PhysicalRiskState
            {
                unsafe_directions = new[] { "forward" },
                safe_directions = new[] { "left" }
            };

            var turnCue = selector.SelectNextGuidanceTarget(
                runtime,
                unsafeForward,
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            Assert.That(turnCue.TargetId, Is.EqualTo("RoleE2"));
            Assert.That(turnCue.TargetRole, Is.EqualTo(NavigationTargetRole.TurnCueAnchor));
            Assert.That(targets.GetSafetyTargetRole("RoleE3"), Is.EqualTo(NavigationTargetRole.NavigableDetour));

            var routeSubgoal = selector.SelectNextGuidanceTarget(
                runtime,
                new PhysicalRiskState { safe_directions = new[] { "forward" } },
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            Assert.That(routeSubgoal.TargetRole, Is.EqualTo(NavigationTargetRole.RouteSubgoal));

            runtime.Begin("T2_main_target", current.Id, "RoleE6", "RoleE6");
            var finalTarget = selector.SelectNextGuidanceTarget(
                runtime,
                new PhysicalRiskState { safe_directions = new[] { "forward" } },
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
            executor.Configure(narration, null, null, null, null, rejection, null);
            var task = root.AddComponent<GuidanceTaskController>();
            task.ConfigureTask(new GuidanceTaskDefinition
            {
                startExhibitId = "E1",
                finalTargetId = "E5",
                routeTargetIds = new[] { "E1", "E3", "E5" }
            });
            task.StartTask();
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            coordinator.Configure(null, executor, task, null, null, null, null, null, null, null, rejection);
            RuntimeInterventionInfo intervention = null;
            coordinator.RuntimeIntervention += value => intervention = value;

            coordinator.HandleGuidanceResponse(new GuidanceResponse
            {
                narration = "不应显示",
                movement_instruction = "turn_left_slowly",
                verifier = new VerifierResult { approved = false, reason = "verifier blocked" }
            });

            Assert.That(intervention, Is.Not.Null);
            Assert.That(intervention.Reason, Is.EqualTo("verifier blocked"));
            Assert.That(narrationText.text, Is.Empty);
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
            Assert.That(task.RuntimeState.currentExhibitId, Is.EqualTo("E1"));
            Assert.That(task.RuntimeState.nextTargetId, Is.EqualTo("E3"));
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
    public void ExperimentLogger_WritesSessionEndedBeforeClosingSession()
    {
        var root = new GameObject("ExperimentLoggerEndSessionTest");
        string logPath = null;
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-log"));
            logPath = logger.LogPath;

            logger.EndSession("completed_auto");

            Assert.That(logger.Session.IsActive, Is.False);
            Assert.That(File.Exists(logPath), Is.True);
            var lines = File.ReadAllLines(logPath);
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(lines[0], Does.Contain("\"event_type\":\"session_started\""));
            Assert.That(lines[1], Does.Contain("\"event_type\":\"session_ended\""));
            Assert.That(lines[1], Does.Contain("\"message\":\"completed_auto\""));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentLogger_RecordsRawGuidanceRuntimeInterventionAndExecutedOutcome()
    {
        var root = new GameObject("ExperimentLoggerRuntimeInterventionTest");
        string logPath = null;
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
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

            logger.LogResponse(response, null);
            logger.LogRuntimeIntervention(response, runtime, null, "unsafe forward");
            logger.LogGuidanceExecuted(response, runtime, null);

            var lines = File.ReadAllLines(logPath);
            Assert.That(lines.Length, Is.EqualTo(4));
            Assert.That(lines[1], Does.Contain("\"event_type\":\"guidance_received\""));
            Assert.That(lines[1], Does.Contain("\"raw_guidance\":"));
            Assert.That(lines[1], Does.Contain("\"runtime_validation_result\":\"pending\""));
            Assert.That(lines[2], Does.Contain("\"event_type\":\"runtime_intervention\""));
            Assert.That(lines[2], Does.Contain("\"runtime_intervention\":true"));
            Assert.That(lines[2], Does.Contain("\"executed_guidance\":\"\""));
            Assert.That(lines[3], Does.Contain("\"event_type\":\"guidance_executed\""));
            Assert.That(lines[3], Does.Contain("\"runtime_intervention\":false"));
            Assert.That(lines[3], Does.Not.Contain("\"executed_guidance\":\"\""));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentLogger_PreservesActiveTraceAcrossDownstreamEvents()
    {
        var root = new GameObject("ExperimentLoggerTraceContextTest");
        string logPath = null;
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
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
                target_role = "route_subgoal",
                condition = "ours_full",
                task_id = "T2_main_target",
                current_exhibit = "E1",
                semantic_next_target = "E3"
            };
            var response = ResponseForRequest(request, "E3");

            logger.LogRequest(request, new PhysicalRiskState
            {
                calibration_id = "calibration-trace",
                risk_snapshot_id = "risk-request"
            });
            logger.LogResponse(response, null);
            logger.LogGuidanceVerified(runtime, null);
            runtime.AdvanceTo("E3", "E5");
            logger.LogStatus("gain_started", runtime, new PhysicalRiskState
            {
                calibration_id = "calibration-trace",
                risk_snapshot_id = "risk-runtime"
            }, "gain started");

            var lines = File.ReadAllLines(logPath);
            Assert.That(lines.Length, Is.EqualTo(5));
            Assert.That(lines[1], Does.Contain("\"event_type\":\"guidance_requested\""));
            Assert.That(lines[2], Does.Contain("\"event_type\":\"guidance_received\""));
            Assert.That(lines[3], Does.Contain("\"event_type\":\"guidance_verified\""));
            Assert.That(lines[4], Does.Contain("\"event_type\":\"gain_started\""));
            Assert.That(lines[4], Does.Contain("\"calibration_id\":\"calibration-trace\""));
            Assert.That(lines[4], Does.Contain("\"request_id\":\"request-trace\""));
            Assert.That(lines[4], Does.Contain("\"decision_id\":\"decision-trace\""));
            Assert.That(lines[4], Does.Contain("\"risk_snapshot_id\":\"risk-runtime\""));
            Assert.That(lines[4], Does.Contain($"\"state_version\":{runtime.stateVersion}"));
            Assert.That(lines[4], Does.Not.Contain("\"raw_guidance\":\"\""));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentLogger_FlushesPreSessionCalibrationEvents()
    {
        var root = new GameObject("ExperimentLoggerCalibrationQueueTest");
        string logPath = null;
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.LogCalibrationStarted();
            logger.LogCalibrationCompleted("calibration-before-session");

            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-calibration-log"));
            logPath = logger.LogPath;
            logger.LogStatus("risk_changed", null, null, "risk refreshed");

            var lines = File.ReadAllLines(logPath);
            Assert.That(lines.Length, Is.EqualTo(4));
            Assert.That(lines[1], Does.Contain("\"event_type\":\"calibration_started\""));
            Assert.That(lines[2], Does.Contain("\"event_type\":\"calibration_completed\""));
            Assert.That(lines[2], Does.Contain("\"calibration_id\":\"calibration-before-session\""));
            Assert.That(lines[3], Does.Contain("\"calibration_id\":\"calibration-before-session\""));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentLogger_MirrorsTraceIdsAndSeverityToUnityConsole()
    {
        var root = new GameObject("ExperimentLoggerUnityConsoleTest");
        string logPath = null;
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
            var logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-console-log"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");
            var risk = new PhysicalRiskState
            {
                calibration_id = "calibration-console",
                risk_snapshot_id = "risk-console",
                boundary_risk = "safe",
                safe_directions = new[] { "forward" }
            };
            var request = new GuidanceRequest
            {
                calibration_id = risk.calibration_id,
                request_id = "request-console",
                decision_id = "decision-console",
                state_version = runtime.stateVersion,
                risk_snapshot_id = risk.risk_snapshot_id,
                target_role = "route_subgoal",
                condition = "ours_full",
                task_id = runtime.taskId,
                current_exhibit = runtime.currentExhibitId,
                semantic_next_target = runtime.nextTargetId
            };
            var response = ResponseForRequest(request, "E3");

            logger.LogRequest(request, risk);
            logger.LogRuntimeIntervention(response, runtime, risk, "unsafe forward");
            LogAssert.Expect(
                LogType.Error,
                new Regex(@"\[VirtualGuidance\]\[ERROR\]\[task_failed\].*request_id=request-console.*decision_id=decision-console.*risk_snapshot_id=risk-console"));
            logger.LogStatus("task_failed", runtime, risk, "backend unavailable");

            var requestIndex = messages.FindIndex(message => message.Contains("[guidance_requested]"));
            var interventionIndex = messages.FindIndex(message => message.Contains("[runtime_intervention]"));
            var failureIndex = messages.FindIndex(message => message.Contains("[task_failed]"));
            Assert.That(requestIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(interventionIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(failureIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(messages[requestIndex], Does.Contain("session_id=" + logger.Session.sessionId));
            Assert.That(messages[requestIndex], Does.Contain("calibration_id=calibration-console"));
            Assert.That(messages[requestIndex], Does.Contain("request_id=request-console"));
            Assert.That(messages[requestIndex], Does.Contain("decision_id=decision-console"));
            Assert.That(messages[requestIndex], Does.Contain($"state_version={runtime.stateVersion}"));
            Assert.That(messages[requestIndex], Does.Contain("risk_snapshot_id=risk-console"));
            Assert.That(levels[requestIndex], Is.EqualTo(LogType.Log));
            Assert.That(levels[interventionIndex], Is.EqualTo(LogType.Warning));
            Assert.That(levels[failureIndex], Is.EqualTo(LogType.Error));
        }
        finally
        {
            Application.logMessageReceived -= callback;
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentLogger_DeduplicatesConsoleRiskWithoutDroppingJsonlSnapshots()
    {
        var root = new GameObject("ExperimentLoggerRiskConsoleTest");
        string logPath = null;
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
            var logger = root.AddComponent<ExperimentLogger>();
            SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceEditModeLogs");
            logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, "P-risk-console"));
            logPath = logger.LogPath;
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "E1", "E3", "E5");

            logger.LogStatus("risk_changed", runtime, new PhysicalRiskState
            {
                risk_snapshot_id = "risk-1",
                boundary_risk = "safe",
                safe_directions = new[] { "forward" }
            }, "first safe snapshot");
            logger.LogStatus("risk_changed", runtime, new PhysicalRiskState
            {
                risk_snapshot_id = "risk-2",
                boundary_risk = "safe",
                safe_directions = new[] { "forward" }
            }, "same semantic risk");
            logger.LogStatus("risk_changed", runtime, new PhysicalRiskState
            {
                risk_snapshot_id = "risk-3",
                boundary_risk = "critical",
                reset_required = true,
                unsafe_directions = new[] { "forward" }
            }, "critical risk");

            Assert.That(riskConsoleCount, Is.EqualTo(2));
            var lines = File.ReadAllLines(logPath);
            Assert.That(lines.Count(line => line.Contains("\"event_type\":\"risk_changed\"")), Is.EqualTo(3));
            Assert.That(lines.Any(line => line.Contains("\"risk_snapshot_id\":\"risk-2\"")), Is.True);
        }
        finally
        {
            Application.logMessageReceived -= callback;
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Test]
    public void ExperimentDashboard_ProducesRequiredTraceabilityEvents()
    {
        var root = new GameObject("ExperimentRequiredEventsTest");
        string logPath = null;
        try
        {
            var logger = root.AddComponent<ExperimentLogger>();
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
                target_role = "navigable_detour",
                condition = "ours_full",
                task_id = "T2_main_target",
                current_exhibit = "E1",
                semantic_next_target = "E2"
            };
            var response = ResponseForRequest(request, "E2");
            var risk = new PhysicalRiskState
            {
                calibration_id = request.calibration_id,
                risk_snapshot_id = request.risk_snapshot_id,
                boundary_risk = "safe",
                safe_directions = new[] { "forward" }
            };
            logger.LogCalibrationStarted();
            logger.LogCalibrationCompleted(request.calibration_id);
            logger.LogRequest(request, risk);
            logger.LogResponse(response, risk);

            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "experimentLogger", logger);
            InvokePrivate(dashboard, "HandleRequestPhaseChanged", GuidanceRequestPhase.Verified);
            InvokePrivate(dashboard, "HandleRequestPhaseChanged", GuidanceRequestPhase.Cancelled);
            InvokePrivate(dashboard, "HandleGuidanceRejected", "protocol rejected");
            InvokePrivate(dashboard, "HandleGuidanceExpired", "state changed");
            InvokePrivate(dashboard, "HandleGuidanceExecuted", response);
            InvokePrivate(dashboard, "HandleRuntimeIntervention", new RuntimeInterventionInfo(response, "unsafe forward"));
            InvokePrivate(dashboard, "HandleRiskStateChanged", risk);

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
            InvokePrivate(dashboard, "HandleGuidanceTargetSelected", detour);
            InvokePrivate(dashboard, "HandleTargetReached", detour);
            InvokePrivate(dashboard, "HandleDetourCompleted", detour);
            InvokePrivate(dashboard, "HandleResetStarted", new ResetEventInfo("left", Vector2.left, "critical risk"));
            InvokePrivate(dashboard, "HandleResetCompleted", new ResetEventInfo("left", Vector2.left, "critical risk"));
            InvokePrivate(dashboard, "HandleTaskCompleted", new GuidanceRuntimeState());
            logger.LogCalibrationInvalidated(request.calibration_id, "space changed");
            LogAssert.Expect(LogType.Error, new Regex(@"\[VirtualGuidance\]\[ERROR\]\[task_failed\].*backend unavailable"));
            InvokePrivate(dashboard, "EnterError", "backend unavailable");

            var lines = File.ReadAllLines(logPath);
            foreach (var requiredEventType in new[]
            {
                "calibration_started",
                "calibration_completed",
                "calibration_invalidated",
                "guidance_requested",
                "guidance_received",
                "guidance_verified",
                "guidance_rejected",
                "guidance_expired",
                "guidance_cancelled",
                "guidance_executed",
                "risk_changed",
                "runtime_intervention",
                "turn_requested",
                "user_turn_started",
                "gain_started",
                "gain_completed",
                "gain_cancelled",
                "target_reached",
                "detour_selected",
                "detour_completed",
                "reset_started",
                "reset_completed",
                "task_completed",
                "task_failed"
            })
            {
                Assert.That(
                    lines.Any(line => line.Contains($"\"event_type\":\"{requiredEventType}\"")),
                    Is.True,
                    requiredEventType);
            }
            var targetLine = lines.Single(line => line.Contains("\"event_type\":\"target_reached\""));
            Assert.That(targetLine, Does.Contain("\"selected_target\":\"E2\""));
            Assert.That(targetLine, Does.Contain("\"target_role\":\"navigable_detour\""));
            Assert.That(targetLine, Does.Contain("\"request_id\":\"request-required-events\""));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                File.Delete(logPath);
            }
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
            "session_id",
            "calibration_id",
            "request_id",
            "decision_id",
            "state_version",
            "risk_snapshot_id",
            "target_role",
            "condition",
            "task_id",
            "physical_space_id",
            "current_exhibit",
            "user_physical_position",
            "user_physical_forward",
            "participant_id",
            "user_task",
            "semantic_next_target",
            "user_virtual_position",
            "user_virtual_forward");

        AssertPublicFieldNames<GuidanceResponse>(
            "session_id",
            "calibration_id",
            "request_id",
            "decision_id",
            "state_version",
            "risk_snapshot_id",
            "target_role",
            "selected_target",
            "route_decision",
            "narration",
            "gaze_cue",
            "gaze_target_id",
            "avatar_action",
            "movement_instruction",
            "physical_intent",
            "verifier");

        AssertPublicFieldNames<VerifierResult>(
            "approved",
            "reason");
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
        return new GuidanceRequest
        {
            session_id = context.session_id,
            calibration_id = context.calibration_id,
            request_id = requestId,
            decision_id = context.decision_id,
            state_version = context.state_version,
            risk_snapshot_id = context.risk_snapshot_id,
            target_role = context.target_role,
            current_exhibit = context.current_exhibit,
            semantic_next_target = context.semantic_next_target,
            physical_space_id = "narrow_room"
        };
    }

    private static GuidanceResponse ResponseForRequest(GuidanceRequest request, string selectedTarget)
    {
        return new GuidanceResponse
        {
            session_id = request.session_id,
            calibration_id = request.calibration_id,
            request_id = request.request_id,
            decision_id = request.decision_id,
            state_version = request.state_version,
            risk_snapshot_id = request.risk_snapshot_id,
            target_role = request.target_role,
            selected_target = selectedTarget,
            movement_instruction = "move_forward_slowly",
            verifier = new VerifierResult { approved = true, reason = "ok" }
        };
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
