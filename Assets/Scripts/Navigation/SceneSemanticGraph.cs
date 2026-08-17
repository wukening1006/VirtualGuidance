using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// scene_semantics.json 的严格 v3 文档 DTO。
/// </summary>
[Serializable]
public sealed class SceneSemanticsDocument
{
    /// <summary>语义契约版本，当前必须为 3。</summary>
    [JsonProperty("schema_version", Required = Required.Always)] public int schema_version;
    /// <summary>场景稳定 ID。</summary>
    [JsonProperty("scene_id", Required = Required.Always)] public string scene_id;
    /// <summary>场景显示名称。</summary>
    [JsonProperty("scene_name", Required = Required.Always)] public string scene_name;
    /// <summary>位置向量采用的坐标系说明。</summary>
    [JsonProperty("coordinate_system", Required = Required.Always)] public string coordinate_system;
    /// <summary>场景布局校验快照。</summary>
    [JsonProperty("layout", Required = Required.Always)] public SceneLayoutDefinition layout;
    /// <summary>知识主题节点。</summary>
    [JsonProperty("themes", Required = Required.Always)] public SceneThemeDefinition[] themes;
    /// <summary>展品知识节点。</summary>
    [JsonProperty("exhibits", Required = Required.Always)] public SceneExhibitDefinition[] exhibits;
    /// <summary>内容语义关系原始边。</summary>
    [JsonProperty("semantic_edges", Required = Required.Always)] public SemanticEdgeDefinition[] semantic_edges;
    /// <summary>虚拟空间拓扑原始边。</summary>
    [JsonProperty("spatial_edges", Required = Required.Always)] public SpatialEdgeDefinition[] spatial_edges;
}

/// <summary>场景布局快照，仅用于一致性校验和上下文。</summary>
[Serializable]
public sealed class SceneLayoutDefinition
{
    /// <summary>布局数值单位。</summary>
    [JsonProperty("units", Required = Required.Always)] public string units;
    /// <summary>地面原点坐标。</summary>
    [JsonProperty("floor_origin", Required = Required.Always)] public float[] floor_origin;
    /// <summary>地面中心坐标。</summary>
    [JsonProperty("floor_center", Required = Required.Always)] public float[] floor_center;
    /// <summary>地面尺寸，单位：米。</summary>
    [JsonProperty("floor_size_m", Required = Required.Always)] public SceneSizeDefinition floor_size_m;
    /// <summary>地面 XZ 边界。</summary>
    [JsonProperty("floor_bounds", Required = Required.Always)] public SceneBoundsDefinition floor_bounds;
    /// <summary>入口所在方向。</summary>
    [JsonProperty("entrance_side", Required = Required.Always)] public string entrance_side;
}

/// <summary>场景平面尺寸。</summary>
[Serializable]
public sealed class SceneSizeDefinition
{
    /// <summary>地面宽度，单位：米。</summary>
    [JsonProperty("width", Required = Required.Always)] public float width;
    /// <summary>地面长度，单位：米。</summary>
    [JsonProperty("length", Required = Required.Always)] public float length;
}

/// <summary>场景平面边界。</summary>
[Serializable]
public sealed class SceneBoundsDefinition
{
    /// <summary>X 轴最小坐标，单位：米。</summary>
    [JsonProperty("x_min", Required = Required.Always)] public float x_min;
    /// <summary>X 轴最大坐标，单位：米。</summary>
    [JsonProperty("x_max", Required = Required.Always)] public float x_max;
    /// <summary>Z 轴最小坐标，单位：米。</summary>
    [JsonProperty("z_min", Required = Required.Always)] public float z_min;
    /// <summary>Z 轴最大坐标，单位：米。</summary>
    [JsonProperty("z_max", Required = Required.Always)] public float z_max;
}

/// <summary>场景知识主题节点。</summary>
[Serializable]
public sealed class SceneThemeDefinition
{
    /// <summary>主题稳定 ID。</summary>
    [JsonProperty("id", Required = Required.Always)] public string id;
    /// <summary>主题显示名称。</summary>
    [JsonProperty("name", Required = Required.Always)] public string name;
    /// <summary>主题知识描述。</summary>
    [JsonProperty("description", Required = Required.Always)] public string description;
}

