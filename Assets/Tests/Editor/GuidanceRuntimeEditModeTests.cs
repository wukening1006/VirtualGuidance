using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

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
    public void ExperimentDashboard_UnsafeGuidanceShowsSemanticStopMessage()
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

            InvokePrivate(dashboard, "HandleGuidanceRejected", "movement_instruction uses unsafe forward");

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Error));
            Assert.That(safetyText.text, Is.EqualTo("请停止前进"));
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
    public void RotationGainController_FollowsTurnLifecycleAndSafetyStops()
    {
        var root = new GameObject("RotationGainLifecycleTest");
        var user = new GameObject("RotationGainVirtualUser");
        try
        {
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(user.transform);
            var detector = root.AddComponent<MotionDetector>();
            detector.Configure(pose);
            var gain = root.AddComponent<RotationGainController>();
            gain.Configure(pose, detector);
            gain.SetGainEnabled(true);

            gain.BeginPendingTurn(1);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));

            gain.ProcessTurnSample(false, 0f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));

            gain.ProcessTurnSample(true, 10f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Applying));
            Assert.That(gain.AccumulatedPhysicalYawDegrees, Is.EqualTo(10f).Within(0.001f));
            Assert.That(gain.AppliedExtraYawDegrees, Is.EqualTo(1.5f).Within(0.001f));

            gain.ProcessTurnSample(false, 0f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));

            gain.BeginPendingTurn(1);
            gain.ProcessTurnSample(true, -5f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));

            gain.BeginPendingTurn(1);
            gain.ProcessTurnSample(true, 30f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Completed));
            Assert.That(gain.AccumulatedPhysicalYawDegrees, Is.EqualTo(30f).Within(0.001f));
            Assert.That(gain.AppliedExtraYawDegrees, Is.EqualTo(4.5f).Within(0.001f));

            gain.SetGainEnabled(false);
            Assert.That(gain.GainEnabled, Is.False);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(user);
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
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(1);
            var pauseReset = root.AddComponent<PauseResetController>();
            pauseReset.Configure(executor, rejection, gain);

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
            Assert.That(rejectionText.text, Does.Contain("All directions unsafe."));
            Assert.That(gain.IsActive, Is.False);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));

            pauseReset.ResumeAfterReset();

            Assert.That(pauseReset.IsPaused, Is.False);
            Assert.That(rejectionRoot.activeSelf, Is.False);
            Assert.That(rejectionText.text, Is.Empty);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));
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
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Idle));
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
            evaluator.Configure(space, pose);
            var risk = evaluator.EvaluateNow();

            Assert.That(risk, Is.Not.Null);
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
    }

    [Test]
    public void GuidanceCoordinator_VerifierRejectionPublishesReasonWithoutApplyingGuidance()
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
            string rejectedReason = null;
            coordinator.GuidanceRejected += reason => rejectedReason = reason;

            coordinator.HandleGuidanceResponse(new GuidanceResponse
            {
                narration = "不应显示",
                movement_instruction = "turn_left_slowly",
                verifier = new VerifierResult { approved = false, reason = "verifier blocked" }
            });

            Assert.That(rejectedReason, Is.EqualTo("verifier blocked"));
            Assert.That(narrationText.text, Is.Empty);
            Assert.That(rejectionText.text, Does.Contain("verifier blocked"));
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
