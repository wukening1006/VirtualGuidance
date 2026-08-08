using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 更新 scene_semantics.json 配置文件，使其与场景中的展品配置保持同步。
/// </summary>
public class SceneSemanticsUpdater : EditorWindow
{
    private string configPath = "Assets/Configs/scene_semantics.json";
    private GameObject exhibitVisualsRoot;

    [MenuItem("VirtualGuidance/Update Scene Semantics JSON")]
    public static void ShowWindow()
    {
        var window = GetWindow<SceneSemanticsUpdater>("Scene Semantics Updater");
        window.Show();
    }

    private void OnGUI()
    {
        GUILayout.Label("Scene Semantics JSON Updater", EditorStyles.boldLabel);
        GUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "此工具将根据场景中的展品配置更新 scene_semantics.json 为 v2 格式。",
            MessageType.Info);

        GUILayout.Space(10);

        configPath = EditorGUILayout.TextField("Config Path", configPath);

        GUILayout.Space(10);

        exhibitVisualsRoot = (GameObject)EditorGUILayout.ObjectField(
            "ExhibitVisuals Root",
            exhibitVisualsRoot,
            typeof(GameObject),
            true);

        GUILayout.Space(10);

        if (GUILayout.Button("Auto Find ExhibitVisuals Root", GUILayout.Height(30)))
        {
            FindExhibitVisualsRoot();
        }

        GUILayout.Space(10);

        GUI.enabled = exhibitVisualsRoot != null && File.Exists(configPath);
        if (GUILayout.Button("Update scene_semantics.json", GUILayout.Height(40)))
        {
            UpdateSceneSemanticsJson();
        }
        GUI.enabled = true;

        GUILayout.Space(20);

