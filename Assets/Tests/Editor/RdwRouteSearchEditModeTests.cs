using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 验证 Local Graph、Hard Filter 与 depth 2-3 Beam Search。
/// </summary>
public sealed class RdwRouteSearchEditModeTests
{
    /// <summary>验证 Local Graph 节点和边按 ID 稳定排序。</summary>
    [Test]
    public void LocalGraph_IsDeterministic()
    {
        var snapshot = RdwPlanningTestFactory.CreateSnapshot("fake", "1");
        var nodes = new[]
        {
            RdwPlanningTestFactory.Node("b", new Vector2(2f, 1f)),
            RdwPlanningTestFactory.Node("a", new Vector2(-2f, 1f)),
            RdwPlanningTestFactory.Node("goal", new Vector2(0f, 4f), role: NavigationTargetRole.FinalTarget)
        };

        var graph = new RdwLocalGraphBuilder(20f).Build(snapshot, nodes);

        Assert.That(graph.Nodes[0].NodeId, Is.EqualTo("a"));
        Assert.That(graph.Nodes[graph.Nodes.Count - 1].NodeId, Is.EqualTo("start"));
        for (var index = 1; index < graph.Edges.Count; index++)
        {
            var previous = graph.Edges[index - 1];
            var current = graph.Edges[index];
            var comparison = string.CompareOrdinal(previous.FromNodeId, current.FromNodeId);
            Assert.That(comparison < 0 ||
                        comparison == 0 && string.CompareOrdinal(previous.ToNodeId, current.ToNodeId) <= 0,
                Is.True);
        }
    }

    /// <summary>验证 Unsafe/Critical 与净空不足不能被 Utility 覆盖。</summary>
    [Test]
    public void HardFilter_RejectsUnsafeAndCriticalEdges()
    {
        var filter = new RdwHardSafetyFilter(1.2f, 1f);
        var edge = new PlanningEdge("start", "target", 2f, true);

        Assert.That(filter.Allows(edge, Evaluation(HeadingSafety.Unsafe, 2f), out _), Is.False);
        Assert.That(filter.Allows(edge, Evaluation(HeadingSafety.Critical, 2f), out _), Is.False);
        Assert.That(filter.Allows(edge, Evaluation(HeadingSafety.Safe, 0.8f), out _), Is.False);
        Assert.That(filter.Allows(edge, Evaluation(HeadingSafety.Safe, 2f), out _), Is.True);

        var expensive = new RdwEdgeEvaluation(
            0f,
            Vector2.up,
            Vector2.up,
            2f,
            HeadingSafety.Safe,
            0.5f,
            0.8f,
            true,
            string.Empty,
            0.1f);
        Assert.That(new RdwHardSafetyFilter(1.2f, 0.5f).Allows(edge, expensive, out _), Is.False);
    }

    /// <summary>验证 Top-K 优先提供不同第一步、无根节点回环且 route_id 稳定。</summary>
    [Test]
    public void BeamSearch_ProducesDiverseAcyclicDeterministicTopK()
    {
        var estimator = new RdwPlanningFakeEstimator();
        var snapshot = RdwPlanningTestFactory.CreateSnapshot(
            estimator.EstimatorId,
            estimator.EstimatorVersion);
        var nodes = new[]
        {
            RdwPlanningTestFactory.Node("a", new Vector2(-2f, 2f), 0.8f, 0.6f),
            RdwPlanningTestFactory.Node("b", new Vector2(0f, 3f), 0.7f, 0.7f),
            RdwPlanningTestFactory.Node("c", new Vector2(2f, 2f), 0.6f, 0.8f),
            RdwPlanningTestFactory.Node("goal", new Vector2(0f, 5f), 1f, 1f, NavigationTargetRole.FinalTarget)
        };
        var graph = new RdwLocalGraphBuilder(20f).Build(snapshot, nodes);
        var settings = new RdwRouteSearchSettings().Configure(3, 24, 3);
        var planner = new RdwBeamRoutePlanner(
            estimator,
            new RdwHardSafetyFilter(1.2f),
            new RdwUtilityWeights(),
            settings);

        var first = planner.Search(snapshot, graph);
        var second = planner.Search(snapshot, graph);

        Assert.That(first.Routes.Count, Is.EqualTo(3));
        var firstTargets = new HashSet<string>(StringComparer.Ordinal);
        var containsMultiStepRoute = false;
        for (var index = 0; index < first.Routes.Count; index++)
        {
            var route = first.Routes[index];
            firstTargets.Add(route.FirstTargetId);
            Assert.That(route.OrderedNodeIds, Does.Not.Contain("start"));
            Assert.That(route.OrderedNodeIds.Count, Is.InRange(1, 3));
            containsMultiStepRoute |= route.OrderedNodeIds.Count >= 2;
            Assert.That(route.RouteId, Is.EqualTo(second.Routes[index].RouteId));
        }
        Assert.That(firstTargets.Count, Is.EqualTo(3));
        Assert.That(containsMultiStepRoute, Is.True);
    }

    private static RdwEdgeEvaluation Evaluation(HeadingSafety safety, float clearance)
    {
        return new RdwEdgeEvaluation(
            0f,
            Vector2.up,
            Vector2.up,
            clearance,
            safety,
            0.5f,
            0.2f,
            safety == HeadingSafety.Safe,
            safety == HeadingSafety.Safe ? string.Empty : "unsafe",
            0.1f);
    }
}

internal sealed class RdwPlanningFakeEstimator : IRdwPlanningCostEstimator
{
    /// <summary>测试估计器身份。</summary>
    public string EstimatorId => "fake";
    /// <summary>测试估计器版本。</summary>
    public string EstimatorVersion => "1";

    /// <summary>返回固定安全代价，用于隔离 Beam Search 行为。</summary>
    public RdwEdgeEvaluation EvaluateEdge(
        RdwPlanningSnapshot snapshot,
        in RdwPlanningTraversalState traversal,
        PlanningNode from,
        PlanningNode to,
        RdwCapabilityProfile capability)
    {
        var delta = to.VirtualPositionXZ - from.VirtualPositionXZ;
        var direction = delta.sqrMagnitude > 0.000001f ? delta.normalized : Vector2.up;
        var yaw = Mathf.DeltaAngle(
            RdwYawConvention.ForwardToYaw(traversal.VirtualForwardXZ),
            RdwYawConvention.ForwardToYaw(direction));
        return new RdwEdgeEvaluation(
            yaw,
            direction,
            traversal.PhysicalPositionXZ + direction,
            3f,
            HeadingSafety.Safe,
            0.7f,
            0.2f,
            true,
            string.Empty,
            0.1f);
    }
}
