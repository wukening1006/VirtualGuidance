using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证请求复验、Detour 历史和 Rotation Gain 的阶段三安全规则。
/// </summary>
public sealed class GuidancePlanningStageThreeEditModeTests
{
    /// <summary>验证 Detour 只有真正到达才计数，放弃后仅进入短暂冷却。</summary>
    [Test]
    public void GuidanceRuntimeState_DetourCountsOnArrivalAndCoolsDownAfterAbandonment()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5", 2);
        var detour = new GuidanceTarget("DetourA", NavigationTargetRole.NavigableDetour);

        Assert.That(state.TryReserveGuidanceTarget(detour, "decision-first"), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.Zero);
        Assert.That(state.AbandonReservation(), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.Zero);
        Assert.That(state.HasVisitedDetour(detour.TargetId), Is.False);
        Assert.That(state.IsDetourCoolingDown(detour.TargetId), Is.True);
        Assert.That(state.TryReserveGuidanceTarget(detour, "decision-during-cooldown"), Is.False);

        state.detourCooldownUntilUtcTicks[0] = DateTime.UtcNow.AddSeconds(-1d).Ticks;
        Assert.That(state.TryReserveGuidanceTarget(detour, "decision-after-cooldown"), Is.True);
        Assert.That(state.CompleteDetour(detour.TargetId), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.EqualTo(1));
        Assert.That(state.HasVisitedDetour(detour.TargetId), Is.True);