/// <summary>展品静态知识节点。</summary>
[Serializable]
public sealed class SceneExhibitDefinition
{
    /// <summary>展品稳定 ID。</summary>
    [JsonProperty("id", Required = Required.Always)] public string id;
    /// <summary>展品显示名称。</summary>
    [JsonProperty("name", Required = Required.Always)] public string name;
    /// <summary>展品别名。</summary>
    [JsonProperty("aliases", Required = Required.Always)] public string[] aliases;
    /// <summary>只读场景绑定快照。</summary>
    [JsonProperty("scene_binding", Required = Required.Always)] public SceneBindingDefinition scene_binding;
    /// <summary>展品静态知识。</summary>
    [JsonProperty("knowledge", Required = Required.Always)] public SceneExhibitKnowledge knowledge;
}

/// <summary>展品在场景中的静态快照，不负责驱动 Transform。</summary>
[Serializable]
public sealed class SceneBindingDefinition
{
    /// <summary>MainScene 中 Anchor 的完整层级路径。</summary>
    [JsonProperty("scene_path", Required = Required.Always)] public string scene_path;
    /// <summary>Anchor 世界坐标，单位：米。</summary>
    [JsonProperty("position", Required = Required.Always)] public float[] position;
    /// <summary>ViewPoint 世界坐标，单位：米。</summary>
    [JsonProperty("viewpoint", Required = Required.Always)] public float[] viewpoint;
    /// <summary>GazeTarget 世界坐标，单位：米。</summary>
    [JsonProperty("gaze_target", Required = Required.Always)] public float[] gaze_target;
}

/// <summary>展品知识和解释边界。</summary>
[Serializable]
public sealed class SceneExhibitKnowledge
{
    /// <summary>知识实体类型。</summary>
    [JsonProperty("entity_type", Required = Required.Always)] public string entity_type;
    /// <summary>展品知识分类。</summary>
    [JsonProperty("category", Required = Required.Always)] public string category;
    /// <summary>材质集合；未知时允许为 null。</summary>
    [JsonProperty("material", Required = Required.AllowNull)] public string[] material;
    /// <summary>文化归属。</summary>
    [JsonProperty("culture", Required = Required.Always)] public string culture;
    /// <summary>年代信息。</summary>
    [JsonProperty("chronology", Required = Required.Always)] public string chronology;
    /// <summary>考古语境。</summary>
    [JsonProperty("archaeological_context", Required = Required.Always)] public string archaeological_context;
    /// <summary>展品知识描述。</summary>
    [JsonProperty("description", Required = Required.Always)] public string description;
    /// <summary>可观察视觉特征。</summary>
    [JsonProperty("visual_features", Required = Required.Always)] public string[] visual_features;
    /// <summary>文化意义陈述。</summary>
    [JsonProperty("cultural_significance", Required = Required.Always)] public string[] cultural_significance;
    /// <summary>关键知识概念。</summary>
    [JsonProperty("key_concepts", Required = Required.Always)] public string[] key_concepts;
    /// <summary>引用的主题 ID。</summary>
    [JsonProperty("themes", Required = Required.Always)] public string[] themes;
    /// <summary>知识可信度与解释边界说明。</summary>
    [JsonProperty("interpretation_notes", Required = Required.Always)] public InterpretationNote[] interpretation_notes;
}

/// <summary>知识陈述的可信度/解释边界标签。</summary>
[Serializable]
public sealed class InterpretationNote
{
    /// <summary>知识解释陈述。</summary>
    [JsonProperty("statement", Required = Required.Always)] public string statement;
    /// <summary>原始可信度或解释边界状态。</summary>
    [JsonProperty("status", Required = Required.Always)] public string status;
    /// <summary>可选补充说明。</summary>
    [JsonProperty("note")] public string note;
}

