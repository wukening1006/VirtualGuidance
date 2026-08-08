using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 使用生产 PhysicalRiskEvaluator 验证 Step 1 的运行期安全决策事实与决策表。
/// </summary>
public sealed class Step1SafetyDecisionEditModeTests
{
    /// <summary>验证安全计划方向继续执行，不触发安全干预。</summary>
    [Test]
    public void RuntimeSafety_SafeDirectionContinues()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(1f, 2.5f), 0f);
            var input = harness.CreateInput(risk, false, false);

            Assert.That(input.RequiresSafetyIntervention, Is.False);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.Continue));
        }
    }

    /// <summary>验证前方不安全且存在安全语义目标时执行 SoftReroute。</summary>
    [Test]
    public void RuntimeSafety_UnsafeWithSafeSemanticCandidateSoftReroutes()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(1f, 4.5f), 0f);
            var input = harness.CreateInput(risk, true, true);

            Assert.That(risk.unsafe_directions, Does.Contain("forward"));
            Assert.That(input.HasRecoveryHeading, Is.True);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
        }
    }

    /// <summary>验证前方不安全、无安全语义目标但有恢复航向时执行 PauseReset。</summary>
    [Test]
    public void RuntimeSafety_UnsafeWithoutSemanticCandidateButWithRecoveryHeadingPausesForReset()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(1f, 4.5f), 0f);
            var input = harness.CreateInput(risk, true, false);

            Assert.That(risk.reset_required, Is.False, "存在恢复航向时必须保持 unity-risk-policy-v1 的 reset_required=false 语义。");
            Assert.That(input.RequiresSafetyIntervention, Is.True);
            Assert.That(input.HasSafeSemanticCandidate, Is.False);
            Assert.That(input.HasRecoveryHeading, Is.True);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
        }
    }

    /// <summary>验证没有任何恢复航向时只允许 HardStop。</summary>
    [Test]
    public void RuntimeSafety_NoRecoveryHeadingHardStops()
    {
        using (var harness = new RiskHarness())
        {
            var risk = harness.Evaluate(new Vector2(0.2f, 0.2f), 0f);
            var input = harness.CreateInput(risk, true, false);

            Assert.That(risk.reset_required, Is.True, "无可接受恢复航向时必须保持 unity-risk-policy-v1 的 reset_required=true 语义。");
            Assert.That(input.HasRecoveryHeading, Is.False);
            Assert.That(harness.Policy.Evaluate(input), Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
        }
    }

    private sealed class RiskHarness : System.IDisposable
    {
        private readonly GameObject root;
        private readonly UserPoseTracker pose;
        private readonly PhysicalRiskEvaluator evaluator;
        private readonly RecoveryHeadingPlanner recoveryPlanner = new RecoveryHeadingPlanner();

        public RuntimeGuidanceSafetyPolicy Policy { get; } = new RuntimeGuidanceSafetyPolicy();

        public RiskHarness()
        {
            root = new GameObject("Step1SafetyDecisionHarness");
            pose = root.AddComponent<UserPoseTracker>();
            var space = root.AddComponent<PhysicalSpaceManager>();
            space.LoadSpaces();
            Assert.That(space.SetPhysicalSpace("narrow_room"), Is.True);
            var calibration = root.AddComponent<PhysicalSpaceCalibration>();
            calibration.Configure(space, pose);
            calibration.SetCalibrationPoint(new Vector2(1f, 2.5f), 0f);
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
            bool hasSafeSemanticCandidate)
        {
            var hasRecoveryHeading = recoveryPlanner.Plan(risk.heading_profile).IsSuccess;
            return new RuntimeGuidanceSafetyInput(
                "step1-decision",
                "E5",
                "move_forward_slowly",
                risk,
                evaluator.IsTrackingValid,
                evaluator.IsCalibrationValid,
                evaluator.IsRiskIdentityCurrent(risk),
                "forward",
                risk.clearance_forward,
                true,
                evaluator.IsCriticalClearance(risk.clearance_forward),
                ResetLifecycleState.Inactive,
                requiresSafetyIntervention ? 2 : 0,
                requiresSafetyIntervention,
                hasSafeSemanticCandidate,
                hasRecoveryHeading);
        }

        public void Dispose()
        {
            Object.DestroyImmediate(root);
        }
    }
}
