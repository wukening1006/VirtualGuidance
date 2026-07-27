using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// 提供 12m x 14m 展厅的装配、同步、导出和只读校验菜单。
/// </summary>
public static class SceneSemanticsTools
{
    private const string MainScenePath = "Assets/Scenes/MainScene.unity";
    private const string PackagePrefabPath =
        "Assets/ThirdParty/VirtualGuidance_B_Package/Prefabs/VirtualGuidance_B_Root.prefab";
    private const string DocumentationFolder = "Assets/Documentation/GalleryLayoutV2";
    private const string MaterialFolder = "Assets/Materials/GalleryLayoutV2";
    private const string ValidationReportPath = DocumentationFolder + "/LayoutValidationReport.txt";
    private const string BaselineReportPath = DocumentationFolder + "/BaselineMeasurements.txt";
    private const float WallHeight = 4.6f;
    private const float WallThickness = 0.16f;

    /// <summary>
    /// 完成基线记录、Prefab 规范化、场景替换、最终截图和验收报告。
    /// </summary>
    [MenuItem("VirtualGuidance/Gallery Layout V2/Build Complete Layout")]
    public static void BuildCompleteLayout()
    {
        EnsureAssetFolder(DocumentationFolder);
        EnsureAssetFolder(MaterialFolder);

        Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        SceneSemanticsConfig config = SceneSemanticsConfigLoader.Load();
        CaptureBaselineIfNeeded(scene);
        BuildPackagePrefab(config);
        InstallPackageInScene(scene);
        ApplyConfigToScene(scene, config);
        EditorSceneManager.SaveScene(scene);

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
        {
            CaptureViews(scene, "Final");
        }
        SceneSemanticsValidationReport report = SceneSemanticsValidator.Validate(scene, config);
        WriteValidationReport(report);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (!report.IsValid)
        {
            throw new InvalidOperationException(
                $"Gallery Layout V2 验证失败，共 {report.errors.Count} 项。详见 {ValidationReportPath}");
        }

        Debug.Log($"[GalleryLayoutV2] 完整布局已生成并通过验证：{ValidationReportPath}");
    }

    /// <summary>
    /// 供 Unity 命令行调用完整装配流程。
    /// </summary>
    public static void BuildFromCommandLine()
    {
        try
        {
            BuildCompleteLayout();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    /// <summary>
    /// 只读比较 JSON、当前场景、Metadata、锚点与柜体外廓。
    /// </summary>
    [MenuItem("VirtualGuidance/Scene Semantics/Validate")]
    public static void ValidateCurrentScene()
    {
        SceneSemanticsConfig config = SceneSemanticsConfigLoader.Load();
        SceneSemanticsValidationReport report =
            SceneSemanticsValidator.Validate(SceneManager.GetActiveScene(), config);
        EnsureAssetFolder(DocumentationFolder);
        WriteValidationReport(report);
        AssetDatabase.Refresh();

        if (report.IsValid)
        {
            Debug.Log($"[SceneSemantics] 验证通过：{ValidationReportPath}");
        }
        else
        {
            Debug.LogError($"[SceneSemantics] 验证失败，共 {report.errors.Count} 项：{ValidationReportPath}");
        }
    }

    /// <summary>
    /// 以 JSON 为输入，将布局、锚点与语义元数据应用到当前场景。
    /// </summary>
    [MenuItem("VirtualGuidance/Scene Semantics/Apply To Scene")]
    public static void ApplyToCurrentScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        ApplyConfigToScene(scene, SceneSemanticsConfigLoader.Load());
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[SceneSemantics] JSON v2 已应用到当前场景。");
    }

    /// <summary>
    /// 从当前场景回写坐标、旋转、锚点和柜体尺寸，不覆盖语义文本。
    /// </summary>
    [MenuItem("VirtualGuidance/Scene Semantics/Export From Scene")]
    public static void ExportFromCurrentScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        SceneSemanticsConfig config = SceneSemanticsConfigLoader.Load();
        PopulateLayoutFromScene(scene, config);
        SceneSemanticsConfigLoader.Save(config);
        AssetDatabase.Refresh();
        Debug.Log("[SceneSemantics] 已从场景回写布局字段，语义文本保持不变。");
    }