/// <summary>展品内容关系，evidence_status 不映射为导航权重。</summary>
[Serializable]
public sealed class SemanticEdgeDefinition
{
    /// <summary>语义边稳定 ID。</summary>
    [JsonProperty("id", Required = Required.Always)] public string id;
    /// <summary>源展品 ID。</summary>
    [JsonProperty("source", Required = Required.Always)] public string source;
    /// <summary>目标展品 ID。</summary>
    [JsonProperty("target", Required = Required.Always)] public string target;
    /// <summary>内容关系类型。</summary>
    [JsonProperty("relation_type", Required = Required.Always)] public string relation_type;
    /// <summary>是否为有向关系；nullable 用于识别缺失字段。</summary>
    [JsonProperty("directed", Required = Required.Always)] public bool? directed;
    /// <summary>关系知识描述。</summary>
    [JsonProperty("description", Required = Required.Always)] public string description;
    /// <summary>原始证据状态，不映射为导航权重。</summary>
    [JsonProperty("evidence_status", Required = Required.Always)] public string evidence_status;
}

/// <summary>虚拟展厅空间拓扑关系。</summary>
[Serializable]
public sealed class SpatialEdgeDefinition
{
    /// <summary>空间边稳定 ID。</summary>
    [JsonProperty("id", Required = Required.Always)] public string id;
    /// <summary>源展品 ID。</summary>
    [JsonProperty("source", Required = Required.Always)] public string source;
    /// <summary>目标展品 ID。</summary>
    [JsonProperty("target", Required = Required.Always)] public string target;
    /// <summary>虚拟空间拓扑关系类型。</summary>
    [JsonProperty("relation_type", Required = Required.Always)] public string relation_type;
    /// <summary>是否为双向拓扑；nullable 用于识别缺失字段。</summary>
    [JsonProperty("bidirectional", Required = Required.Always)] public bool? bidirectional;
    /// <summary>虚拟空间距离，单位：米。</summary>
    [JsonProperty("virtual_distance_m", Required = Required.Always)] public float virtual_distance_m;
    /// <summary>从源节点观察目标节点的相对方向。</summary>
    [JsonProperty("relative_direction_from_source", Required = Required.Always)] public string relative_direction_from_source;
}

/// <summary>语义配置校验失败异常。</summary>
public sealed class SceneSemanticValidationException : Exception
{
    /// <summary>创建带稳定错误码的配置异常。</summary>
    public SceneSemanticValidationException(string message) : base(message) { }
}

/// <summary>
/// 从 TextAsset 或 JSON 文本加载并严格校验场景语义配置。
/// Loader 只读配置，不创建、移动或修改任何场景对象。
/// </summary>
public sealed class SceneSemanticsLoader : MonoBehaviour
{
    [Header("场景语义配置")]
    [Tooltip("Build-safe 的 v3 场景语义 JSON TextAsset；缺失时语义就绪失败。")]
    [SerializeField] private TextAsset sceneSemanticsJson;

    /// <summary>当前绑定的 JSON TextAsset。</summary>
    public TextAsset SceneSemanticsJson => sceneSemanticsJson;

    /// <summary>读取绑定配置并构建唯一静态语义图。</summary>
    public bool TryLoad(out StaticSceneSemanticGraph graph, out string error)
    {
        graph = null;
        if (sceneSemanticsJson == null)
        {
            error = "semantic_config_missing_text_asset";
            return false;
        }

        return TryLoadJson(sceneSemanticsJson.text, out graph, out error);
    }

    /// <summary>严格解析 JSON；未知字段、断引用和图契约错误都会失败。</summary>
    public static bool TryLoadJson(string json, out StaticSceneSemanticGraph graph, out string error)
    {
        graph = null;
        error = string.Empty;
        try
        {
            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Error,
                FloatParseHandling = FloatParseHandling.Double,
                DateParseHandling = DateParseHandling.None,
                NullValueHandling = NullValueHandling.Include
            };
            var document = JsonConvert.DeserializeObject<SceneSemanticsDocument>(json ?? string.Empty, settings);
            ValidateDocument(document);
            graph = new StaticSceneSemanticGraph(document);
            return true;
        }
        catch (SceneSemanticValidationException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (JsonException exception)
        {
            error = "semantic_config_json_invalid:" + exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            error = "semantic_config_unexpected:" + exception.Message;
            return false;
        }
    }

