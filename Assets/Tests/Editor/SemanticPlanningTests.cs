using System;
using NUnit.Framework;

/// <summary>自然语言 T2 局部规划、最终目标锁和运行状态回归测试。</summary>
public sealed class SemanticPlanningTests
{
    /// <summary>最少三件中间展品完成前不能选择最终目标。</summary>
    [Test]
    public void SemanticPlanVerifierRejectsFinalBeforeMinimumVisits()
    {
        var request = CreateRequest(0, "exploring", "E1", new[] { "E1" }, string.Empty);

        Assert.That(SemanticPlanVerifier.TryVerify(request, new[] { "E5" }, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("semantic_plan_final_goal_locked"));
    }

    /// <summary>空节点序列必须作为规划失败处理，不能被解释为任务完成。</summary>
    [Test]
    public void SemanticPlanResponseRejectsEmptyPlan()
    {
        var request = CreateRequest(3, "exploring", "E4", new[] { "E1", "E2", "E3", "E4" }, string.Empty);
        var response = new SemanticPlanResponse
        {
            planning_schema_version = request.planning_schema_version,
            session_id = request.session_id,
            request_id = request.request_id,
            generation = request.generation,
            ordered_node_ids = Array.Empty<string>(),
            verifier = new GuidanceVerifierResult { approved = true }
        };

        Assert.That(response.TryValidate(request, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("semantic_plan_empty"));
    }

    /// <summary>完成三件中间展品后允许以单节点计划前往最终目标。</summary>
    [Test]
    public void SemanticPlanVerifierAllowsFinalAfterMinimumVisits()
    {
        var request = CreateRequest(3, "exploring", "E4", new[] { "E1", "E2", "E3", "E4" }, string.Empty);

        Assert.That(SemanticPlanVerifier.TryVerify(request, new[] { "E5" }, out var reason), Is.True, reason);
    }

    /// <summary>完成四件中间展品后，计划必须只选择最终目标。</summary>
    [Test]
    public void SemanticPlanVerifierRequiresFinalAtMaximumVisits()
    {
        var request = CreateRequest(
            4,
            "final_approach",
            "E4",
            new[] { "E1", "E2", "E8", "E4" },
            "E5");

        Assert.That(SemanticPlanVerifier.TryVerify(request, new[] { "E3" }, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("semantic_plan_final_goal_required"));
    }

    /// <summary>没有 direct Spatial Edge 的中间展品仍允许进入计划。</summary>
    [Test]
    public void SemanticPlanVerifierAllowsCandidateWithoutDirectSpatialEdge()
    {
        var request = CreateRequest(0, "exploring", "E1", new[] { "E1" }, string.Empty);
        request.local_spatial_edges = new[]
        {
            Spatial("E1", "E2"),
            Spatial("E2", "E3"),
            Spatial("E3", "E4"),
            Spatial("E4", "E5"),
            Spatial("E1", "E5")
        };

        Assert.That(SemanticPlanVerifier.TryVerify(request, new[] { "E8" }, out var reason), Is.True, reason);
    }

    /// <summary>到达语义第一步后旧计划立即失效并累计唯一中间展品。</summary>
    [Test]
    public void SemanticRuntimeStateInvalidatesPlanAfterArrival()
    {
        var state = CreateRuntimeState();
        state.SetActiveSemanticPlan("plan-1", new[] { "E2", "E3" });

        Assert.That(state.TryReserveGuidanceTarget(
            new GuidanceTarget("E2", NavigationTargetRole.RouteSubgoal), "decision-1"), Is.True);
        state.CommitReservation(state.ActiveReservation);
        state.AdvanceSemanticTo("E2");

        Assert.That(state.currentExhibitId, Is.EqualTo("E2"));
        Assert.That(state.intermediateVisitCount, Is.EqualTo(1));
        Assert.That(state.activePlanId, Is.Empty);
        Assert.That(state.planGeneration, Is.EqualTo(2));
    }

    /// <summary>只有满足游览数量并确认抵达 E5 才能完成任务。</summary>
    [Test]
    public void SemanticRuntimeCompletesOnlyAtFinalGoal()
    {
        var state = CreateRuntimeState();

        Assert.That(state.CompleteFinalGoal("E5"), Is.False);
        state.AdvanceSemanticTo("E2");
        state.AdvanceSemanticTo("E3");
        state.AdvanceSemanticTo("E4");
        Assert.That(state.EnterFinalApproach(), Is.True);
        Assert.That(state.CompleteFinalGoal("E4"), Is.False);
        Assert.That(state.CompleteFinalGoal("E5"), Is.True);
        Assert.That(state.taskStage, Is.EqualTo(GuidanceTaskStage.Completed));
        Assert.That(state.IsCompleted, Is.True);
    }

    /// <summary>导航绕行只改变执行目标，不得替换任务最终目标。</summary>
    [Test]
    public void NavigableDetourDoesNotChangeFinalGoal()
    {
        var state = CreateRuntimeState();

        Assert.That(state.TryReserveGuidanceTarget(
            new GuidanceTarget("D1", NavigationTargetRole.NavigableDetour), "detour-1"), Is.True);
        Assert.That(state.finalTargetId, Is.EqualTo("E5"));
        Assert.That(state.taskStage, Is.EqualTo(GuidanceTaskStage.Exploring));
    }

    /// <summary>正式矩阵仅包含共享同一任务语义的三个条件。</summary>
    [Test]
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
        Assert.That(ExperimentConditionMatrix.GetProfile("ours_rdw_grounded_topk").rdwPlanningMode,
            Is.EqualTo(RdwPlanningMode.RdwConstrained));
    }

    private static GuidanceRuntimeState CreateRuntimeState()
    {
        var state = new GuidanceRuntimeState();
        state.Begin("T2_main_target", "E1", string.Empty, "E5");
        state.ConfigureIntermediateVisitPolicy("参观相关展品后，最后抵达青铜神树。", 3, 4);
        return state;
    }

    private static SemanticPlanRequest CreateRequest(
        int intermediateCount,
        string phase,
        string currentExhibitId,
        string[] visitedExhibitIds,
        string requiredTargetId)
    {
        return new SemanticPlanRequest
        {
            session_id = "session",
            request_id = "request",
            generation = 1,
            task_state_version = 1,
            task = new NaturalLanguageTaskContract
            {
                task_id = "T2_main_target",
                user_task = "参观相关展品后，最后抵达青铜神树。",
                final_goal = new FinalGoalContract
                {
                    exhibit_id = "E5",
                    name = "青铜神树",
                    required = true
                },
                intermediate_policy = new IntermediateVisitPolicyContract
                {
                    minimum_visits = 3,
                    maximum_visits = 4,
                    exclude_final_until_ready = true,
                    require_semantic_connection = true
                }
            },
            task_state = new NaturalLanguageTaskState
            {
                phase = phase,
                current_exhibit_id = currentExhibitId,
                visited_exhibit_ids = visitedExhibitIds,
                intermediate_visit_count = intermediateCount,
                required_target_id = requiredTargetId
            },
            local_semantic_nodes = new[]
            {
                new SemanticPlanningNode { id = "E1" },
                new SemanticPlanningNode { id = "E2" },
                new SemanticPlanningNode { id = "E3" },
                new SemanticPlanningNode { id = "E4" },
                new SemanticPlanningNode { id = "E5" },
                new SemanticPlanningNode { id = "E8" }
            },
            local_spatial_edges = new[]
            {
                Spatial("E1", "E2"),
                Spatial("E2", "E3"),
                Spatial("E3", "E4"),
                Spatial("E4", "E5"),
                Spatial("E1", "E5"),
                Spatial("E1", "E8")
            },
            local_semantic_edges = new[]
            {
                Semantic("E2", "E5"),
                Semantic("E3", "E5"),
                Semantic("E4", "E5")
            }
        };
    }

    private static SpatialEdgeDefinition Spatial(string source, string target)
    {
        return new SpatialEdgeDefinition { source = source, target = target, bidirectional = true };
    }

    private static SemanticEdgeDefinition Semantic(string source, string target)
    {
        return new SemanticEdgeDefinition { source = source, target = target, directed = false };
    }
}
