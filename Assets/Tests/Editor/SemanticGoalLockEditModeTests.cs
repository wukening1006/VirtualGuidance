using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证语义目标锁、候选预留释放和 SoftReroute 默认关闭策略。
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

    /// <summary>验证安全策略默认不选择 SoftReroute。</summary>
    [Test]
    public void RuntimeSafetyPolicy_DefaultsSoftRerouteToDisabled()
    {
        var decision = new RuntimeGuidanceSafetyPolicy().Evaluate(CreateUnsafeInput());

        Assert.That(decision, Is.EqualTo(RuntimeGuidanceSafetyDecision.PauseReset));
    }

    /// <summary>验证只有显式启用开关后才会选择 SoftReroute。</summary>
    [Test]
    public void RuntimeSafetyPolicy_UsesSoftRerouteOnlyWhenEnabled()
    {
        var policy = new RuntimeGuidanceSafetyPolicy { SoftRerouteEnabled = true };

        Assert.That(policy.Evaluate(CreateUnsafeInput()), Is.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
    }

    /// <summary>验证 Coordinator 的计划方向失效入口不能绕过默认关闭开关。</summary>
    [Test]
    public void CoordinatorUnavailableDirection_DoesNotBypassDisabledSoftReroute()
    {
        var root = new GameObject("SoftRerouteDisabledTest");
        try
        {
            var coordinator = root.AddComponent<GuidanceCoordinator>();
            var decisions = new List<RuntimeGuidanceSafetyDecision>();
            coordinator.RuntimeIntervention += info => decisions.Add(info.Decision);
            var risk = CreateRisk();
            var routed = (bool)InvokePrivate(
                coordinator,
                "TryRouteUnavailablePlannedDirection",
                CreateUnsafeInput(false),
                new GuidanceTarget("E3", NavigationTargetRole.NavigableDetour),
                risk);

            Assert.That(coordinator.SoftRerouteSafetyPolicyEnabled, Is.False);
            Assert.That(routed, Is.False);
            Assert.That(decisions, Has.None.EqualTo(RuntimeGuidanceSafetyDecision.SoftReroute));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static GuidanceRuntimeState CreateNavigatingState()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5");
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
            Array.Empty<SemanticEdgeDefinition>(),
            Array.Empty<SpatialEdgeDefinition>());
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

    private static object InvokePrivate(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, methodName);
        return method.Invoke(target, arguments);
    }
}