    /// <summary>解析成功时返回图，否则抛出结构化配置异常。</summary>
    public static StaticSceneSemanticGraph LoadJson(string json)
    {
        if (!TryLoadJson(json, out var graph, out var error))
        {
            throw new SceneSemanticValidationException(error);
        }

        return graph;
    }

    private static void ValidateDocument(SceneSemanticsDocument document)
    {
        if (document == null) Fail("semantic_config_not_object");
        if (document.schema_version != 3) Fail("unsupported_schema_version:" + document.schema_version);
        Require(document.scene_id, "scene_id");
        Require(document.scene_name, "scene_name");
        Require(document.coordinate_system, "coordinate_system");
        if (document.layout == null) Fail("missing_layout");
        ValidateLayout(document.layout);
        ValidateThemes(document.themes);
        ValidateExhibits(document.exhibits);
        ValidateThemeReferences(document.themes, document.exhibits);
        ValidateSemanticEdges(document.semantic_edges, document.exhibits);
        ValidateSpatialEdges(document.spatial_edges, document.exhibits);

        if (string.Equals(document.scene_id, "sanxingdui_gallery", StringComparison.Ordinal))
        {
            RequireCount(document.themes, 7, "themes");
            RequireCount(document.exhibits, 8, "exhibits");
            RequireCount(document.semantic_edges, 14, "semantic_edges");
            RequireCount(document.spatial_edges, 14, "spatial_edges");
            RequireRelationCounts(document.semantic_edges, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["context_of"] = 3, ["same_category"] = 1, ["variant_of"] = 1,
                ["compare_with"] = 3, ["thematic_association"] = 3, ["synthesis_into"] = 3
            }, "semantic relation_type");
            RequireRelationCounts(document.spatial_edges, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["adjacent_central_axis"] = 3, ["adjacent_diagonal"] = 8,
                ["adjacent_branch"] = 2, ["same_row"] = 1
            }, "spatial relation_type");
            foreach (var edge in document.spatial_edges)
            {
                if (edge == null || edge.bidirectional != true)
                {
                    Fail("sanxingdui_spatial_edge_not_bidirectional");
                }
            }
        }
    }

    private static void ValidateLayout(SceneLayoutDefinition layout)
    {
        Require(layout.units, "layout.units");
        Require(layout.entrance_side, "layout.entrance_side");
        ValidateVector(layout.floor_origin, "layout.floor_origin");
        ValidateVector(layout.floor_center, "layout.floor_center");
        if (layout.floor_size_m == null) Fail("missing_layout.floor_size_m");
        if (layout.floor_bounds == null) Fail("missing_layout.floor_bounds");
        ValidateFinite(layout.floor_size_m.width, "layout.floor_size_m.width");
        ValidateFinite(layout.floor_size_m.length, "layout.floor_size_m.length");
        ValidateFinite(layout.floor_bounds.x_min, "layout.floor_bounds.x_min");
        ValidateFinite(layout.floor_bounds.x_max, "layout.floor_bounds.x_max");
        ValidateFinite(layout.floor_bounds.z_min, "layout.floor_bounds.z_min");
        ValidateFinite(layout.floor_bounds.z_max, "layout.floor_bounds.z_max");
    }

    private static void ValidateThemes(SceneThemeDefinition[] themes)
    {
        if (themes == null) Fail("missing_themes");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in themes)
        {
            if (theme == null) Fail("null_theme");
            Require(theme.id, "theme.id");
            Require(theme.name, "theme.name");
            Require(theme.description, "theme.description");
            if (!ids.Add(theme.id)) Fail("duplicate_theme_id:" + theme.id);
        }
    }

    private static void ValidateExhibits(SceneExhibitDefinition[] exhibits)
    {
        if (exhibits == null) Fail("missing_exhibits");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exhibit in exhibits)
        {
            if (exhibit == null) Fail("null_exhibit");
            Require(exhibit.id, "exhibit.id");
            Require(exhibit.name, "exhibit.name");
            if (!ids.Add(exhibit.id)) Fail("duplicate_exhibit_id:" + exhibit.id);
            if (exhibit.aliases == null) Fail("missing_exhibit_aliases:" + exhibit.id);
            ValidateStringArray(exhibit.aliases, "exhibit.aliases:" + exhibit.id, false);
            if (exhibit.scene_binding == null) Fail("missing_scene_binding:" + exhibit.id);
            Require(exhibit.scene_binding.scene_path, "scene_binding.scene_path:" + exhibit.id);
            ValidateVector(exhibit.scene_binding.position, "scene_binding.position:" + exhibit.id);
            ValidateVector(exhibit.scene_binding.viewpoint, "scene_binding.viewpoint:" + exhibit.id);
            ValidateVector(exhibit.scene_binding.gaze_target, "scene_binding.gaze_target:" + exhibit.id);
            if (exhibit.knowledge == null) Fail("missing_knowledge:" + exhibit.id);
            ValidateKnowledge(exhibit.knowledge, exhibit.id);
        }
    }

    private static void ValidateKnowledge(SceneExhibitKnowledge knowledge, string exhibitId)
    {
        Require(knowledge.entity_type, "knowledge.entity_type:" + exhibitId);
        Require(knowledge.category, "knowledge.category:" + exhibitId);
        Require(knowledge.culture, "knowledge.culture:" + exhibitId);
        Require(knowledge.chronology, "knowledge.chronology:" + exhibitId);
        Require(knowledge.archaeological_context, "knowledge.archaeological_context:" + exhibitId);
        Require(knowledge.description, "knowledge.description:" + exhibitId);
        if (knowledge.material != null) ValidateStringArray(knowledge.material, "knowledge.material:" + exhibitId, false);
        ValidateStringArray(knowledge.visual_features, "knowledge.visual_features:" + exhibitId, true);
        ValidateStringArray(knowledge.cultural_significance, "knowledge.cultural_significance:" + exhibitId, true);
        ValidateStringArray(knowledge.key_concepts, "knowledge.key_concepts:" + exhibitId, true);
        ValidateStringArray(knowledge.themes, "knowledge.themes:" + exhibitId, true);
        if (knowledge.interpretation_notes == null) Fail("missing_interpretation_notes:" + exhibitId);
        foreach (var note in knowledge.interpretation_notes)
        {
            if (note == null) Fail("null_interpretation_note:" + exhibitId);
            Require(note.statement, "interpretation_note.statement:" + exhibitId);
            Require(note.status, "interpretation_note.status:" + exhibitId);
        }
    }

    private static void ValidateSemanticEdges(SemanticEdgeDefinition[] edges, SceneExhibitDefinition[] exhibits)
    {
        if (edges == null) Fail("missing_semantic_edges");
        var exhibitIds = ExhibitIds(exhibits);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var logical = new HashSet<string>(StringComparer.Ordinal);
        var relationTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "context_of", "same_category", "variant_of", "compare_with", "thematic_association", "synthesis_into"
        };
        var evidenceStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "direct", "supported", "contextual", "interpretive"
        };
        foreach (var edge in edges)
        {
            if (edge == null) Fail("null_semantic_edge");
            Require(edge.id, "semantic_edge.id");
            if (!edgeIds.Add(edge.id)) Fail("duplicate_semantic_edge_id:" + edge.id);
            Require(edge.source, "semantic_edge.source:" + edge.id);
            Require(edge.target, "semantic_edge.target:" + edge.id);
            if (!exhibitIds.Contains(edge.source) || !exhibitIds.Contains(edge.target)) Fail("broken_semantic_edge:" + edge.id);
            if (string.Equals(edge.source, edge.target, StringComparison.Ordinal)) Fail("semantic_edge_self_loop:" + edge.id);
            Require(edge.relation_type, "semantic_edge.relation_type:" + edge.id);
            if (!relationTypes.Contains(edge.relation_type)) Fail("unknown_semantic_relation_type:" + edge.relation_type);
            if (!edge.directed.HasValue) Fail("missing_semantic_directed:" + edge.id);
            Require(edge.description, "semantic_edge.description:" + edge.id);
            Require(edge.evidence_status, "semantic_edge.evidence_status:" + edge.id);
            if (!evidenceStatuses.Contains(edge.evidence_status)) Fail("unknown_evidence_status:" + edge.evidence_status);
            var logicalKey = edge.directed.Value
                ? edge.source + "->" + edge.target + "|" + edge.relation_type
                : CanonicalPair(edge.source, edge.target) + "|" + edge.relation_type;
            if (!logical.Add(logicalKey)) Fail("duplicate_semantic_logical_edge:" + logicalKey);
        }
    }

    private static void ValidateThemeReferences(SceneThemeDefinition[] themes, SceneExhibitDefinition[] exhibits)
    {
        var themeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in themes) themeIds.Add(theme.id);
        foreach (var exhibit in exhibits)
        {
            foreach (var themeId in exhibit.knowledge.themes)
            {
                if (!themeIds.Contains(themeId)) Fail("unknown_theme_reference:" + themeId);
            }
        }
    }

    private static void ValidateSpatialEdges(SpatialEdgeDefinition[] edges, SceneExhibitDefinition[] exhibits)
    {
        if (edges == null) Fail("missing_spatial_edges");
        var exhibitIds = ExhibitIds(exhibits);
        var exhibitPositions = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var exhibit in exhibits) exhibitPositions[exhibit.id] = exhibit.scene_binding.position;
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var logical = new HashSet<string>(StringComparer.Ordinal);
        var relationTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "adjacent_central_axis", "adjacent_diagonal", "adjacent_branch", "same_row"
        };
        foreach (var edge in edges)
        {
            if (edge == null) Fail("null_spatial_edge");
            Require(edge.id, "spatial_edge.id");
            if (!edgeIds.Add(edge.id)) Fail("duplicate_spatial_edge_id:" + edge.id);
            Require(edge.source, "spatial_edge.source:" + edge.id);
            Require(edge.target, "spatial_edge.target:" + edge.id);
            if (!exhibitIds.Contains(edge.source) || !exhibitIds.Contains(edge.target)) Fail("broken_spatial_edge:" + edge.id);
            if (string.Equals(edge.source, edge.target, StringComparison.Ordinal)) Fail("spatial_edge_self_loop:" + edge.id);
            Require(edge.relation_type, "spatial_edge.relation_type:" + edge.id);
            if (!relationTypes.Contains(edge.relation_type)) Fail("unknown_spatial_relation_type:" + edge.relation_type);
            if (!edge.bidirectional.HasValue) Fail("missing_spatial_bidirectional:" + edge.id);
            if (edge.virtual_distance_m <= 0f || !IsFinite(edge.virtual_distance_m)) Fail("invalid_spatial_distance:" + edge.id);
            Require(edge.relative_direction_from_source, "spatial_edge.relative_direction_from_source:" + edge.id);
            var logicalKey = (edge.bidirectional.Value ? CanonicalPair(edge.source, edge.target) : edge.source + "->" + edge.target) + "|" + edge.relation_type;
            if (!logical.Add(logicalKey)) Fail("duplicate_spatial_logical_edge:" + logicalKey);
            var sourcePosition = exhibitPositions[edge.source];
            var targetPosition = exhibitPositions[edge.target];
            var dx = sourcePosition[0] - targetPosition[0];
            var dz = sourcePosition[2] - targetPosition[2];
            var computed = Mathf.Sqrt(dx * dx + dz * dz);
            if (Mathf.Abs(computed - edge.virtual_distance_m) > 0.05f)
            {
                Fail("spatial_distance_mismatch:" + edge.id);
            }
        }
    }

    private static HashSet<string> ExhibitIds(SceneExhibitDefinition[] exhibits)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exhibit in exhibits ?? Array.Empty<SceneExhibitDefinition>())
        {
            if (exhibit != null && !string.IsNullOrWhiteSpace(exhibit.id)) ids.Add(exhibit.id);
        }
        return ids;
    }

    private static void RequireRelationCounts<T>(IEnumerable<T> edges, Dictionary<string, int> expected, string label)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            var relation = edge is SemanticEdgeDefinition semantic ? semantic.relation_type : ((SpatialEdgeDefinition)(object)edge).relation_type;
            counts[relation] = counts.TryGetValue(relation, out var count) ? count + 1 : 1;
        }
        foreach (var pair in expected)
        {
            if (!counts.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
            {
                Fail("invalid_" + label.Replace(" ", "_") + "_count:" + pair.Key);
            }
        }
    }

    private static void ValidateVector(float[] value, string field)
    {
        if (value == null || value.Length != 3) Fail("invalid_vector_length:" + field);
        foreach (var item in value) ValidateFinite(item, field);
    }

    private static void ValidateStringArray(string[] value, string field, bool requireNonEmpty)
    {
        if (value == null || (requireNonEmpty && value.Length == 0)) Fail("invalid_string_array:" + field);
        foreach (var item in value) Require(item, field);
    }

    private static void RequireCount<T>(T[] values, int expected, string field)
    {
        if (values == null || values.Length != expected) Fail("invalid_count:" + field);
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) Fail("missing_or_empty:" + field);
    }

    private static void ValidateFinite(float value, string field)
    {
        if (!IsFinite(value)) Fail("non_finite:" + field);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string CanonicalPair(string source, string target)
    {
        return string.CompareOrdinal(source, target) <= 0 ? source + "<->" + target : target + "<->" + source;
    }

    private static void Fail(string message)
    {
        throw new SceneSemanticValidationException(message);
    }
}

