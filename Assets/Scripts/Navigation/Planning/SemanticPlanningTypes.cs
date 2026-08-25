using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>任务不可变的最终目标。</summary>
[Serializable]
public sealed class FinalGoalContract
{
    /// <summary>最终目标展品 ID。</summary>
    [JsonProperty("exhibit_id", Required = Required.Always)] public string exhibit_id;
    /// <summary>最终目标显示名称。</summary>
    [JsonProperty("name", Required = Required.Always)] public string name;
    /// <summary>是否必须到达该目标才能完成任务。</summary>
    [JsonProperty("required", Required = Required.Always)] public bool required = true;

    /// <summary>从静态语义图读取最终展品，找不到时返回 null。</summary>
    public static FinalGoalContract From(StaticSceneSemanticGraph graph, string exhibitId)
    {
        if (graph == null || string.IsNullOrWhiteSpace(exhibitId) ||
            !graph.TryGetExhibit(exhibitId, out var definition)) return null;
        return new FinalGoalContract
        {
            exhibit_id = definition.id,
            name = definition.name,
            required = true
        };
    }
}

/// <summary>约束最终目标解锁前允许执行的中间游览数量。</summary>
[Serializable]
public sealed class IntermediateVisitPolicyContract
{
    /// <summary>最终目标允许被选择前的最少中间展品数量。</summary>
    [JsonProperty("minimum_visits", Required = Required.Always)] public int minimum_visits;
    /// <summary>达到后必须选择最终目标的中间展品数量。</summary>
    [JsonProperty("maximum_visits", Required = Required.Always)] public int maximum_visits;
    /// <summary>最少数量达成前是否排除最终目标。</summary>
    [JsonProperty("exclude_final_until_ready", Required = Required.Always)] public bool exclude_final_until_ready = true;
    /// <summary>中间展品是否必须与最终目标存在语义图连接。</summary>
    [JsonProperty("require_semantic_connection", Required = Required.Always)] public bool require_semantic_connection = true;
}

/// <summary>三个正式条件共享的自然语言任务契约。</summary>
[Serializable]
public sealed class NaturalLanguageTaskContract
{
    /// <summary>稳定任务 ID。</summary>
    [JsonProperty("task_id", Required = Required.Always)] public string task_id;
    /// <summary>提供给模型理解意图的自然语言任务。</summary>
    [JsonProperty("user_task", Required = Required.Always)] public string user_task;
    /// <summary>不可变最终目标。</summary>
    [JsonProperty("final_goal", Required = Required.Always)] public FinalGoalContract final_goal;
    /// <summary>中间游览约束。</summary>
    [JsonProperty("intermediate_policy", Required = Required.Always)] public IntermediateVisitPolicyContract intermediate_policy;

    /// <summary>从运行状态和静态语义图构建权威任务契约。</summary>
    public static NaturalLanguageTaskContract From(StaticSceneSemanticGraph graph, GuidanceRuntimeState state)
    {
        if (state == null) return null;
        var finalGoal = FinalGoalContract.From(graph, state.finalTargetId);
        if (finalGoal == null) return null;
        return new NaturalLanguageTaskContract
        {
            task_id = state.taskId,
            user_task = state.userTask,
            final_goal = finalGoal,
            intermediate_policy = new IntermediateVisitPolicyContract
            {
                minimum_visits = state.minimumIntermediateVisits,
                maximum_visits = state.maximumIntermediateVisits,
                exclude_final_until_ready = true,
                require_semantic_connection = true
            }
        };
    }
}

/// <summary>发送给模型的只读任务进度快照。</summary>
[Serializable]
public sealed class NaturalLanguageTaskState
{
    /// <summary>exploring、final_approach 或 completed。</summary>
    [JsonProperty("phase", Required = Required.Always)] public string phase;
    /// <summary>当前展品 ID。</summary>
    [JsonProperty("current_exhibit_id", Required = Required.Always)] public string current_exhibit_id;
    /// <summary>已访问展品 ID。</summary>
    [JsonProperty("visited_exhibit_ids", Required = Required.Always)] public string[] visited_exhibit_ids = Array.Empty<string>();
    /// <summary>已完成的中间展品数量。</summary>
    [JsonProperty("intermediate_visit_count", Required = Required.Always)] public int intermediate_visit_count;
    /// <summary>当前强制目标；探索阶段为空。</summary>
    [JsonProperty("required_target_id", Required = Required.Always)] public string required_target_id;