    internal static void PopulateLayoutFromScene(Scene scene, SceneSemanticsConfig config)
    {
        foreach (ExhibitSemanticsConfig exhibit in config.exhibits)
        {
            Transform root = SceneSemanticsValidator.FindPath(scene, exhibit.scene_path);
            if (root == null)
            {
                throw new InvalidOperationException($"找不到场景路径：{exhibit.scene_path}");
            }

            ExhibitMetadata metadata = root.GetComponent<ExhibitMetadata>();
            BoxCollider envelope = root.Find("CabinetEnvelope")?.GetComponent<BoxCollider>();
            if (metadata == null || metadata.ViewPoint == null || metadata.GazeTarget == null || envelope == null)
            {
                throw new InvalidOperationException($"{exhibit.id} 缺少 Metadata、锚点或柜体外廓。");
            }

            exhibit.position = ToArray(root.position);
            exhibit.rotation_y_degrees = NormalizeYaw(root.eulerAngles.y);
            exhibit.viewpoint = ToArray(metadata.ViewPoint.position);
            exhibit.gaze_target = ToArray(metadata.GazeTarget.position);
            exhibit.cabinet.width_m = envelope.size.x;
            exhibit.cabinet.depth_m = envelope.size.z;
            exhibit.cabinet.height_m = envelope.size.y;
        }
    }

    private static void BuildPackagePrefab(SceneSemanticsConfig config)
    {
        GameObject prefabRoot = PrefabUtility.LoadPrefabContents(PackagePrefabPath);
        try
        {
            prefabRoot.name = "VirtualGuidance_B_Root";
            prefabRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            prefabRoot.transform.localScale = Vector3.one;

            BuildEnvironment(prefabRoot.transform);
            ApplyConfigToRoot(prefabRoot.transform, config);
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, PackagePrefabPath);
            AssetDatabase.ImportAsset(
                PackagePrefabPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }
    }

    private static void InstallPackageInScene(Scene scene)
    {
        foreach (GameObject rootObject in scene.GetRootGameObjects())
        {
            if (PrefabUtility.IsPrefabAssetMissing(rootObject))
            {
                Object.DestroyImmediate(rootObject);
            }
        }

        Transform existingRoot = SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root");
        if (existingRoot != null)
        {
            Object.DestroyImmediate(existingRoot.gameObject);
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PackagePrefabPath);
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
        if (instance == null)
        {
            throw new InvalidOperationException("无法在 MainScene 中实例化 VirtualGuidance_B_Root。");
        }

        instance.name = "VirtualGuidance_B_Root";
        instance.SetActive(true);

        foreach (string legacyRootName in new[] { "Environment", "Exhibits" })
        {
            Transform legacyRoot = SceneSemanticsValidator.FindRoot(scene, legacyRootName);
            if (legacyRoot != null)
            {
                legacyRoot.gameObject.SetActive(false);
            }
        }
    }

    internal static void ApplyConfigToScene(Scene scene, SceneSemanticsConfig config)
    {
        Transform packageRoot = SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root");
        if (packageRoot == null)
        {
            throw new InvalidOperationException("当前场景缺少 VirtualGuidance_B_Root。");
        }

        packageRoot.gameObject.SetActive(true);
        packageRoot.localScale = Vector3.one;
        ApplyConfigToRoot(packageRoot, config);

        foreach (string legacyRootName in new[] { "Environment", "Exhibits" })
        {
            Transform legacyRoot = SceneSemanticsValidator.FindRoot(scene, legacyRootName);
            if (legacyRoot != null)
            {
                legacyRoot.gameObject.SetActive(false);
            }
        }
    }

