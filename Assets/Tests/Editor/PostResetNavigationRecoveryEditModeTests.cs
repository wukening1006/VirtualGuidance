using System;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Reset 后导航延续等级与 Physical Safety 硬约束。
/// </summary>
public sealed class PostResetNavigationRecoveryEditModeTests
{
    /// <summary>当前任务目标可执行时必须优先于安全绕行。</summary>
    [Test]
    public void ContinuationEvaluator_DirectTargetOutranksSafeReroute()
    {
        var evaluator = new PostResetContinuationEvaluator();
        var physical = new SelectiveHeadingEvaluator(_ => HeadingSafety.Safe);
        var assessment = evaluator.EvaluateHeading(
            CreateRisk(),
            Vector2.up,
            1f,
            new[]
            {
                Candidate("detour", NavigationTargetRole.NavigableDetour, 0f),
                Candidate("final", NavigationTargetRole.FinalTarget, 30f)
            },
            physical,
            true);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.DirectTarget));
        Assert.That(assessment.TargetId, Is.EqualTo("final"));
    }

    /// <summary>当前任务目标不可执行时允许选择安全语义绕行。</summary>
    [Test]
    public void ContinuationEvaluator_SafeRerouteOutranksRdwRecovery()
    {
        var evaluator = new PostResetContinuationEvaluator();
        var physical = new SelectiveHeadingEvaluator(direction =>
            direction.x < -0.5f ? HeadingSafety.Unsafe : HeadingSafety.Safe);
        var assessment = evaluator.EvaluateHeading(
            CreateRisk(),
            Vector2.up,
            1f,
            new[]
            {
                Candidate("final", NavigationTargetRole.FinalTarget, -90f),
                Candidate("detour", NavigationTargetRole.NavigableDetour, 0f)
            },
            physical,
            true);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.SafeReroute));
        Assert.That(assessment.TargetId, Is.EqualTo("detour"));
    }

    /// <summary>没有语义候选但已验证 RDW 可执行时保留主动恢复等级。</summary>
    [Test]
    public void ContinuationEvaluator_NoSemanticCandidateUsesVerifiedRdwRecovery()
    {
        var assessment = new PostResetContinuationEvaluator().EvaluateHeading(
            CreateRisk(),
            Vector2.up,
            1f,
            Array.Empty<NavigationCandidate>(),
            new SelectiveHeadingEvaluator(_ => HeadingSafety.Safe),
            true);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.RdwRecovery));
        Assert.That(assessment.IsPhysicalSafe, Is.True);
    }

    /// <summary>只有 RDW 组件但没有可执行性证明时不得产生 RdwRecovery 假阳性。</summary>
    [Test]
    public void ContinuationEvaluator_UnverifiedRdwRecoveryFallsBackToHold()
    {
        var assessment = new PostResetContinuationEvaluator().EvaluateHeading(
            CreateRisk(),
            Vector2.up,
            1f,
            Array.Empty<NavigationCandidate>(),
            new SelectiveHeadingEvaluator(_ => HeadingSafety.Safe),
            false);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.HoldOnly));
        Assert.That(assessment.IsPhysicalSafe, Is.True);
        Assert.That(assessment.Reason, Is.EqualTo("no_continuation_candidate_and_rdw_unverified"));
    }

    /// <summary>所有目标方向均不安全时不得因导航连续性将其提升为可执行。</summary>
    [Test]
    public void ContinuationEvaluator_NeverRelaxesPhysicalSafety()
    {
        var assessment = new PostResetContinuationEvaluator().EvaluateHeading(
            CreateRisk(),
            Vector2.up,
            1f,
            new[] { Candidate("final", NavigationTargetRole.FinalTarget, 0f) },
            new SelectiveHeadingEvaluator(_ => HeadingSafety.Unsafe),
            false);

        Assert.That(assessment.Level, Is.EqualTo(PostResetContinuationLevel.HoldOnly));
        Assert.That(assessment.IsPhysicalSafe, Is.False);
    }

    private static NavigationCandidate Candidate(
        string targetId,
        NavigationTargetRole role,
        float relativeYawDegrees)
    {
        return new NavigationCandidate(
            targetId,
            role,
            "forward",
            targetId,
            string.Empty,
            string.Empty,
            null,
            null,
            relativeYawDegrees);
    }

    private static PhysicalRiskState CreateRisk()
    {
        return new PhysicalRiskState(
            riskSnapshotId: "post-reset-test",
            riskRevision: 1,
            calibrationId: "calibration",
            snapshotSource: "unity",
            physicalSpace: "test-space",
            physicalForward: Vector2.up,
            boundaryContext: new[]
            {
                new Vector2(-2f, -2f),
                new Vector2(-2f, 2f),
                new Vector2(2f, 2f),
                new Vector2(2f, -2f)
            });
    }

    private sealed class SelectiveHeadingEvaluator : IPhysicalHeadingEvaluator
    {
        private readonly Func<Vector2, HeadingSafety> classify;

        public SelectiveHeadingEvaluator(Func<Vector2, HeadingSafety> classifyHeading)
        {
            classify = classifyHeading;
        }

        public bool TryEvaluateAbsoluteHeading(
            PhysicalRiskState riskState,
            Vector2 absolutePhysicalForward,
            out HeadingSample sample)
        {
            var direction = absolutePhysicalForward.normalized;
            var safety = classify(direction);
            sample = new HeadingSample(
                Vector2.SignedAngle(riskState.user_physical_forward, direction),
                direction,
                safety == HeadingSafety.Safe ? 2f : 0.2f,
                safety);
            return true;
        }
    }
}
