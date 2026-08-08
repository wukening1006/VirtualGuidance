using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Editor 工具：为 VirtualGuidance_B_Root/ExhibitVisuals 下的 E1-E8 配置完整的运行时组件。
/// </summary>
public class ExhibitVisualsConfigurator : EditorWindow
{
    private GameObject exhibitVisualsRoot;
    private bool showDebugInfo = false;

    [MenuItem("VirtualGuidance/Configure Exhibit Visuals")]
    public static void ShowWindow()
    {
        var window = GetWindow<ExhibitVisualsConfigurator>("Exhibit Configurator");
        window.Show();
    }

    private void OnGUI()
    {
        GUILayout.Label("Exhibit Visuals Configurator", EditorStyles.boldLabel);
        GUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "此工具将为 VirtualGuidance_B_Root/ExhibitVisuals 下的 E1-E8 配置：\n" +
            "1. 设置展品位置和旋转\n" +
            "2. 创建 ViewPoint 和 GazeTarget 子对象\n" +
            "3. 添加并配置 ExhibitMetadata 组件",
            MessageType.Info);

        GUILayout.Space(10);

        exhibitVisualsRoot = (GameObject)EditorGUILayout.ObjectField(
            "ExhibitVisuals Root",
            exhibitVisualsRoot,
            typeof(GameObject),
            true);

        GUILayout.Space(10);

        showDebugInfo = EditorGUILayout.Toggle("Show Debug Info", showDebugInfo);

        GUILayout.Space(20);

        GUI.enabled = exhibitVisualsRoot != null;
        if (GUILayout.Button("Auto Find ExhibitVisuals Root", GUILayout.Height(30)))
        {
            FindExhibitVisualsRoot();
        }

        GUILayout.Space(10);

        if (GUILayout.Button("Configure All Exhibits (E1-E8)", GUILayout.Height(40)))
        {
            ConfigureAllExhibits();
        }

        GUI.enabled = true;

        GUILayout.Space(20);

