using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// 冻结 RDW Route Selection 跨端协议版本。
/// </summary>
public static class RdwPlanningProtocolVersions
{
    /// <summary>规划路线选择协议版本。</summary>
    public const string RouteSelectionSchema = "rdw-route-selection-v4";
}

/// <summary>提供给 OURS Route Selection 的静态语义主题定义。</summary>
[Serializable]
public sealed class RdwSemanticThemeDto
{
    /// <summary>主题稳定 ID。</summary>
    public string id;
    /// <summary>主题显示名称。</summary>
    public string name;
    /// <summary>主题知识描述。</summary>
    public string description;

    /// <summary>从静态语义图主题创建请求副本。</summary>
    public static RdwSemanticThemeDto From(SceneThemeDefinition theme)
    {
        if (theme == null) return null;
        return new RdwSemanticThemeDto
        {
            id = theme.id,
            name = theme.name,
            description = theme.description
        };
    }
}

/// <summary>提供给 OURS Route Selection 的展品语义知识节点。</summary>
[Serializable]
public sealed class RdwSemanticContextNode
{
    /// <summary>展品稳定 ID。</summary>
    public string id;
    /// <summary>展品名称。</summary>
    public string name;
    /// <summary>展品分类。</summary>
    public string category;
    /// <summary>文化归属。</summary>
    public string culture;
    /// <summary>年代信息。</summary>
    public string chronology;
    /// <summary>考古语境。</summary>
    public string archaeological_context;
    /// <summary>展品描述。</summary>
    public string description;
    /// <summary>可观察视觉特征。</summary>
    public string[] visual_features = Array.Empty<string>();
    /// <summary>文化意义。</summary>
    public string[] cultural_significance = Array.Empty<string>();
    /// <summary>关键知识概念。</summary>
    public string[] key_concepts = Array.Empty<string>();
    /// <summary>所属主题 ID。</summary>
    public string[] themes = Array.Empty<string>();
    /// <summary>解释边界说明。</summary>
    public InterpretationNote[] interpretation_notes = Array.Empty<InterpretationNote>();

    /// <summary>从静态展品知识创建请求副本。</summary>
    public static RdwSemanticContextNode From(SceneExhibitDefinition exhibit)
    {
        if (exhibit == null || exhibit.knowledge == null) return null;
        return new RdwSemanticContextNode
        {
            id = exhibit.id,
            name = exhibit.name,
            category = exhibit.knowledge.category,
            culture = exhibit.knowledge.culture,
            chronology = exhibit.knowledge.chronology,
            archaeological_context = exhibit.knowledge.archaeological_context,
            description = exhibit.knowledge.description,
            visual_features = Copy(exhibit.knowledge.visual_features),
            cultural_significance = Copy(exhibit.knowledge.cultural_significance),
            key_concepts = Copy(exhibit.knowledge.key_concepts),
            themes = Copy(exhibit.knowledge.themes),
            interpretation_notes = Copy(exhibit.knowledge.interpretation_notes)
        };
    }

    private static string[] Copy(string[] values) =>
        values != null ? (string[])values.Clone() : Array.Empty<string>();

    private static InterpretationNote[] Copy(InterpretationNote[] values) =>
        values != null ? (InterpretationNote[])values.Clone() : Array.Empty<InterpretationNote>();
}

/// <summary>从静态语义图提取 OURS 路线相关的局部语义上下文。</summary>
public static class RdwSemanticContextBuilder
{
    /// <summary>仅包含当前节点、候选路线节点和最终目标，避免发送无关知识。</summary>
    public static void Build(
        StaticSceneSemanticGraph graph,
        RdwRoutePlanningResult result,
        out RdwSemanticContextNode[] nodes,
        out SemanticEdgeDefinition[] edges,
        out RdwSemanticThemeDto[] themes)
    {
        nodes = Array.Empty<RdwSemanticContextNode>();
        edges = Array.Empty<SemanticEdgeDefinition>();
        themes = Array.Empty<RdwSemanticThemeDto>();
        if (graph == null || result?.Snapshot == null) return;

        var included = new HashSet<string>(StringComparer.Ordinal)
        {
            result.Snapshot.CurrentNodeId,
            result.Snapshot.FinalTargetId
        };
        foreach (var route in result.Routes ?? Array.Empty<RouteCandidate>())
        {
            foreach (var nodeId in route?.OrderedNodeIds ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(nodeId)) included.Add(nodeId);
            }
        }

