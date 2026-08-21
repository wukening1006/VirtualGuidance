using System;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证语义目标锁、候选预留释放和运行期安全策略。
/// </summary>
public sealed class SemanticGoalLockEditModeTests
{
    /// <summary>验证候选集冻结后不能并发创建第二轮语义规划。</summary>
    [Test]
    public void CandidateSetReservation_BlocksConcurrentPlanning()
    {
        var state = CreateNavigatingState();

        Assert.That(ReserveCandidateSet(state, "first", out _), Is.True);
        Assert.That(state.HasActiveSemanticPlanning, Is.True);
        Assert.That(ReserveCandidateSet(state, "second", out _), Is.False);
    }

    /// <summary>验证提交候选后由目标预留接管语义目标锁。</summary>
    [Test]
    public void CommittedCandidate_LocksSemanticGoal()
    {
        var state = CreateCommittedState(out var candidateReservation);

        Assert.That(candidateReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Reserved));
        Assert.That(state.ActiveCandidateSetReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Committed));
        Assert.That(state.HasActiveSemanticPlanning, Is.False);
        Assert.That(state.HasLockedSemanticGoal, Is.True);
        Assert.That(ReserveCandidateSet(state, "blocked", out _), Is.False);
    }

    /// <summary>OURS 动态提交普通路线目标时，必须同步任务目标以保持 Reset 锁身份一致。</summary>
    [Test]
    public void CommittedRouteSubgoal_SynchronizesNextTargetId()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", string.Empty, "E5");
        state.ConfigureIntermediateVisitPolicy("测试任务", 3, 4);
        var candidate = new NavigationCandidate(
            "E8",
            NavigationTargetRole.RouteSubgoal,
            "forward",
            null,
            Array.Empty<SemanticEdgeDefinition>());
        var candidateSet = new NavigationCandidateSet(
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate },
            1);

        Assert.That(state.TryReserveCandidateSet(
            candidateSet,
            "candidate-set-ours-route",
            "request-ours-route",
            "decision-ours-route",
            "ours_rdw_grounded_topk",
            CreateRisk(),
            out var reservation), Is.True);
        Assert.That(state.TryValidateCandidateSelection(reservation, "E8", out var selected), Is.True);
        Assert.That(state.CommitCurrentSubgoal(reservation, selected, out var targetReservation), Is.True);

        Assert.That(state.CurrentGuidanceTarget.TargetId, Is.EqualTo("E8"));
        Assert.That(state.nextTargetId, Is.EqualTo("E8"));
        Assert.That(targetReservation.TargetId, Is.EqualTo("E8"));
        Assert.That(state.HasLockedSemanticGoal, Is.True);
    }

    /// <summary>验证取消已提交目标时同步释放其候选集，并允许重新规划。</summary>
    [Test]
    public void AbandonCommittedTarget_ReleasesCandidateSetLock()
    {
        var state = CreateCommittedState(out _);

        Assert.That(state.AbandonReservation(), Is.True);
        Assert.That(state.ActiveCandidateSetReservation.Phase, Is.EqualTo(CandidateSetReservationPhase.Abandoned));
        Assert.That(state.HasLockedSemanticGoal, Is.False);
        Assert.That(state.HasActiveSemanticPlanning, Is.False);
        Assert.That(ReserveCandidateSet(state, "replacement", out _), Is.True);
    }

    /// <summary>验证正常到达会完成目标并允许规划下一段。</summary>
    [Test]
    public void ArrivedTarget_ReleasesSemanticGoalLock()
    {
        var state = CreateCommittedState(out _);

        state.AdvanceTo("E5", "E6");

        Assert.That(state.HasLockedSemanticGoal, Is.False);
        Assert.That(state.HasActiveSemanticPlanning, Is.False);
        Assert.That(ReserveCandidateSet(state, "next-leg", out _), Is.True);
    }

    /// <summary>Reset 暂停只改变执行阶段，不得释放当前目标或推进局部计划。</summary>
    [Test]
    public void PauseForReset_PreservesLockedTargetAndPlanStep()
    {
        var state = CreateCommittedState(out _);
        var reservation = state.ActiveReservation;
        var version = state.stateVersion;
        var nextTargetId = state.nextTargetId;

        state.PauseForReset();

        Assert.That(state.phase, Is.EqualTo(GuidanceRuntimePhase.PausedForReset));
        Assert.That(state.stateVersion, Is.EqualTo(version));
        Assert.That(state.nextTargetId, Is.EqualTo(nextTargetId));
        Assert.That(state.ActiveReservation, Is.SameAs(reservation));
        Assert.That(state.HasLockedSemanticGoal, Is.True);
    }

    /// <summary>SafeHeading Mapping 的 Virtual yaw 预测包含 Physical turn 与注入 yaw。</summary>
    [Test]
    public void ResetYawPrediction_IncludesPhysicalTurnAndInjectedYaw()
    {
        var root = new GameObject("ResetYawPredictionTest");
        try
        {
            var controller = root.AddComponent<PauseResetController>();

            Assert.That(
                controller.PredictVirtualYawDelta(Vector2.up, Vector2.right),
                Is.EqualTo(180f).Within(0.001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>Reset 后延续评估只返回冻结目标，并应用 Mapping 修正后的相对偏航。</summary>
    [Test]
    public void FixedTargetContinuation_NeverSelectsAnotherSemanticTarget()
    {
        var evaluator = new CapturingHeadingEvaluator();
        var fixedTarget = new NavigationCandidate(
            "E5",
            NavigationTargetRole.RouteSubgoal,
            "right",
            null,
            Array.Empty<SemanticEdgeDefinition>(),
            relativeYawDegrees: 30f);

        var assessment = new PostResetContinuationEvaluator().EvaluateFixedTarget(
            CreateRisk(),
            Vector2.right,
            180f,
            fixedTarget,
            evaluator);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.DirectTarget));
        Assert.That(assessment.TargetId, Is.EqualTo("E5"));
        Assert.That(
            Vector2.Angle(evaluator.LastForward, RdwYawConvention.YawToForward(-60f)),
            Is.LessThan(0.01f));
    }

    /// <summary>验证可信物理事实下的安全介入只进入 PauseReset。</summary>
    [Test]
    public void RuntimeSafetyPolicy_TrustedUnsafeInputRequestsPauseReset()
    {
        var decision = new RuntimeGuidanceSafetyPolicy().Evaluate(CreateUnsafeInput());

        Assert.That(decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
    }

    /// <summary>验证安全事实不完整或计划方向不可解析时必须停止。</summary>
    [Test]
    public void RuntimeSafetyPolicy_InvalidInputRequestsHardStop()
    {
        var policy = new RuntimeGuidanceSafetyPolicy();

        Assert.That(
            policy.Evaluate(CreateUnsafeInput(plannedDirectionAvailable: false)),
            Is.EqualTo(RuntimeGuidanceSafetyDecision.HardStop));
    }

    private static GuidanceRuntimeState CreateNavigatingState()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5");
        state.ConfigureIntermediateVisitPolicy("测试任务", 0, 0);
        return state;
    }

    private static GuidanceRuntimeState CreateCommittedState(out CandidateSetReservation reservation)
    {
        var state = CreateNavigatingState();
        Assert.That(ReserveCandidateSet(state, "committed", out reservation), Is.True);
        Assert.That(state.TryValidateCandidateSelection(reservation, "E5", out var candidate), Is.True);
        Assert.That(state.CommitCurrentSubgoal(reservation, candidate, out _), Is.True);
        return state;
    }

    private static bool ReserveCandidateSet(
        GuidanceRuntimeState state,
        string identity,
        out CandidateSetReservation reservation)
    {
        var candidate = new NavigationCandidate(
            "E5",
            NavigationTargetRole.FinalTarget,
            "forward",
            null,
            Array.Empty<SemanticEdgeDefinition>());
        var candidateSet = new NavigationCandidateSet(
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate },
            1);
        return state.TryReserveCandidateSet(
            candidateSet,
            $"candidate-set-{identity}",
            $"request-{identity}",
            $"decision-{identity}",
            "ours_full",
            CreateRisk(),
            out reservation);
    }

    private static RuntimeGuidanceSafetyInput CreateUnsafeInput(bool plannedDirectionAvailable = true)
    {
        return new RuntimeGuidanceSafetyInput(
            "decision-safety",
            "E5",
            "move_forward_slowly",
            CreateRisk(),
            true,
            true,
            true,
            "forward",
            0.1f,
            plannedDirectionAvailable,
            true,
            ResetLifecycleState.Inactive,
            2,
            true,
            true);
    }

    private static PhysicalRiskState CreateRisk()
    {
        return new PhysicalRiskState(
            riskSnapshotId: "risk-goal-lock",
            riskRevision: 1,
            calibrationId: "calibration-goal-lock",
            physicalPosition: Vector2.zero,
            physicalForward: Vector2.up,
            boundaryContext: new[]
            {
                new Vector2(-2f, -2f),
                new Vector2(2f, -2f),
                new Vector2(2f, 2f),
                new Vector2(-2f, 2f)
            });
    }

    private sealed class CapturingHeadingEvaluator : IPhysicalHeadingEvaluator
    {
        public Vector2 LastForward { get; private set; }

        public bool TryEvaluateAbsoluteHeading(
            PhysicalRiskState riskState,
            Vector2 absolutePhysicalForward,
            out HeadingSample sample)
        {
            LastForward = absolutePhysicalForward.normalized;
            sample = new HeadingSample(0f, LastForward, 2f, HeadingSafety.Safe);
            return true;
        }
    }
}
