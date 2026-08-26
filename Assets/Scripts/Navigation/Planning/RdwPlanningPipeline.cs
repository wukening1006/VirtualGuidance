using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 保存规划层 Inspector 配置；默认模式固定为 Shadow。
/// </summary>
[Serializable]
public sealed class RdwPlanningSettings
{
    [SerializeField, Tooltip("规划运行模式；第一轮必须保持 Shadow。")]
    private RdwPlanningMode mode = RdwPlanningMode.Shadow;
    [SerializeField, Min(0f), Tooltip("Hard Filter 最小净空，单位：米。")]
    private float hardClearanceMeters = 1.2f;
    [SerializeField, Range(0f, 1f), Tooltip("Hard Filter 允许的最大归一化 Gain Cost。")]
    private float maximumGainCost = 1f;
    [SerializeField, Tooltip("Beam Search 深度、宽度与 Top-K 配置。")]
    private RdwRouteSearchSettings routeSearch = new RdwRouteSearchSettings();
    [SerializeField, Tooltip("通过 Hard Filter 后使用的 Utility 权重。")]
    private RdwUtilityWeights utilityWeights = new RdwUtilityWeights();

    /// <summary>规划运行模式。</summary>
    public RdwPlanningMode Mode => mode;
    /// <summary>Local Graph 最大普通边距离。</summary>
    /// <summary>Hard Filter 最小净空。</summary>
    public float HardClearanceMeters => Mathf.Max(0f, hardClearanceMeters);
    /// <summary>Hard Filter 最大 Gain Cost。</summary>
    public float MaximumGainCost => Mathf.Clamp01(maximumGainCost);
    /// <summary>Beam Search 配置。</summary>
    public RdwRouteSearchSettings RouteSearch => routeSearch ?? (routeSearch = new RdwRouteSearchSettings());
    /// <summary>Utility 权重。</summary>
    public RdwUtilityWeights UtilityWeights => utilityWeights ?? (utilityWeights = new RdwUtilityWeights());

    /// <summary>测试或显式运行时切换模式。</summary>
    public void SetMode(RdwPlanningMode value)
    {
        mode = value;
    }
}

/// <summary>
/// 从同一 RDW Frame、Risk 和 Task 状态创建 Fresh Planning Snapshot。
/// </summary>
public sealed class RdwPlanningSnapshotFactory
{
    /// <summary>冻结一次规划事实；输入不完整时仍返回可诊断的无效快照。</summary>
    public RdwPlanningSnapshot Capture(
        in RdwPoseFrame pose,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        int registryRevision,
        RdwCapabilityProfile capability)
    {
        var geometry = riskState != null
            ? new RdwGeometrySnapshot(
                riskState.physical_space,
                riskState.boundary_context,
                riskState.obstacle_context,
                riskState.risk_snapshot_id,
                riskState.risk_revision,
                riskState.critical,
                riskState.calibration_id,
                riskState.user_radius_meters)
            : default;
        var visited = runtimeState != null
            ? runtimeState.visitedExhibitIds
            : Array.Empty<string>();
        var poseIdentityMatches = pose.IsValid && riskState != null &&
                                  string.Equals(
                                      pose.CalibrationId,
                                      riskState.calibration_id,
                                      StringComparison.Ordinal) &&
                                  string.Equals(
                                      pose.PhysicalSpaceId,
                                      riskState.physical_space,
                                      StringComparison.Ordinal);
        var currentNodeId = runtimeState != null && !string.IsNullOrWhiteSpace(runtimeState.currentExhibitId)
            ? runtimeState.currentExhibitId
            : RdwPlanningPipeline.RuntimeStartNodeId;
        return new RdwPlanningSnapshot(
            RdwPlanningSnapshot.NewSnapshotId(),
            pose.Sequence,
            pose.Timestamp,
            pose.PhysicalPositionXZ,
            pose.PhysicalForwardXZ,
            pose.VirtualPositionXZ,
            pose.VirtualForwardXZ,
            riskState?.calibration_id,
            riskState?.physical_space,
            riskState?.risk_snapshot_id,
            riskState?.risk_revision ?? 0,
            currentNodeId,
            runtimeState?.finalTargetId,
            runtimeState?.stateVersion ?? 0,
            registryRevision,
            visited,
            geometry,
            riskState,
            capability,
            poseIdentityMatches);
    }
}

