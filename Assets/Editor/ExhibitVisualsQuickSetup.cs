using UnityEngine;
using UnityEditor;

/// <summary>
/// 快速设置菜单：一键配置展品并更新 JSON
/// </summary>
public class ExhibitVisualsQuickSetup
{
    [MenuItem("VirtualGuidance/Quick Setup/1. Configure All Exhibits + Update JSON")]
    public static void QuickSetupAll()
    {
        if (!EditorUtility.DisplayDialog(
            "快速配置",
            "此操作将：\n" +
            "1. 自动查找 ExhibitVisuals 根节点\n" +
            "2. 配置所有展品 (E1-E8)\n" +
            "3. 更新 scene_semantics.json\n\n" +
            "是否继续？",
            "确认",
            "取消"))
        {
            return;
        }

        try
        {
            // 1. 查找根节点
            GameObject rootObj = GameObject.Find("VirtualGuidance_B_Root");
            if (rootObj == null)
            {
                EditorUtility.DisplayDialog("错误", "找不到 VirtualGuidance_B_Root 节点", "OK");
                return;
            }

            Transform exhibitVisuals = rootObj.transform.Find("ExhibitVisuals");
            if (exhibitVisuals == null)
            {
                EditorUtility.DisplayDialog("错误", "找不到 ExhibitVisuals 节点", "OK");
                return;
            }

            GameObject exhibitVisualsRoot = exhibitVisuals.gameObject;
            Debug.Log($"[QuickSetup] 找到 ExhibitVisuals: {exhibitVisualsRoot.name}");

            // 2. 配置所有展品
            EditorUtility.DisplayProgressBar("快速配置", "正在配置展品...", 0.3f);

            if (!ConfigureAllExhibits(exhibitVisualsRoot))
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("错误", "展品配置失败", "OK");
                return;
            }

            Debug.Log("[QuickSetup] 展品配置完成");

            // 3. 更新 JSON
            EditorUtility.DisplayProgressBar("快速配置", "正在更新 JSON...", 0.7f);

            string configPath = "Assets/Configs/scene_semantics.json";
            if (System.IO.File.Exists(configPath))
            {
                // 备份原文件
                string backupPath = configPath + ".backup";
                System.IO.File.Copy(configPath, backupPath, true);
                Debug.Log($"[QuickSetup] 已备份原配置到: {backupPath}");
            }

            // 生成新 JSON（简化版本，不依赖 SceneSemanticsUpdater 窗口）
            GenerateAndSaveJson(exhibitVisualsRoot, configPath);

            EditorUtility.ClearProgressBar();

            // 标记场景为已修改
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "配置完成",
                "✓ 所有展品配置完成\n" +
                "✓ scene_semantics.json 已更新\n\n" +
                "请保存场景以应用更改。",
                "OK");