    private static void ApplyConfigToRoot(Transform packageRoot, SceneSemanticsConfig config)
    {
        Transform exhibitVisuals = EnsureChild(packageRoot, "ExhibitVisuals");
        foreach (ExhibitSemanticsConfig exhibit in config.exhibits)
        {
            Transform exhibitRoot = exhibitVisuals.Find($"{exhibit.id}_Visual");
            if (exhibitRoot == null)
            {
                throw new InvalidOperationException($"B Root 中缺少 {exhibit.id}_Visual。");
            }

            ConfigureExhibit(exhibitRoot, exhibit);
        }
    }

    private static void ConfigureExhibit(Transform exhibitRoot, ExhibitSemanticsConfig exhibit)
    {
        exhibitRoot.gameObject.SetActive(true);
        exhibitRoot.position = SceneSemanticsValidator.ToVector3(exhibit.position);
        exhibitRoot.rotation = Quaternion.Euler(0f, exhibit.rotation_y_degrees, 0f);
        exhibitRoot.localScale = Vector3.one;

        Transform visualContent = EnsureVisualContent(exhibitRoot);
        BoxCollider envelope = EnsureCabinetEnvelope(exhibitRoot, exhibit.cabinet);
        FitVisualContent(visualContent, exhibitRoot, exhibit.cabinet);

        Transform viewPoint = EnsureDirectChild(exhibitRoot, "ViewPoint");
        Transform gazeTarget = EnsureDirectChild(exhibitRoot, "GazeTarget");
        viewPoint.position = SceneSemanticsValidator.ToVector3(exhibit.viewpoint);
        gazeTarget.position = SceneSemanticsValidator.ToVector3(exhibit.gaze_target);
        viewPoint.localScale = Vector3.one;
        gazeTarget.localScale = Vector3.one;

        ExhibitMetadata[] metadataComponents = exhibitRoot.GetComponentsInChildren<ExhibitMetadata>(true);
        ExhibitMetadata metadata = metadataComponents.FirstOrDefault(
            component => component != null && component.transform == exhibitRoot);
        foreach (ExhibitMetadata component in metadataComponents)
        {
            if (component != null && component != metadata)
            {
                Object.DestroyImmediate(component);
            }
        }

        if (metadata == null)
        {
            metadata = exhibitRoot.gameObject.AddComponent<ExhibitMetadata>();
        }
        metadata.Configure(
            exhibit.id,
            exhibit.name,
            exhibit.tags,
            exhibit.description,
            exhibit.semantic_role,
            exhibit.related_exhibits,
            viewPoint,
            gazeTarget);

        EditorUtility.SetDirty(metadata);
        EditorUtility.SetDirty(envelope);
        EditorUtility.SetDirty(exhibitRoot.gameObject);
    }

    private static Transform EnsureVisualContent(Transform exhibitRoot)
    {
        Transform visualContent = exhibitRoot.Find("VisualContent");
        if (visualContent == null)
        {
            visualContent = new GameObject("VisualContent").transform;
            visualContent.SetParent(exhibitRoot, false);
        }

        var movableChildren = new List<Transform>();
        for (int index = 0; index < exhibitRoot.childCount; index++)
        {
            Transform child = exhibitRoot.GetChild(index);
            if (child != visualContent
                && child.name != "ViewPoint"
                && child.name != "GazeTarget"
                && child.name != "CabinetEnvelope")
            {
                movableChildren.Add(child);
            }
        }

        foreach (Transform child in movableChildren)
        {
            child.SetParent(visualContent, true);
        }

        visualContent.localPosition = Vector3.zero;
        visualContent.localRotation = Quaternion.identity;
        visualContent.localScale = Vector3.one;
        return visualContent;
    }