    /// <summary>从运行状态创建不可变请求快照。</summary>
    public static NaturalLanguageTaskState From(GuidanceRuntimeState state)
    {
        if (state == null) return null;
        return new NaturalLanguageTaskState
        {
            phase = state.taskStage == GuidanceTaskStage.FinalApproach
                ? "final_approach"
                : state.taskStage == GuidanceTaskStage.Completed ? "completed" : "exploring",
            current_exhibit_id = state.currentExhibitId,
            visited_exhibit_ids = state.visitedExhibitIds != null
                ? (string[])state.visitedExhibitIds.Clone()
                : Array.Empty<string>(),
            intermediate_visit_count = state.intermediateVisitCount,
            required_target_id = state.taskStage == GuidanceTaskStage.FinalApproach
                ? state.finalTargetId
                : string.Empty
        };
    }
}

/// <summary>
/// Semantic-only Planner 使用的节点；不包含 Physical Risk、Clearance 或 RDW 字段。
/// </summary>
[Serializable]
public sealed class SemanticPlanningNode
{
    /// <summary>展品稳定 ID。</summary>
    [JsonProperty("id", Required = Required.Always)] public string id;
    /// <summary>展品名称。</summary>
    [JsonProperty("name", Required = Required.Always)] public string name;
    /// <summary>展品分类。</summary>
    [JsonProperty("category", Required = Required.Always)] public string category;
    /// <summary>文化归属。</summary>
    [JsonProperty("culture", Required = Required.Always)] public string culture;
    /// <summary>年代信息。</summary>
    [JsonProperty("chronology", Required = Required.Always)] public string chronology;
    /// <summary>展品描述。</summary>
    [JsonProperty("description", Required = Required.Always)] public string description;
    /// <summary>可观察视觉特征。</summary>
    [JsonProperty("visual_features", Required = Required.Always)] public string[] visual_features = Array.Empty<string>();
    /// <summary>文化意义。</summary>
    [JsonProperty("cultural_significance", Required = Required.Always)] public string[] cultural_significance = Array.Empty<string>();
    /// <summary>关键知识概念。</summary>
    [JsonProperty("key_concepts", Required = Required.Always)] public string[] key_concepts = Array.Empty<string>();
    /// <summary>解释边界说明。</summary>
    [JsonProperty("interpretation_notes", Required = Required.Always)] public InterpretationNote[] interpretation_notes = Array.Empty<InterpretationNote>();
    /// <summary>局部语义边。</summary>
    [JsonProperty("semantic_edges", Required = Required.Always)] public SemanticEdgeDefinition[] semantic_edges = Array.Empty<SemanticEdgeDefinition>();

    /// <summary>从静态展品知识创建不带物理执行事实的 Planner 节点。</summary>
    public static SemanticPlanningNode From(
        SceneExhibitDefinition exhibit,
        IEnumerable<SemanticEdgeDefinition> semanticEdges)
    {
        if (exhibit == null || exhibit.knowledge == null) return null;
        return new SemanticPlanningNode
        {
            id = exhibit.id,
            name = exhibit.name,
            category = exhibit.knowledge.category,
            culture = exhibit.knowledge.culture,
            chronology = exhibit.knowledge.chronology,
            description = exhibit.knowledge.description,
            visual_features = Copy(exhibit.knowledge.visual_features),
            cultural_significance = Copy(exhibit.knowledge.cultural_significance),
            key_concepts = Copy(exhibit.knowledge.key_concepts),
            interpretation_notes = Copy(exhibit.knowledge.interpretation_notes),
            semantic_edges = Copy(semanticEdges)
        };
    }

    private static string[] Copy(string[] values) => values != null ? (string[])values.Clone() : Array.Empty<string>();

    private static InterpretationNote[] Copy(InterpretationNote[] values) =>
        values != null ? (InterpretationNote[])values.Clone() : Array.Empty<InterpretationNote>();

    private static SemanticEdgeDefinition[] Copy(IEnumerable<SemanticEdgeDefinition> values) =>
        new List<SemanticEdgeDefinition>(values ?? Array.Empty<SemanticEdgeDefinition>()).ToArray();

}