/// <summary>
/// v3 静态场景知识图谱。原始边只保存一份，双向查询通过索引展开。
/// </summary>
public sealed class StaticSceneSemanticGraph
{
    private readonly SceneSemanticsDocument document;
    private readonly Dictionary<string, SceneThemeDefinition> themesById;
    private readonly Dictionary<string, SceneExhibitDefinition> exhibitsById;
    private readonly Dictionary<string, List<SemanticEdgeDefinition>> semanticByNode;
    private readonly Dictionary<string, List<SpatialEdgeDefinition>> spatialByNode;

    /// <summary>创建已完成严格校验的静态图。</summary>
    public StaticSceneSemanticGraph(SceneSemanticsDocument source)
    {
        document = source ?? throw new ArgumentNullException(nameof(source));
        themesById = new Dictionary<string, SceneThemeDefinition>(StringComparer.Ordinal);
        exhibitsById = new Dictionary<string, SceneExhibitDefinition>(StringComparer.Ordinal);
        semanticByNode = new Dictionary<string, List<SemanticEdgeDefinition>>(StringComparer.Ordinal);
        spatialByNode = new Dictionary<string, List<SpatialEdgeDefinition>>(StringComparer.Ordinal);
        foreach (var theme in document.themes)
        {
            themesById[theme.id] = theme;
        }
        foreach (var exhibit in document.exhibits)
        {
            exhibitsById[exhibit.id] = exhibit;
        }
        foreach (var edge in document.semantic_edges)
        {
            AddSemanticIndex(edge.source, edge);
            if (!edge.directed.Value) AddSemanticIndex(edge.target, edge);
        }
        foreach (var edge in document.spatial_edges)
        {
            AddSpatialIndex(edge.source, edge);
            if (edge.bidirectional.Value) AddSpatialIndex(edge.target, edge);
        }
    }

