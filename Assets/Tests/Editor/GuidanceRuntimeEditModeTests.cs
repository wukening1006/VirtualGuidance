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

    private static TMP_Text CreateText(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<TextMeshProUGUI>();
    }
}