    private static BoxCollider EnsureCabinetEnvelope(Transform exhibitRoot, ExhibitCabinetConfig cabinet)
    {
        Transform envelopeTransform = EnsureDirectChild(exhibitRoot, "CabinetEnvelope");
        envelopeTransform.localPosition = Vector3.zero;
        envelopeTransform.localRotation = Quaternion.identity;
        envelopeTransform.localScale = Vector3.one;

        BoxCollider envelope = envelopeTransform.GetComponent<BoxCollider>();
        if (envelope == null)
        {
            envelope = envelopeTransform.gameObject.AddComponent<BoxCollider>();
        }
        envelope.center = new Vector3(0f, cabinet.height_m * 0.5f, 0f);
        envelope.size = new Vector3(cabinet.width_m, cabinet.height_m, cabinet.depth_m);
        envelope.isTrigger = false;
        return envelope;
    }

    private static void FitVisualContent(
        Transform visualContent,
        Transform exhibitRoot,
        ExhibitCabinetConfig cabinet)
    {
        Bounds initialBounds =
            SceneSemanticsGeometry.MeasureRendererBoundsInRootSpace(visualContent, exhibitRoot);
        if (initialBounds.size.sqrMagnitude <= Mathf.Epsilon)
        {
            return;
        }

        float widthScale = cabinet.width_m * 0.92f / Mathf.Max(initialBounds.size.x, 0.001f);
        float heightScale = cabinet.height_m * 0.92f / Mathf.Max(initialBounds.size.y, 0.001f);
        float depthScale = cabinet.depth_m * 0.92f / Mathf.Max(initialBounds.size.z, 0.001f);
        float uniformScale = Mathf.Min(widthScale, heightScale, depthScale);
        visualContent.localScale = Vector3.one * uniformScale;

        Transform showcaseVisual = visualContent.Find("ShowcaseVisual");
        if (showcaseVisual != null)
        {
            Bounds showcaseBounds =
                SceneSemanticsGeometry.MeasureRendererBoundsInRootSpace(showcaseVisual, exhibitRoot);
            if (showcaseBounds.size.sqrMagnitude > Mathf.Epsilon)
            {
                const float targetFill = 0.995f;
                Vector3 showcaseScale = showcaseVisual.localScale;
                showcaseScale.x *= cabinet.width_m * targetFill / Mathf.Max(showcaseBounds.size.x, 0.001f);
                showcaseScale.y *= cabinet.height_m * targetFill / Mathf.Max(showcaseBounds.size.y, 0.001f);
                showcaseScale.z *= cabinet.depth_m * targetFill / Mathf.Max(showcaseBounds.size.z, 0.001f);
                showcaseVisual.localScale = showcaseScale;
            }
        }

        Bounds scaledBounds =
            SceneSemanticsGeometry.MeasureRendererBoundsInRootSpace(visualContent, exhibitRoot);
        visualContent.localPosition += new Vector3(
            -scaledBounds.center.x,
            0.04f - scaledBounds.min.y,
            -scaledBounds.center.z);
    }