        var nodeList = new List<RdwSemanticContextNode>();
        foreach (var nodeId in included)
        {
            if (graph.TryGetExhibit(nodeId, out var exhibit))
            {
                var node = RdwSemanticContextNode.From(exhibit);
                if (node != null) nodeList.Add(node);
            }
        }
        nodeList.Sort((left, right) => string.CompareOrdinal(left?.id, right?.id));

        var edgeList = new List<SemanticEdgeDefinition>();
        foreach (var edge in graph.SemanticEdges ?? Array.Empty<SemanticEdgeDefinition>())
        {
            if (edge != null && included.Contains(edge.source) && included.Contains(edge.target))
            {
                edgeList.Add(edge);
            }
        }
        edgeList.Sort((left, right) => string.CompareOrdinal(left?.id, right?.id));

        var themeList = new List<RdwSemanticThemeDto>();
        foreach (var theme in graph.Themes ?? Array.Empty<SceneThemeDefinition>())
        {
            if (theme != null) themeList.Add(RdwSemanticThemeDto.From(theme));
        }
        themeList.Sort((left, right) => string.CompareOrdinal(left?.id, right?.id));
        nodes = nodeList.ToArray();
        edges = edgeList.ToArray();
        themes = themeList.ToArray();
    }
}

/// <summary>
/// 序列化单条规划边的通用代价，不暴露 Gain 或 Reset 执行命令。
/// </summary>
[Serializable]
public sealed class RdwRouteEdgeDto
{
    /// <summary>起点 ID。</summary>
    public string from_node_id;
    /// <summary>终点 ID。</summary>
    public string to_node_id;
    /// <summary>连续相对偏航角，单位：度。</summary>
    public float relative_yaw_deg;
    /// <summary>Unity Exact Safety 净空，单位：米。</summary>
    public float clearance_m;
    /// <summary>归一化 RDW benefit。</summary>
    public float rdw_benefit;
    /// <summary>归一化 Gain cost。</summary>
    public float gain_cost;
    /// <summary>归一化恢复压力。</summary>
    public float reset_pressure;
}

/// <summary>
/// 序列化可由 MLLM 选择的单条 RouteCandidate。
/// </summary>
[Serializable]
public sealed class RdwRouteCandidateDto
{
    /// <summary>候选路线 ID。</summary>
    public string route_id;
    /// <summary>有序节点 ID。</summary>
    public string[] ordered_node_ids = Array.Empty<string>();
    /// <summary>第一步目标 ID。</summary>
    public string first_target_id;
    /// <summary>路线 Utility。</summary>
    public float route_utility;
    /// <summary>路线带来的任务语义进展，范围 0-1。</summary>
    public float semantic_progress;
    /// <summary>估计器 ID。</summary>
    public string estimator_id;
    /// <summary>估计器版本。</summary>
    public string estimator_version;
    /// <summary>路线边代价。</summary>
    public RdwRouteEdgeDto[] edges = Array.Empty<RdwRouteEdgeDto>();

    /// <summary>从不可变 RouteCandidate 创建协议副本。</summary>
    public static RdwRouteCandidateDto From(RouteCandidate route, string rootNodeId)
    {
        if (route == null) return null;
        var nodes = new string[route.OrderedNodeIds.Count];
        for (var index = 0; index < nodes.Length; index++) nodes[index] = route.OrderedNodeIds[index];
        var edgeDtos = new List<RdwRouteEdgeDto>();
        for (var index = 0; index < route.EdgeEvaluations.Count; index++)
        {
            var evaluation = route.EdgeEvaluations[index];
            edgeDtos.Add(new RdwRouteEdgeDto
            {
                from_node_id = index == 0 ? rootNodeId ?? string.Empty : route.OrderedNodeIds[index - 1],
                to_node_id = index < route.OrderedNodeIds.Count ? route.OrderedNodeIds[index] : string.Empty,
                relative_yaw_deg = evaluation.RelativeYawDegrees,
                clearance_m = evaluation.ClearanceMeters,
                rdw_benefit = evaluation.RdwBenefit,
                gain_cost = evaluation.GainCost,
                reset_pressure = evaluation.ResetPressure
            });
        }
        return new RdwRouteCandidateDto
        {
            route_id = route.RouteId,
            ordered_node_ids = nodes,
            first_target_id = route.FirstTargetId,
            route_utility = route.RouteUtility,
            semantic_progress = route.SemanticProgress,
            estimator_id = route.EstimatorId,
            estimator_version = route.EstimatorVersion,
            edges = edgeDtos.ToArray()
        };
    }
}