/// <summary>Semantic-only Planner 请求；不携带 Physical Risk 快照。</summary>
[Serializable]
public sealed class SemanticPlanRequest
{
    /// <summary>语义局部规划协议版本。</summary>
    public string planning_schema_version = "semantic-local-planning-v2";
    /// <summary>实验会话 ID。</summary>
    public string session_id;
    /// <summary>请求 ID。</summary>
    public string request_id;
    /// <summary>语义规划代际。</summary>
    public int generation;
    /// <summary>任务状态版本。</summary>
    public int task_state_version;
    /// <summary>自然语言与不可变最终目标组成的任务契约。</summary>
    public NaturalLanguageTaskContract task;
    /// <summary>当前任务进度快照。</summary>
    public NaturalLanguageTaskState task_state;
    /// <summary>局部语义节点。</summary>
    public SemanticPlanningNode[] local_semantic_nodes = Array.Empty<SemanticPlanningNode>();
    /// <summary>局部语义边。</summary>
    public SemanticEdgeDefinition[] local_semantic_edges = Array.Empty<SemanticEdgeDefinition>();
}

/// <summary>Semantic-only Planner 响应；LLM 只能控制节点序列和讲解文本。</summary>
[Serializable]
public sealed class SemanticPlanResponse
{
    /// <summary>语义局部规划协议版本。</summary>
    public string planning_schema_version;
    /// <summary>实验会话 ID。</summary>
    public string session_id;
    /// <summary>请求 ID。</summary>
    public string request_id;
    /// <summary>规划代际。</summary>
    public int generation;
    /// <summary>计划节点序列。</summary>
    public string[] ordered_node_ids = Array.Empty<string>();
    /// <summary>计划讲解。</summary>
    public string narration;
    /// <summary>静态 Verifier 结果。</summary>
    public GuidanceVerifierResult verifier;

    /// <summary>验证响应身份和节点成员关系，失败时返回稳定原因。</summary>
    public bool TryValidate(SemanticPlanRequest request, out string failureReason)
    {
        failureReason = string.Empty;
        if (request == null)
        {
            failureReason = "semantic_plan_request_missing";
            return false;
        }
        if (request.task?.final_goal == null || request.task.intermediate_policy == null ||
            request.task_state == null)
        {
            failureReason = "semantic_plan_task_contract_missing";
            return false;
        }
        if (!string.Equals(planning_schema_version, request.planning_schema_version, StringComparison.Ordinal) ||
            !string.Equals(session_id, request.session_id, StringComparison.Ordinal) ||
            !string.Equals(request_id, request.request_id, StringComparison.Ordinal) ||
            generation != request.generation)
        {
            failureReason = "semantic_plan_identity_mismatch";
            return false;
        }
        if (verifier == null || !verifier.approved)
        {
            failureReason = verifier != null ? verifier.reason : "semantic_plan_verifier_missing";
            return false;
        }
        if (ordered_node_ids == null || ordered_node_ids.Length == 0)
        {
            failureReason = "semantic_plan_empty";
            return false;
        }
        return SemanticPlanVerifier.TryVerify(request, ordered_node_ids, out failureReason);
    }
}

/// <summary>Unity 与 Backend 共用的确定性 Verifier 结果字段。</summary>
[Serializable]
public sealed class GuidanceVerifierResult
{
    /// <summary>是否通过验证。</summary>
    public bool approved;
    /// <summary>稳定原因码。</summary>
    public string reason;
}

/// <summary>
/// Unity 侧 Semantic-only 计划验证器；只读取节点成员关系和任务约束。
/// </summary>
public static class SemanticPlanVerifier
{
    /// <summary>验证单节点计划的成员关系、重复节点和任务约束。</summary>
    public static bool TryVerify(
        SemanticPlanRequest request,
        IReadOnlyList<string> orderedNodeIds,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (request == null)
        {
            failureReason = "semantic_plan_request_missing";
            return false;
        }
        if (request.task?.final_goal == null || request.task.intermediate_policy == null ||
            request.task_state == null)
        {
            failureReason = "semantic_plan_task_contract_missing";
            return false;
        }
        if (orderedNodeIds == null || orderedNodeIds.Count != 1)
        {
            failureReason = "semantic_plan_length_invalid";
            return false;
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in request.local_semantic_nodes ?? Array.Empty<SemanticPlanningNode>())
        {
            if (node != null && !string.IsNullOrWhiteSpace(node.id)) nodeIds.Add(node.id);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeId in orderedNodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || !nodeIds.Contains(nodeId) || !seen.Add(nodeId))
            {
                failureReason = "semantic_plan_node_membership_invalid";
                return false;
            }
        }
        var containsCurrent = !string.IsNullOrWhiteSpace(request.task_state.current_exhibit_id) &&
                              IndexOf(orderedNodeIds, request.task_state.current_exhibit_id) >= 0;
        if (containsCurrent)
        {
            failureReason = "semantic_plan_starts_at_current_exhibit";
            return false;
        }

