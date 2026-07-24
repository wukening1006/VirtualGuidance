using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 将 E1-E8 的占位展板替换为玻璃展柜、实体模型和中文铭牌，同时保留导航语义节点。
/// </summary>
[InitializeOnLoad]
public static class ExhibitShowcaseBuilder
{
    private const string MainScenePath = "Assets/Scenes/MainScene.unity";
    private const string MarkerPath = "Temp/ApplyExhibitShowcases.flag";
    private const string BeforeShowcasesScenePath = "Assets/Scenes/Archives/MainScene_Before_ExhibitShowcases.unity";
    private const string ExhibitionPrefabPath = "Assets/Prefabs/Exhibits/Exihibition.prefab";
    private const string ExhibitionPrefabArchivePath = "Assets/Prefabs/Exhibits/Archives/Exihibition_Before_Showcases.prefab";
    private const string ShowcasePrefabPath = "Assets/ThirdParty/Display room/Prefabs/Showing.prefab";
    private const string ChineseFontPath = "Assets/Fonts/NotoSansCJKSC/NotoSansSC Dynamic SDF.asset";
    private const string PlaqueMaterialPath = "Assets/Materials/DG_Panel.mat";
    private const string ShowcaseGlassMaterialPath = "Assets/Materials/DG_ShowcaseGlass.mat";
    private const string VisualRootName = "ShowcaseVisual";
    private const float SideRowInwardOffset = 0.9f;
    private const string EntrancePreviewFileName = "Preview_Exhibits_Showcase_Entrance.png";
    private const string SidePreviewFileName = "Preview_Exhibits_Showcase_Sides.png";
    private const string CorePreviewFileName = "Preview_Exhibits_Showcase_Core.png";

    private const string ComputerModelPath = "Assets/Low Poly Factory/Meshes/Office/SM_Comp_01.fbx";
    private const string Machine01ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Machine_01.fbx";
    private const string Machine02ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Machine_02.fbx";
    private const string Robot01ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Robo_01.fbx";
    private const string Robot02ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Robo_02.fbx";
    private const string RackModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Rack_01.fbx";
    private const string TransporterModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Transporter_01.fbx";
    private const string Forklift01ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Forklift_01.fbx";
    private const string Forklift02ModelPath = "Assets/Low Poly Factory/Meshes/Props/SM_Forklift_02.fbx";

    static ExhibitShowcaseBuilder()
    {
        if (!File.Exists(MarkerPath))
        {
            return;
        }

        File.Delete(MarkerPath);
        EditorApplication.delayCall += BuildShowcasesInMainScene;
    }

    /// <summary>
    /// 打开主场景并生成 E1-E8 实体展柜、展品和中文铭牌。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/生成 E1-E8 实体展柜")]
    public static void BuildShowcasesInMainScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[ExhibitShowcase] 请先退出播放模式再生成实体展柜。");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScenePath)
        {
            scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        }

        if (!File.Exists(BeforeShowcasesScenePath))
        {
            EditorSceneManager.SaveScene(scene, BeforeShowcasesScenePath, true);
        }

        UpdateExhibitionPrefabTemplate();
        ApplyToScene(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        CapturePreview(EntrancePreviewFileName, new Vector3(0f, 1.65f, -2.15f), new Vector3(0f, 0.9f, 7f), 68f);
        CapturePreview(SidePreviewFileName, new Vector3(0f, 2.1f, 3.1f), new Vector3(0f, 0.95f, 6.8f), 78f);
        CapturePreview(CorePreviewFileName, new Vector3(0f, 1.55f, 5.5f), new Vector3(0f, 0.9f, 8.2f), 62f);
        Debug.Log("[ExhibitShowcase] E1-E8 玻璃展柜、实体展品和中文铭牌生成完成。");
    }

    /// <summary>
    /// 在指定场景中重建展柜视觉层，不修改 E1-E8 的导航 ID、到达点和语义关系。
    /// </summary>
    public static void ApplyToScene(Scene scene)
    {
        var definitions = CreateDefinitions().ToDictionary(item => item.Id);
        var metadataList = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<ExhibitMetadata>(true))
            .Where(metadata => metadata != null && definitions.ContainsKey(metadata.Id))
            .OrderBy(metadata => metadata.Id)
            .ToArray();

        if (metadataList.Length != definitions.Count)
        {
            Debug.LogWarning($"[ExhibitShowcase] 预计找到 {definitions.Count} 个展品节点，实际找到 {metadataList.Length} 个。");
        }