/// <summary>
/// Unity 发送给后端的 Top-K Route Selection 请求。
/// </summary>
[Serializable]
public sealed class RdwRouteSelectionRequest
{
    /// <summary>规划协议版本。</summary>
    public string planning_schema_version = RdwPlanningProtocolVersions.RouteSelectionSchema;
    /// <summary>实验会话 ID。</summary>
    public string session_id;
    /// <summary>路线选择请求 ID。</summary>
    public string request_id;
    /// <summary>计划尝试 ID。</summary>
    public string plan_id;
    /// <summary>Planning Snapshot ID。</summary>
    public string snapshot_id;
    /// <summary>规划代际。</summary>
    public long generation;
    /// <summary>任务状态版本。</summary>
    public int task_state_version;
    /// <summary>Calibration ID。</summary>
    public string calibration_id;
    /// <summary>Physical Space ID。</summary>
    public string physical_space_id;
    /// <summary>Risk Snapshot ID。</summary>
    public string risk_snapshot_id;
    /// <summary>风险修订号。</summary>
    public int risk_revision;
    /// <summary>当前节点 ID。</summary>
    public string current_node_id;
    /// <summary>最终目标 ID。</summary>
    public string final_target_id;
    /// <summary>Estimator ID。</summary>
    public string estimator_id;
    /// <summary>Estimator 版本。</summary>
    public string estimator_version;
    /// <summary>可供模型选择的 Top-K 路线。</summary>
    public RdwRouteCandidateDto[] route_candidates = Array.Empty<RdwRouteCandidateDto>();
    /// <summary>三个正式条件共享的自然语言任务契约。</summary>
    public NaturalLanguageTaskContract task;
    /// <summary>当前任务进度快照。</summary>
    public NaturalLanguageTaskState task_state;
    /// <summary>当前路线相关的静态语义知识节点；仅 OURS Route Selection 使用。</summary>
    public RdwSemanticContextNode[] local_semantic_nodes = Array.Empty<RdwSemanticContextNode>();
    /// <summary>当前路线相关的语义关系边；不作为物理可达性门禁。</summary>
    public SemanticEdgeDefinition[] local_semantic_edges = Array.Empty<SemanticEdgeDefinition>();
    /// <summary>语义主题定义，供模型理解主题 ID。</summary>
    public RdwSemanticThemeDto[] semantic_themes = Array.Empty<RdwSemanticThemeDto>();

    /// <summary>从 Planning result 与 Call identity 创建严格请求。</summary>
    public static RdwRouteSelectionRequest From(
        string sessionId,
        PlanningCallIdentity identity,
        RdwRoutePlanningResult result,
        NaturalLanguageTaskContract task,
        NaturalLanguageTaskState taskState,
        StaticSceneSemanticGraph semanticGraph = null)
    {
        if (identity == null || result?.Snapshot == null) return null;
        var routes = new RdwRouteCandidateDto[result.Routes.Count];
        for (var index = 0; index < routes.Length; index++)
        {
            routes[index] = RdwRouteCandidateDto.From(result.Routes[index], result.Snapshot.CurrentNodeId);
        }
        var snapshot = result.Snapshot;
        RdwSemanticContextBuilder.Build(
            semanticGraph,
            result,
            out var semanticNodes,
            out var semanticEdges,
            out var semanticThemes);
        return new RdwRouteSelectionRequest
        {
            session_id = sessionId ?? string.Empty,
            request_id = identity.RequestId,
            plan_id = identity.PlanId,
            snapshot_id = identity.SnapshotId,
            generation = identity.Generation,
            task_state_version = snapshot.TaskStateVersion,
            calibration_id = snapshot.CalibrationId,
            physical_space_id = snapshot.PhysicalSpaceId,
            risk_snapshot_id = snapshot.RiskSnapshotId,
            risk_revision = snapshot.RiskRevision,
            current_node_id = snapshot.CurrentNodeId,
            final_target_id = snapshot.FinalTargetId,
            estimator_id = snapshot.Capability.EstimatorId,
            estimator_version = snapshot.Capability.EstimatorVersion,
            route_candidates = routes,
            task = task,
            task_state = taskState,
            local_semantic_nodes = semanticNodes,
            local_semantic_edges = semanticEdges,
            semantic_themes = semanticThemes
        };
    }