        if (GUILayout.Button("Generate New scene_semantics.json (v2)", GUILayout.Height(40)))
        {
            GenerateNewSceneSemanticsJson();
        }
    }

    private void FindExhibitVisualsRoot()
    {
        GameObject rootObj = GameObject.Find("VirtualGuidance_B_Root");
        if (rootObj == null)
        {
            EditorUtility.DisplayDialog("Error", "找不到 VirtualGuidance_B_Root 节点", "OK");
            return;
        }

        Transform exhibitVisuals = rootObj.transform.Find("ExhibitVisuals");
        if (exhibitVisuals == null)
        {
            EditorUtility.DisplayDialog("Error", "找不到 ExhibitVisuals 节点", "OK");
            return;
        }

        exhibitVisualsRoot = exhibitVisuals.gameObject;
        Debug.Log($"已找到 ExhibitVisuals: {exhibitVisualsRoot.name}");
    }

    private void UpdateSceneSemanticsJson()
    {
        if (!EditorUtility.DisplayDialog(
            "确认更新",
            $"此操作将更新配置文件:\n{configPath}\n\n是否继续？",
            "确认",
            "取消"))
        {
            return;
        }

        try
        {
            string jsonContent = GenerateSceneSemanticsJsonContent();
            File.WriteAllText(configPath, jsonContent, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"已更新配置文件:\n{configPath}", "OK");
            Debug.Log($"[SceneSemantics] 已更新配置文件: {configPath}");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("错误", $"更新失败:\n{e.Message}", "OK");
            Debug.LogError($"更新配置文件失败: {e.Message}\n{e.StackTrace}");
        }
    }

    private void GenerateNewSceneSemanticsJson()
    {
        if (exhibitVisualsRoot == null)
        {
            FindExhibitVisualsRoot();
            if (exhibitVisualsRoot == null)
            {
                EditorUtility.DisplayDialog("Error", "请先设置 ExhibitVisuals Root", "OK");
                return;
            }
        }

        string savePath = EditorUtility.SaveFilePanel(
            "保存 scene_semantics.json",
            "Assets/Configs",
            "scene_semantics_v2.json",
            "json");

        if (string.IsNullOrEmpty(savePath))
        {
            return;
        }

        try
        {
            string jsonContent = GenerateSceneSemanticsJsonContent();
            File.WriteAllText(savePath, jsonContent, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"已生成配置文件:\n{savePath}", "OK");
            Debug.Log($"[SceneSemantics] 已生成配置文件: {savePath}");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("错误", $"生成失败:\n{e.Message}", "OK");
            Debug.LogError($"生成配置文件失败: {e.Message}\n{e.StackTrace}");
        }
    }

    private string GenerateSceneSemanticsJsonContent()
    {
        if (exhibitVisualsRoot == null)
        {
            throw new System.Exception("ExhibitVisuals Root 未设置");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"schema_version\": 2,");
        sb.AppendLine("  \"scene_id\": \"dual_world_gallery\",");
        sb.AppendLine("  \"scene_name\": \"Dual World Gallery\",");
        sb.AppendLine("  \"coordinate_system\": \"Unity X-Z ground plane, Y-up\",");
        sb.AppendLine("  \"layout\": {");
        sb.AppendLine("    \"units\": \"meters\",");
        sb.AppendLine("    \"floor_origin\": [0, 0, 0],");
        sb.AppendLine("    \"floor_center\": [0, 0, 7],");
        sb.AppendLine("    \"floor_size_m\": {");
        sb.AppendLine("      \"width\": 12,");
        sb.AppendLine("      \"length\": 14");
        sb.AppendLine("    },");
        sb.AppendLine("    \"floor_bounds\": {");
        sb.AppendLine("      \"x_min\": -6,");
        sb.AppendLine("      \"x_max\": 6,");
        sb.AppendLine("      \"z_min\": 0,");
        sb.AppendLine("      \"z_max\": 14");
        sb.AppendLine("    },");
        sb.AppendLine("    \"entrance_side\": \"z_min\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"exhibits\": [");

        string[] exhibitIds = { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" };
        for (int i = 0; i < exhibitIds.Length; i++)
        {
            string exhibitId = exhibitIds[i];
            Transform exhibitTransform = exhibitVisualsRoot.transform.Find($"{exhibitId}_Visual");

            if (exhibitTransform == null)
            {
                Debug.LogWarning($"找不到节点: {exhibitId}_Visual，跳过");
                continue;
            }

            ExhibitMetadata metadata = exhibitTransform.GetComponent<ExhibitMetadata>();
            if (metadata == null)
            {
                Debug.LogWarning($"{exhibitId}_Visual 缺少 ExhibitMetadata 组件，跳过");
                continue;
            }

            sb.AppendLine("    {");
            sb.AppendLine($"      \"id\": \"{metadata.Id}\",");
            sb.AppendLine($"      \"name\": \"{EscapeJson(metadata.ExhibitName)}\",");
            sb.AppendLine($"      \"scene_path\": \"VirtualGuidance_B_Root/ExhibitVisuals/{exhibitId}_Visual\",");
            sb.AppendLine($"      \"semantic_role\": \"{metadata.SemanticRole}\",");

            Vector3 pos = exhibitTransform.position;
            sb.AppendLine($"      \"position\": [{pos.x:F2}, {pos.y:F2}, {pos.z:F2}],");

            float yaw = exhibitTransform.eulerAngles.y;
            sb.AppendLine($"      \"rotation_y_degrees\": {yaw:F0},");

            // 获取展柜尺寸（从 VisualContent 的 Bounds）
            Transform visualContent = exhibitTransform.Find("VisualContent");
            if (visualContent != null)
            {
                Bounds bounds = CalculateBounds(visualContent.gameObject);
                sb.AppendLine("      \"cabinet\": {");
                sb.AppendLine($"        \"width_m\": {bounds.size.x:F2},");
                sb.AppendLine($"        \"depth_m\": {bounds.size.z:F2},");
                sb.AppendLine($"        \"height_m\": {bounds.size.y:F2}");
                sb.AppendLine("      },");
            }
            else
            {
                sb.AppendLine("      \"cabinet\": {");
                sb.AppendLine("        \"width_m\": 1.8,");
                sb.AppendLine("        \"depth_m\": 1.0,");
                sb.AppendLine("        \"height_m\": 1.5");
                sb.AppendLine("      },");
            }

            if (metadata.ViewPoint != null)
            {
                Vector3 vp = metadata.ViewPoint.position;
                sb.AppendLine($"      \"viewpoint\": [{vp.x:F2}, {vp.y:F2}, {vp.z:F2}],");
            }
            else
            {
                sb.AppendLine($"      \"viewpoint\": [0, 0, 0],");
            }

            if (metadata.GazeTarget != null)
            {
                Vector3 gt = metadata.GazeTarget.position;
                sb.AppendLine($"      \"gaze_target\": [{gt.x:F2}, {gt.y:F2}, {gt.z:F2}],");
            }
            else
            {
                sb.AppendLine($"      \"gaze_target\": [0, 0, 0],");
            }

            // Tags
            sb.Append("      \"tags\": [");
            if (metadata.Tags != null && metadata.Tags.Length > 0)
            {
                for (int t = 0; t < metadata.Tags.Length; t++)
                {
                    sb.Append($"\"{metadata.Tags[t]}\"");
                    if (t < metadata.Tags.Length - 1) sb.Append(", ");
                }
            }
            sb.AppendLine("],");

            // Related Exhibits
            sb.Append("      \"related_exhibits\": [");
            if (metadata.RelatedExhibits != null && metadata.RelatedExhibits.Length > 0)
            {
                for (int r = 0; r < metadata.RelatedExhibits.Length; r++)
                {
                    sb.Append($"\"{metadata.RelatedExhibits[r]}\"");
                    if (r < metadata.RelatedExhibits.Length - 1) sb.Append(", ");
                }
            }
            sb.AppendLine("],");

            sb.AppendLine($"      \"description\": \"{EscapeJson(metadata.Description)}\"");

            if (i < exhibitIds.Length - 1)
            {
                sb.AppendLine("    },");
            }
            else
            {
                sb.AppendLine("    }");
            }
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return new Bounds(obj.transform.position, Vector3.one);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private string EscapeJson(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\n", "\\n")
                   .Replace("\r", "\\r")
                   .Replace("\t", "\\t");
    }
}