        foreach (var metadata in metadataList)
        {
            BuildShowcase(scene, metadata, definitions[metadata.Id]);
        }
    }

    private static void UpdateExhibitionPrefabTemplate()
    {
        var archiveDirectory = Path.GetDirectoryName(ExhibitionPrefabArchivePath);
        if (!Directory.Exists(archiveDirectory))
        {
            Directory.CreateDirectory(archiveDirectory);
            AssetDatabase.Refresh();
        }

        if (!File.Exists(ExhibitionPrefabArchivePath))
        {
            AssetDatabase.CopyAsset(ExhibitionPrefabPath, ExhibitionPrefabArchivePath);
        }

        var prefabRoot = PrefabUtility.LoadPrefabContents(ExhibitionPrefabPath);
        try
        {
            SetInactive(prefabRoot.transform.Find("Display/Model_or_ImageBoard"));
            SetInactive(prefabRoot.transform.Find("Display/TitleText"));
            SetInactive(prefabRoot.transform.Find("Display/DescriptionPanel"));
            if (prefabRoot.transform.Find("ShowcaseMount") == null)
            {
                var mount = new GameObject("ShowcaseMount");
                mount.transform.SetParent(prefabRoot.transform, false);
            }

            PrefabUtility.SaveAsPrefabAsset(prefabRoot, ExhibitionPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }
    }

    private static void BuildShowcase(Scene scene, ExhibitMetadata metadata, ShowcaseDefinition definition)
    {
        HideLegacyDisplay(metadata.transform.Find("Display"));
        HideNavigationMarkers(metadata);
        var previousVisual = metadata.transform.Find(VisualRootName);
        if (previousVisual != null)
        {
            Object.DestroyImmediate(previousVisual.gameObject);
        }

        var visualRoot = new GameObject(VisualRootName);
        visualRoot.transform.SetParent(metadata.transform, false);
        MoveSideRowTowardCenter(visualRoot.transform, metadata.transform.position.x);
        CreateCase(scene, visualRoot.transform, definition.Id);
        CreateModels(scene, visualRoot.transform, definition);
        CreatePlaque(visualRoot.transform, definition);

        metadata.Configure(
            metadata.Id,
            definition.Title,
            metadata.Tags,
            definition.Description,
            metadata.SemanticRole,
            metadata.RelatedExhibits,
            metadata.ViewPoint,
            metadata.GazeTarget);
    }

    private static void HideLegacyDisplay(Transform display)
    {
        if (display == null)
        {
            return;
        }

        SetInactive(display.Find("Model_or_ImageBoard"));
        SetInactive(display.Find("TitleText"));
        SetInactive(display.Find("DescriptionPanel"));
    }

    private static void HideNavigationMarkers(ExhibitMetadata metadata)
    {
        HideRenderers(metadata.GazeTarget);
        HideRenderers(metadata.ViewPoint);
    }

    private static void MoveSideRowTowardCenter(Transform visualRoot, float exhibitWorldX)
    {
        if (Mathf.Abs(exhibitWorldX) < 1f)
        {
            return;
        }

        visualRoot.position += Vector3.left * Mathf.Sign(exhibitWorldX) * SideRowInwardOffset;
    }

    private static void HideRenderers(Transform root)
    {
        if (root == null)
        {
            return;
        }

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = false;
        }
    }

    private static void CreateCase(Scene scene, Transform parent, string exhibitId)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShowcasePrefabPath);
        var instance = InstantiateAsset(prefab, scene, parent, $"{exhibitId}_GlassCase");
        if (instance == null)
        {
            return;
        }

        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;
        FitObjectToBox(instance, parent.TransformPoint(new Vector3(0f, 0.92f, 0f)), new Vector3(1.5f, 1.84f, 1.12f));

        var glassMaterial = GetOrCreateGlassMaterial(instance);
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.name == "Glass" && glassMaterial != null)
            {
                renderer.sharedMaterial = glassMaterial;
            }
        }

        foreach (var displayLight in instance.GetComponentsInChildren<Light>(true))
        {
            displayLight.color = new Color(1f, 0.88f, 0.72f);
            displayLight.intensity = 0.7f;
            displayLight.range = 2.2f;
            displayLight.shadows = LightShadows.None;
        }
    }

    private static void CreateModels(Scene scene, Transform parent, ShowcaseDefinition definition)
    {
        var modelRoot = new GameObject("ExhibitModels");
        modelRoot.transform.SetParent(parent, false);
        foreach (var model in definition.Models)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(model.AssetPath);
            var instance = InstantiateAsset(asset, scene, modelRoot.transform, model.Name);
            if (instance == null)
            {
                continue;
            }

            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.Euler(0f, model.YawDegrees, 0f);
            instance.transform.localScale = Vector3.one;
            FitObjectToBox(instance, parent.TransformPoint(model.LocalCenter), model.TargetSize);
            RemoveModelColliders(instance);
        }
    }

    private static void CreatePlaque(Transform parent, ShowcaseDefinition definition)
    {
        var plaque = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plaque.name = "ChinesePlaque";
        plaque.transform.SetParent(parent, false);
        plaque.transform.localPosition = new Vector3(0f, 0.28f, -0.72f);
        plaque.transform.localRotation = Quaternion.identity;
        plaque.transform.localScale = new Vector3(1.42f, 0.42f, 0.055f);
        plaque.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(PlaqueMaterialPath);

        CreatePlaqueText(parent, "ChinesePlaqueTitle", $"{definition.Id}  {definition.Title}",
            new Vector3(0f, 0.39f, -0.751f), new Vector2(1.3f, 0.15f), 0.36f, FontStyles.Bold);
        CreatePlaqueText(parent, "ChinesePlaqueDescription", definition.Description,
            new Vector3(0f, 0.2f, -0.751f), new Vector2(1.3f, 0.2f), 0.21f, FontStyles.Normal);
    }

    private static void CreatePlaqueText(
        Transform parent,
        string objectName,
        string content,
        Vector3 localPosition,
        Vector2 size,
        float fontSize,
        FontStyles fontStyle)
    {
        var textObject = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshPro));
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localRotation = Quaternion.identity;
        textObject.transform.localScale = Vector3.one;
        var text = textObject.GetComponent<TextMeshPro>();
        text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ChineseFontPath);
        text.text = content;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.enableAutoSizing = true;
        text.fontSizeMin = fontSize * 0.55f;
        text.fontSizeMax = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.rectTransform.sizeDelta = size;
    }

    private static GameObject InstantiateAsset(GameObject asset, Scene scene, Transform parent, string objectName)
    {
        if (asset == null)
        {
            Debug.LogWarning($"[ExhibitShowcase] 未找到展柜或展品资源：{objectName}");
            return null;
        }

        var instance = PrefabUtility.InstantiatePrefab(asset, scene) as GameObject;
        if (instance == null)
        {
            return null;
        }

        instance.name = objectName;
        instance.transform.SetParent(parent, true);
        return instance;
    }

    private static void FitObjectToBox(GameObject instance, Vector3 targetCenter, Vector3 targetSize)
    {
        if (!TryGetRendererBounds(instance, out var bounds))
        {
            return;
        }

        var scale = Mathf.Min(
            SafeRatio(targetSize.x, bounds.size.x),
            SafeRatio(targetSize.y, bounds.size.y),
            SafeRatio(targetSize.z, bounds.size.z));
        instance.transform.localScale *= scale;

        if (TryGetRendererBounds(instance, out bounds))
        {
            instance.transform.position += targetCenter - bounds.center;
        }
    }

    private static bool TryGetRendererBounds(GameObject instance, out Bounds bounds)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>(true)
            .Where(renderer => renderer.enabled)
            .ToArray();
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        foreach (var renderer in renderers.Skip(1))
        {
            bounds.Encapsulate(renderer.bounds);
        }

        return true;
    }

    private static float SafeRatio(float target, float current)
    {
        return current > 0.0001f ? target / current : 1f;
    }

    private static void RemoveModelColliders(GameObject instance)
    {
        foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
        {
            Object.DestroyImmediate(collider);
        }
    }

    private static Material GetOrCreateGlassMaterial(GameObject caseInstance)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(ShowcaseGlassMaterialPath);
        if (existing != null)
        {
            ConfigureGlassMaterial(existing);
            return existing;
        }

        var glassRenderer = caseInstance.GetComponentsInChildren<Renderer>(true)
            .FirstOrDefault(renderer => renderer.name == "Glass");
        if (glassRenderer == null || glassRenderer.sharedMaterial == null)
        {
            return null;
        }

        var material = new Material(glassRenderer.sharedMaterial)
        {
            name = "DG_ShowcaseGlass"
        };
        ConfigureGlassMaterial(material);
        AssetDatabase.CreateAsset(material, ShowcaseGlassMaterialPath);
        return material;
    }

    private static void ConfigureGlassMaterial(Material material)
    {
        if (material.HasProperty("_Color"))
        {
            var color = material.color;
            color.a = 0.1f;
            material.color = color;
        }

        if (material.HasProperty("_Opacity"))
        {
            material.SetFloat("_Opacity", 0.16f);
        }

        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", 0.35f);
        }

        EditorUtility.SetDirty(material);
    }

    private static List<ShowcaseDefinition> CreateDefinitions()
    {
        return new List<ShowcaseDefinition>
        {
            new ShowcaseDefinition("E1", "智造序章", "工业技术从机械化走向数字化与智能化。",
                new ModelSpec("Computer", ComputerModelPath, new Vector3(0f, 0.92f, 0f), new Vector3(0.88f, 0.78f, 0.68f), 180f)),
            new ShowcaseDefinition("E2", "机械化起步", "早期机械设备提高了生产效率与稳定性。",
                new ModelSpec("EarlyMachine", Machine01ModelPath, new Vector3(0f, 0.92f, 0f), new Vector3(0.9f, 0.8f, 0.7f), 180f)),
            new ShowcaseDefinition("E3", "传动与控制", "传动结构与控制机构让机器能够精确协作。",
                new ModelSpec("ControlMachine", Machine02ModelPath, new Vector3(0f, 0.92f, 0f), new Vector3(0.9f, 0.8f, 0.7f), 180f)),
            new ShowcaseDefinition("E4", "数字化转型", "计算机控制让生产过程变得可编程、可监测。",
                new ModelSpec("DigitalController", ComputerModelPath, new Vector3(0f, 0.92f, 0f), new Vector3(0.88f, 0.78f, 0.68f), 180f)),
            new ShowcaseDefinition("E5", "智能机器人", "机器人感知环境并完成自动化协同生产。",
                new ModelSpec("RobotA", Robot01ModelPath, new Vector3(-0.28f, 0.9f, 0f), new Vector3(0.5f, 0.7f, 0.55f), 165f),
                new ModelSpec("RobotB", Robot02ModelPath, new Vector3(0.28f, 0.9f, 0f), new Vector3(0.5f, 0.7f, 0.55f), 195f)),
            new ShowcaseDefinition("E6", "仓储与物流", "仓储系统与运输设备连接生产和交付环节。",
                new ModelSpec("StorageRack", RackModelPath, new Vector3(-0.27f, 0.88f, 0f), new Vector3(0.48f, 0.66f, 0.52f), 180f),
                new ModelSpec("Transporter", TransporterModelPath, new Vector3(0.28f, 0.85f, 0f), new Vector3(0.5f, 0.55f, 0.52f), 180f)),
            new ShowcaseDefinition("E7", "搬运方式对比", "对比不同搬运设备的结构、效率与转向能力。",
                new ModelSpec("ForkliftA", Forklift01ModelPath, new Vector3(-0.28f, 0.86f, 0f), new Vector3(0.5f, 0.6f, 0.52f), 165f),
                new ModelSpec("ForkliftB", Forklift02ModelPath, new Vector3(0.28f, 0.86f, 0f), new Vector3(0.5f, 0.6f, 0.52f), 195f)),
            new ShowcaseDefinition("E8", "智能工厂全景", "机器、机器人与物流系统共同组成智能工厂。",
                new ModelSpec("SummaryMachine", Machine01ModelPath, new Vector3(-0.36f, 0.86f, 0f), new Vector3(0.34f, 0.55f, 0.44f), 170f),
                new ModelSpec("SummaryRobot", Robot02ModelPath, new Vector3(0f, 0.88f, 0f), new Vector3(0.34f, 0.58f, 0.44f), 180f),
                new ModelSpec("SummaryTransport", TransporterModelPath, new Vector3(0.36f, 0.84f, 0f), new Vector3(0.34f, 0.48f, 0.44f), 190f))
        };
    }

    private static void CapturePreview(string fileName, Vector3 cameraPosition, Vector3 lookTarget, float fieldOfView)
    {
        var cameraObject = new GameObject("__ExhibitShowcasePreviewCamera");
        var previewCamera = cameraObject.AddComponent<Camera>();
        previewCamera.transform.position = cameraPosition;
        previewCamera.transform.LookAt(lookTarget);
        previewCamera.fieldOfView = fieldOfView;
        previewCamera.nearClipPlane = 0.05f;
        previewCamera.farClipPlane = 100f;

        var renderTexture = new RenderTexture(1600, 900, 24);
        var texture = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        var previousActive = RenderTexture.active;
        previewCamera.targetTexture = renderTexture;
        previewCamera.Render();
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
        texture.Apply();
        File.WriteAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName, fileName), texture.EncodeToPNG());

        previewCamera.targetTexture = null;
        RenderTexture.active = previousActive;
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(renderTexture);
        Object.DestroyImmediate(cameraObject);
    }

    private static void SetInactive(Transform target)
    {
        if (target != null)
        {
            target.gameObject.SetActive(false);
        }
    }

    private sealed class ShowcaseDefinition
    {
        public ShowcaseDefinition(string id, string title, string description, params ModelSpec[] models)
        {
            Id = id;
            Title = title;
            Description = description;
            Models = models;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public ModelSpec[] Models { get; }
    }

    private sealed class ModelSpec
    {
        public ModelSpec(string name, string assetPath, Vector3 localCenter, Vector3 targetSize, float yawDegrees)
        {
            Name = name;
            AssetPath = assetPath;
            LocalCenter = localCenter;
            TargetSize = targetSize;
            YawDegrees = yawDegrees;
        }

        public string Name { get; }
        public string AssetPath { get; }
        public Vector3 LocalCenter { get; }
        public Vector3 TargetSize { get; }
        public float YawDegrees { get; }
    }
}
