using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GuidanceLiveBackendValidator
{
    private const string MenuPath = "Tools/VirtualGuidance/Run Live Backend Validation";
    private const string PendingKey = "VirtualGuidance.LiveBackendValidation.Pending";
    private const string RejectionReason = "Editor validation injected verifier rejection.";

    private static readonly List<ValidationCheck> Checks = new List<ValidationCheck>();

    private static ValidationStage stage;
    private static double deadline;
    private static double notBefore;
    private static AgentClient agentClient;
    private static PhysicalSpaceManager physicalSpaceManager;
    private static UserPoseTracker userPoseTracker;
    private static PhysicalRiskEvaluator riskEvaluator;
    private static GuidanceTaskController taskController;
    private static NavigationTargetManager navigationTargets;
    private static ArrivalDetector arrivalDetector;
    private static ExperimentLogger experimentLogger;
    private static GameObject validationUser;
    private static GuidanceResponse liveResponse;
    private static string requestFailure;
    private static string arrivedTarget;
    private static string reportPath;

    static GuidanceLiveBackendValidator()
    {
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.delayCall += ResumePendingValidation;
    }

    [MenuItem(MenuPath)]
    private static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[VirtualGuidance Validation] Stop Play Mode before starting live backend validation.");
            return;
        }

        ResetState();
        SessionState.SetBool(PendingKey, true);
        Debug.Log("[VirtualGuidance Validation] Starting Play Mode validation against http://127.0.0.1:8000/api/guidance.");
        EditorApplication.EnterPlaymode();
    }

    [MenuItem(MenuPath, true)]
    private static bool CanRun()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;
    }

    private static void ResumePendingValidation()
    {
        if (SessionState.GetBool(PendingKey, false) && EditorApplication.isPlaying && stage == ValidationStage.Idle)
        {
            BeginValidation();
        }
    }

    private static void HandlePlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
        {
            BeginValidation();
        }
        else if (change == PlayModeStateChange.ExitingPlayMode && stage != ValidationStage.Idle && stage != ValidationStage.Exiting)
        {
            Fail("Play Mode exited before validation completed.", true);
        }
    }

    private static void BeginValidation()
    {
        if (stage != ValidationStage.Idle)
        {
            return;
        }

        Checks.Clear();
        stage = ValidationStage.WaitingForScene;
        notBefore = EditorApplication.timeSinceStartup + 0.5d;
        deadline = EditorApplication.timeSinceStartup + 10d;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
        {
            return;
        }

        try
        {
            if (EditorApplication.timeSinceStartup < notBefore)
            {
                return;
            }

            if (deadline > 0d && EditorApplication.timeSinceStartup > deadline)
            {
                Fail($"Timed out during validation stage {stage}.");
                return;
            }

            switch (stage)
            {
                case ValidationStage.WaitingForScene:
                    PrepareSceneAndRequestGuidance();
                    break;
                case ValidationStage.WaitingForApprovedResponse:
                    if (!string.IsNullOrWhiteSpace(requestFailure))
                    {
                        Fail(requestFailure);
                    }
                    break;
                case ValidationStage.VerifyingApprovedPresentation:
                    VerifyApprovedPresentation();
                    break;
                case ValidationStage.VerifyingRejectedPresentation:
                    VerifyRejectedPresentation();
                    break;
                case ValidationStage.WaitingForE3Arrival:
                    VerifyArrival("E3", "E5", false);
                    break;
                case ValidationStage.WaitingForE5Arrival:
                    VerifyArrival("E5", string.Empty, true);
                    break;
                case ValidationStage.VerifyingSessionLog:
                    VerifySessionLog();
                    break;
                case ValidationStage.Exiting:
                    SessionState.SetBool(PendingKey, false);
                    stage = ValidationStage.Idle;
                    EditorApplication.ExitPlaymode();
                    break;
            }
        }
        catch (Exception exception)
        {
            Fail($"Unexpected validation exception: {exception}");
        }
    }

    private static void PrepareSceneAndRequestGuidance()
    {
        var installer = UnityEngine.Object.FindObjectOfType<GuidanceSceneInstaller>();
        installer?.Install();

        agentClient = UnityEngine.Object.FindObjectOfType<AgentClient>();
        physicalSpaceManager = UnityEngine.Object.FindObjectOfType<PhysicalSpaceManager>();
        userPoseTracker = UnityEngine.Object.FindObjectOfType<UserPoseTracker>();
        riskEvaluator = UnityEngine.Object.FindObjectOfType<PhysicalRiskEvaluator>();
        taskController = UnityEngine.Object.FindObjectOfType<GuidanceTaskController>();
        navigationTargets = UnityEngine.Object.FindObjectOfType<NavigationTargetManager>();
        arrivalDetector = UnityEngine.Object.FindObjectOfType<ArrivalDetector>();
        experimentLogger = UnityEngine.Object.FindObjectOfType<ExperimentLogger>();

        if (agentClient == null || physicalSpaceManager == null || userPoseTracker == null ||
            riskEvaluator == null || taskController == null || navigationTargets == null ||
            arrivalDetector == null || experimentLogger == null)
        {
            return;
        }

        Record("scene_contract", true, "Installer resolved AgentClient, RDW, task, navigation, arrival, and experiment logging components.");

        agentClient.GuidanceReceived.RemoveListener(HandleGuidanceReceived);
        agentClient.GuidanceReceived.AddListener(HandleGuidanceReceived);
        agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        agentClient.GuidanceRequestFailed.AddListener(HandleRequestFailed);
        arrivalDetector.TargetArrived -= HandleTargetArrived;
        arrivalDetector.TargetArrived += HandleTargetArrived;

        navigationTargets.RebuildIndex();
        if (!navigationTargets.TryGetTarget("E1", out var e1) || e1 == null ||
            !navigationTargets.TryGetTarget("E3", out var e3) || e3 == null ||
            !navigationTargets.TryGetTarget("E5", out var e5) || e5 == null)
        {
            Fail("Scene navigation index does not contain E1, E3, and E5.");
            return;
        }

        validationUser = new GameObject("__GuidanceLiveBackendValidationUser")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        validationUser.transform.SetPositionAndRotation(TargetPosition(e1), Quaternion.identity);
        userPoseTracker.ConfigureVirtualUserRoot(validationUser.transform);
        userPoseTracker.SetSimulatedPhysicalPose(new Vector2(1.8f, 4.3f), 0f);

        if (!physicalSpaceManager.SetPhysicalSpace("narrow_room"))
        {
            Fail("Could not select narrow_room for live backend validation.");
            return;
        }

        var riskState = riskEvaluator.EvaluateNow();
        if (riskState == null || riskState.physical_space != "narrow_room")
        {
            Fail("Local risk evaluator did not produce the narrow_room state.");
            return;
        }

        var spec = ExperimentConditionMatrix.Build(5, 1, 1, "CODEX_VALIDATION");
        experimentLogger.StartSession(spec);
        taskController.ConfigureTask(spec.ToTaskDefinition());

        requestFailure = string.Empty;
        liveResponse = null;
        stage = ValidationStage.WaitingForApprovedResponse;
        deadline = EditorApplication.timeSinceStartup + 12d;
        agentClient.RequestGuidance(new GuidanceRequestContext
        {
            condition = spec.condition,
            task_id = spec.taskId,
            current_exhibit = spec.startExhibitId,
            semantic_next_target = spec.semanticNextTargetId,
            participant_id = spec.participantId,
            user_task = spec.userTask
        });
    }

    private static void HandleGuidanceReceived(GuidanceResponse response)
    {
        if (stage != ValidationStage.WaitingForApprovedResponse)
        {
            return;
        }

        liveResponse = response;
        stage = ValidationStage.VerifyingApprovedPresentation;
        notBefore = EditorApplication.timeSinceStartup + 0.1d;
        deadline = EditorApplication.timeSinceStartup + 3d;
    }

    private static void HandleRequestFailed(string message)
    {
        if (stage == ValidationStage.WaitingForApprovedResponse)
        {
            requestFailure = string.IsNullOrWhiteSpace(message) ? "Live backend request failed." : message;
        }
    }

    private static void VerifyApprovedPresentation()
    {
        if (liveResponse == null || liveResponse.verifier == null || !liveResponse.verifier.approved)
        {
            Fail("Live backend did not return an approved guidance response.");
            return;
        }

        if (liveResponse.selected_target != "E3" || liveResponse.movement_instruction != "turn_left_slowly")
        {
            Fail($"Unexpected live response: target={liveResponse.selected_target}, movement={liveResponse.movement_instruction}.");
            return;
        }

        var narrationText = FindText("GuidanceTextPanel_Text");
        var movementText = FindText("MovementHintPanel_Text");
        var rejectionText = FindText("RejectionPanel_Text");
        var presentationApplied = narrationText != null && narrationText.text == liveResponse.narration &&
                                  movementText != null && movementText.text == "Movement: turn left slowly" &&
                                  (rejectionText == null || string.IsNullOrWhiteSpace(rejectionText.text));
        if (!presentationApplied)
        {
            Fail("Approved response reached Unity but the narration/movement presentation was not applied cleanly.");
            return;
        }

        Record("live_backend_approved", true, "Unity received E3 / turn_left_slowly from the Anaconda-hosted backend and applied the visible guidance.");

        var rejected = CopyResponse(liveResponse);
        rejected.verifier = new VerifierResult
        {
            approved = false,
            reason = RejectionReason
        };
        agentClient.GuidanceReceived.Invoke(rejected);
        stage = ValidationStage.VerifyingRejectedPresentation;
        notBefore = EditorApplication.timeSinceStartup + 0.1d;
        deadline = EditorApplication.timeSinceStartup + 3d;
    }

    private static void VerifyRejectedPresentation()
    {
        var narrationText = FindText("GuidanceTextPanel_Text");
        var movementText = FindText("MovementHintPanel_Text");
        var rejectionText = FindText("RejectionPanel_Text");
        var rejectedCleanly = narrationText != null && narrationText.text == "Guide narration will appear here." &&
                              movementText != null && movementText.text == "Movement: waiting" &&
                              rejectionText != null && rejectionText.text.Contains(RejectionReason);
        if (!rejectedCleanly)
        {
            Fail("Verifier-rejected response did not clear guidance and expose its rejection reason.");
            return;
        }

        Record("verifier_rejected_presentation", true, "A rejected clone of the live response traversed the real event wiring, cleared guidance, and displayed the verifier reason.");
        StartArrivalCheck("E3", ValidationStage.WaitingForE3Arrival);
    }

    private static void StartArrivalCheck(string targetId, ValidationStage nextStage)
    {
        if (!navigationTargets.TryGetTarget(targetId, out var target) || target == null)
        {
            Fail($"Could not resolve scene target {targetId}.");
            return;
        }

        if (targetId == "E3")
        {
            var spec = ExperimentConditionMatrix.Build(5, 1, 1, "CODEX_VALIDATION");
            taskController.ConfigureTask(spec.ToTaskDefinition());
        }

        arrivedTarget = string.Empty;
        arrivalDetector.WatchTarget(targetId);
        validationUser.transform.position = TargetPosition(target);
        stage = nextStage;
        notBefore = EditorApplication.timeSinceStartup + 0.05d;
        deadline = EditorApplication.timeSinceStartup + 3d;
    }

    private static void HandleTargetArrived(string targetId)
    {
        arrivedTarget = targetId;
    }

    private static void VerifyArrival(string expectedCurrent, string expectedNext, bool expectCompleted)
    {
        var state = taskController != null ? taskController.RuntimeState : null;
        if (arrivedTarget != expectedCurrent || state == null || state.currentExhibitId != expectedCurrent ||
            state.nextTargetId != expectedNext || state.IsCompleted != expectCompleted)
        {
            return;
        }

        Record(
            $"arrival_{expectedCurrent.ToLowerInvariant()}",
            true,
            expectCompleted
                ? "Moving the transient user to the real E5 scene anchor completed the E1 -> E3 -> E5 task."
                : "Moving the transient user to the real E3 scene anchor advanced the task toward E5.");

        if (expectCompleted)
        {
            stage = ValidationStage.VerifyingSessionLog;
            notBefore = EditorApplication.timeSinceStartup + 0.1d;
            deadline = EditorApplication.timeSinceStartup + 3d;
        }
        else
        {
            StartArrivalCheck("E5", ValidationStage.WaitingForE5Arrival);
        }
    }

    private static void VerifySessionLog()
    {
        var path = experimentLogger != null ? experimentLogger.LogPath : string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Fail("Experiment session log was not created.");
            return;
        }

        var lines = File.ReadAllLines(path);
        var hasRequest = Contains(lines, "\"event_type\":\"request_built\"");
        var hasResponse = Contains(lines, "\"event_type\":\"response_received\"");
        var hasTaskState = Contains(lines, "\"event_type\":\"task_state_changed\"");
        var hasParticipant = Contains(lines, "\"participant_id\":\"CODEX_VALIDATION\"");
        if (!hasRequest || !hasResponse || !hasTaskState || !hasParticipant)
        {
            Fail("Experiment session JSONL is missing request, response, task-state, or participant evidence.");
            return;
        }

        Record("experiment_session_log", true, $"Validated {lines.Length} JSONL records at {path}.");
        Complete();
    }

    private static void Complete()
    {
        WriteReport(true, "Live backend, visible guidance branches, scene arrival detection, and session logging passed.");
        Cleanup();
        Debug.Log($"[VirtualGuidance Validation] PASS. Report: {reportPath}");
        stage = ValidationStage.Exiting;
        notBefore = EditorApplication.timeSinceStartup + 0.2d;
        deadline = 0d;
    }

    private static void Fail(string message, bool editorAlreadyExiting = false)
    {
        if (stage == ValidationStage.Exiting || stage == ValidationStage.Idle)
        {
            return;
        }

        Record("failure", false, message);
        WriteReport(false, message);
        Cleanup();
        Debug.LogError($"[VirtualGuidance Validation] FAIL: {message} Report: {reportPath}");
        if (editorAlreadyExiting)
        {
            SessionState.SetBool(PendingKey, false);
            stage = ValidationStage.Idle;
            return;
        }

        stage = ValidationStage.Exiting;
        notBefore = EditorApplication.timeSinceStartup + 0.2d;
        deadline = 0d;
    }

    private static void Cleanup()
    {
        if (agentClient != null)
        {
            agentClient.GuidanceReceived.RemoveListener(HandleGuidanceReceived);
            agentClient.GuidanceRequestFailed.RemoveListener(HandleRequestFailed);
        }

        if (arrivalDetector != null)
        {
            arrivalDetector.TargetArrived -= HandleTargetArrived;
        }

        if (validationUser != null)
        {
            UnityEngine.Object.DestroyImmediate(validationUser);
        }
    }

    private static void WriteReport(bool passed, string summary)
    {
        var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "Validation"));
        Directory.CreateDirectory(folder);
        reportPath = Path.Combine(folder, "unity_live_backend_validation_latest.json");
        var report = new ValidationReport
        {
            timestampUtc = DateTime.UtcNow.ToString("o"),
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
            passed = passed,
            summary = summary,
            liveSelectedTarget = liveResponse != null ? liveResponse.selected_target : string.Empty,
            liveMovementInstruction = liveResponse != null ? liveResponse.movement_instruction : string.Empty,
            experimentLogPath = experimentLogger != null ? experimentLogger.LogPath : string.Empty,
            checks = Checks.ToArray()
        };
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
    }

    private static GuidanceResponse CopyResponse(GuidanceResponse source)
    {
        return new GuidanceResponse
        {
            selected_target = source.selected_target,
            route_decision = source.route_decision,
            narration = source.narration,
            gaze_cue = source.gaze_cue,
            gaze_target_id = source.gaze_target_id,
            avatar_action = source.avatar_action,
            movement_instruction = source.movement_instruction,
            physical_intent = source.physical_intent,
            verifier = source.verifier
        };
    }

    private static TMP_Text FindText(string objectName)
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text != null && text.name == objectName && text.gameObject.scene.IsValid())
            {
                return text;
            }
        }

        return null;
    }

    private static Vector3 TargetPosition(ExhibitMetadata target)
    {
        return target.ViewPoint != null ? target.ViewPoint.position : target.transform.position;
    }

    private static bool Contains(string[] lines, string value)
    {
        foreach (var line in lines)
        {
            if (line.Contains(value))
            {
                return true;
            }
        }

        return false;
    }

    private static void Record(string name, bool passed, string details)
    {
        Checks.Add(new ValidationCheck
        {
            name = name,
            passed = passed,
            details = details
        });
    }

    private static void ResetState()
    {
        Cleanup();
        Checks.Clear();
        stage = ValidationStage.Idle;
        deadline = 0d;
        notBefore = 0d;
        agentClient = null;
        physicalSpaceManager = null;
        userPoseTracker = null;
        riskEvaluator = null;
        taskController = null;
        navigationTargets = null;
        arrivalDetector = null;
        experimentLogger = null;
        validationUser = null;
        liveResponse = null;
        requestFailure = string.Empty;
        arrivedTarget = string.Empty;
        reportPath = string.Empty;
    }

    private enum ValidationStage
    {
        Idle,
        WaitingForScene,
        WaitingForApprovedResponse,
        VerifyingApprovedPresentation,
        VerifyingRejectedPresentation,
        WaitingForE3Arrival,
        WaitingForE5Arrival,
        VerifyingSessionLog,
        Exiting
    }

    [Serializable]
    private sealed class ValidationCheck
    {
        public string name;
        public bool passed;
        public string details;
    }

    [Serializable]
    private sealed class ValidationReport
    {
        public string timestampUtc;
        public string scene;
        public bool passed;
        public string summary;
        public string liveSelectedTarget;
        public string liveMovementInstruction;
        public string experimentLogPath;
        public ValidationCheck[] checks;
    }
}