    private static void BuildEnvironment(Transform packageRoot)
    {
        Transform environment = EnsureChild(packageRoot, "Environment");
        Transform importedRoom = environment.Find("Room_Environment");
        if (importedRoom != null)
        {
            importedRoom.gameObject.SetActive(false);
        }

        Transform dressing = environment.Find("Room_Environment_Dressing");
        if (dressing != null)
        {
            dressing.gameObject.SetActive(true);
            DisableOutOfBoundsDecorations(dressing);
        }

        DestroyChild(environment, "EffectiveFloor_12x14");
        DestroyChild(environment, "Walls");
        DestroyChild(environment, "Lighting");

        Material floorMaterial = EnsureMaterial(
            "GalleryFloor",
            new Color(0.075f, 0.09f, 0.12f),
            0.18f,
            0.72f);
        Material wallMaterial = EnsureMaterial(
            "GalleryWall",
            new Color(0.68f, 0.71f, 0.74f),
            0.05f,
            0.38f);
        Material ceilingMaterial = EnsureMaterial(
            "GalleryCeiling",
            new Color(0.82f, 0.83f, 0.84f),
            0f,
            0.3f);

        GameObject floor = CreateCube(
            "EffectiveFloor_12x14",
            environment,
            new Vector3(0f, -0.05f, 7f),
            new Vector3(12f, 0.1f, 14f),
            floorMaterial);
        floor.isStatic = true;

        Transform walls = new GameObject("Walls").transform;
        walls.SetParent(environment, false);
        CreateCube(
            "Wall_Left",
            walls,
            new Vector3(-6f, WallHeight * 0.5f, 7f),
            new Vector3(WallThickness, WallHeight, 14f),
            wallMaterial);
        CreateCube(
            "Wall_Right",
            walls,
            new Vector3(6f, WallHeight * 0.5f, 7f),
            new Vector3(WallThickness, WallHeight, 14f),
            wallMaterial);
        CreateCube(
            "Wall_Back",
            walls,
            new Vector3(0f, WallHeight * 0.5f, 14f),
            new Vector3(12f, WallHeight, WallThickness),
            wallMaterial);
        CreateCube(
            "Wall_Front_Left",
            walls,
            new Vector3(-4.25f, WallHeight * 0.5f, 0f),
            new Vector3(3.5f, WallHeight, WallThickness),
            wallMaterial);
        CreateCube(
            "Wall_Front_Right",
            walls,
            new Vector3(4.25f, WallHeight * 0.5f, 0f),
            new Vector3(3.5f, WallHeight, WallThickness),
            wallMaterial);
        CreateCube(
            "Entrance_Header",
            walls,
            new Vector3(0f, 4.1f, 0f),
            new Vector3(5f, 1f, WallThickness),
            wallMaterial);
        CreateCube(
            "Ceiling",
            walls,
            new Vector3(0f, WallHeight, 7f),
            new Vector3(12f, 0.12f, 14f),
            ceilingMaterial);

        Transform lighting = new GameObject("Lighting").transform;
        lighting.SetParent(environment, false);
        CreateDirectionalLight(lighting);
        foreach (float zPosition in new[] { 2.4f, 7f, 11.6f })
        {
            CreatePointLight(lighting, new Vector3(-3f, 4f, zPosition));
            CreatePointLight(lighting, new Vector3(3f, 4f, zPosition));
        }
    }

    private static void DisableOutOfBoundsDecorations(Transform dressing)
    {
        foreach (Transform child in dressing.GetComponentsInChildren<Transform>(true))
        {
            if (child == dressing)
            {
                continue;
            }

            Vector3 position = child.position;
            if (Mathf.Abs(position.x) > 5.7f || position.z < 0f || position.z > 14f)
            {
                child.gameObject.SetActive(false);
            }
        }
    }

    private static void CaptureBaselineIfNeeded(Scene scene)
    {
        string baselineTop = DocumentationFolder + "/Baseline_Top.png";
        if (File.Exists(baselineTop))
        {
            return;
        }

        Transform packageRoot = SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root");
        if (packageRoot == null)
        {
            return;
        }

        var previousStates = new Dictionary<GameObject, bool>();
        foreach (string rootName in new[] { "VirtualGuidance_B_Root", "Environment", "Exhibits" })
        {
            Transform root = SceneSemanticsValidator.FindRoot(scene, rootName);
            if (root != null)
            {
                previousStates[root.gameObject] = root.gameObject.activeSelf;
                root.gameObject.SetActive(rootName == "VirtualGuidance_B_Root");
            }
        }

        WriteBaselineReport(packageRoot);
        CaptureViews(scene, "Baseline");
        foreach (KeyValuePair<GameObject, bool> state in previousStates)
        {
            state.Key.SetActive(state.Value);
        }
    }