        state.AdvanceTo("E5", string.Empty);
        Assert.That(state.consecutiveDetourCount, Is.Zero);
        Assert.That(state.visitedDetourIds, Is.Empty);
        Assert.That(state.coolingDownDetourIds, Is.Empty);
    }

    /// <summary>验证候选原子提交只标记 Detour 已开始，不提前增加完成次数。</summary>
    [Test]
    public void GuidanceRuntimeState_CommittedDetourIncrementsOnlyAfterArrival()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", "E5", "E5", 1);
        var candidate = new NavigationCandidate(
            "DetourB",
            NavigationTargetRole.NavigableDetour,
            "left",
            "DetourB",
            string.Empty,
            "side_context",
            Array.Empty<string>(),
            Array.Empty<string>());
        var candidateSet = new NavigationCandidateSet(
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate.TargetId },
            new[] { candidate },
            1);
        var risk = new PhysicalRiskState(
            riskSnapshotId: "risk-detour",
            riskRevision: 1,
            calibrationId: "calibration-detour");

        Assert.That(
            state.TryReserveCandidateSet(
                candidateSet,
                "candidate-set-detour",
                "request-detour",
                "decision-detour",
                "ours_full",
                risk,
                out var reservation),
            Is.True);
        Assert.That(state.TryValidateCandidateSelection(reservation, candidate.TargetId, out var selected), Is.True);
        Assert.That(state.CommitCurrentSubgoal(reservation, selected, out _), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.Zero);

        Assert.That(state.CompleteDetour(candidate.TargetId), Is.True);
        Assert.That(state.consecutiveDetourCount, Is.EqualTo(1));
    }

    /// <summary>验证请求期间方向标签变化不会使身份未变的候选失效。</summary>
    [Test]
    public void SemanticTargetSelector_BearingLabelChangeKeepsSelectedCandidateCurrent()
    {
        var root = new GameObject("StageThreeBearingRevalidationTest");
        try
        {
            var user = new GameObject("StageThreeUser");
            user.transform.SetParent(root.transform, false);
            var target = CreateExhibit(root.transform, "StageThreeFinal", Vector3.forward * 2f);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(user.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var riskEvaluator = root.AddComponent<PhysicalRiskEvaluator>();
            var state = new GuidanceRuntimeState();
            state.Begin("T2_main_target", "E1", target.Id, target.Id);
            var risk = SafeHeadingRisk();
            var policy = ExperimentConditionMatrix.GetPolicy("ours_full");
            var candidateSet = selector.BuildCandidateSet(state, risk, policy);
            Assert.That(
                state.TryReserveCandidateSet(
                    candidateSet,
                    "candidate-set-bearing",
                    "request-bearing",
                    "decision-bearing",
                    policy.Condition,
                    risk,
                    out var reservation),
                Is.True);
            Assert.That(reservation.TryGetCandidate(target.Id, out var frozen), Is.True);

            user.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            Assert.That(targets.TryGetTargetBearing(target.Id, out var latestBearing), Is.True);
            Assert.That(latestBearing.DisplayLabel, Is.Not.EqualTo(frozen.DisplayLabel));

            Assert.That(
                selector.IsSelectedCandidateCurrent(
                    reservation,
                    target.Id,
                    state,
                    risk,
                    policy,
                    riskEvaluator,
                    out var reason),
                Is.True,
                reason);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证无关方向 critical 不会中止当前计划的安全转向。</summary>
    [Test]
    public void RotationGainController_IgnoresCriticalRiskOutsidePlannedHeading()
    {
        var root = new GameObject("StageThreeDirectionalRiskTest");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.SetGainEnabled(true);
            gain.BeginPendingTurn(1, 90f, "decision-directional-risk");
            var unrelatedCritical = new PhysicalRiskState(
                physicalPosition: Vector2.zero,
                boundaryContext: SquareBoundary(),
                boundaryRisk: "critical",
                resetRisk: "critical",
                headingProfile: new[]
                {
                    new HeadingSample(90f, Vector2.left, 2f, HeadingSafety.Safe),
                    new HeadingSample(-90f, Vector2.right, 0.2f, HeadingSafety.Critical)
                });

            InvokeRiskChanged(gain, unrelatedCritical);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));

            var plannedCritical = new PhysicalRiskState(
                physicalPosition: Vector2.zero,
                boundaryContext: SquareBoundary(),
                headingProfile: new[]
                {
                    new HeadingSample(90f, Vector2.left, 0.2f, HeadingSafety.Critical),
                    new HeadingSample(-90f, Vector2.right, 2f, HeadingSafety.Safe)
                });
            InvokeRiskChanged(gain, plannedCritical);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("planned_heading_critical"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证短暂停顿先重新提示同方向转动，重试耗尽后才中止。</summary>
    [Test]
    public void RotationGainController_RetriesSameDirectionBeforeStopTimeoutAbort()
    {
        var root = new GameObject("StageThreeTurnRetryTest");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.ConfigureLimits(0f, 100f, 30f, 0.5f, 1, 0f);
            gain.SetGainEnabled(true);
            var turnRequests = new List<RotationGainEventInfo>();
            gain.LifecycleEventOccurred += info =>
            {
                if (info.EventType == RotationGainEventType.TurnRequested)
                {
                    turnRequests.Add(info);
                }
            };

            gain.BeginPendingTurn(1, 30f, "decision-retry");
            gain.ProcessTurnSample(true, 5f, 0.1f);
            gain.ProcessTurnSample(false, 0f, 0.5f);

            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.WaitingForUserTurn));
            Assert.That(gain.TurnStopRetryCount, Is.EqualTo(1));
            Assert.That(turnRequests, Has.Count.EqualTo(2));
            Assert.That(turnRequests[1].Reason, Is.EqualTo("turn_retry"));

            gain.ProcessTurnSample(true, 5f, 0.1f);
            gain.ProcessTurnSample(false, 0f, 0.5f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Aborted));
            Assert.That(gain.LastTerminationReason, Is.EqualTo("turn_stopped_timeout"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证达到目标转角后必须完成稳定窗口才请求风险复验。</summary>
    [Test]
    public void RotationGainController_RequiresStableWindowBeforeCompletion()
    {
        var root = new GameObject("StageThreeStableCompletionTest");
        try
        {
            var gain = root.AddComponent<RotationGainController>();
            Assert.That(gain.ConfigureRotationRoot(root.transform), Is.True);
            gain.ConfigureLimits(0f, 100f, 30f, 0.5f, 1, 0.4f);
            gain.SetGainEnabled(true);

            gain.BeginPendingTurn(1, 30f, "decision-stable");
            gain.ProcessTurnSample(true, 30f, 0.1f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Stabilizing));

            gain.ProcessTurnSample(false, 0f, 0.39f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Stabilizing));
            gain.ProcessTurnSample(false, 0f, 0.02f);
            Assert.That(gain.State, Is.EqualTo(RotationGainController.GainState.Reevaluating));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static PhysicalRiskState SafeHeadingRisk()
    {
        return new PhysicalRiskState(
            riskSnapshotId: "risk-bearing",
            riskRevision: 1,
            calibrationId: "calibration-bearing",
            physicalSpace: "room-bearing",
            headingProfile: new[]
            {
                new HeadingSample(0f, Vector2.up, 2f, HeadingSafety.Safe),
                new HeadingSample(-90f, Vector2.right, 2f, HeadingSafety.Safe),
                new HeadingSample(90f, Vector2.left, 2f, HeadingSafety.Safe)
            });
    }

    private static Vector2[] SquareBoundary()
    {
        return new[]
        {
            new Vector2(-2f, -2f),
            new Vector2(2f, -2f),
            new Vector2(2f, 2f),
            new Vector2(-2f, 2f)
        };
    }

    private static ExhibitMetadata CreateExhibit(Transform parent, string id, Vector3 position)
    {
        var exhibit = new GameObject(id);
        exhibit.transform.SetParent(parent, false);
        exhibit.transform.position = position;
        var metadata = exhibit.AddComponent<ExhibitMetadata>();
        metadata.Configure(
            id,
            id,
            Array.Empty<string>(),
            string.Empty,
            "main_target",
            Array.Empty<string>(),
            exhibit.transform,
            exhibit.transform);
        return metadata;
    }

    private static void InvokeRiskChanged(RotationGainController gain, PhysicalRiskState riskState)
    {
        var method = typeof(RotationGainController).GetMethod(
            "HandleRiskStateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method.Invoke(gain, new object[] { riskState });
    }
}