            Debug.Log("[QuickSetup] 快速配置完成！");
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("错误", $"配置失败:\n{e.Message}", "OK");
            Debug.LogError($"[QuickSetup] 配置失败: {e.Message}\n{e.StackTrace}");
        }
    }

    [MenuItem("VirtualGuidance/Quick Setup/2. Only Configure Exhibits")]
    public static void ConfigureExhibitsOnly()
    {
        GameObject rootObj = GameObject.Find("VirtualGuidance_B_Root");
        if (rootObj == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 VirtualGuidance_B_Root 节点", "OK");
            return;
        }

        Transform exhibitVisuals = rootObj.transform.Find("ExhibitVisuals");
        if (exhibitVisuals == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 ExhibitVisuals 节点", "OK");
            return;
        }

        if (ConfigureAllExhibits(exhibitVisuals.gameObject))
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            EditorUtility.DisplayDialog("完成", "展品配置完成！请保存场景。", "OK");
        }
    }

    [MenuItem("VirtualGuidance/Quick Setup/3. Only Update JSON")]
    public static void UpdateJsonOnly()
    {
        GameObject rootObj = GameObject.Find("VirtualGuidance_B_Root");
        if (rootObj == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 VirtualGuidance_B_Root 节点", "OK");
            return;
        }

        Transform exhibitVisuals = rootObj.transform.Find("ExhibitVisuals");
        if (exhibitVisuals == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 ExhibitVisuals 节点", "OK");
            return;
        }

        string configPath = "Assets/Configs/scene_semantics.json";
        GenerateAndSaveJson(exhibitVisuals.gameObject, configPath);

        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("完成", "scene_semantics.json 已更新！", "OK");
    }

    [MenuItem("VirtualGuidance/Quick Setup/4. Validate Configuration")]
    public static void ValidateConfiguration()
    {
        GameObject rootObj = GameObject.Find("VirtualGuidance_B_Root");
        if (rootObj == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 VirtualGuidance_B_Root 节点", "OK");
            return;
        }

        Transform exhibitVisuals = rootObj.transform.Find("ExhibitVisuals");
        if (exhibitVisuals == null)
        {
            EditorUtility.DisplayDialog("错误", "找不到 ExhibitVisuals 节点", "OK");
            return;
        }

        ValidateExhibits(exhibitVisuals.gameObject);
    }

    private static bool ConfigureAllExhibits(GameObject exhibitVisualsRoot)
    {
        Undo.SetCurrentGroupName("Configure All Exhibits");
        int undoGroup = Undo.GetCurrentGroup();

        var configs = GetExhibitConfigs();
        int successCount = 0;

        foreach (var config in configs)
        {
            if (ConfigureExhibit(exhibitVisualsRoot, config))
            {
                successCount++;
            }
        }

        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"[QuickSetup] 成功配置 {successCount}/{configs.Count} 个展品");
        return successCount == configs.Count;
    }

    private static bool ConfigureExhibit(GameObject exhibitVisualsRoot, ExhibitConfig config)
    {
        Transform exhibitTransform = exhibitVisualsRoot.transform.Find(config.nodeName);
        if (exhibitTransform == null)
        {
            Debug.LogWarning($"找不到节点: {config.nodeName}");
            return false;
        }

        GameObject exhibitObj = exhibitTransform.gameObject;
        Undo.RecordObject(exhibitObj.transform, "Configure Exhibit Transform");

        // 设置位置（保持现有旋转不变）
        exhibitTransform.position = config.position;
        // 不修改旋转：exhibitTransform.rotation = Quaternion.Euler(0, config.yaw, 0);
        exhibitTransform.localScale = Vector3.one;

        // 创建 ViewPoint
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

        // 创建 GazeTarget
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

        // 添加或更新 ExhibitMetadata
        ExhibitMetadata metadata = exhibitObj.GetComponent<ExhibitMetadata>();
        if (metadata == null)
        {
            metadata = Undo.AddComponent<ExhibitMetadata>(exhibitObj);
        }
        else
        {
            Undo.RecordObject(metadata, "Update ExhibitMetadata");
        }

        SerializedObject so = new SerializedObject(metadata);
        so.FindProperty("id").stringValue = config.id;
        so.FindProperty("exhibitName").stringValue = config.exhibitName;
        so.FindProperty("description").stringValue = config.description;
        so.FindProperty("semanticRole").stringValue = config.semanticRole;

        SerializedProperty tagsProp = so.FindProperty("tags");
        tagsProp.ClearArray();
        for (int i = 0; i < config.tags.Length; i++)
        {
            tagsProp.InsertArrayElementAtIndex(i);
            tagsProp.GetArrayElementAtIndex(i).stringValue = config.tags[i];
        }

        SerializedProperty relatedProp = so.FindProperty("relatedExhibits");
        relatedProp.ClearArray();
        for (int i = 0; i < config.relatedExhibits.Length; i++)
        {
            relatedProp.InsertArrayElementAtIndex(i);
            relatedProp.GetArrayElementAtIndex(i).stringValue = config.relatedExhibits[i];
        }

        so.FindProperty("viewPoint").objectReferenceValue = viewPoint;
        so.FindProperty("gazeTarget").objectReferenceValue = gazeTarget;
        so.ApplyModifiedProperties();

        Debug.Log($"✓ 配置完成: {config.id} - {config.exhibitName}");
        return true;
    }

    private static void GenerateAndSaveJson(GameObject exhibitVisualsRoot, string configPath)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"schema_version\": 2,");
        sb.AppendLine("  \"scene_id\": \"dual_world_gallery\",");
        sb.AppendLine("  \"scene_name\": \"Dual World Gallery - 12m x 14m Layout\",");
        sb.AppendLine("  \"coordinate_system\": \"Unity X-Z ground plane, Y-up\",");
        sb.AppendLine("  \"layout\": {");
        sb.AppendLine("    \"units\": \"meters\",");
        sb.AppendLine("    \"floor_origin\": [0, 0, 0],");
        sb.AppendLine("    \"floor_center\": [0, 0, 7],");
        sb.AppendLine("    \"floor_size_m\": { \"width\": 12, \"length\": 14 },");
        sb.AppendLine("    \"floor_bounds\": { \"x_min\": -6, \"x_max\": 6, \"z_min\": 0, \"z_max\": 14 },");
        sb.AppendLine("    \"entrance_side\": \"z_min\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"exhibits\": [");

        var configs = GetExhibitConfigs();
        for (int i = 0; i < configs.Count; i++)
        {
            var config = configs[i];
            Transform exhibitTransform = exhibitVisualsRoot.transform.Find(config.nodeName);

            if (exhibitTransform == null) continue;

            ExhibitMetadata metadata = exhibitTransform.GetComponent<ExhibitMetadata>();
            if (metadata == null) continue;

            sb.AppendLine("    {");
            sb.AppendLine($"      \"id\": \"{config.id}\",");
            sb.AppendLine($"      \"name\": \"{config.exhibitName}\",");
            sb.AppendLine($"      \"scene_path\": \"VirtualGuidance_B_Root/ExhibitVisuals/{config.nodeName}\",");
            sb.AppendLine($"      \"semantic_role\": \"{config.semanticRole}\",");
            sb.AppendLine($"      \"position\": [{config.position.x:F2}, {config.position.y:F2}, {config.position.z:F2}],");
            sb.AppendLine($"      \"rotation_y_degrees\": {config.yaw:F0},");
            sb.AppendLine($"      \"viewpoint\": [{config.viewPoint.x:F2}, {config.viewPoint.y:F2}, {config.viewPoint.z:F2}],");
            sb.AppendLine($"      \"gaze_target\": [{config.gazeTarget.x:F2}, {config.gazeTarget.y:F2}, {config.gazeTarget.z:F2}],");

            sb.Append("      \"tags\": [");
            for (int t = 0; t < config.tags.Length; t++)
            {
                sb.Append($"\"{config.tags[t]}\"");
                if (t < config.tags.Length - 1) sb.Append(", ");
            }
            sb.AppendLine("],");

            sb.Append("      \"related_exhibits\": [");
            for (int r = 0; r < config.relatedExhibits.Length; r++)
            {
                sb.Append($"\"{config.relatedExhibits[r]}\"");
                if (r < config.relatedExhibits.Length - 1) sb.Append(", ");
            }
            sb.AppendLine("],");

            sb.Append($"      \"description\": \"{config.description}\"");

            if (i < configs.Count - 1)
                sb.AppendLine();
            sb.Append("    }");
            if (i < configs.Count - 1) sb.Append(",");
            sb.AppendLine();
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        System.IO.File.WriteAllText(configPath, sb.ToString(), System.Text.Encoding.UTF8);
        Debug.Log($"[QuickSetup] JSON 已保存到: {configPath}");
    }

    private static void ValidateExhibits(GameObject exhibitVisualsRoot)
    {
        System.Collections.Generic.List<string> errors = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> warnings = new System.Collections.Generic.List<string>();
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

            if (!exhibitTransform.gameObject.activeInHierarchy)
            {
                warnings.Add($"{config.id} 未激活");
            }

            ExhibitMetadata metadata = exhibitTransform.GetComponent<ExhibitMetadata>();
            if (metadata == null)
            {
                errors.Add($"{config.id}: 缺少 ExhibitMetadata 组件");
                continue;
            }

            if (metadata.ViewPoint == null)
                errors.Add($"{config.id}: ViewPoint 引用为空");

            if (metadata.GazeTarget == null)
                errors.Add($"{config.id}: GazeTarget 引用为空");

            if (string.IsNullOrEmpty(metadata.Id))
                errors.Add($"{config.nodeName}: ID 为空");

            if (exhibitTransform.localScale != Vector3.one)
                warnings.Add($"{config.id}: Scale 不是 (1,1,1)");

            validCount++;
        }

        string message = $"验证完成:\n\n✓ 有效展品: {validCount}/{configs.Count}\n\n";

        if (errors.Count > 0)
        {
            message += $"错误 ({errors.Count}):\n";
            foreach (var error in errors)
                message += $"  • {error}\n";
            message += "\n";
        }

        if (warnings.Count > 0)
        {
            message += $"警告 ({warnings.Count}):\n";
            foreach (var warning in warnings)
                message += $"  • {warning}\n";
        }

        if (errors.Count == 0 && warnings.Count == 0)
        {
            message += "✓ 所有检查通过！";
        }

        EditorUtility.DisplayDialog("验证结果", message, "OK");
        Debug.Log($"[Validation] {message}");
    }

    private static System.Collections.Generic.List<ExhibitConfig> GetExhibitConfigs()
    {
        return new System.Collections.Generic.List<ExhibitConfig>
        {
            new ExhibitConfig { id = "E1", nodeName = "E1_Visual", exhibitName = "智造序章",
                description = "工业技术从机械化走向数字化与智能化。", semanticRole = "introduction",
                tags = new[] { "introduction", "overview", "smart_manufacturing", "start" },
                relatedExhibits = new[] { "E2", "E3", "E4" },
                position = new Vector3(0, 0, 2.20f), yaw = 180f,
                viewPoint = new Vector3(0, 0, 0.80f), gazeTarget = new Vector3(0, 1.25f, 2.20f) },

            new ExhibitConfig { id = "E2", nodeName = "E2_Visual", exhibitName = "机械化起步",
                description = "早期机械设备提高了生产效率与稳定性。", semanticRole = "forward_next",
                tags = new[] { "mechanization", "early_industry", "production", "timeline" },
                relatedExhibits = new[] { "E1", "E3", "E4", "E5" },
                position = new Vector3(0, 0, 5.42f), yaw = 180f,
                viewPoint = new Vector3(0, 0, 3.82f), gazeTarget = new Vector3(0, 1.35f, 5.42f) },

            new ExhibitConfig { id = "E3", nodeName = "E3_Visual", exhibitName = "传动与控制",
                description = "传动结构与控制机构让机器能够精确协作。", semanticRole = "left_related_anchor",
                tags = new[] { "transmission", "control", "mechanism", "left_redirect" },
                relatedExhibits = new[] { "E2", "E4", "E5" },
                position = new Vector3(-4.35f, 0, 3.80f), yaw = 90f,
                viewPoint = new Vector3(-2.70f, 0, 3.80f), gazeTarget = new Vector3(-4.35f, 1.30f, 3.80f) },

            new ExhibitConfig { id = "E4", nodeName = "E4_Visual", exhibitName = "数字化转型",
                description = "计算机控制让生产过程变得可编程、可监测。", semanticRole = "right_alternative_anchor",
                tags = new[] { "digitalization", "computer_control", "programmable", "right_redirect" },
                relatedExhibits = new[] { "E2", "E3", "E5", "E8" },
                position = new Vector3(4.35f, 0, 3.80f), yaw = -90f,
                viewPoint = new Vector3(2.70f, 0, 3.80f), gazeTarget = new Vector3(4.35f, 1.30f, 3.80f) },

            new ExhibitConfig { id = "E5", nodeName = "E5_Visual", exhibitName = "智能机器人",
                description = "机器人感知环境并完成自动化协同生产。", semanticRole = "main_target",
                tags = new[] { "robotics", "perception", "automation", "collaboration", "main" },
                relatedExhibits = new[] { "E3", "E4", "E6", "E7", "E8" },
                position = new Vector3(0, 0, 8.83f), yaw = 180f,
                viewPoint = new Vector3(0, 0, 7.08f), gazeTarget = new Vector3(0, 1.40f, 8.83f) },

            new ExhibitConfig { id = "E6", nodeName = "E6_Visual", exhibitName = "仓储与物流",
                description = "仓储系统与运输设备连接生产和交付环节。", semanticRole = "side_context",
                tags = new[] { "storage", "logistics", "transport", "reset_camouflage" },
                relatedExhibits = new[] { "E5", "E7", "E8" },
                position = new Vector3(-4.35f, 0, 10.50f), yaw = 90f,
                viewPoint = new Vector3(-2.55f, 0, 10.50f), gazeTarget = new Vector3(-4.35f, 1.40f, 10.50f) },

            new ExhibitConfig { id = "E7", nodeName = "E7_Visual", exhibitName = "搬运方式对比",
                description = "对比不同搬运设备的结构、效率与转向能力。", semanticRole = "comparison_object",
                tags = new[] { "material_handling", "comparison", "forklift", "turn" },
                relatedExhibits = new[] { "E5", "E6", "E8" },
                position = new Vector3(4.35f, 0, 10.50f), yaw = -90f,
                viewPoint = new Vector3(2.55f, 0, 10.50f), gazeTarget = new Vector3(4.35f, 1.40f, 10.50f) },

            new ExhibitConfig { id = "E8", nodeName = "E8_Visual", exhibitName = "智能工厂全景",
                description = "机器、机器人与物流系统共同组成智能工厂。", semanticRole = "summary",
                tags = new[] { "integration", "smart_factory", "system", "summary", "ending" },
                relatedExhibits = new[] { "E4", "E5", "E6", "E7" },
                position = new Vector3(0, 0, 12.45f), yaw = 180f,
                viewPoint = new Vector3(0, 0, 10.65f), gazeTarget = new Vector3(0, 1.45f, 12.45f) }
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