    private static void WriteBaselineReport(Transform packageRoot)
    {
        var report = new StringBuilder()
            .AppendLine("Gallery Layout V2 Baseline")
            .AppendLine($"Captured: {DateTime.Now:O}")
            .AppendLine();
        Transform exhibitVisuals = packageRoot.Find("ExhibitVisuals");
        if (exhibitVisuals != null)
        {
            foreach (Transform exhibit in exhibitVisuals.Cast<Transform>().OrderBy(item => item.name))
            {
                Renderer[] renderers = exhibit.GetComponentsInChildren<Renderer>(true);
                Collider[] colliders = exhibit.GetComponentsInChildren<Collider>(true);
                Bounds rendererBounds = CombineWorldBounds(renderers);
                Bounds colliderBounds = CombineWorldBounds(colliders);
                ExhibitMetadata metadata = exhibit.GetComponent<ExhibitMetadata>();
                Transform viewPoint = metadata != null ? metadata.ViewPoint : exhibit.Find("ViewPoint");
                Transform gazeTarget = metadata != null ? metadata.GazeTarget : exhibit.Find("GazeTarget");
                report.AppendLine(
                    $"{exhibit.name}: position={Format(exhibit.position)}, yaw={exhibit.eulerAngles.y:F2}, "
                    + $"scale={Format(exhibit.localScale)}, rendererBounds={Format(rendererBounds.size)}, "
                    + $"colliderBounds={Format(colliderBounds.size)}, "
                    + $"viewPoint={(viewPoint != null ? Format(viewPoint.position) : "<missing>")}, "
                    + $"gazeTarget={(gazeTarget != null ? Format(gazeTarget.position) : "<missing>")}");
            }
        }

        File.WriteAllText(BaselineReportPath, report.ToString());
    }

    private static void CaptureViews(Scene scene, string prefix)
    {
        Transform ceiling = SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root")
            ?.Find("Environment/Walls/Ceiling");
        bool ceilingWasActive = ceiling != null && ceiling.gameObject.activeSelf;
        if (ceiling != null)
        {
            ceiling.gameObject.SetActive(false);
        }

        CaptureView(
            $"{DocumentationFolder}/{prefix}_Top.png",
            new Vector3(0f, 18f, 7f),
            new Vector3(0f, 0f, 7f),
            true,
            8.5f);
        if (ceiling != null)
        {
            ceiling.gameObject.SetActive(ceilingWasActive);
        }

        CaptureView(
            $"{DocumentationFolder}/{prefix}_Entrance.png",
            new Vector3(0f, 1.65f, -2.5f),
            new Vector3(0f, 1.2f, 7f),
            false,
            0f);
        CaptureView(
            $"{DocumentationFolder}/{prefix}_XR_Sim.png",
            new Vector3(0f, 1.7f, 0.65f),
            new Vector3(0f, 1.25f, 8.5f),
            false,
            0f);
    }

    private static void CaptureView(
        string path,
        Vector3 position,
        Vector3 target,
        bool orthographic,
        float orthographicSize)
    {
        var cameraObject = new GameObject("GalleryLayoutCaptureCamera");
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.transform.position = position;
        cameraObject.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.18f, 0.2f, 0.23f);
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 100f;
        camera.orthographic = orthographic;
        camera.orthographicSize = orthographicSize;