    /// <summary>场景 ID。</summary>
    public string SceneId => document.scene_id;
    /// <summary>schema 版本。</summary>
    public int SchemaVersion => document.schema_version;
    /// <summary>场景主题集合。</summary>
    public IReadOnlyList<SceneThemeDefinition> Themes => Array.AsReadOnly(document.themes);
    /// <summary>场景展品集合。</summary>
    public IReadOnlyList<SceneExhibitDefinition> Exhibits => Array.AsReadOnly(document.exhibits);
    /// <summary>原始语义边集合，不含索引复制。</summary>
    public IReadOnlyList<SemanticEdgeDefinition> SemanticEdges => Array.AsReadOnly(document.semantic_edges);
    /// <summary>原始空间边集合，不含索引复制。</summary>
    public IReadOnlyList<SpatialEdgeDefinition> SpatialEdges => Array.AsReadOnly(document.spatial_edges);

    /// <summary>按 ID 获取展品。</summary>
    public SceneExhibitDefinition GetExhibit(string id)
    {
        return TryGetExhibit(id, out var exhibit) ? exhibit : null;
    }

    /// <summary>按 ID 尝试获取展品。</summary>
    public bool TryGetExhibit(string id, out SceneExhibitDefinition exhibit)
    {
        return exhibitsById.TryGetValue(id ?? string.Empty, out exhibit);
    }

