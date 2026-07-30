using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// 验证第六阶段日志诊断字段、恢复转换上下文和有界 Tracking 超时。
/// </summary>
public sealed class GuidanceDiagnosticsStageSixEditModeTests
{
    /// <summary>验证请求事件包含完整方位与候选诊断，且不改变后端请求结构。</summary>
    [Test]
    public void ExperimentLogger_WritesHeadingProfileAndCandidateAssessmentsWithoutChangingRequest()
    {
        var root = new GameObject("StageSixRequestDiagnosticsTest");
        ExperimentLogger logger = null;
        string logPath = null;
        try
        {
            logger = CreateLogger(root, "P-stage-six-request");
            logPath = logger.LogPath;
            var risk = RiskWithTenHeadings();
            var action = new GuidanceAction(GuidanceActionType.TurnInPlace, TurnSide.Right, 60f, true);
            var candidate = Candidate("E5", 60f, NavigationTargetRole.FinalTarget);
            var blocked = Candidate("E3", -30f, NavigationTargetRole.NavigableDetour);
            var set = new NavigationCandidateSet(
                new[] { "E5" },
                new[] { "E5" },
                Array.Empty<string>(),
                new[] { candidate },
                7,
                new[]
                {
                    new CandidateAssessment(
                        candidate,
                        Bearing(candidate),
                        action,
                        CandidateExecutionState.Blocked,
                        "target_heading_not_safe"),
                    new CandidateAssessment(
                        blocked,
                        Bearing(blocked),
                        new GuidanceAction(GuidanceActionType.TurnInPlace, TurnSide.Left, -30f, true),
                        CandidateExecutionState.Executable,
                        string.Empty,
                        false,
                        "detour_cooldown")
                });
            var reservation = new CandidateSetReservation(
                "set-6", "request-6", "decision-6", "T2_main_target", 3,
                "ours_full", risk.calibration_id, risk.risk_snapshot_id, risk.risk_revision,
                "E5", set, 1, DateTime.UtcNow.ToString("o"));
            var request = RequestFor(risk);

            logger.LogRequest(request, risk, reservation);
            logger.Flush();

            var record = FindEvent(logger.LogPath, "guidance.requested");
            Assert.That(record["data"]["heading_profile"], Has.Count.EqualTo(10));
            var assessments = (JArray)record["data"]["candidate_assessments"];
            Assert.That(assessments, Has.Count.EqualTo(2));
            Assert.That((string)assessments[0]["target_id"], Is.EqualTo("E5"));
            Assert.That((string)assessments[0]["execution_state"], Is.EqualTo("blocked"));
            Assert.That((string)assessments[0]["execution_reason"], Is.EqualTo("target_heading_not_safe"));
            Assert.That((string)assessments[1]["semantic_exclusion_reason"], Is.EqualTo("detour_cooldown"));
            Assert.That(record["data"]["request"]["candidate_assessments"], Is.Null);
        }
        finally
        {
            CloseAndDelete(logger, logPath);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证恢复事件记录状态转换原因与冻结恢复计划。</summary>
    [Test]
    public void ExperimentLogger_WritesStructuredRecoveryPlanAndTransition()
    {
        var root = new GameObject("StageSixRecoveryDiagnosticsTest");
        ExperimentLogger logger = null;
        string logPath = null;
        try
        {
            logger = CreateLogger(root, "P-stage-six-recovery");
            logPath = logger.LogPath;
            var risk = RiskWithTenHeadings();
            var plan = new RecoveryHeadingPlan(
                new HeadingSample(60f, ForwardAt(60f), 2.4f, HeadingSafety.Safe),
                2.75f);
            var transition = new GuidanceRecoveryTransitionInfo(
                GuidanceRecoveryState.SelectingHeading,
                GuidanceRecoveryState.WaitingForUserTurn,
                "recovery_heading_selected",
                plan);

            logger.LogRecoveryTransition(transition, LogTraceContext.Empty, null, risk);
            logger.Flush();

            var record = FindEvent(logger.LogPath, "rdw.recovery_state_changed");
            Assert.That((string)record["data"]["previous_state"], Is.EqualTo("selecting_heading"));
            Assert.That((string)record["data"]["next_state"], Is.EqualTo("waiting_for_user_turn"));
            Assert.That((string)record["data"]["transition_reason"], Is.EqualTo("recovery_heading_selected"));
            Assert.That((float)record["data"]["recovery_plan"]["relative_yaw_deg"], Is.EqualTo(60f));
            Assert.That((float)record["data"]["recovery_plan"]["clearance_m"], Is.EqualTo(2.4f));
            Assert.That((float)record["data"]["recovery_plan"]["score"], Is.EqualTo(2.75f));
        }
        finally
        {
            CloseAndDelete(logger, logPath);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证遥测记录保留生产风险模型的全部十个方位样本。</summary>
    [Test]
    public void ExperimentLogger_TelemetryCarriesAllTenHeadingSamples()
    {
        var root = new GameObject("StageSixTelemetryDiagnosticsTest");
        ExperimentLogger logger = null;
        string logPath = null;
        try
        {
            logger = CreateLogger(root, "P-stage-six-telemetry");
            logPath = logger.LogPath;
            logger.LogRiskSnapshot(
                LogTraceContext.Empty,
                RiskWithTenHeadings(),
                null,
                true,
                true,
                new Vector3(1f, 1.65f, 2f),
                Vector3.forward);
            logger.Flush();

            var record = JObject.Parse(ReadLines(logger.TelemetryLogPath).Single());
            var profile = (JArray)record["data"]["heading_profile"];
            Assert.That(profile, Has.Count.EqualTo(10));
            Assert.That((float)profile[0]["relative_yaw_deg"], Is.EqualTo(0f));
            Assert.That((float)profile[8]["relative_yaw_deg"], Is.EqualTo(135f));
            Assert.That((string)profile[9]["safety"], Is.EqualTo("safe"));
            Assert.That(profile[9]["physical_forward"]["x"], Is.Not.Null);
            Assert.That(profile[9]["clearance_m"], Is.Not.Null);
        }
        finally
        {
            CloseAndDelete(logger, logPath);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证 Tracking 持续丢失会在配置的有界时间内自动结束运行会话。</summary>
    [UnityTest]
    public IEnumerator ExperimentDashboard_TrackingTimeoutEndsRunningSessionInBoundedTime()
    {
        var root = new GameObject("StageSixTrackingTimeoutTest");
        root.SetActive(false);
        var task = root.AddComponent<GuidanceTaskController>();
        task.ConfigureTask(new GuidanceTaskDefinition
        {
            startExhibitId = "E1",
            finalTargetId = "E5",
            routeTargetIds = new[] { "E1", "E5" }
        });
        task.StartTask();
        var dashboard = root.AddComponent<ExperimentDashboard>();
        SetPrivateField(dashboard, "taskController", task);
        SetPrivateField(dashboard, "trackingRecoveryTimeoutSeconds", 0.1f);
        SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

        InvokePrivate(dashboard, "HandleTrackingStateChanged", false);
        var routine = (IEnumerator)InvokePrivate(dashboard, "RuntimePrerequisiteRecoveryTimeoutRoutine");
        Assert.That(routine, Is.Not.Null);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (dashboard.RunState == ExperimentRunState.Running && stopwatch.Elapsed.TotalSeconds < 1d)
        {
            routine.MoveNext();
            yield return null;
        }

        Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Ended));
        Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
        UnityEngine.Object.DestroyImmediate(root);
    }

    /// <summary>验证候选历史规则持续输出四个稳定的语义排除原因码。</summary>
    [Test]
    public void SemanticTargetSelector_UsesStableSemanticExclusionReasonCodes()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5", 2);
        state.completedTurnCueAnchorIds = new[] { "turn-cue" };
        state.visitedDetourIds = new[] { "visited-detour" };
        state.coolingDownDetourIds = new[] { "cooldown-detour" };
        state.detourCooldownUntilUtcTicks = new[] { DateTime.UtcNow.AddMinutes(1d).Ticks };
        state.consecutiveDetourCount = state.maxConsecutiveDetours;

        Assert.That(SemanticExclusionReason(state, "turn-cue", NavigationTargetRole.TurnCueAnchor),
            Is.EqualTo("turn_cue_completed"));
        Assert.That(SemanticExclusionReason(state, "visited-detour", NavigationTargetRole.NavigableDetour),
            Is.EqualTo("detour_visited"));
        Assert.That(SemanticExclusionReason(state, "cooldown-detour", NavigationTargetRole.NavigableDetour),
            Is.EqualTo("detour_cooldown"));
        Assert.That(SemanticExclusionReason(state, "limited-detour", NavigationTargetRole.NavigableDetour),
            Is.EqualTo("detour_limit_reached"));
    }

    /// <summary>验证低风险 TurnCue 软失败在无替代候选时进入 Hold/Replan，而不启动 Reset。</summary>
    [Test]
    public void GuidanceCoordinator_TurnCueSoftFailureWithoutAlternativeHoldsWithoutReset()
    {
        var root = new GameObject("StageSixTurnCueSoftFailureTest");
        root.SetActive(false);
        try
        {
            CreateCoordinatorHarness(root, out var coordinator, out var task, out var reset);
            RuntimeInterventionInfo intervention = null;
            coordinator.RuntimeIntervention += info => intervention = info;
            SetPrivateField(coordinator, "requestPhase", GuidanceRequestPhase.Executing);

            InvokePrivate(coordinator, "TriggerSoftRerouteFromLatestRisk", "turn_stopped_timeout");

            Assert.That(intervention, Is.Not.Null);
            Assert.That(intervention.Decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
            Assert.That(intervention.StartReset, Is.False);
            Assert.That(intervention.Reason, Does.Contain("turn_stopped_timeout"));
            Assert.That(coordinator.IsHoldingForReplan, Is.True);
            Assert.That(coordinator.IsAutomaticRecoveryActive, Is.False);
            Assert.That(coordinator.AutomaticResetAttempts, Is.Zero);
            Assert.That(coordinator.RecoveryState, Is.EqualTo(GuidanceRecoveryState.Inactive));
            Assert.That(reset.State, Is.EqualTo(ResetLifecycleState.Inactive));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Navigating));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static ExperimentLogger CreateLogger(GameObject root, string participantId)
    {
        var logger = root.AddComponent<ExperimentLogger>();
        SetPrivateField(logger, "logFolderRelativeToProject", "Temp/VirtualGuidanceStageSixLogs");
        SetPrivateField(logger, "mirrorToUnityConsole", false);
        logger.StartSession(ExperimentConditionMatrix.Build(5, 1, 1, participantId));
        return logger;
    }

    private static GuidanceRequest RequestFor(PhysicalRiskState risk)
    {
        return new GuidanceRequest
        {
            session_id = "session-6",
            calibration_id = risk.calibration_id,
            request_id = "request-6",
            decision_id = "decision-6",
            candidate_set_id = "set-6",
            state_version = 3,
            risk_snapshot_id = risk.risk_snapshot_id,
            candidate_ids = new[] { "E5" },
            model_candidate_ids = new[] { "E5" },
            allowed_candidate_ids = Array.Empty<string>()
        };
    }

    private static NavigationCandidate Candidate(string id, float yaw, NavigationTargetRole role)
    {
        return new NavigationCandidate(
            id, role, yaw < 0f ? "left" : "right", id, string.Empty, string.Empty,
            Array.Empty<string>(), Array.Empty<string>(), yaw, 2f,
            yaw < 0f ? BearingLabel.Left : BearingLabel.Right, 1);
    }

    private static TargetBearing Bearing(NavigationCandidate candidate)
    {
        return new TargetBearing(
            candidate.TargetId,
            candidate.RelativeYawDegrees,
            candidate.DistanceMeters,
            candidate.DisplayLabel,
            candidate.PoseRevision);
    }

    private static PhysicalRiskState RiskWithTenHeadings()
    {
        var yaws = new[] { 0f, -30f, 30f, -60f, 60f, -90f, 90f, -135f, 135f, 180f };
        return new PhysicalRiskState(
            riskSnapshotId: "risk-stage-6",
            riskRevision: 6,
            calibrationId: "calibration-stage-6",
            physicalSpace: "wide_room",
            physicalPosition: new Vector2(2f, 2f),
            physicalForward: Vector2.up,
            clearanceForward: 1f,
            clearanceLeft: 1.5f,
            clearanceRight: 2f,
            boundaryRisk: "low",
            resetRisk: "low",
            safeDirections: new[] { "forward", "left", "right" },
            headingProfile: yaws.Select((yaw, index) =>
                new HeadingSample(yaw, ForwardAt(yaw), 1f + index * 0.1f, HeadingSafety.Safe)));
    }

    private static Vector2 ForwardAt(float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }

    private static string SemanticExclusionReason(
        GuidanceRuntimeState state,
        string targetId,
        NavigationTargetRole role)
    {
        var method = typeof(SemanticTargetSelector).GetMethod(
            "GetSemanticExclusionReason",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (string)method.Invoke(null, new object[] { state, targetId, role });
    }

    private static void CreateCoordinatorHarness(
        GameObject root,
        out GuidanceCoordinator coordinator,
        out GuidanceTaskController task,
        out PauseResetController reset)
    {
        var virtualUser = new GameObject("StageSixVirtualUser");
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
        var risk = root.AddComponent<PhysicalRiskEvaluator>();
        risk.Configure(space, pose, calibration);

        task = root.AddComponent<GuidanceTaskController>();
        task.ConfigureTask(new GuidanceTaskDefinition
        {
            condition = "ours_full",
            startExhibitId = "E1",
            finalTargetId = "MissingFinal",
            routeTargetIds = new[] { "E1", "MissingFinal" },
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

    private static JObject FindEvent(string path, string eventName)
    {
        return ReadLines(path)
            .Select(JObject.Parse)
            .Single(record => (string)record["event_name"] == eventName);
    }

    private static string[] ReadLines(string path)
    {
        var lines = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
        }
        return lines.ToArray();
    }

    private static void CloseAndDelete(ExperimentLogger logger, string logPath)
    {
        logger?.EndSession("test_cleanup", LogTraceContext.Empty);
        var folder = !string.IsNullOrWhiteSpace(logPath) ? Path.GetDirectoryName(logPath) : string.Empty;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, name);
        field.SetValue(target, value);
    }

    private static object InvokePrivate(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, name);
        return method.Invoke(target, arguments);
    }

    private sealed class NoOpRequestClient : IGuidanceRequestClient
    {
        /// <summary>测试桩始终没有在途请求。</summary>
        public bool RequestInFlight => false;

        /// <summary>测试桩忽略请求发送。</summary>
        public void RequestGuidance(GuidanceRequestContext context)
        {
        }

        /// <summary>测试桩忽略取消操作。</summary>
        public void CancelPendingRequest()
        {
        }
    }
}
