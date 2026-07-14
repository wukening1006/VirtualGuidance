using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

public sealed class GuidanceRuntimeEditModeTests
{
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
            var pauseReset = root.AddComponent<PauseResetController>();
            pauseReset.Configure(executor, rejection);

            executor.Apply(new GuidanceResponse
            {
                narration = "Move toward exhibit E5.",
                movement_instruction = "move_forward_slowly",
                gaze_cue = "look_forward",
                gaze_target_id = "E5_GazeTarget",
                verifier = new VerifierResult { approved = true, reason = "ok" }
            });

            Assert.That(narrationText.text, Is.EqualTo("Move toward exhibit E5."));
            Assert.That(movementText.text, Is.EqualTo("Movement: move forward slowly"));
            Assert.That(gazeText.text, Does.Contain("look_forward"));

            pauseReset.PauseForReset("All directions unsafe.");

            Assert.That(pauseReset.IsPaused, Is.True);
            Assert.That(narrationText.text, Is.EqualTo("Guide narration will appear here."));
            Assert.That(movementText.text, Is.EqualTo("Movement: waiting"));
            Assert.That(gazeText.text, Is.Empty);
            Assert.That(rejectionRoot.activeSelf, Is.True);
            Assert.That(rejectionText.text, Does.Contain("All directions unsafe."));

            pauseReset.ResumeAfterReset();

            Assert.That(pauseReset.IsPaused, Is.False);
            Assert.That(rejectionRoot.activeSelf, Is.False);
            Assert.That(rejectionText.text, Is.Empty);
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
}