    /// <summary>返回全部展品。</summary>
    public IReadOnlyList<SceneExhibitDefinition> GetAllExhibits() => Exhibits;

    /// <summary>按 ID 获取主题。</summary>
    public SceneThemeDefinition GetTheme(string id)
    {
        return themesById.TryGetValue(id ?? string.Empty, out var theme) ? theme : null;
    }

    /// <summary>返回指定节点的语义出边；无向边从两侧均可查询。</summary>
    public IReadOnlyList<SemanticEdgeDefinition> GetOutgoingSemanticEdges(string id)
    {
        return semanticByNode.TryGetValue(id ?? string.Empty, out var edges)
            ? Array.AsReadOnly(edges.ToArray())
            : Array.Empty<SemanticEdgeDefinition>();
    }

    /// <summary>返回指定节点可见的语义邻居 ID，不自动推导多跳关系。</summary>
    public IReadOnlyList<string> GetSemanticNeighbors(string id)
    {
        var result = new List<string>();
        foreach (var edge in GetOutgoingSemanticEdges(id))
        {
            var neighbor = string.Equals(edge.source, id, StringComparison.Ordinal) ? edge.target : edge.source;
            if (!result.Contains(neighbor)) result.Add(neighbor);
        }
        result.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>返回指定节点的空间边；双向边从两侧均可查询。</summary>
    public IReadOnlyList<SpatialEdgeDefinition> GetSpatialEdges(string id)
    {
        return spatialByNode.TryGetValue(id ?? string.Empty, out var edges)
            ? Array.AsReadOnly(edges.ToArray())
            : Array.Empty<SpatialEdgeDefinition>();
    }

    /// <summary>返回指定节点的空间邻居 ID。</summary>
    public IReadOnlyList<string> GetSpatialNeighbors(string id)
    {
        var result = new List<string>();
        foreach (var edge in GetSpatialEdges(id))
        {
            var neighbor = string.Equals(edge.source, id, StringComparison.Ordinal) ? edge.target : edge.source;
            if (!result.Contains(neighbor)) result.Add(neighbor);
        }
        result.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>检查两个节点是否存在静态空间连接。</summary>
    public bool HasSpatialConnection(string source, string target)
    {
        foreach (var edge in GetSpatialEdges(source))
        {
            if (string.Equals(edge.source, target, StringComparison.Ordinal) ||
                string.Equals(edge.target, target, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void AddSemanticIndex(string id, SemanticEdgeDefinition edge)
    {
        if (!semanticByNode.TryGetValue(id, out var list))
        {
            list = new List<SemanticEdgeDefinition>();
            semanticByNode[id] = list;
        }
        list.Add(edge);
    }

    private void AddSpatialIndex(string id, SpatialEdgeDefinition edge)
    {
        if (!spatialByNode.TryGetValue(id, out var list))
        {
            list = new List<SpatialEdgeDefinition>();
            spatialByNode[id] = list;
        }
        list.Add(edge);
    }
}
