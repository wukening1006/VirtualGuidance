using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 在 Utility 之前执行不可被语义价值覆盖的硬安全筛选。
/// </summary>
public sealed class RdwHardSafetyFilter
{
    private readonly float minimumClearanceMeters;
    private readonly float maximumGainCost;

    /// <summary>创建 Hard Safety Filter。</summary>
    public RdwHardSafetyFilter(float minimumClearance = 1.2f, float maximumAllowedGainCost = 1f)
    {
        minimumClearanceMeters = Mathf.Max(0f, minimumClearance);
        maximumGainCost = Mathf.Clamp01(maximumAllowedGainCost);
    }

    /// <summary>检查 Exact Safety、净空和标准化 Gain 代价。</summary>
    public bool Allows(PlanningEdge edge, RdwEdgeEvaluation evaluation, out string reason)
    {
        if (edge == null)
        {
            reason = "edge_missing";
            return false;
        }
        if (evaluation == null || !evaluation.Feasible)
        {
            reason = evaluation != null && !string.IsNullOrWhiteSpace(evaluation.RejectionReason)
                ? evaluation.RejectionReason
                : "edge_evaluation_unavailable";
            return false;
        }
        if (evaluation.SafetyLevel != HeadingSafety.Safe)
        {
            reason = "hard_filter_requires_safe";
            return false;
        }
        if (evaluation.ClearanceMeters < minimumClearanceMeters)
        {
            reason = "hard_filter_clearance";
            return false;
        }
        if (evaluation.GainCost > maximumGainCost)
        {
            reason = "hard_filter_gain_cost";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// 配置路线 Utility 的归一化权重。
/// </summary>
[Serializable]
public sealed class RdwUtilityWeights
{
    [SerializeField, Min(0f), Tooltip("语义重要性权重。")]
    private float semanticImportance = 1f;
    [SerializeField, Min(0f), Tooltip("任务相关度权重。")]
    private float taskRelevance = 1.2f;
    [SerializeField, Min(0f), Tooltip("Physical Safety Margin 权重。")]
    private float safetyMargin = 1.5f;
    [SerializeField, Min(0f), Tooltip("Continuous RDW Benefit 权重。")]
    private float rdwBenefit = 1f;
    [SerializeField, Min(0f), Tooltip("未访问覆盖奖励权重。")]
    private float unvisitedCoverage = 0.6f;
    [SerializeField, Min(0f), Tooltip("Gain Cost 惩罚权重。")]
    private float gainCost = 1f;
    [SerializeField, Min(0f), Tooltip("Virtual 距离惩罚权重。")]
    private float distanceCost = 0.35f;
    [SerializeField, Min(0f), Tooltip("连续转向角惩罚权重。")]
    private float turnCost = 0.45f;
    [SerializeField, Min(0f), Tooltip("标准化恢复压力惩罚权重。")]
    private float resetPressure = 0.8f;

    /// <summary>语义重要性权重。</summary>
    public float SemanticImportance => semanticImportance;
    /// <summary>任务相关度权重。</summary>
    public float TaskRelevance => taskRelevance;
    /// <summary>Safety Margin 权重。</summary>
    public float SafetyMargin => safetyMargin;
    /// <summary>RDW Benefit 权重。</summary>
    public float RdwBenefit => rdwBenefit;
    /// <summary>未访问覆盖权重。</summary>
    public float UnvisitedCoverage => unvisitedCoverage;
    /// <summary>Gain Cost 权重。</summary>
    public float GainCost => gainCost;
    /// <summary>距离成本权重。</summary>
    public float DistanceCost => distanceCost;
    /// <summary>转向成本权重。</summary>
    public float TurnCost => turnCost;
    /// <summary>恢复压力权重。</summary>
    public float ResetPressure => resetPressure;
}

/// <summary>
/// 配置 Beam Search 深度、宽度、Top-K 与成本归一化尺度。
/// </summary>
[Serializable]
public sealed class RdwRouteSearchSettings
{
    [SerializeField, Range(2, 3), Tooltip("Beam Search 最大深度，第一版限制为 2-3。")]
    private int maximumDepth = 3;
    [SerializeField, Min(1), Tooltip("每层保留的 Beam 数量。")]
    private int beamWidth = 8;
    [SerializeField, Range(1, 5), Tooltip("输出给后端的候选路线数量，建议为 3。")]
    private int topK = 3;
    [SerializeField, Min(0.1f), Tooltip("Utility 中距离成本归一化尺度，单位：米。")]
    private float distanceNormalizationMeters = 12f;
    [SerializeField, Min(0.1f), Tooltip("Utility 中净空收益归一化尺度，单位：米。")]
    private float clearanceNormalizationMeters = 3f;
    [SerializeField, Range(0f, 1f), Tooltip("后续边 Utility 的折扣系数。")]
    private float depthDiscount = 0.9f;

    /// <summary>Beam Search 最大深度。</summary>
    public int MaximumDepth => Mathf.Clamp(maximumDepth, 2, 3);
    /// <summary>每层 Beam 宽度。</summary>
    public int BeamWidth => Mathf.Max(1, beamWidth);
    /// <summary>输出路线数量。</summary>
    public int TopK => Mathf.Max(1, topK);
    /// <summary>距离成本归一化尺度。</summary>
    public float DistanceNormalizationMeters => Mathf.Max(0.1f, distanceNormalizationMeters);
    /// <summary>净空收益归一化尺度。</summary>
    public float ClearanceNormalizationMeters => Mathf.Max(0.1f, clearanceNormalizationMeters);
    /// <summary>深度折扣。</summary>
    public float DepthDiscount => Mathf.Clamp01(depthDiscount);

    /// <summary>测试或运行时以显式参数创建搜索配置。</summary>
    public RdwRouteSearchSettings Configure(int depth, int width, int routeCount)
    {
        maximumDepth = Mathf.Clamp(depth, 2, 3);
        beamWidth = Mathf.Max(1, width);
        topK = Mathf.Max(1, routeCount);
        return this;
    }
}

/// <summary>
/// 表示一条可交给后端选择的只读路线候选。
/// </summary>
public sealed class RouteCandidate
{
    private readonly IReadOnlyList<string> orderedNodeIds;
    private readonly IReadOnlyList<RdwEdgeEvaluation> edgeEvaluations;

    /// <summary>候选路线身份。</summary>
    public string RouteId { get; }
    /// <summary>按执行顺序排列的节点 ID。</summary>
    public IReadOnlyList<string> OrderedNodeIds => orderedNodeIds;
    /// <summary>路线第一步目标 ID。</summary>
    public string FirstTargetId => orderedNodeIds.Count > 0 ? orderedNodeIds[0] : string.Empty;
    /// <summary>与路线各边一一对应的 RDW 评估。</summary>
    public IReadOnlyList<RdwEdgeEvaluation> EdgeEvaluations => edgeEvaluations;
    /// <summary>路线归一化比较分数。</summary>
    public float RouteUtility { get; }
    /// <summary>路线带来的未访问主题内容覆盖增量，范围 0-1。</summary>
    public float SemanticProgress { get; }
    /// <summary>路线所属 Planning Snapshot ID。</summary>
    public string PlanningSnapshotId { get; }
    /// <summary>生成路线的估计器 ID。</summary>
    public string EstimatorId { get; }
    /// <summary>生成路线的估计器版本。</summary>
    public string EstimatorVersion { get; }

    /// <summary>创建不可变 RouteCandidate。</summary>
    public RouteCandidate(
        string routeId,
        IEnumerable<string> nodeIds,
        IEnumerable<RdwEdgeEvaluation> evaluations,
        float routeUtility,
        string planningSnapshotId,
        string estimatorId,
        string estimatorVersion,
        float semanticProgress = 0f)
    {
        RouteId = routeId ?? string.Empty;
        orderedNodeIds = Array.AsReadOnly(new List<string>(nodeIds ?? Array.Empty<string>()).ToArray());
        edgeEvaluations = Array.AsReadOnly(new List<RdwEdgeEvaluation>(
            evaluations ?? Array.Empty<RdwEdgeEvaluation>()).ToArray());
        RouteUtility = routeUtility;
        SemanticProgress = Mathf.Clamp01(semanticProgress);
        PlanningSnapshotId = planningSnapshotId ?? string.Empty;
        EstimatorId = estimatorId ?? string.Empty;
        EstimatorVersion = estimatorVersion ?? string.Empty;
    }
}

/// <summary>
/// 保存一次 Local Graph 搜索的图、Top-K 和拒绝原因。
/// </summary>
public sealed class RdwRoutePlanningResult
{
    private readonly IReadOnlyList<RouteCandidate> routes;
    private readonly IReadOnlyList<string> rejectionReasons;

    /// <summary>规划使用的根快照。</summary>
    public RdwPlanningSnapshot Snapshot { get; }
    /// <summary>包含根状态动态评估的 Local Graph。</summary>
    public LocalNavigationGraph Graph { get; }
    /// <summary>通过 Hard Filter 的 Top-K 路线。</summary>
    public IReadOnlyList<RouteCandidate> Routes => routes;
    /// <summary>搜索期间产生的稳定拒绝原因。</summary>
    public IReadOnlyList<string> RejectionReasons => rejectionReasons;
    /// <summary>是否存在可交给后端的路线。</summary>
    public bool HasRoutes => routes.Count > 0;

    /// <summary>创建不可变规划结果。</summary>
    public RdwRoutePlanningResult(
        RdwPlanningSnapshot snapshot,
        LocalNavigationGraph graph,
        IEnumerable<RouteCandidate> routeCandidates,
        IEnumerable<string> rejectedReasons)
    {
        Snapshot = snapshot;
        Graph = graph;
        routes = Array.AsReadOnly(new List<RouteCandidate>(routeCandidates ?? Array.Empty<RouteCandidate>()).ToArray());
        rejectionReasons = Array.AsReadOnly(new List<string>(rejectedReasons ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>
/// 为静态 Local Graph 附加根快照下的动态 RDW Edge Evaluation。
/// </summary>
public sealed class RdwLocalGraphEvaluator
{
    /// <summary>评估全部静态边并返回新图，不修改输入图。</summary>
    public LocalNavigationGraph Enrich(
        RdwPlanningSnapshot snapshot,
        LocalNavigationGraph graph,
        IRdwPlanningCostEstimator estimator,
        RdwCapabilityProfile capability)
    {
        if (snapshot == null || graph == null || estimator == null)
        {
            return graph;
        }

        var traversal = RdwPlanningTraversalState.FromSnapshot(snapshot);
        var enriched = new List<PlanningEdge>();
        foreach (var edge in graph.Edges)
        {
            if (!graph.TryGetNode(edge.FromNodeId, out var from) ||
                !graph.TryGetNode(edge.ToNodeId, out var to))
            {
                enriched.Add(edge);
                continue;
            }
            enriched.Add(edge.WithEvaluation(
                estimator.EvaluateEdge(snapshot, traversal, from, to, capability)));
        }
        return new LocalNavigationGraph(snapshot.SnapshotId, graph.Nodes, enriched);
    }
}

/// <summary>
/// 对通过 Hard Filter 的局部图执行确定性 depth 2-3 Beam Search。
/// </summary>
public sealed class RdwBeamRoutePlanner
{
    private readonly IRdwPlanningCostEstimator estimator;
    private readonly RdwHardSafetyFilter hardFilter;
    private readonly RdwUtilityWeights weights;
    private readonly RdwRouteSearchSettings settings;

    /// <summary>创建 Beam Route Planner。</summary>
    public RdwBeamRoutePlanner(
        IRdwPlanningCostEstimator costEstimator,
        RdwHardSafetyFilter safetyFilter,
        RdwUtilityWeights utilityWeights,
        RdwRouteSearchSettings searchSettings)
    {
        estimator = costEstimator;
        hardFilter = safetyFilter ?? new RdwHardSafetyFilter();
        weights = utilityWeights ?? new RdwUtilityWeights();
        settings = searchSettings ?? new RdwRouteSearchSettings();
    }

    /// <summary>生成安全、确定且优先具有不同第一站的 Top-K 路线。</summary>
    public RdwRoutePlanningResult Search(
        RdwPlanningSnapshot snapshot,
        LocalNavigationGraph graph)
    {
        var rejected = new List<string>();
        if (snapshot == null || graph == null || estimator == null ||
            !graph.TryGetNode(snapshot.CurrentNodeId, out var startNode))
        {
            return new RdwRoutePlanningResult(snapshot, graph, null, new[] { "route_search_input_invalid" });
        }

        var enrichedGraph = new RdwLocalGraphEvaluator().Enrich(
            snapshot,
            graph,
            estimator,
            snapshot.Capability);
        var beams = new List<BeamState>
        {
            new BeamState(startNode, RdwPlanningTraversalState.FromSnapshot(snapshot))
        };
        var completed = new List<BeamState>();
        var oneStepFallback = new List<BeamState>();

        for (var depth = 0; depth < settings.MaximumDepth; depth++)
        {
            var expanded = new List<BeamState>();
            foreach (var beam in beams)
            {
                if (string.Equals(beam.CurrentNode.NodeId, snapshot.FinalTargetId, StringComparison.Ordinal))
                {
                    completed.Add(beam);
                    continue;
                }

                foreach (var edge in graph.GetOutgoing(beam.CurrentNode.NodeId))
                {
                    if (beam.Contains(edge.ToNodeId) ||
                        !graph.TryGetNode(edge.ToNodeId, out var nextNode))
                    {
                        continue;
                    }

                    var evaluation = estimator.EvaluateEdge(
                        snapshot,
                        beam.Traversal,
                        beam.CurrentNode,
                        nextNode,
                        snapshot.Capability);
                    if (!hardFilter.Allows(edge, evaluation, out var reason))
                    {
                        rejected.Add($"{edge.FromNodeId}->{edge.ToNodeId}:{reason}");
                        continue;
                    }

                    var edgeUtility = CalculateUtility(nextNode, edge, evaluation);
                    var next = beam.Expand(nextNode, evaluation, edgeUtility, settings.DepthDiscount);
                    expanded.Add(next);
                    if (next.NodeIds.Count == 1) oneStepFallback.Add(next);
                    if (next.NodeIds.Count >= 2 ||
                        string.Equals(nextNode.NodeId, snapshot.FinalTargetId, StringComparison.Ordinal))
                    {
                        completed.Add(next);
                    }
                }
            }

            expanded.Sort(CompareBeams);
            if (expanded.Count > settings.BeamWidth)
            {
                expanded.RemoveRange(settings.BeamWidth, expanded.Count - settings.BeamWidth);
            }
            beams = expanded;
            if (beams.Count == 0) break;
        }

        if (completed.Count == 0)
        {
            completed.AddRange(oneStepFallback);
        }
        var routes = SelectDiverseTopK(snapshot, enrichedGraph, completed);
        return new RdwRoutePlanningResult(snapshot, enrichedGraph, routes, rejected);
    }

    private float CalculateUtility(
        PlanningNode node,
        PlanningEdge edge,
        RdwEdgeEvaluation evaluation)
    {
        var safety = Mathf.Clamp01(evaluation.ClearanceMeters / settings.ClearanceNormalizationMeters);
        var distance = Mathf.Clamp01(edge.DistanceMeters / settings.DistanceNormalizationMeters);
        var turn = Mathf.Clamp01(Mathf.Abs(evaluation.RelativeYawDegrees) / 180f);
        return weights.SemanticImportance * node.SemanticImportance +
               weights.TaskRelevance * node.TaskRelevance +
               weights.SafetyMargin * safety +
               weights.RdwBenefit * evaluation.RdwBenefit +
               weights.UnvisitedCoverage * (node.Visited ? 0f : 1f) -
               weights.GainCost * evaluation.GainCost -
               weights.DistanceCost * distance -
               weights.TurnCost * turn -
               weights.ResetPressure * evaluation.ResetPressure;
    }

    private List<RouteCandidate> SelectDiverseTopK(
        RdwPlanningSnapshot snapshot,
        LocalNavigationGraph graph,
        List<BeamState> beams)
    {
        beams.Sort(CompareBeams);
        var selected = new List<BeamState>();
        var firstTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var beam in beams)
        {
            if (beam.NodeIds.Count == 0 || !firstTargets.Add(beam.NodeIds[0])) continue;
            selected.Add(beam);
            if (selected.Count >= settings.TopK) break;
        }
        if (selected.Count < settings.TopK)
        {
            foreach (var beam in beams)
            {
                if (selected.Contains(beam)) continue;
                selected.Add(beam);
                if (selected.Count >= settings.TopK) break;
            }
        }

        var routes = new List<RouteCandidate>();
        foreach (var beam in selected)
        {
            var path = string.Join(">", beam.NodeIds);
            var routeId = RdwPlanningIdentity.CreateStableId(
                "route",
                snapshot.SnapshotId,
                estimator.EstimatorId,
                estimator.EstimatorVersion,
                path);
            routes.Add(new RouteCandidate(
                routeId,
                beam.NodeIds,
                beam.Evaluations,
                beam.NormalizedUtility,
                snapshot.SnapshotId,
                estimator.EstimatorId,
                estimator.EstimatorVersion,
                CalculateSemanticProgress(beam, graph)));
        }
        return routes;
    }

    private static float CalculateSemanticProgress(BeamState beam, LocalNavigationGraph graph)
    {
        if (beam == null || graph == null || beam.NodeIds.Count == 0) return 0f;
        var unvisited = 0;
        foreach (var nodeId in beam.NodeIds)
        {
            if (graph.TryGetNode(nodeId, out var node) && node != null && !node.Visited)
            {
                unvisited++;
            }
        }
        return Mathf.Clamp01((float)unvisited / beam.NodeIds.Count);
    }

    private static int CompareBeams(BeamState left, BeamState right)
    {
        var utility = right.NormalizedUtility.CompareTo(left.NormalizedUtility);
        return utility != 0
            ? utility
            : string.CompareOrdinal(string.Join(">", left.NodeIds), string.Join(">", right.NodeIds));
    }

    private sealed class BeamState
    {
        private readonly List<string> nodeIds;
        private readonly List<RdwEdgeEvaluation> evaluations;
        private readonly HashSet<string> visitedNodeIds;
        private readonly float weightedUtility;
        private readonly float totalWeight;

        public PlanningNode CurrentNode { get; }
        public PlanningNode FirstNode { get; }
        public RdwPlanningTraversalState Traversal { get; }
        public IReadOnlyList<string> NodeIds => nodeIds;
        public IReadOnlyList<RdwEdgeEvaluation> Evaluations => evaluations;
        public float NormalizedUtility => totalWeight > 0.0001f ? weightedUtility / totalWeight : 0f;

        public BeamState(PlanningNode startNode, in RdwPlanningTraversalState traversal)
            : this(
                startNode,
                null,
                traversal,
                new List<string>(),
                new List<RdwEdgeEvaluation>(),
                new HashSet<string>(StringComparer.Ordinal) { startNode.NodeId },
                0f,
                0f)
        {
        }

        private BeamState(
            PlanningNode currentNode,
            PlanningNode firstNode,
            in RdwPlanningTraversalState traversal,
            List<string> path,
            List<RdwEdgeEvaluation> edgeEvaluations,
            HashSet<string> visitedNodes,
            float utility,
            float weight)
        {
            CurrentNode = currentNode;
            FirstNode = firstNode;
            Traversal = traversal;
            nodeIds = path;
            evaluations = edgeEvaluations;
            visitedNodeIds = visitedNodes;
            weightedUtility = utility;
            totalWeight = weight;
        }

        public bool Contains(string nodeId)
        {
            return visitedNodeIds.Contains(nodeId);
        }

        public BeamState Expand(
            PlanningNode nextNode,
            RdwEdgeEvaluation evaluation,
            float edgeUtility,
            float discount)
        {
            var nextPath = new List<string>(nodeIds) { nextNode.NodeId };
            var nextEvaluations = new List<RdwEdgeEvaluation>(evaluations) { evaluation };
            var nextVisited = new HashSet<string>(visitedNodeIds, StringComparer.Ordinal)
            {
                nextNode.NodeId
            };
            var edgeWeight = Mathf.Pow(discount, nodeIds.Count);
            var nextTraversal = new RdwPlanningTraversalState(
                nextNode.VirtualPositionXZ,
                (nextNode.VirtualPositionXZ - CurrentNode.VirtualPositionXZ).normalized,
                evaluation.PredictedPhysicalPositionXZ,
                evaluation.PredictedPhysicalHeading);
            return new BeamState(
                nextNode,
                FirstNode ?? nextNode,
                nextTraversal,
                nextPath,
                nextEvaluations,
                nextVisited,
                weightedUtility + edgeUtility * edgeWeight,
                totalWeight + edgeWeight);
        }
    }
}
