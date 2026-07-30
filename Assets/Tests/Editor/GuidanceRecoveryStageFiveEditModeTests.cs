using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

/// <summary>
/// 验证第五阶段的恢复事件、参与者 UI 与自动终止边界。
/// </summary>
public sealed class GuidanceRecoveryStageFiveEditModeTests
{
    [Test]
    public void PauseResetController_EventCarriesFrozenPlanWithoutWritingRejectionPanel()
    {
        var root = new GameObject("StageFiveResetEventTest");
        try
        {
            var rejectionRoot = new GameObject("LegacyRejectionPanel");
            rejectionRoot.transform.SetParent(root.transform, false);
            var rejectionText = rejectionRoot.AddComponent<TextMeshProUGUI>();
            var rejection = root.AddComponent<RejectionPanel>();
            rejection.Configure(rejectionText, rejectionRoot);
            rejectionText.text = "legacy sentinel";
            rejectionRoot.SetActive(false);

            var reset = root.AddComponent<PauseResetController>();
            reset.Configure(null, rejection);
            var plan = RecoveryPlan(-60f, new Vector2(-0.8660254f, 0.5f));
            ResetEventInfo started = null;
            reset.ResetStarted += info => started = info;

            Assert.That(reset.PauseForReset("stage five", plan), Is.True);

            Assert.That(started, Is.Not.Null);
            Assert.That(started.RecoveryPlan, Is.SameAs(plan));
            Assert.That(started.RelativeYawDegrees, Is.EqualTo(-60f).Within(0.01f));
            Assert.That(started.ApproximateTurnDegrees, Is.EqualTo(60f).Within(0.01f));
            Assert.That(rejectionText.text, Is.EqualTo("legacy sentinel"));
            Assert.That(rejectionRoot.activeSelf, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ParticipantUiController_ShowsDirectionAngleAndReplanningWithoutResearcherWait()
    {
        var root = new GameObject("StageFiveParticipantUiTest");
        try
        {
            var guidance = new GameObject("Guidance").AddComponent<CanvasGroup>();
            guidance.transform.SetParent(root.transform, false);
            var safety = new GameObject("Safety").AddComponent<CanvasGroup>();
            safety.transform.SetParent(root.transform, false);
            var safetyText = safety.gameObject.AddComponent<TextMeshProUGUI>();
            var controller = root.AddComponent<ParticipantUiController>();
            controller.Configure(guidance, safety, null, null, null, safetyText);
            var info = new ResetEventInfo(
                RecoveryPlan(60f, new Vector2(0.8660254f, 0.5f)),
                "critical corridor");

            controller.ShowRecoveryStarted(info);

            Assert.That(safetyText.text, Is.EqualTo("请停止移动，原地缓慢向右转约 60°"));
            Assert.That(safetyText.text, Does.Not.Contain("研究员"));
            controller.ShowRecoveryReplanning();
            Assert.That(safetyText.text, Is.EqualTo("请保持朝向，正在重新规划"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_HardStopEndsSessionInsteadOfEnteringSafetyHold()
    {
        var root = new GameObject("StageFiveHardStopTest");
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
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "taskController", task);
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

            InvokePrivate(
                dashboard,
                "HandleRuntimeIntervention",
                new RuntimeInterventionInfo(null, "no acceptable recovery heading"));

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Ended));
            Assert.That(dashboard.RunState, Is.Not.EqualTo(ExperimentRunState.SafetyHold));
            Assert.That(task.RuntimeState.phase, Is.EqualTo(GuidanceRuntimePhase.Ended));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_TrackingLossKeepsRunningWhileBoundedRecoveryIsPending()
    {
        var root = new GameObject("StageFiveTrackingGraceTest");
        root.SetActive(false);
        try
        {
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

            InvokePrivate(dashboard, "HandleTrackingStateChanged", false);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Running));
            Assert.That(GetPrivateField<bool>(dashboard, "runtimePrerequisiteRecoveryPending"), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ExperimentDashboard_InvalidCalibrationTerminatesAutomatically()
    {
        var root = new GameObject("StageFiveInvalidCalibrationTest");
        root.SetActive(false);
        try
        {
            var dashboard = root.AddComponent<ExperimentDashboard>();
            SetPrivateField(dashboard, "runState", ExperimentRunState.Running);

            InvokePrivate(dashboard, "HandleCalibrationStateChanged", PhysicalCalibrationState.Invalid);

            Assert.That(dashboard.RunState, Is.EqualTo(ExperimentRunState.Ended));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static RecoveryHeadingPlan RecoveryPlan(float yawDegrees, Vector2 physicalForward)
    {
        return new RecoveryHeadingPlan(
            new HeadingSample(yawDegrees, physicalForward, 1.8f, HeadingSafety.Safe),
            1f);
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);
    }

    private static object InvokePrivate(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, args);
    }
}