        if (GUILayout.Button("Validate Configuration", GUILayout.Height(30)))
        {
            ValidateConfiguration();
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

    private void ConfigureAllExhibits()
    {
        if (exhibitVisualsRoot == null)
        {
            EditorUtility.DisplayDialog("Error", "请先设置 ExhibitVisuals Root", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog(
            "确认配置",
            "此操作将配置所有展品的位置、锚点和元数据。\n是否继续？",
            "确认",
            "取消"))
        {
            return;
        }

        Undo.SetCurrentGroupName("Configure All Exhibits");
        int undoGroup = Undo.GetCurrentGroup();

        try
        {
            int successCount = 0;
            var exhibits = GetExhibitConfigs();

            foreach (var config in exhibits)
            {
                if (ConfigureExhibit(config))
                {
                    successCount++;
                }
            }

            EditorUtility.DisplayDialog(
                "配置完成",
                $"成功配置 {successCount}/{exhibits.Count} 个展品",
                "OK");

            // 标记场景为已修改
            EditorUtility.SetDirty(exhibitVisualsRoot);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        }
        catch (System.Exception e)
        {
            Debug.LogError($"配置失败: {e.Message}\n{e.StackTrace}");
            EditorUtility.DisplayDialog("Error", $"配置失败: {e.Message}", "OK");
        }

        Undo.CollapseUndoOperations(undoGroup);
    }

    private bool ConfigureExhibit(ExhibitConfig config)
    {
        Transform exhibitTransform = exhibitVisualsRoot.transform.Find(config.nodeName);
        if (exhibitTransform == null)
        {
            Debug.LogWarning($"找不到节点: {config.nodeName}");
            return false;
        }

        GameObject exhibitObj = exhibitTransform.gameObject;
        Undo.RecordObject(exhibitObj.transform, "Configure Exhibit Transform");

        if (showDebugInfo)
        {
            Debug.Log($"正在配置: {config.id} - {config.exhibitName}");
        }

        // 1. 设置展品根节点位置（保持现有旋转不变）
        exhibitTransform.position = config.position;
        // 不修改旋转：exhibitTransform.rotation = Quaternion.Euler(0, config.yaw, 0);
        exhibitTransform.localScale = Vector3.one;

        // 2. 确保或创建 VisualContent
        Transform visualContent = exhibitTransform.Find("VisualContent");
        if (visualContent == null)
        {
            // 如果没有 VisualContent，将现有的视觉子对象放入一个新的 VisualContent
            GameObject visualContentObj = new GameObject("VisualContent");
            Undo.RegisterCreatedObjectUndo(visualContentObj, "Create VisualContent");
            visualContentObj.transform.SetParent(exhibitTransform, false);
            visualContent = visualContentObj.transform;

            // 将现有的非锚点子对象移动到 VisualContent 下
            List<Transform> childrenToMove = new List<Transform>();
            foreach (Transform child in exhibitTransform)
            {
                if (child != visualContent &&
                    child.name != "ViewPoint" &&
                    child.name != "GazeTarget")
                {
                    childrenToMove.Add(child);
                }
            }

            foreach (Transform child in childrenToMove)
            {
                Undo.SetTransformParent(child, visualContent, "Move to VisualContent");
            }
        }

        // 3. 创建或更新 ViewPoint
        Transform viewPoint = exhibitTransform.Find("ViewPoint");
        if (viewPoint == null)
        {
            GameObject vpObj = new GameObject("ViewPoint");
            Undo.RegisterCreatedObjectUndo(vpObj, "Create ViewPoint");
            vpObj.transform.SetParent(exhibitTransform, false);
            viewPoint = vpObj.transform;
        }
        Undo.RecordObject(viewPoint, "Set ViewPoint Position");
        viewPoint.position = config.viewPoint;

        // 4. 创建或更新 GazeTarget
        Transform gazeTarget = exhibitTransform.Find("GazeTarget");
        if (gazeTarget == null)
        {
            GameObject gtObj = new GameObject("GazeTarget");
            Undo.RegisterCreatedObjectUndo(gtObj, "Create GazeTarget");
            gtObj.transform.SetParent(exhibitTransform, false);
            gazeTarget = gtObj.transform;
        }
        Undo.RecordObject(gazeTarget, "Set GazeTarget Position");
        gazeTarget.position = config.gazeTarget;

        // 5. 添加或更新 ExhibitMetadata
        ExhibitMetadata metadata = exhibitObj.GetComponent<ExhibitMetadata>();
        if (metadata == null)
        {
            metadata = Undo.AddComponent<ExhibitMetadata>(exhibitObj);
        }
        else
        {
            Undo.RecordObject(metadata, "Update ExhibitMetadata");
        }

        // 使用 SerializedObject 来修改私有字段
        SerializedObject so = new SerializedObject(metadata);
        so.FindProperty("id").stringValue = config.id;
        so.FindProperty("exhibitName").stringValue = config.exhibitName;
        so.FindProperty("description").stringValue = config.description;
        so.FindProperty("semanticRole").stringValue = config.semanticRole;

        // 设置 tags
        SerializedProperty tagsProp = so.FindProperty("tags");
        tagsProp.ClearArray();
        for (int i = 0; i < config.tags.Length; i++)
        {
            tagsProp.InsertArrayElementAtIndex(i);
            tagsProp.GetArrayElementAtIndex(i).stringValue = config.tags[i];
        }

        // 设置 relatedExhibits
        SerializedProperty relatedProp = so.FindProperty("relatedExhibits");
        relatedProp.ClearArray();
        for (int i = 0; i < config.relatedExhibits.Length; i++)
        {
            relatedProp.InsertArrayElementAtIndex(i);
            relatedProp.GetArrayElementAtIndex(i).stringValue = config.relatedExhibits[i];
        }

        // 设置 Transform 引用
        so.FindProperty("viewPoint").objectReferenceValue = viewPoint;
        so.FindProperty("gazeTarget").objectReferenceValue = gazeTarget;

        so.ApplyModifiedProperties();

        if (showDebugInfo)
        {
            Debug.Log($"✓ 配置完成: {config.id} - {config.exhibitName}");
        }

        return true;
    }

    private void ValidateConfiguration()
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

        List<string> errors = new List<string>();
        List<string> warnings = new List<string>();
        int validCount = 0;

        var configs = GetExhibitConfigs();
        foreach (var config in configs)
        {
            Transform exhibitTransform = exhibitVisualsRoot.transform.Find(config.nodeName);
            if (exhibitTransform == null)
            {
                errors.Add($"缺失节点: {config.nodeName}");
                continue;
            }

            GameObject exhibitObj = exhibitTransform.gameObject;
            if (!exhibitObj.activeInHierarchy)
            {
                warnings.Add($"{config.id} 未激活");
            }

            ExhibitMetadata metadata = exhibitObj.GetComponent<ExhibitMetadata>();
            if (metadata == null)
            {
                errors.Add($"{config.id}: 缺少 ExhibitMetadata 组件");
                continue;
            }

            if (metadata.ViewPoint == null)
            {
                errors.Add($"{config.id}: ViewPoint 引用为空");
            }

            if (metadata.GazeTarget == null)
            {
                errors.Add($"{config.id}: GazeTarget 引用为空");
            }

            if (string.IsNullOrEmpty(metadata.Id))
            {
                errors.Add($"{config.nodeName}: ID 为空");
            }
            else if (metadata.Id != config.id)
            {
                warnings.Add($"{config.nodeName}: ID 不匹配 (期望 {config.id}, 实际 {metadata.Id})");
            }

            if (exhibitTransform.localScale != Vector3.one)
            {
                warnings.Add($"{config.id}: Scale 不是 (1,1,1)");
            }

            validCount++;
        }

        string message = $"验证完成:\n\n";
        message += $"✓ 有效展品: {validCount}/{configs.Count}\n\n";

        if (errors.Count > 0)
        {
            message += $"错误 ({errors.Count}):\n";
            foreach (var error in errors)
            {
                message += $"  • {error}\n";
            }
            message += "\n";
        }

        if (warnings.Count > 0)
        {
            message += $"警告 ({warnings.Count}):\n";
            foreach (var warning in warnings)
            {
                message += $"  • {warning}\n";
            }
        }

        if (errors.Count == 0 && warnings.Count == 0)
        {
            message += "✓ 所有检查通过！";
        }

        EditorUtility.DisplayDialog(
            "验证结果",
            message,
            "OK");

        Debug.Log($"[Validation] {message}");
    }

    private List<ExhibitConfig> GetExhibitConfigs()
    {
        return new List<ExhibitConfig>
        {
            new ExhibitConfig
            {
                id = "E1",
                nodeName = "E1_Visual",
                exhibitName = "智造序章",
                description = "工业技术从机械化走向数字化与智能化。",
                semanticRole = "introduction",
                tags = new[] { "introduction", "overview", "smart_manufacturing", "start" },
                relatedExhibits = new[] { "E2", "E3", "E4" },
                position = new Vector3(0, 0, 2.20f),
                yaw = 180f,
                viewPoint = new Vector3(0, 0, 0.80f),
                gazeTarget = new Vector3(0, 1.25f, 2.20f)
            },
            new ExhibitConfig
            {
                id = "E2",
                nodeName = "E2_Visual",
                exhibitName = "机械化起步",
                description = "早期机械设备提高了生产效率与稳定性。",
                semanticRole = "forward_next",
                tags = new[] { "mechanization", "early_industry", "production", "timeline" },
                relatedExhibits = new[] { "E1", "E3", "E4", "E5" },
                position = new Vector3(0, 0, 5.42f),
                yaw = 180f,
                viewPoint = new Vector3(0, 0, 3.82f),
                gazeTarget = new Vector3(0, 1.35f, 5.42f)
            },
            new ExhibitConfig
            {
                id = "E3",
                nodeName = "E3_Visual",
                exhibitName = "传动与控制",
                description = "传动结构与控制机构让机器能够精确协作。",
                semanticRole = "left_related_anchor",
                tags = new[] { "transmission", "control", "mechanism", "left_redirect" },
                relatedExhibits = new[] { "E2", "E4", "E5" },
                position = new Vector3(-4.35f, 0, 3.80f),
                yaw = 90f,
                viewPoint = new Vector3(-2.70f, 0, 3.80f),
                gazeTarget = new Vector3(-4.35f, 1.30f, 3.80f)
            },
            new ExhibitConfig
            {
                id = "E4",
                nodeName = "E4_Visual",
                exhibitName = "数字化转型",
                description = "计算机控制让生产过程变得可编程、可监测。",
                semanticRole = "right_alternative_anchor",
                tags = new[] { "digitalization", "computer_control", "programmable", "right_redirect" },
                relatedExhibits = new[] { "E2", "E3", "E5", "E8" },
                position = new Vector3(4.35f, 0, 3.80f),
                yaw = -90f,
                viewPoint = new Vector3(2.70f, 0, 3.80f),
                gazeTarget = new Vector3(4.35f, 1.30f, 3.80f)
            },
            new ExhibitConfig
            {
                id = "E5",
                nodeName = "E5_Visual",
                exhibitName = "智能机器人",
                description = "机器人感知环境并完成自动化协同生产。",
                semanticRole = "main_target",
                tags = new[] { "robotics", "perception", "automation", "collaboration", "main" },
                relatedExhibits = new[] { "E3", "E4", "E6", "E7", "E8" },
                position = new Vector3(0, 0, 8.83f),
                yaw = 180f,
                viewPoint = new Vector3(0, 0, 7.08f),
                gazeTarget = new Vector3(0, 1.40f, 8.83f)
            },
            new ExhibitConfig
            {
                id = "E6",
                nodeName = "E6_Visual",
                exhibitName = "仓储与物流",
                description = "仓储系统与运输设备连接生产和交付环节。",
                semanticRole = "side_context",
                tags = new[] { "storage", "logistics", "transport", "reset_camouflage" },
                relatedExhibits = new[] { "E5", "E7", "E8" },
                position = new Vector3(-4.35f, 0, 10.50f),
                yaw = 90f,
                viewPoint = new Vector3(-2.55f, 0, 10.50f),
                gazeTarget = new Vector3(-4.35f, 1.40f, 10.50f)
            },
            new ExhibitConfig
            {
                id = "E7",
                nodeName = "E7_Visual",
                exhibitName = "搬运方式对比",
                description = "对比不同搬运设备的结构、效率与转向能力。",
                semanticRole = "comparison_object",
                tags = new[] { "material_handling", "comparison", "forklift", "turn" },
                relatedExhibits = new[] { "E5", "E6", "E8" },
                position = new Vector3(4.35f, 0, 10.50f),
                yaw = -90f,
                viewPoint = new Vector3(2.55f, 0, 10.50f),
                gazeTarget = new Vector3(4.35f, 1.40f, 10.50f)
            },
            new ExhibitConfig
            {
                id = "E8",
                nodeName = "E8_Visual",
                exhibitName = "智能工厂全景",
                description = "机器、机器人与物流系统共同组成智能工厂。",
                semanticRole = "summary",
                tags = new[] { "integration", "smart_factory", "system", "summary", "ending" },
                relatedExhibits = new[] { "E4", "E5", "E6", "E7" },
                position = new Vector3(0, 0, 12.45f),
                yaw = 180f,
                viewPoint = new Vector3(0, 0, 10.65f),
                gazeTarget = new Vector3(0, 1.45f, 12.45f)
            }
        };
    }

    private class ExhibitConfig
    {
        public string id;
        public string nodeName;
        public string exhibitName;
        public string description;
        public string semanticRole;
        public string[] tags;
        public string[] relatedExhibits;
        public Vector3 position;
        public float yaw;
        public Vector3 viewPoint;
        public Vector3 gazeTarget;
    }
}