/// <summary>
/// 从 MainScene Target Registry 读取并冻结 PlanningNode；只读取 Transform，不修改场景。
/// </summary>
public sealed class RdwPlanningNodeFactory
{
    /// <summary>把 Candidate Set 与运行时 GazeTarget 转换为规划节点集合。</summary>
    public IReadOnlyList<PlanningNode> Capture(
            RdwPlanningSnapshot snapshot,
            NavigationCandidateSet candidateSet,
            NavigationTargetManager navigationTargets,
            StaticSceneSemanticGraph semanticGraph = null)
    {
        var result = new List<PlanningNode>();
        if (snapshot == null || candidateSet == null || navigationTargets == null)
        {
            return result.AsReadOnly();
        }

        foreach (var candidate in candidateSet.Candidates)
        {
            if (candidate == null ||
                !Contains(candidateSet.ModelCandidateIds, candidate.TargetId) ||
                !navigationTargets.TryGetTarget(candidate.TargetId, out var metadata) ||
                metadata == null)
            {
                continue;
            }
            var anchor = metadata.GazeTarget != null ? metadata.GazeTarget : metadata.transform;
            var position = anchor.position;
            result.Add(new PlanningNode(
                candidate.TargetId,
                new Vector2(position.x, position.z),
                candidate.TargetRole,
                ResolveSemanticImportance(candidate, snapshot, semanticGraph),
                ResolveTaskRelevance(snapshot, candidate, semanticGraph),
                IsVisited(snapshot.VisitedNodeIds, candidate.TargetId)));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.CurrentNodeId) &&
            !ContainsNode(result, snapshot.CurrentNodeId))
        {
            var currentAnchor = navigationTargets.TryGetTarget(snapshot.CurrentNodeId, out var currentMetadata)
                ? (currentMetadata.GazeTarget != null ? currentMetadata.GazeTarget : currentMetadata.transform)
                : null;
            var currentPosition = currentAnchor != null
                ? new Vector2(currentAnchor.position.x, currentAnchor.position.z)
                : snapshot.VirtualPositionXZ;
            result.Add(new PlanningNode(
                snapshot.CurrentNodeId,
                currentPosition,
                NavigationTargetRole.RouteSubgoal,
                0f,
                0f,
                true));
        }
        return result.AsReadOnly();
    }

    private static float ResolveSemanticImportance(
        NavigationCandidate candidate,
        RdwPlanningSnapshot snapshot,
        StaticSceneSemanticGraph semanticGraph)
    {
        var baseScore = candidate.TargetRole == NavigationTargetRole.FinalTarget
            ? 1f
            : candidate.TargetRole == NavigationTargetRole.RouteSubgoal
                ? 0.8f
                : candidate.TargetRole == NavigationTargetRole.NavigableDetour ? 0.55f : 0.35f;
        if (semanticGraph == null || !semanticGraph.TryGetExhibit(candidate.TargetId, out var exhibit) ||
            exhibit?.knowledge == null)
        {
            return baseScore;
        }

        var targetThemes = snapshot != null &&
                           semanticGraph.TryGetExhibit(snapshot.FinalTargetId, out var target) &&
                           target?.knowledge != null
            ? target.knowledge.themes
            : null;
        var themeOverlap = CountOverlap(exhibit.knowledge.themes, targetThemes);
        var edgeCount = semanticGraph.GetOutgoingSemanticEdges(candidate.TargetId).Count;
        return Mathf.Clamp01(baseScore + Mathf.Min(0.15f, themeOverlap * 0.05f) +
                             Mathf.Min(0.1f, edgeCount * 0.02f));
    }

    private static float ResolveTaskRelevance(
        RdwPlanningSnapshot snapshot,
        NavigationCandidate candidate,
        StaticSceneSemanticGraph semanticGraph)
    {
        if (string.Equals(candidate.TargetId, snapshot.FinalTargetId, StringComparison.Ordinal)) return 1f;
        var score = candidate.TargetRole == NavigationTargetRole.RouteSubgoal ? 0.65f : 0.35f;
        if (semanticGraph == null || !semanticGraph.TryGetExhibit(candidate.TargetId, out var exhibit) ||
            !semanticGraph.TryGetExhibit(snapshot.FinalTargetId, out var finalTarget) ||
            exhibit?.knowledge == null || finalTarget?.knowledge == null)
        {
            return score;
        }

        return Mathf.Clamp01(score + Mathf.Min(
            0.2f,
            CountOverlap(exhibit.knowledge.key_concepts, finalTarget.knowledge.key_concepts) * 0.05f));
    }

    private static int CountOverlap(string[] left, string[] right)
    {
        if (left == null || right == null) return 0;
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        var count = 0;
        foreach (var value in left)
        {
            if (!string.IsNullOrWhiteSpace(value) && rightSet.Contains(value)) count++;
        }
        return count;
    }

    private static bool ContainsNode(IReadOnlyList<PlanningNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node != null && string.Equals(node.NodeId, id, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsVisited(IReadOnlyList<string> values, string expected)
    {
        return Contains(values, expected);
    }

    private static bool Contains(IReadOnlyList<string> values, string expected)
    {
        if (values == null) return false;
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// 组合 Snapshot、Local Graph、Estimator 和 Beam Search 的纯规划入口。
/// </summary>
public sealed class RdwPlanningPipeline
{
    /// <summary>任务启动时仅供 RDW 搜索使用的临时根节点，不属于展品或访问记录。</summary>
    public const string RuntimeStartNodeId = "__runtime_start__";
    private readonly RdwPlanningSnapshotFactory snapshotFactory;
    private readonly RdwPlanningNodeFactory nodeFactory;
    private readonly RdwLocalGraphBuilder graphBuilder;
    private readonly RdwBeamRoutePlanner routePlanner;
    private readonly RdwCapabilityProfile capability;

    /// <summary>当前 Pipeline 使用的 capability identity。</summary>
    public RdwCapabilityProfile Capability => capability;

    /// <summary>创建规划 Pipeline；所有依赖均通过构造函数显式注入。</summary>
    public RdwPlanningPipeline(
        IRdwPlanningCostEstimator estimator,
        RdwCapabilityProfile capabilityProfile,
        RdwPlanningSettings settings)
    {
        var resolvedSettings = settings ?? new RdwPlanningSettings();
        if (estimator == null) throw new ArgumentNullException(nameof(estimator));
        if (capabilityProfile == null) throw new ArgumentNullException(nameof(capabilityProfile));
        if (!string.Equals(estimator.EstimatorId, capabilityProfile.EstimatorId, StringComparison.Ordinal) ||
            !string.Equals(estimator.EstimatorVersion, capabilityProfile.EstimatorVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("Estimator identity must match the capability profile.", nameof(capabilityProfile));
        }

        capability = capabilityProfile;
        snapshotFactory = new RdwPlanningSnapshotFactory();
        nodeFactory = new RdwPlanningNodeFactory();
        graphBuilder = new RdwLocalGraphBuilder();
        routePlanner = new RdwBeamRoutePlanner(
            estimator,
            new RdwHardSafetyFilter(
                resolvedSettings.HardClearanceMeters,
                resolvedSettings.MaximumGainCost),
            resolvedSettings.UtilityWeights,
            resolvedSettings.RouteSearch);
    }

    /// <summary>使用同一冻结事实生成 Local Graph 与 Top-K RouteCandidates。</summary>
    public RdwRoutePlanningResult Plan(
        in RdwPoseFrame pose,
        PhysicalRiskState riskState,
        GuidanceRuntimeState runtimeState,
        NavigationCandidateSet candidateSet,
        NavigationTargetManager navigationTargets,
        StaticSceneSemanticGraph semanticGraph = null)
    {
        var snapshot = snapshotFactory.Capture(
            pose,
            riskState,
            runtimeState,
            navigationTargets != null ? navigationTargets.RegistryRevision : 0,
            capability);
        var nodes = nodeFactory.Capture(snapshot, candidateSet, navigationTargets, semanticGraph);
        var graph = graphBuilder.Build(snapshot, nodes);
        return routePlanner.Search(snapshot, graph);
    }
}
