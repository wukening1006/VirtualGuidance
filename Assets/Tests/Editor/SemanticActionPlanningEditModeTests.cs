using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证语义候选、动作规划与物理执行资格相互独立。
/// </summary>
public sealed class SemanticActionPlanningEditModeTests
{
    /// <summary>验证身后目标只生成原地转向，且转向后要求重新规划。</summary>
    [Test]
    public void GuidanceActionPlanner_BehindTargetGeneratesTurnInPlace()
    {
        var planner = new GuidanceActionPlanner();

        var left = planner.Plan(new TargetBearing("left", -170f, 2f, BearingLabel.Behind, 1));
        var right = planner.Plan(new TargetBearing("right", 170f, 2f, BearingLabel.Behind, 1));

        Assert.That(left.Type, Is.EqualTo(GuidanceActionType.TurnInPlace));
        Assert.That(left.TurnSide, Is.EqualTo(TurnSide.Left));
        Assert.That(left.RequiresReevaluation, Is.True);
        Assert.That(right.Type, Is.EqualTo(GuidanceActionType.TurnInPlace));
        Assert.That(right.TurnSide, Is.EqualTo(TurnSide.Right));
        Assert.That(GuidanceActionProtocolMapper.ToMovementInstruction(left), Is.EqualTo("turn_left_slowly"));
    }

    /// <summary>
    /// 验证 behind 和 medium 不会从语义候选消失，只有当前执行资格会变化。
    /// </summary>
    [Test]
    public void SemanticTargetSelector_SeparatesBaseCandidatesFromActionExecutability()
    {
        var root = new GameObject("SemanticActionSeparationTest");
        try
        {
            var user = new GameObject("PlanningUser");
            user.transform.SetParent(root.transform, false);
            var final = CreateExhibit(root.transform, "PlanningFinal", new Vector3(0f, 0f, -2f), "main_target", null);
            var pose = root.AddComponent<UserPoseTracker>();
            pose.ConfigureVirtualUserRoot(user.transform);
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.Configure(pose);
            targets.RebuildIndex();
            var selector = root.AddComponent<SemanticTargetSelector>();
            selector.Configure(targets);
            var runtime = new GuidanceRuntimeState();
            runtime.Begin("T2_main_target", "Start", final.Id, final.Id);
            var behindRisk = new PhysicalRiskState(
                headingProfile: new[]
                {
                    new HeadingSample(180f, Vector2.down, 1.5f, HeadingSafety.Safe)
                });

            var behindSet = selector.BuildCandidateSet(
                runtime,
                behindRisk,
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            var behindAssessment = behindSet.Assessments.Single(item => item.Candidate.TargetId == final.Id);

            Assert.That(behindSet.CandidateIds, Does.Contain(final.Id));
            Assert.That(behindSet.ModelCandidateIds, Does.Contain(final.Id));
            Assert.That(behindSet.AllowedCandidateIds, Does.Contain(final.Id));
            Assert.That(behindAssessment.Bearing.DisplayLabel, Is.EqualTo(BearingLabel.Behind));
            Assert.That(behindAssessment.SuggestedAction.Type, Is.EqualTo(GuidanceActionType.TurnInPlace));
            Assert.That(behindAssessment.IsExecutable, Is.True);

            final.transform.position = new Vector3(0f, 0f, 2f);
            var mediumRisk = new PhysicalRiskState(
                headingProfile: new[]
                {
                    new HeadingSample(0f, Vector2.up, 0.9f, HeadingSafety.Unknown)
                });
            var mediumSet = selector.BuildCandidateSet(
                runtime,
                mediumRisk,
                ExperimentConditionMatrix.GetPolicy("ours_full"));
            var mediumAssessment = mediumSet.Assessments.Single(item => item.Candidate.TargetId == final.Id);

            Assert.That(mediumSet.CandidateIds, Does.Contain(final.Id));
            Assert.That(mediumSet.ModelCandidateIds, Does.Contain(final.Id));
            Assert.That(mediumSet.AllowedCandidateIds, Does.Not.Contain(final.Id));
            Assert.That(mediumAssessment.SuggestedAction.Type, Is.EqualTo(GuidanceActionType.MoveForward));
            Assert.That(mediumAssessment.ExecutionState, Is.EqualTo(CandidateExecutionState.TemporarilyBlocked));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>验证安全绕行候选由 Registry 元数据声明，而不是 Selector 内的 ID 常量。</summary>
    [Test]
    public void NavigationTargetManager_DiscoversConfiguredSafetyPlanningTargets()
    {
        var root = new GameObject("ConfiguredSafetyTargetTest");
        try
        {
            CreateExhibit(
                root.transform,
                "ConfiguredDetour",
                Vector3.left,
                "side_context",
                new[] { "navigable_detour" });
            CreateExhibit(
                root.transform,
                "OrdinaryExhibit",
                Vector3.right,
                "side_context",
                Array.Empty<string>());
            var targets = root.AddComponent<NavigationTargetManager>();
            targets.RebuildIndex();

            var planningIds = targets.GetSafetyPlanningTargetIds();

            Assert.That(planningIds, Does.Contain("ConfiguredDetour"));
            Assert.That(planningIds, Does.Not.Contain("OrdinaryExhibit"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static ExhibitMetadata CreateExhibit(
        Transform parent,
        string id,
        Vector3 position,
        string semanticRole,
        string[] tags)
    {
        var exhibit = new GameObject(id);
        exhibit.transform.SetParent(parent, false);
        exhibit.transform.position = position;
        var metadata = exhibit.AddComponent<ExhibitMetadata>();
        metadata.Configure(
            id,
            id,
            tags ?? Array.Empty<string>(),
            string.Empty,
            semanticRole,
            Array.Empty<string>(),
            exhibit.transform,
            exhibit.transform);
        return metadata;
    }
}