        var visited = new HashSet<string>(
            request.task_state.visited_exhibit_ids ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        foreach (var nodeId in orderedNodeIds)
        {
            if (visited.Contains(nodeId))
            {
                failureReason = "semantic_plan_targets_visited_exhibit";
                return false;
            }
        }

        var finalTargetId = request.task?.final_goal?.exhibit_id ?? string.Empty;
        var policy = request.task?.intermediate_policy;
        var progress = request.task_state;
        if (string.IsNullOrWhiteSpace(finalTargetId) || policy == null || progress == null)
        {
            failureReason = "semantic_plan_task_contract_missing";
            return false;
        }

        var finalIndex = IndexOf(orderedNodeIds, finalTargetId);
        var finalReady = progress.intermediate_visit_count >= policy.minimum_visits;
        var finalRequired = string.Equals(progress.phase, "final_approach", StringComparison.Ordinal) ||
                            progress.intermediate_visit_count >= policy.maximum_visits;
        if (finalIndex >= 0 && (!finalReady || finalIndex != 0 || orderedNodeIds.Count != 1))
        {
            failureReason = finalReady
                ? "semantic_plan_final_goal_must_be_single_target"
                : "semantic_plan_final_goal_locked";
            return false;
        }
        if (finalRequired && finalIndex != 0)
        {
            failureReason = "semantic_plan_final_goal_required";
            return false;
        }

        // Semantic Edge 只提供相关性上下文，不作为候选可达性门禁。
        return true;
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], target, StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    /// <summary>检查两个展品是否通过静态语义关系链连通。</summary>
    public static bool HasSemanticPath(
        IReadOnlyList<SemanticEdgeDefinition> edges,
        string source,
        string target)
    {
        if (string.Equals(source, target, StringComparison.Ordinal)) return true;
        var visited = new HashSet<string>(StringComparer.Ordinal) { source };
        var frontier = new Queue<string>();
        frontier.Enqueue(source);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var edge in edges ?? Array.Empty<SemanticEdgeDefinition>())
            {
                if (edge == null) continue;
                var neighbor = string.Equals(edge.source, current, StringComparison.Ordinal)
                    ? edge.target
                    : string.Equals(edge.target, current, StringComparison.Ordinal)
                        ? edge.source
                        : string.Empty;
                if (string.IsNullOrWhiteSpace(neighbor) || !visited.Add(neighbor)) continue;
                if (string.Equals(neighbor, target, StringComparison.Ordinal)) return true;
                frontier.Enqueue(neighbor);
            }
        }
        return false;
    }

    private static bool HasSemanticConnection(
        IReadOnlyList<SemanticEdgeDefinition> edges,
        string source,
        string target)
    {
        foreach (var edge in edges ?? Array.Empty<SemanticEdgeDefinition>())
        {
            if (edge == null) continue;
            if (string.Equals(edge.source, source, StringComparison.Ordinal) &&
                string.Equals(edge.target, target, StringComparison.Ordinal)) return true;
            if (edge.directed != true &&
                string.Equals(edge.source, target, StringComparison.Ordinal) &&
                string.Equals(edge.target, source, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>Semantic-only 规划响应的严格 JSON 解析和序列化协议。</summary>
public static class SemanticPlanProtocol
{
    private static readonly HashSet<string> ResponseFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "planning_schema_version", "session_id", "request_id", "generation",
        "ordered_node_ids", "narration", "verifier"
    };

    private static readonly HashSet<string> VerifierFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "approved", "reason"
    };

    /// <summary>解析并 Fail-Closed 校验语义规划响应。</summary>
    public static bool TryParseResponse(
        string json,
        SemanticPlanRequest request,
        out SemanticPlanResponse response,
        out string failureReason)
    {
        response = null;
        failureReason = string.Empty;
        if (request == null || string.IsNullOrWhiteSpace(json))
        {
            failureReason = "semantic_plan_response_or_request_missing";
            return false;
        }
        try
        {
            var root = JObject.Parse(json);
            if (!HasExactFields(root, ResponseFields, "response", out failureReason) ||
                !(root["verifier"] is JObject verifier) ||
                !HasExactFields(verifier, VerifierFields, "verifier", out failureReason) ||
                !TryVerifyResponseScalarTypes(root, verifier, out failureReason))
            {
                return false;
            }
            response = root.ToObject<SemanticPlanResponse>();
        }
        catch (JsonException exception)
        {
            failureReason = "invalid_semantic_plan_json:" + exception.Message;
            return false;
        }

        if (response == null || response.verifier == null || !response.verifier.approved)
        {
            failureReason = response?.verifier?.reason ?? "semantic_plan_verifier_missing";
            return false;
        }
        if (!string.Equals(response.planning_schema_version, request.planning_schema_version, StringComparison.Ordinal) ||
            !string.Equals(response.session_id, request.session_id, StringComparison.Ordinal) ||
            !string.Equals(response.request_id, request.request_id, StringComparison.Ordinal) ||
            response.generation != request.generation)
        {
            failureReason = "semantic_plan_identity_mismatch";
            return false;
        }
        if (string.IsNullOrWhiteSpace(response.narration) ||
            string.IsNullOrWhiteSpace(response.verifier.reason))
        {
            return false;
        }
        return response.TryValidate(request, out failureReason);
    }

    /// <summary>使用 JSON.NET 序列化 Semantic-only 请求。</summary>
    public static string SerializeRequest(SemanticPlanRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return JsonConvert.SerializeObject(request, Formatting.None);
    }

    private static bool TryVerifyResponseScalarTypes(JObject root, JObject verifier, out string reason)
    {
        var stringFields = new[] { "planning_schema_version", "session_id", "request_id", "narration" };
        foreach (var field in stringFields)
        {
            if (root[field]?.Type != JTokenType.String)
            {
                reason = "invalid_semantic_plan_field_type:" + field;
                return false;
            }
        }
        if (root["generation"]?.Type != JTokenType.Integer ||
            root["ordered_node_ids"]?.Type != JTokenType.Array ||
            verifier["approved"]?.Type != JTokenType.Boolean ||
            verifier["reason"]?.Type != JTokenType.String)
        {
            reason = "invalid_semantic_plan_field_type";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool HasExactFields(JObject value, HashSet<string> expected, string name, out string reason)
    {
        foreach (var field in expected)
        {
            if (value.Property(field) == null)
            {
                reason = $"required_{name}_field_missing:{field}";
                return false;
            }
        }
        foreach (var property in value.Properties())
        {
            if (!expected.Contains(property.Name))
            {
                reason = $"unexpected_{name}_field:{property.Name}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// 由静态语义图构建局部 Semantic-only Planner 输入；不依赖 PhysicalRiskEvaluator。
/// </summary>
public sealed class SemanticLocalGraphBuilder
{
    /// <summary>根据当前任务策略从全部展品构建局部语义图。</summary>
    public SemanticPlanRequest Build(
        StaticSceneSemanticGraph graph,
        NaturalLanguageTaskContract task,
        NaturalLanguageTaskState taskState,
        string sessionId,
        string requestId,
        int generation,
        int taskStateVersion)
    {
        if (graph == null) throw new ArgumentNullException(nameof(graph));
        if (task == null) throw new ArgumentNullException(nameof(task));
        if (taskState == null) throw new ArgumentNullException(nameof(taskState));
        var currentExhibitId = taskState.current_exhibit_id ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentExhibitId) && !graph.TryGetExhibit(currentExhibitId, out _))
        {
            throw new ArgumentException("current exhibit is not present in the static semantic graph", nameof(currentExhibitId));
        }

        var included = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(currentExhibitId)) included.Add(currentExhibitId);
        var visited = new HashSet<string>(taskState.visited_exhibit_ids ?? Array.Empty<string>(), StringComparer.Ordinal);
        var finalTargetId = task.final_goal.exhibit_id;
        var finalReady = taskState.intermediate_visit_count >= task.intermediate_policy.minimum_visits;
        var finalRequired = taskState.phase == "final_approach" ||
                            taskState.intermediate_visit_count >= task.intermediate_policy.maximum_visits;
        foreach (var exhibit in graph.GetAllExhibits())
        {
            if (exhibit == null || string.Equals(exhibit.id, currentExhibitId, StringComparison.Ordinal) || visited.Contains(exhibit.id)) continue;
            if (string.Equals(exhibit.id, finalTargetId, StringComparison.Ordinal))
            {
                if (finalReady || finalRequired) included.Add(exhibit.id);
                continue;
            }
            if (!finalRequired) included.Add(exhibit.id);
        }
        if (finalRequired && graph.TryGetExhibit(finalTargetId, out _)) included.Add(finalTargetId);
        var nodes = new List<SemanticPlanningNode>();
        foreach (var exhibitId in included)
        {
            if (!graph.TryGetExhibit(exhibitId, out var exhibit)) continue;
            nodes.Add(SemanticPlanningNode.From(
                exhibit,
                FilterSemanticEdges(graph.SemanticEdges, included)));
        }
        nodes.Sort((left, right) => string.CompareOrdinal(left.id, right.id));
        var localSemanticEdges = FilterSemanticEdges(graph.SemanticEdges, included);
        return new SemanticPlanRequest
        {
            session_id = sessionId ?? string.Empty,
            request_id = requestId ?? string.Empty,
            generation = Math.Max(1, generation),
            task_state_version = Math.Max(0, taskStateVersion),
            task = task,
            task_state = taskState,
            local_semantic_nodes = nodes.ToArray(),
            local_semantic_edges = localSemanticEdges
        };
    }

    private static SemanticEdgeDefinition[] FilterSemanticEdges(
        IEnumerable<SemanticEdgeDefinition> edges,
        HashSet<string> included)
    {
        var result = new List<SemanticEdgeDefinition>();
        foreach (var edge in edges ?? Array.Empty<SemanticEdgeDefinition>())
        {
            if (edge != null && included.Contains(edge.source) && included.Contains(edge.target)) result.Add(edge);
        }
        return result.ToArray();
    }

}

/// <summary>Unity 执行前使用的物理安全候选；不作为 Semantic-only Planner 输入。</summary>
public sealed class ExecutableTargetCandidate
{
    /// <summary>目标展品 ID。</summary>
    public string TargetId { get; }
    /// <summary>目标职责。</summary>
    public NavigationTargetRole TargetRole { get; }
    /// <summary>相对方向标签。</summary>
    public string RelativeDirection { get; }
    /// <summary>相对偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>目标距离，单位：米。</summary>
    public float DistanceMeters { get; }
    /// <summary>当前方向净空，单位：米。</summary>
    public float ClearanceMeters { get; }
    /// <summary>当前方向安全状态。</summary>
    public HeadingSafety HeadingSafety { get; }
    /// <summary>执行资格。</summary>
    public CandidateExecutionState ExecutionState { get; }
    /// <summary>创建执行候选。</summary>
    public ExecutableTargetCandidate(
        string targetId,
        NavigationTargetRole targetRole,
        string relativeDirection,
        float relativeYawDegrees,
        float distanceMeters,
        float clearanceMeters,
        HeadingSafety headingSafety,
        CandidateExecutionState executionState)
    {
        TargetId = targetId ?? string.Empty;
        TargetRole = targetRole;
        RelativeDirection = relativeDirection ?? string.Empty;
        RelativeYawDegrees = relativeYawDegrees;
        DistanceMeters = Math.Max(0f, distanceMeters);
        ClearanceMeters = Math.Max(0f, clearanceMeters);
        HeadingSafety = headingSafety;
        ExecutionState = executionState;
    }
}

/// <summary>
/// 将语义计划的首个节点转换为 Unity 内部物理执行候选。
/// </summary>
public sealed class GuidanceExecutionSafetyEvaluator
{
    private readonly NavigationTargetManager navigationTargets;
    private readonly PhysicalRiskEvaluator riskEvaluator;

    /// <summary>创建只依赖导航索引和物理风险评估器的执行安全层。</summary>
    public GuidanceExecutionSafetyEvaluator(
        NavigationTargetManager navigationTargets,
        PhysicalRiskEvaluator riskEvaluator)
    {
        this.navigationTargets = navigationTargets;
        this.riskEvaluator = riskEvaluator;
    }

    /// <summary>按最新物理快照评估语义目标，输出不回传 Planner 的执行候选。</summary>
    public bool TryEvaluate(
        string targetId,
        NavigationTargetRole targetRole,
        PhysicalRiskState riskState,
        out ExecutableTargetCandidate candidate,
        out string failureReason)
    {
        candidate = null;
        failureReason = string.Empty;
        if (navigationTargets == null || riskEvaluator == null || riskState == null ||
            !navigationTargets.TryGetTargetBearing(targetId, out var bearing))
        {
            failureReason = "execution_safety_dependencies_unavailable";
            return false;
        }

        var physicalForward = RotatePhysicalForward(
            riskState.user_physical_forward,
            bearing.RelativeYawDegrees);
        if (!riskEvaluator.TryEvaluateAbsoluteHeading(riskState, physicalForward, out var heading))
        {
            failureReason = "execution_safety_heading_evaluation_failed";
            return false;
        }

        var executionState = PhysicalRiskEvaluator.IsMovementExecutable(heading)
            ? CandidateExecutionState.Executable
            : CandidateExecutionState.Blocked;
        candidate = new ExecutableTargetCandidate(
            targetId,
            targetRole,
            BearingLabelProtocolMapper.ToProtocol(bearing.DisplayLabel),
            bearing.RelativeYawDegrees,
            bearing.DistanceMeters,
            heading.ClearanceMeters,
            heading.Safety,
            executionState);
        if (executionState != CandidateExecutionState.Executable)
        {
            failureReason = "execution_safety_rejected:" + heading.Safety;
            return false;
        }
        return true;
    }

    private static Vector2 RotatePhysicalForward(Vector2 forward, float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        var cosine = Mathf.Cos(radians);
        var sine = Mathf.Sin(radians);
        var normalized = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        return new Vector2(
            normalized.x * cosine + normalized.y * sine,
            -normalized.x * sine + normalized.y * cosine).normalized;
    }
}

/// <summary>Ours Reset 期间使用的只读语义偏好上下文，不包含任何安全判定。</summary>
public sealed class SemanticResetContext
{
    /// <summary>不可变最终目标 ID。</summary>
    public string FinalGoalId { get; }
    /// <summary>当前活动计划 ID。</summary>
    public string ActivePlanId { get; }
    /// <summary>当前活动路线 ID。</summary>
    public string ActiveRouteId { get; }
    /// <summary>当前展品 ID。</summary>
    public string CurrentExhibitId { get; }
    /// <summary>下一语义目标 ID。</summary>
    public string NextSemanticTargetId { get; }
    /// <summary>当前计划尚未执行的节点。</summary>
    public IReadOnlyList<string> RemainingPlanNodeIds { get; }

    /// <summary>创建 Reset 语义偏好上下文。</summary>
    public SemanticResetContext(
        string finalGoalId,
        string activePlanId,
        string activeRouteId,
        string currentExhibitId,
        string nextSemanticTargetId,
        IEnumerable<string> remainingPlanNodeIds)
    {
        FinalGoalId = finalGoalId ?? string.Empty;
        ActivePlanId = activePlanId ?? string.Empty;
        ActiveRouteId = activeRouteId ?? string.Empty;
        CurrentExhibitId = currentExhibitId ?? string.Empty;
        NextSemanticTargetId = nextSemanticTargetId ?? string.Empty;
        RemainingPlanNodeIds = Array.AsReadOnly(
            new List<string>(remainingPlanNodeIds ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>Ours 的 RDW-grounded Top-K 路线候选；允许包含物理规划代价。</summary>
[Serializable]
public sealed class RdwRouteCandidate
{
    /// <summary>路线稳定 ID。</summary>
    public string route_id;
    /// <summary>有序路线节点。</summary>
    public string[] ordered_node_ids = Array.Empty<string>();
    /// <summary>第一步目标。</summary>
    public string first_target_id;
    /// <summary>路线 utility。</summary>
    public float route_utility;
    /// <summary>任务语义进展。</summary>
    public float semantic_progress;
    /// <summary>路线边代价。</summary>
    public RdwRouteCandidateEdge[] edges = Array.Empty<RdwRouteCandidateEdge>();
}

/// <summary>Ours 路线边中的物理与 RDW 代价。</summary>
[Serializable]
public sealed class RdwRouteCandidateEdge
{
    /// <summary>起点。</summary>
    public string from_node_id;
    /// <summary>终点。</summary>
    public string to_node_id;
    /// <summary>相对偏航角，单位：度。</summary>
    public float relative_yaw_deg;
    /// <summary>净空，单位：米。</summary>
    public float clearance_m;
    /// <summary>RDW benefit。</summary>
    public float rdw_benefit;
    /// <summary>Gain cost。</summary>
    public float gain_cost;
    /// <summary>Reset pressure。</summary>
    public float reset_pressure;
}