        const int width = 1280;
        const int height = 720;
        var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        RenderTexture previous = RenderTexture.active;
        camera.targetTexture = renderTexture;
        camera.Render();
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());

        camera.targetTexture = null;
        RenderTexture.active = previous;
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(renderTexture);
        Object.DestroyImmediate(cameraObject);
    }

    private static void WriteValidationReport(SceneSemanticsValidationReport report)
    {
        var output = new StringBuilder()
            .AppendLine("Gallery Layout V2 Validation")
            .AppendLine($"Generated: {DateTime.Now:O}")
            .AppendLine($"Result: {(report.IsValid ? "PASS" : "FAIL")}")
            .AppendLine($"Errors: {report.errors.Count}")
            .AppendLine($"Warnings: {report.warnings.Count}")
            .AppendLine()
            .AppendLine("[Errors]");
        foreach (string error in report.errors)
        {
            output.AppendLine("- " + error);
        }

        output.AppendLine().AppendLine("[Warnings]");
        foreach (string warning in report.warnings)
        {
            output.AppendLine("- " + warning);
        }

        output.AppendLine().AppendLine("[Measurements]");
        foreach (string measurement in report.measurements)
        {
            output.AppendLine(measurement);
        }

        File.WriteAllText(ValidationReportPath, output.ToString());
    }

    private static Material EnsureMaterial(string name, Color color, float metallic, float smoothness)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Standard");
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Glossiness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GameObject CreateCube(
        string name,
        Transform parent,
        Vector3 localPosition,
        Vector3 localScale,
        Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = localPosition;
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = localScale;
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
        return cube;
    }

    private static void CreateDirectionalLight(Transform parent)
    {
        var lightObject = new GameObject("Directional_Soft");
        lightObject.transform.SetParent(parent, false);
        lightObject.transform.localRotation = Quaternion.Euler(42f, -32f, 0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.92f, 0.82f);
        light.intensity = 0.68f;
        light.shadows = LightShadows.Soft;
    }

    private static void CreatePointLight(Transform parent, Vector3 localPosition)
    {
        var lightObject = new GameObject($"Warm_Light_{localPosition.z:F1}_{localPosition.x:F0}");
        lightObject.transform.SetParent(parent, false);
        lightObject.transform.localPosition = localPosition;
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.82f, 0.68f);
        light.intensity = 1.15f;
        light.range = 6.2f;
        light.shadows = LightShadows.Soft;
    }

    private static Transform EnsureChild(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null)
        {
            return child;
        }

        var childObject = new GameObject(name);
        childObject.transform.SetParent(parent, false);
        return childObject.transform;
    }

    private static Transform EnsureDirectChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null && existing.parent == parent)
        {
            return existing;
        }

        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child.transform;
    }

    private static void DestroyChild(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null)
        {
            Object.DestroyImmediate(child.gameObject);
        }
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        string[] segments = folderPath.Split('/');
        string current = segments[0];
        for (int index = 1; index < segments.Length; index++)
        {
            string next = $"{current}/{segments[index]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, segments[index]);
            }

            current = next;
        }
    }

    private static Bounds CombineWorldBounds(Renderer[] renderers)
    {
        if (renderers == null || renderers.Length == 0)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++)
        {
            bounds.Encapsulate(renderers[index].bounds);
        }

        return bounds;
    }

    private static Bounds CombineWorldBounds(Collider[] colliders)
    {
        if (colliders == null || colliders.Length == 0)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        Bounds bounds = colliders[0].bounds;
        for (int index = 1; index < colliders.Length; index++)
        {
            bounds.Encapsulate(colliders[index].bounds);
        }

        return bounds;
    }

    private static float[] ToArray(Vector3 value)
    {
        return new[] { value.x, value.y, value.z };
    }

    private static float NormalizeYaw(float yaw)
    {
        return yaw > 180f ? yaw - 360f : yaw;
    }

    private static string Format(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }
}

[InitializeOnLoad]
internal static class GalleryLayoutBoundaryGizmo
{
    static GalleryLayoutBoundaryGizmo()
    {
        SceneView.duringSceneGui += DrawBoundary;
    }

    private static void DrawBoundary(SceneView sceneView)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (SceneSemanticsValidator.FindRoot(scene, "VirtualGuidance_B_Root") == null)
        {
            return;
        }

        Handles.color = new Color(0.1f, 0.85f, 1f, 0.9f);
        Vector3[] points =
        {
            new Vector3(-6f, 0.02f, 0f),
            new Vector3(6f, 0.02f, 0f),
            new Vector3(6f, 0.02f, 14f),
            new Vector3(-6f, 0.02f, 14f),
            new Vector3(-6f, 0.02f, 0f)
        };
        Handles.DrawAAPolyLine(4f, points);
    }
}
