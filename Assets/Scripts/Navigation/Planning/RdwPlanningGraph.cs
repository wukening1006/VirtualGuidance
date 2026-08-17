using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 保存局部导航图中的运行时节点事实与静态空间邻接。
/// </summary>
public sealed class PlanningNode
{
    private readonly IReadOnlyList<string> spatialNeighborIds;

    /// <summary>节点唯一 ID。</summary>
    public string NodeId { get; }
    /// <summary>节点的 Virtual XZ 位置，单位：米。</summary>
    public Vector2 VirtualPositionXZ { get; }
    /// <summary>节点在当前任务中的导航角色。</summary>
    public NavigationTargetRole TargetRole { get; }
    /// <summary>归一化语义重要性，范围 0-1。</summary>
    public float SemanticImportance { get; }
    /// <summary>归一化任务相关度，范围 0-1。</summary>
    public float TaskRelevance { get; }
    /// <summary>创建快照时该节点是否已访问。</summary>
    public bool Visited { get; }
    /// <summary>静态虚拟空间拓扑邻居的只读副本。</summary>
    public IReadOnlyList<string> SpatialNeighborIds => spatialNeighborIds;

    /// <summary>创建不可变的规划节点。</summary>
    public PlanningNode(
        string nodeId,
        Vector2 virtualPositionXZ,
        NavigationTargetRole targetRole,
        float semanticImportance,
        float taskRelevance,
        bool visited,
        IEnumerable<string> spatialNeighbors = null)
    {
        NodeId = nodeId ?? string.Empty;
        VirtualPositionXZ = virtualPositionXZ;
        TargetRole = targetRole;
        SemanticImportance = Mathf.Clamp01(semanticImportance);
        TaskRelevance = Mathf.Clamp01(taskRelevance);
        Visited = visited;
        spatialNeighborIds = Array.AsReadOnly(new List<string>(spatialNeighbors ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>
/// 描述 Local Graph 的静态空间边和可选动态 RDW 评估。
/// </summary>
public sealed class PlanningEdge
{
    /// <summary>起点 ID。</summary>
    public string FromNodeId { get; }
    /// <summary>终点 ID。</summary>
    public string ToNodeId { get; }
    /// <summary>Virtual XZ 距离，单位：米。</summary>
    public float DistanceMeters { get; }
    /// <summary>静态空间拓扑是否允许连接。</summary>
    public bool Reachable { get; }
    /// <summary>静态不可达原因码。</summary>
    public string ReachabilityReason { get; }
    /// <summary>基于根快照生成的动态 RDW 评估；静态图中为 null。</summary>
    public RdwEdgeEvaluation Evaluation { get; }

    /// <summary>创建一条不可变 Local Graph 边。</summary>
    public PlanningEdge(
        string fromNodeId,
        string toNodeId,
        float distanceMeters,
        bool reachable,
        string reachabilityReason = "",
        RdwEdgeEvaluation evaluation = null)
    {
        FromNodeId = fromNodeId ?? string.Empty;
        ToNodeId = toNodeId ?? string.Empty;
        DistanceMeters = Mathf.Max(0f, distanceMeters);
        Reachable = reachable;
        ReachabilityReason = reachabilityReason ?? string.Empty;
        Evaluation = evaluation;
    }

    /// <summary>返回附加动态 RDW 评估的新边实例。</summary>
    public PlanningEdge WithEvaluation(RdwEdgeEvaluation evaluation)
    {
        return new PlanningEdge(
            FromNodeId,
            ToNodeId,
            DistanceMeters,
            Reachable,
            ReachabilityReason,
            evaluation);
    }
}

/// <summary>
/// 冻结单次规划使用的局部有向图。
/// </summary>
public sealed class LocalNavigationGraph
{
    private readonly IReadOnlyList<PlanningNode> nodes;
    private readonly IReadOnlyList<PlanningEdge> edges;
    private readonly Dictionary<string, PlanningNode> nodesById;
    private readonly Dictionary<string, IReadOnlyList<PlanningEdge>> outgoingByNode;

    /// <summary>图所属 Planning Snapshot ID。</summary>
    public string PlanningSnapshotId { get; }
    /// <summary>稳定排序的节点集合。</summary>
    public IReadOnlyList<PlanningNode> Nodes => nodes;
    /// <summary>稳定排序的有向边集合。</summary>
    public IReadOnlyList<PlanningEdge> Edges => edges;

    /// <summary>创建并索引一份不可变 Local Graph。</summary>
    public LocalNavigationGraph(
        string planningSnapshotId,
        IEnumerable<PlanningNode> graphNodes,
        IEnumerable<PlanningEdge> graphEdges)
    {
        PlanningSnapshotId = planningSnapshotId ?? string.Empty;
        var nodeList = new List<PlanningNode>(graphNodes ?? Array.Empty<PlanningNode>());
        nodeList.Sort((left, right) => string.CompareOrdinal(left?.NodeId, right?.NodeId));
        var edgeList = new List<PlanningEdge>(graphEdges ?? Array.Empty<PlanningEdge>());
        edgeList.Sort(CompareEdges);
        nodes = Array.AsReadOnly(nodeList.ToArray());
        edges = Array.AsReadOnly(edgeList.ToArray());
        nodesById = new Dictionary<string, PlanningNode>(StringComparer.Ordinal);
        outgoingByNode = new Dictionary<string, IReadOnlyList<PlanningEdge>>(StringComparer.Ordinal);

        foreach (var node in nodeList)
        {
            if (node != null && !string.IsNullOrWhiteSpace(node.NodeId))
            {
                nodesById[node.NodeId] = node;
            }
        }

        var mutableOutgoing = new Dictionary<string, List<PlanningEdge>>(StringComparer.Ordinal);
        foreach (var edge in edgeList)
        {
            if (edge == null) continue;
            if (!mutableOutgoing.TryGetValue(edge.FromNodeId, out var outgoing))
            {
                outgoing = new List<PlanningEdge>();
                mutableOutgoing[edge.FromNodeId] = outgoing;
            }
            outgoing.Add(edge);
        }
        foreach (var pair in mutableOutgoing)
        {
            outgoingByNode[pair.Key] = Array.AsReadOnly(pair.Value.ToArray());
        }
    }

    /// <summary>按 ID 查询节点。</summary>
    public bool TryGetNode(string nodeId, out PlanningNode node)
    {
        return nodesById.TryGetValue(nodeId ?? string.Empty, out node);
    }

    /// <summary>返回指定节点的稳定排序出边。</summary>
    public IReadOnlyList<PlanningEdge> GetOutgoing(string nodeId)
    {
        return outgoingByNode.TryGetValue(nodeId ?? string.Empty, out var outgoing)
            ? outgoing
            : Array.Empty<PlanningEdge>();
    }

    private static int CompareEdges(PlanningEdge left, PlanningEdge right)
    {
        var from = string.CompareOrdinal(left?.FromNodeId, right?.FromNodeId);
        return from != 0 ? from : string.CompareOrdinal(left?.ToNodeId, right?.ToNodeId);
    }
}

/// <summary>
/// 从冻结节点事实构建确定性的局部空间图。
/// </summary>
public sealed class RdwLocalGraphBuilder
{
    private readonly float maximumEdgeDistanceMeters;

    /// <summary>创建局部图构建器。</summary>
    public RdwLocalGraphBuilder(float maximumEdgeDistance = 12f)
    {
        maximumEdgeDistanceMeters = Mathf.Max(0.01f, maximumEdgeDistance);
    }

    /// <summary>
    /// 构建 Local Graph；唯一静态连接来源是 spatial_edges，距离只作为边事实。
    /// </summary>
    public LocalNavigationGraph Build(
        RdwPlanningSnapshot snapshot,
        IEnumerable<PlanningNode> candidateNodes)
    {
        if (snapshot == null)
        {
            return new LocalNavigationGraph(string.Empty, null, null);
        }

        var unique = new Dictionary<string, PlanningNode>(StringComparer.Ordinal);
        foreach (var node in candidateNodes ?? Array.Empty<PlanningNode>())
        {
            if (node != null && !string.IsNullOrWhiteSpace(node.NodeId))
            {
                unique[node.NodeId] = node;
            }
        }
        if (!unique.ContainsKey(snapshot.CurrentNodeId))
        {
            unique[snapshot.CurrentNodeId] = new PlanningNode(
                snapshot.CurrentNodeId,
                snapshot.VirtualPositionXZ,
                NavigationTargetRole.RouteSubgoal,
                0f,
                0f,
                true);
        }

        var orderedNodes = new List<PlanningNode>(unique.Values);
        orderedNodes.Sort((left, right) => string.CompareOrdinal(left.NodeId, right.NodeId));
        var edges = new List<PlanningEdge>();
        foreach (var from in orderedNodes)
        {
            foreach (var to in orderedNodes)
            {
                if (ReferenceEquals(from, to) || string.Equals(from.NodeId, to.NodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var distance = Vector2.Distance(from.VirtualPositionXZ, to.VirtualPositionXZ);
                var reachable = Contains(from.SpatialNeighborIds, to.NodeId);
                edges.Add(new PlanningEdge(
                    from.NodeId,
                    to.NodeId,
                    distance,
                    reachable,
                    reachable ? string.Empty : "edge_distance_exceeded"));
            }
        }

        return new LocalNavigationGraph(snapshot.SnapshotId, orderedNodes, edges);
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
