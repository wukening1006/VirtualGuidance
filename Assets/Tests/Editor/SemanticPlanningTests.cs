using NUnit.Framework;

/// <summary>Theme-driven 三条件局部规划的纯 DTO 与状态回归测试。</summary>
public sealed class SemanticPlanningTests
{
    [Test]
    /// <summary>首个计划节点必须与当前展品存在空间边。</summary>
    public void SemanticPlanVerifierRequiresCurrentToFirstSpatialEdge()
    {
        var request = new SemanticPlanRequest
        {
            session_id = "session",
            request_id = "request",
            generation = 1,
            task_state_version = 1,
            theme_goal = new ThemeGoal
            {
                theme_id = "TH_RITUAL",
                name = "祭祀",
                description = "主题"
            },
            current_exhibit_id = "E1",
            local_semantic_nodes = new[]
            {
                new SemanticPlanningNode { id = "E1" },
                new SemanticPlanningNode { id = "E3" },
                new SemanticPlanningNode { id = "E5" }
            },
            local_spatial_edges = new[]
            {
                new SpatialEdgeDefinition
                {
                    source = "E3",
                    target = "E5",
                    bidirectional = true
                }
            }
        };

        Assert.IsFalse(SemanticPlanVerifier.TryVerify(
            request,
            new[] { "E3", "E5" },
            out var reason));
        Assert.That(reason, Does.Contain("E1->E3"));
    }

    [Test]
    /// <summary>计划不能在后续节点回到当前展品或已访问展品。</summary>
    public void SemanticPlanVerifierRejectsVisitedNodeInPlan()
    {
        var request = new SemanticPlanRequest
        {
            session_id = "session",
            request_id = "request",
            generation = 1,
            task_state_version = 1,
            theme_goal = new ThemeGoal { theme_id = "TH_RITUAL", name = "祭祀", description = "主题" },
            current_exhibit_id = "E1",
            visited_exhibit_ids = new[] { "E1" },
            local_semantic_nodes = new[]
            {
                new SemanticPlanningNode { id = "E1" },
                new SemanticPlanningNode { id = "E3" }
            },
            local_spatial_edges = new[]
            {
                new SpatialEdgeDefinition { source = "E1", target = "E3", bidirectional = true },
                new SpatialEdgeDefinition { source = "E3", target = "E1", bidirectional = true }
            }
        };

        Assert.IsFalse(SemanticPlanVerifier.TryVerify(request, new[] { "E3", "E1" }, out var reason));
        Assert.That(reason, Is.EqualTo("semantic_plan_starts_at_current_exhibit"));
    }

    [Test]
    /// <summary>正式矩阵仅包含三种主题驱动条件。</summary>
    public void FormalProfilesOnlyExposeThreeConditions()
    {
        Assert.That(ExperimentConditionMatrix.Conditions, Is.EqualTo(new[]
        {
            "B1_semantic_only",
            "B2_semantic_only_continuous",
            "ours_rdw_grounded_topk"
        }));
        Assert.That(ExperimentConditionMatrix.GetProfile("B1_semantic_only").plannerMode,
            Is.EqualTo(ExperimentPlannerMode.SemanticOnly));
        Assert.That(ExperimentConditionMatrix.GetProfile("B2_semantic_only_continuous").continuousRdwEnabled,
            Is.True);
        Assert.That(ExperimentConditionMatrix.GetProfile("B1_semantic_only").resetMode,
            Is.EqualTo(ExperimentResetMode.Normal));
        Assert.That(ExperimentConditionMatrix.GetPolicy("B1_semantic_only").UseRotationGain,
            Is.EqualTo(ConditionCapabilityMode.Disabled));
        Assert.That(ExperimentConditionMatrix.GetPolicy("B2_semantic_only_continuous").UseRotationGain,
            Is.EqualTo(ConditionCapabilityMode.Disabled));
        Assert.That(ExperimentConditionMatrix.GetProfile("ours_rdw_grounded_topk").rdwPlanningMode,
            Is.EqualTo(RdwPlanningMode.RdwConstrained));
    }

    [Test]
    /// <summary>到达语义第一步后旧计划立即失效并推进访问历史。</summary>
    public void SemanticRuntimeStateInvalidatesPlanAfterArrival()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", string.Empty, string.Empty);
        state.ConfigureTheme("TH_RITUAL", 0f, 0.6f, 8);
        state.SetActiveSemanticPlan("plan-1", new[] { "E3", "E5" });
        Assert.That(state.nextTargetId, Is.EqualTo("E3"));

        state.TryReserveGuidanceTarget(new GuidanceTarget("E3", NavigationTargetRole.RouteSubgoal), "decision-1");
        state.CommitReservation(state.ActiveReservation);
        state.AdvanceSemanticTo("E3");

        Assert.That(state.currentExhibitId, Is.EqualTo("E3"));
        Assert.That(state.activePlanId, Is.Empty);
        Assert.That(state.planGeneration, Is.EqualTo(2));
        Assert.That(state.visitedExhibitIds, Does.Contain("E3"));
    }
}
