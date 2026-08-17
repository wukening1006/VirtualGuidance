using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>场景 Anchor 与静态语义图的绑定校验结果。</summary>
public sealed class SceneSemanticBindingValidationResult
{
    private readonly IReadOnlyList<string> errors;

    /// <summary>是否通过一一绑定校验。</summary>
    public bool IsValid => errors.Count == 0;
    /// <summary>稳定排序的错误码。</summary>
    public IReadOnlyList<string> Errors => errors;
    /// <summary>面向日志的错误摘要。</summary>
    public string ErrorSummary => errors.Count == 0 ? string.Empty : string.Join(", ", errors);

    /// <summary>创建绑定校验结果。</summary>
    public SceneSemanticBindingValidationResult(IEnumerable<string> values)
    {
        var copy = new List<string>(values ?? Array.Empty<string>());
        copy.Sort(StringComparer.Ordinal);
        errors = Array.AsReadOnly(copy.ToArray());
    }
}

/// <summary>
/// 检查 JSON 展品节点与 MainScene Anchor 的一一对应及几何快照。
/// 校验器只读 Transform，不会反向修改场景。
/// </summary>
public static class SceneSemanticBindingValidator
{
    /// <summary>默认 JSON/场景位置容差，单位：米。</summary>
    public const float DefaultPositionToleranceMeters = 0.05f;

    /// <summary>扫描当前场景并校验所有 ExhibitMetadata Anchor。</summary>
    public static SceneSemanticBindingValidationResult ValidateLoadedScene(
        StaticSceneSemanticGraph graph,
        float positionToleranceMeters = DefaultPositionToleranceMeters,
        float viewpointToleranceMeters = DefaultPositionToleranceMeters,
        float gazeTargetToleranceMeters = DefaultPositionToleranceMeters)
    {
        var anchors = UnityEngine.Object.FindObjectsOfType<ExhibitMetadata>();
        return Validate(graph, anchors, positionToleranceMeters, viewpointToleranceMeters, gazeTargetToleranceMeters);
    }

    /// <summary>校验指定 Anchor 集合，便于 EditMode 测试和启动门禁复用。</summary>
    public static SceneSemanticBindingValidationResult Validate(
        StaticSceneSemanticGraph graph,
        IReadOnlyList<ExhibitMetadata> anchors,
        float positionToleranceMeters = DefaultPositionToleranceMeters,
        float viewpointToleranceMeters = DefaultPositionToleranceMeters,
        float gazeTargetToleranceMeters = DefaultPositionToleranceMeters)
    {
        var errors = new List<string>();
        if (graph == null)
        {
            errors.Add("scene_binding_graph_missing");
            return new SceneSemanticBindingValidationResult(errors);
        }

        var byId = new Dictionary<string, ExhibitMetadata>(StringComparer.Ordinal);
        foreach (var anchor in anchors ?? Array.Empty<ExhibitMetadata>())
        {
            if (anchor == null)
            {
                errors.Add("scene_binding_null_anchor");
                continue;
            }

            if (string.IsNullOrWhiteSpace(anchor.Id))
            {
                errors.Add("scene_binding_empty_anchor_id:" + anchor.name);
                continue;
            }

            if (!byId.TryAdd(anchor.Id, anchor))
            {
                errors.Add("scene_binding_duplicate_anchor_id:" + anchor.Id);
            }
        }

        foreach (var exhibit in graph.Exhibits)
        {
            if (!byId.TryGetValue(exhibit.id, out var anchor))
            {
                errors.Add("scene_binding_missing_anchor:" + exhibit.id);
                continue;
            }

            if (anchor.ViewPoint == null) errors.Add("scene_binding_missing_viewpoint:" + exhibit.id);
            if (anchor.GazeTarget == null) errors.Add("scene_binding_missing_gaze_target:" + exhibit.id);
            if (!string.Equals(exhibit.scene_binding.scene_path, anchor.HierarchyPath, StringComparison.Ordinal))
            {
                errors.Add("scene_binding_path_mismatch:" + exhibit.id);
            }

            CheckPosition(anchor.transform.position, exhibit.scene_binding.position, positionToleranceMeters,
                "scene_binding_position_mismatch:" + exhibit.id, errors);
            if (anchor.ViewPoint != null)
            {
                CheckPosition(anchor.ViewPoint.position, exhibit.scene_binding.viewpoint, viewpointToleranceMeters,
                    "scene_binding_viewpoint_mismatch:" + exhibit.id, errors);
            }
            if (anchor.GazeTarget != null)
            {
                CheckPosition(anchor.GazeTarget.position, exhibit.scene_binding.gaze_target, gazeTargetToleranceMeters,
                    "scene_binding_gaze_target_mismatch:" + exhibit.id, errors);
            }
        }

        foreach (var pair in byId)
        {
            if (!graph.TryGetExhibit(pair.Key, out _))
            {
                errors.Add("scene_binding_unknown_anchor:" + pair.Key);
            }
        }

        return new SceneSemanticBindingValidationResult(errors);
    }

    private static void CheckPosition(Vector3 actual, float[] expected, float tolerance, string error, List<string> errors)
    {
        if (expected == null || expected.Length != 3)
        {
            errors.Add(error);
            return;
        }

        var expectedPosition = new Vector3(expected[0], expected[1], expected[2]);
        if (Vector3.Distance(actual, expectedPosition) > Mathf.Max(0f, tolerance)) errors.Add(error);
    }
}