    /// <summary>检查 route_id 是否属于冻结候选集合。</summary>
    public bool ContainsRoute(string routeId)
    {
        foreach (var route in route_candidates ?? Array.Empty<RdwRouteCandidateDto>())
        {
            if (route != null && string.Equals(route.route_id, routeId, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// 后端返回的受约束路线选择响应。
/// </summary>
[Serializable]
public sealed class RdwRouteSelectionResponse
{
    /// <summary>规划协议版本。</summary>
    public string planning_schema_version;
    /// <summary>实验会话 ID。</summary>
    public string session_id;
    /// <summary>路线选择请求 ID。</summary>
    public string request_id;
    /// <summary>计划尝试 ID。</summary>
    public string plan_id;
    /// <summary>Planning Snapshot ID。</summary>
    public string snapshot_id;
    /// <summary>规划代际。</summary>
    public long generation;
    /// <summary>模型从候选集合中选择的 route_id。</summary>
    public string route_id;
    /// <summary>路线选择后返回的讲解文本。</summary>
    public string narration;
    /// <summary>后端 membership verifier 结果。</summary>
    public VerifierResult verifier;
}

/// <summary>
/// Unity 对 Route Selection 响应执行精确字段、身份与 membership 校验。
/// </summary>
public static class RdwRouteSelectionProtocol
{
    private static readonly HashSet<string> ResponseFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "planning_schema_version",
        "session_id",
        "request_id",
        "plan_id",
        "snapshot_id",
        "generation",
        "route_id",
        "narration",
        "verifier"
    };

    private static readonly HashSet<string> VerifierFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "approved",
        "reason"
    };

    /// <summary>解析并 Fail-Closed 校验路线响应。</summary>
    public static bool TryParseResponse(
        string json,
        RdwRouteSelectionRequest request,
        out RdwRouteSelectionResponse response,
        out string failureReason)
    {
        response = null;
        failureReason = string.Empty;
        if (request == null || string.IsNullOrWhiteSpace(json))
        {
            failureReason = "route_selection_response_or_request_missing";
            return false;
        }
        try
        {
            var root = JObject.Parse(json);
            if (!HasExactFields(root, ResponseFields, "response", out failureReason) ||
                !(root["verifier"] is JObject verifier) ||
                !HasExactFields(verifier, VerifierFields, "verifier", out failureReason) ||
                !HasExpectedScalarTypes(root, verifier, out failureReason))
            {
                return false;
            }
            response = root.ToObject<RdwRouteSelectionResponse>();
        }
        catch (JsonException exception)
        {
            failureReason = $"invalid_route_selection_json:{exception.Message}";
            return false;
        }

        if (response == null || response.verifier == null || !response.verifier.approved)
        {
            failureReason = response?.verifier?.reason ?? "route_selection_verifier_missing";
            return false;
        }
        if (string.IsNullOrWhiteSpace(response.narration) ||
            string.IsNullOrWhiteSpace(response.verifier.reason))
        {
            failureReason = "route_selection_text_field_invalid";
            return false;
        }
        if (!string.Equals(response.planning_schema_version, RdwPlanningProtocolVersions.RouteSelectionSchema, StringComparison.Ordinal) ||
            !string.Equals(response.session_id, request.session_id, StringComparison.Ordinal) ||
            !string.Equals(response.request_id, request.request_id, StringComparison.Ordinal) ||
            !string.Equals(response.plan_id, request.plan_id, StringComparison.Ordinal) ||
            !string.Equals(response.snapshot_id, request.snapshot_id, StringComparison.Ordinal) ||
            response.generation != request.generation)
        {
            failureReason = "route_selection_identity_mismatch";
            return false;
        }
        if (!request.ContainsRoute(response.route_id))
        {
            failureReason = "route_id_outside_candidates";
            return false;
        }
        return true;
    }

    private static bool HasExpectedScalarTypes(
        JObject response,
        JObject verifier,
        out string reason)
    {
        var stringFields = new[]
        {
            "planning_schema_version",
            "session_id",
            "request_id",
            "plan_id",
            "snapshot_id",
            "route_id",
            "narration"
        };
        foreach (var field in stringFields)
        {
            if (response[field]?.Type != JTokenType.String)
            {
                reason = $"invalid_response_field_type:{field}";
                return false;
            }
        }
        if (response["generation"]?.Type != JTokenType.Integer)
        {
            reason = "invalid_response_field_type:generation";
            return false;
        }
        if (verifier["approved"]?.Type != JTokenType.Boolean)
        {
            reason = "invalid_verifier_field_type:approved";
            return false;
        }
        if (verifier["reason"]?.Type != JTokenType.String)
        {
            reason = "invalid_verifier_field_type:reason";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>使用 JSON.NET 序列化路线请求。</summary>
    public static string SerializeRequest(RdwRouteSelectionRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return JsonConvert.SerializeObject(request, Formatting.None);
    }

    private static bool HasExactFields(
        JObject value,
        HashSet<string> expected,
        string name,
        out string reason)
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
