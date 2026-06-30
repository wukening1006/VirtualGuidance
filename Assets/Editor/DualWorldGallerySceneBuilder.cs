using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 一次性场景生成器：创建 DualWorldGallery 实验展厅骨架、展品、metadata、UI 和管理器占位。
public static class DualWorldGallerySceneBuilder
{
    private const string ScenePath = "Assets/Scenes/DualWorldGallery.unity";
    private const string ExhibitPrefabPath = "Assets/Prefarbs/Exihibition.prefab";
    private const string MaterialsFolder = "Assets/Materials";

    private static readonly ExhibitSpec[] Exhibits =
    {
        new ExhibitSpec("E1", "Introduction", "introduction", new Vector3(0f, 0f, 2f), new Vector3(0f, 0f, 1.2f), new Vector3(0f, 1.5f, 2f), new[] {"intro", "overview", "start"}, new[] {"E2", "E3", "E4"}, "An introductory exhibit that gives visitors an overview of the gallery.", ExhibitLane.Center),
        new ExhibitSpec("E2", "Early Development", "forward_next", new Vector3(0f, 0f, 4.2f), new Vector3(0f, 0f, 3.3f), new Vector3(0f, 1.5f, 4.2f), new[] {"early", "development", "timeline"}, new[] {"E1", "E3", "E5"}, "A natural next exhibit that continues the main storyline.", ExhibitLane.Center),
        new ExhibitSpec("E3", "Related Mechanism", "left_related_anchor", new Vector3(-3.6f, 0f, 4.8f), new Vector3(-2.5f, 0f, 4.8f), new Vector3(-3.6f, 1.5f, 4.8f), new[] {"mechanism", "related", "left_redirect"}, new[] {"E1", "E2", "E6"}, "A related exhibit on the left that can naturally redirect the visitor.", ExhibitLane.Left),
        new ExhibitSpec("E4", "Alternative Example", "right_alternative_anchor", new Vector3(3.6f, 0f, 4.8f), new Vector3(2.5f, 0f, 4.8f), new Vector3(3.6f, 1.5f, 4.8f), new[] {"alternative", "example", "right_redirect"}, new[] {"E1", "E2", "E7"}, "An alternative exhibit on the right that supports safe redirection.", ExhibitLane.Right),
        new ExhibitSpec("E5", "Key Target", "main_target", new Vector3(0f, 0f, 7.8f), new Vector3(0f, 0f, 6.8f), new Vector3(0f, 1.5f, 7.8f), new[] {"key", "important", "main"}, new[] {"E2", "E6", "E7", "E8"}, "The most important exhibit in the gallery.", ExhibitLane.Center),
        new ExhibitSpec("E6", "Side Context", "side_context", new Vector3(-3.8f, 0f, 8.2f), new Vector3(-2.7f, 0f, 8.2f), new Vector3(-3.8f, 1.5f, 8.2f), new[] {"context", "review", "reset_camouflage"}, new[] {"E3", "E5", "E8"}, "A side-context exhibit that supports reviewing and redirection.", ExhibitLane.Left),
        new ExhibitSpec("E7", "Comparison Object", "comparison_object", new Vector3(3.8f, 0f, 8.2f), new Vector3(2.7f, 0f, 8.2f), new Vector3(3.8f, 1.5f, 8.2f), new[] {"comparison", "contrast", "turn"}, new[] {"E4", "E5", "E8"}, "A comparison exhibit that supports contrastive guidance and turning.", ExhibitLane.Right),
        new ExhibitSpec("E8", "Summary", "summary", new Vector3(0f, 0f, 10.6f), new Vector3(0f, 0f, 9.6f), new Vector3(0f, 1.5f, 10.6f), new[] {"summary", "ending", "exit"}, new[] {"E5", "E6", "E7"}, "A summary exhibit near the end of the gallery.", ExhibitLane.Center)
    };

    [MenuItem("RDW/Dual World Gallery/Build Scene")]
    public static void BuildScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Scripts");
        Directory.CreateDirectory("Assets/Configs");
        Directory.CreateDirectory(MaterialsFolder);

        var materials = CreateMaterials();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "DualWorldGallery";

        var root = CreateEmpty("DualWorldGallery");
        var environment = CreateEmpty("Environment", root.transform);
        var user = CreateEmpty("User", root.transform);
        var guideAvatar = CreateEmpty("GuideAvatar");
        var exhibits = CreateEmpty("Exhibits", root.transform);
        var managers = CreateEmpty("Managers", root.transform);
        var ui = CreateEmpty("UI");

        BuildEnvironment(environment.transform, materials);
        BuildUser(user.transform, materials);
        BuildGuideAvatar(guideAvatar.transform, materials);
        BuildExhibits(exhibits.transform, materials);
        BuildManagers(managers.transform);
        BuildUi(ui.transform);
        BuildLighting();

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"DualWorldGallery scene generated at {ScenePath}");
    }

    [MenuItem("RDW/Dual World Gallery/Convert Exhibits To Prefab")]
    public static void ConvertExhibitsToPrefab()
    {
        Directory.CreateDirectory("Assets/Prefarbs");
        var materials = CreateMaterials();
        CreateOrUpdateExhibitPrefab(materials);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var exhibitsRoot = GameObject.Find("Exhibits");
        if (exhibitsRoot == null)
        {
            var galleryRoot = GameObject.Find("DualWorldGallery");
            exhibitsRoot = CreateEmpty("Exhibits", galleryRoot != null ? galleryRoot.transform : null);
        }

        for (var i = exhibitsRoot.transform.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(exhibitsRoot.transform.GetChild(i).gameObject);
        }

        var exhibitPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ExhibitPrefabPath);
        if (exhibitPrefab == null)
        {
            throw new FileNotFoundException($"Exhibit prefab was not created at {ExhibitPrefabPath}");
        }

        foreach (var spec in Exhibits)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(exhibitPrefab, exhibitsRoot.transform);
            ApplyExhibitSpec(instance, spec, materials);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"Converted {Exhibits.Length} exhibits to prefab instances from {ExhibitPrefabPath}");
    }

    private static void CreateOrUpdateExhibitPrefab(IReadOnlyDictionary<string, Material> materials)
    {
        var prefabRoot = PrefabUtility.LoadPrefabContents(ExhibitPrefabPath);
        if (prefabRoot == null)
        {
            prefabRoot = new GameObject("Exhibit_Template");
        }

        prefabRoot.name = "Exhibit_Template";
        prefabRoot.transform.localPosition = Vector3.zero;
        prefabRoot.transform.localRotation = Quaternion.identity;
        prefabRoot.transform.localScale = Vector3.one;

        for (var i = prefabRoot.transform.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(prefabRoot.transform.GetChild(i).gameObject);
        }

        var display = CreateEmpty("Display", prefabRoot.transform);
        display.transform.localPosition = Vector3.zero;

        CreatePrimitiveLocal("Model_or_ImageBoard", PrimitiveType.Cube, display.transform, new Vector3(0f, 1.05f, 0f), Quaternion.identity, new Vector3(1.1f, 1.25f, 0.08f), materials["center"]);
        CreateTextLocal("TitleText", display.transform, "Exhibit Title", new Vector3(0f, 1.85f, 0.08f), Quaternion.identity, 0.38f, Color.white, TextAlignmentOptions.Center);
        var descriptionPanel = CreatePrimitiveLocal("DescriptionPanel", PrimitiveType.Cube, display.transform, new Vector3(0f, 0.42f, 0.08f), Quaternion.identity, new Vector3(1.25f, 0.42f, 0.05f), materials["panel"]);
        CreateTextLocal("DescriptionText", descriptionPanel.transform, "Exhibit description.", new Vector3(0f, 0.01f, 0.04f), Quaternion.identity, 0.12f, Color.white, TextAlignmentOptions.Center);

        var viewPoint = CreateEmpty("ViewPoint", prefabRoot.transform);
        viewPoint.transform.localPosition = new Vector3(0f, 0f, -0.8f);
        CreatePrimitiveLocal("ViewPointMarker", PrimitiveType.Cylinder, viewPoint.transform, new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(0.28f, 0.03f, 0.28f), materials["viewPoint"]);

        var gazeTarget = CreateEmpty("GazeTarget", prefabRoot.transform);
        gazeTarget.transform.localPosition = new Vector3(0f, 1.5f, 0f);
        CreatePrimitiveLocal("GazeTargetMarker", PrimitiveType.Sphere, gazeTarget.transform, Vector3.zero, Quaternion.identity, new Vector3(0.18f, 0.18f, 0.18f), materials["gazeTarget"]);

        var metadata = prefabRoot.GetComponent<ExhibitMetadata>();
        if (metadata == null)
        {
            metadata = prefabRoot.AddComponent<ExhibitMetadata>();
        }

        metadata.Configure(string.Empty, string.Empty, new string[0], string.Empty, string.Empty, new string[0], viewPoint.transform, gazeTarget.transform);

        EditorUtility.SetDirty(prefabRoot);
        PrefabUtility.SaveAsPrefabAsset(prefabRoot, ExhibitPrefabPath);
        PrefabUtility.UnloadPrefabContents(prefabRoot);
    }

    private static void ApplyExhibitSpec(GameObject instance, ExhibitSpec spec, IReadOnlyDictionary<string, Material> materials)
    {
        instance.name = $"Exhibit_{spec.Id}_{CompactName(spec.Name)}";
        instance.transform.localPosition = spec.Position;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        var facingRotation = LookAtYaw(spec.Position, spec.ViewPoint);
        var display = FindRequiredChild(instance.transform, "Display");
        display.localPosition = Vector3.zero;
        display.localRotation = facingRotation;

        var exhibitMaterial = spec.Lane == ExhibitLane.Left ? materials["left"] : spec.Lane == ExhibitLane.Right ? materials["right"] : materials["center"];
        SetRendererMaterial(FindRequiredChild(display, "Model_or_ImageBoard"), exhibitMaterial);
        SetRendererMaterial(FindRequiredChild(display, "DescriptionPanel"), materials["panel"]);
        SetText(FindRequiredChild(display, "TitleText"), $"{spec.Id} {spec.Name}", 0.38f, new Vector2(1.3f, 0.35f));

        var descriptionPanel = FindRequiredChild(display, "DescriptionPanel");
        var descriptionText = FindRequiredChild(descriptionPanel, "DescriptionText");
        descriptionText.localPosition = new Vector3(0f, 0.01f, 0.04f);
        SetText(descriptionText, spec.Description, 0.12f, new Vector2(1.05f, 0.38f));

        var viewPoint = FindRequiredChild(instance.transform, "ViewPoint");
        viewPoint.localPosition = spec.ViewPoint - spec.Position;
        PrefabUtility.RecordPrefabInstancePropertyModifications(viewPoint);

        var gazeTarget = FindRequiredChild(instance.transform, "GazeTarget");
        gazeTarget.localPosition = spec.GazeTarget - spec.Position;
        PrefabUtility.RecordPrefabInstancePropertyModifications(gazeTarget);

        var metadata = instance.GetComponent<ExhibitMetadata>();
        if (metadata == null)
        {
            metadata = instance.AddComponent<ExhibitMetadata>();
        }

        metadata.Configure(spec.Id, spec.Name, spec.Tags, spec.Description, spec.SemanticRole, spec.RelatedExhibits, viewPoint, gazeTarget);
        PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
        PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(display);
        PrefabUtility.RecordPrefabInstancePropertyModifications(metadata);
    }

    private static Dictionary<string, Material> CreateMaterials()
    {
        return new Dictionary<string, Material>
        {
            ["floor"] = CreateMaterial("DG_Floor", new Color(0.72f, 0.74f, 0.73f)),
            ["wall"] = CreateMaterial("DG_Wall", new Color(0.28f, 0.32f, 0.36f)),
            ["center"] = CreateMaterial("DG_Exhibit_Center", new Color(0.18f, 0.42f, 0.78f)),
            ["left"] = CreateMaterial("DG_Exhibit_Left", new Color(0.18f, 0.58f, 0.36f)),
            ["right"] = CreateMaterial("DG_Exhibit_Right", new Color(0.92f, 0.48f, 0.16f)),
            ["viewPoint"] = CreateMaterial("DG_ViewPoint", new Color(0.0f, 0.85f, 0.95f, 0.55f)),
            ["gazeTarget"] = CreateMaterial("DG_GazeTarget", new Color(1f, 0.86f, 0.12f)),
            ["avatar"] = CreateMaterial("DG_GuideAvatar", new Color(0.62f, 0.42f, 0.86f)),
            ["panel"] = CreateMaterial("DG_Panel", new Color(0.12f, 0.12f, 0.13f)),
            ["line"] = CreateMaterial("DG_CenterLine", new Color(0.08f, 0.12f, 0.16f))
        };
    }

    private static Material CreateMaterial(string name, Color color)
    {
        var path = $"{MaterialsFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        if (color.a < 1f)
        {
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = 3000;
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    private static void BuildEnvironment(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        CreatePrimitive("Floor_12x12", PrimitiveType.Cube, parent, new Vector3(0f, -0.025f, 6f), Quaternion.identity, new Vector3(12f, 0.05f, 12f), materials["floor"]);
        CreatePrimitive("Wall_North", PrimitiveType.Cube, parent, new Vector3(0f, 0.6f, 12f), Quaternion.identity, new Vector3(12f, 1.2f, 0.12f), materials["wall"]);
        CreatePrimitive("Wall_South", PrimitiveType.Cube, parent, new Vector3(0f, 0.6f, 0f), Quaternion.identity, new Vector3(12f, 1.2f, 0.12f), materials["wall"]);
        CreatePrimitive("Wall_East", PrimitiveType.Cube, parent, new Vector3(6f, 0.6f, 6f), Quaternion.identity, new Vector3(0.12f, 1.2f, 12f), materials["wall"]);
        CreatePrimitive("Wall_West", PrimitiveType.Cube, parent, new Vector3(-6f, 0.6f, 6f), Quaternion.identity, new Vector3(0.12f, 1.2f, 12f), materials["wall"]);
        CreatePrimitive("CenterRoute_DebugLine", PrimitiveType.Cube, parent, new Vector3(0f, 0.012f, 6.4f), Quaternion.identity, new Vector3(0.05f, 0.025f, 10.4f), materials["line"]);
    }

    private static void BuildUser(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var rig = CreateEmpty("XR_Origin_or_UserStart", parent);
        rig.transform.position = new Vector3(0f, 0f, 1f);

        var userStart = CreateEmpty("UserStart", rig.transform);
        userStart.transform.localPosition = Vector3.zero;
        CreatePrimitive("UserStartMarker", PrimitiveType.Cylinder, userStart.transform, new Vector3(0f, 0.03f, 1f), Quaternion.identity, new Vector3(0.45f, 0.03f, 0.45f), materials["viewPoint"]);

        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(rig.transform, false);
        cameraObject.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        cameraObject.transform.localRotation = Quaternion.identity;
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 65f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 80f;
        cameraObject.AddComponent<AudioListener>();
    }

    private static void BuildGuideAvatar(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        parent.position = new Vector3(1.2f, 0f, 1.5f);
        CreatePrimitive("Body", PrimitiveType.Capsule, parent, parent.position + new Vector3(0f, 0.8f, 0f), Quaternion.identity, new Vector3(0.38f, 0.7f, 0.38f), materials["avatar"]);
        CreatePrimitive("Head", PrimitiveType.Sphere, parent, parent.position + new Vector3(0f, 1.55f, 0f), Quaternion.identity, new Vector3(0.35f, 0.35f, 0.35f), materials["avatar"]);
        CreatePrimitive("PointingArm_Debug", PrimitiveType.Cylinder, parent, parent.position + new Vector3(-0.38f, 1.12f, 0.2f), Quaternion.Euler(0f, 0f, 82f), new Vector3(0.07f, 0.55f, 0.07f), materials["avatar"]);
    }

    private static void BuildExhibits(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        foreach (var spec in Exhibits)
        {
            var exhibit = CreateEmpty($"Exhibit_{spec.Id}_{CompactName(spec.Name)}", parent);
            exhibit.transform.position = spec.Position;

            var exhibitMaterial = spec.Lane == ExhibitLane.Left ? materials["left"] : spec.Lane == ExhibitLane.Right ? materials["right"] : materials["center"];
            var facingRotation = LookAtYaw(spec.Position, spec.ViewPoint);
            var facingDirection = (spec.ViewPoint - spec.Position).normalized;

            CreatePrimitive("Model_or_ImageBoard", PrimitiveType.Cube, exhibit.transform, spec.Position + new Vector3(0f, 1.05f, 0f), facingRotation, new Vector3(1.1f, 1.25f, 0.08f), exhibitMaterial);
            CreateText("TitleText", exhibit.transform, $"{spec.Id} {spec.Name}", spec.Position + new Vector3(0f, 1.85f, 0f) + facingDirection * 0.08f, facingRotation, 0.38f, Color.white, TextAlignmentOptions.Center);
            CreatePrimitive("DescriptionPanel", PrimitiveType.Cube, exhibit.transform, spec.Position + new Vector3(0f, 0.42f, 0f) + facingDirection * 0.08f, facingRotation, new Vector3(1.25f, 0.42f, 0.05f), materials["panel"]);
            CreateText("DescriptionText", exhibit.transform, spec.Description, spec.Position + new Vector3(0f, 0.43f, 0f) + facingDirection * 0.12f, facingRotation, 0.12f, Color.white, TextAlignmentOptions.Center);

            var viewPoint = CreateEmpty("ViewPoint", exhibit.transform);
            viewPoint.transform.position = spec.ViewPoint;
            CreatePrimitive("ViewPointMarker", PrimitiveType.Cylinder, viewPoint.transform, spec.ViewPoint + new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(0.28f, 0.03f, 0.28f), materials["viewPoint"]);

            var gazeTarget = CreateEmpty("GazeTarget", exhibit.transform);
            gazeTarget.transform.position = spec.GazeTarget;
            CreatePrimitive("GazeTargetMarker", PrimitiveType.Sphere, gazeTarget.transform, spec.GazeTarget, Quaternion.identity, new Vector3(0.18f, 0.18f, 0.18f), materials["gazeTarget"]);

            var metadata = exhibit.AddComponent<ExhibitMetadata>();
            metadata.Configure(spec.Id, spec.Name, spec.Tags, spec.Description, spec.SemanticRole, spec.RelatedExhibits, viewPoint.transform, gazeTarget.transform);
        }
    }

    private static void BuildManagers(Transform parent)
    {
        string[] managerNames =
        {
            "SceneSemanticExporter",
            "PhysicalSpaceManager",
            "BoundaryVisualizer",
            "UserPoseTracker",
            "AgentClient",
            "ExperimentDashboard",
            "ExperimentLogger"
        };

        foreach (var managerName in managerNames)
        {
            CreateEmpty(managerName, parent);
        }
    }

    private static void BuildUi(Transform parent)
    {
        var canvasObject = new GameObject("ResearcherCanvas");
        canvasObject.transform.SetParent(parent, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();

        CreateUiPanel(canvasObject.transform, "TaskPanel", "Task: T2_main_target\nRequest: Take me to the most important exhibit.", new Vector2(16f, -16f), new Vector2(360f, 86f), TextAnchor.UpperLeft);
        CreateUiPanel(canvasObject.transform, "RiskDebugPanel", "Physical Space: wide_room\nRisk: placeholder\nSafe Directions: placeholder", new Vector2(16f, -112f), new Vector2(360f, 106f), TextAnchor.UpperLeft);
        CreateUiPanel(canvasObject.transform, "GuidanceTextPanel", "Guidance: waiting for approved guidance JSON.", new Vector2(16f, 16f), new Vector2(520f, 78f), TextAnchor.LowerLeft);
        CreateUiPanel(canvasObject.transform, "ResearcherDebugPanel", "Condition: ours_full\nLogger: placeholder\nBackend: mock/rule-based later", new Vector2(-16f, -16f), new Vector2(360f, 106f), TextAnchor.UpperRight);
    }

    private static void CreateUiPanel(Transform canvas, string name, string text, Vector2 offset, Vector2 size, TextAnchor anchor)
    {
        var panel = new GameObject(name);
        panel.transform.SetParent(canvas, false);
        var rect = panel.AddComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.anchorMin = AnchorMin(anchor);
        rect.anchorMax = AnchorMax(anchor);
        rect.pivot = Pivot(anchor);
        rect.anchoredPosition = offset;

        var image = panel.AddComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.58f);

        var label = new GameObject($"{name}_Text");
        label.transform.SetParent(panel.transform, false);
        var labelRect = label.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(10f, 8f);
        labelRect.offsetMax = new Vector2(-10f, -8f);

        var textComponent = label.AddComponent<TextMeshProUGUI>();
        textComponent.text = text;
        textComponent.fontSize = 18f;
        textComponent.color = Color.white;
        textComponent.alignment = TextAlignmentOptions.TopLeft;
        textComponent.enableWordWrapping = true;
    }

    private static Vector2 AnchorMin(TextAnchor anchor)
    {
        return anchor switch
        {
            TextAnchor.UpperRight => new Vector2(1f, 1f),
            TextAnchor.LowerLeft => new Vector2(0f, 0f),
            _ => new Vector2(0f, 1f)
        };
    }

    private static Vector2 AnchorMax(TextAnchor anchor)
    {
        return AnchorMin(anchor);
    }

    private static Vector2 Pivot(TextAnchor anchor)
    {
        return anchor switch
        {
            TextAnchor.UpperRight => new Vector2(1f, 1f),
            TextAnchor.LowerLeft => new Vector2(0f, 0f),
            _ => new Vector2(0f, 1f)
        };
    }

    private static void BuildLighting()
    {
        var lightObject = new GameObject("Directional Light");
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);
    }

    private static GameObject CreateEmpty(string name, Transform parent = null)
    {
        var gameObject = new GameObject(name);
        if (parent != null)
        {
            gameObject.transform.SetParent(parent, false);
        }

        return gameObject;
    }

    private static GameObject CreatePrimitive(string name, PrimitiveType primitiveType, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
    {
        var gameObject = GameObject.CreatePrimitive(primitiveType);
        gameObject.name = name;
        gameObject.transform.position = position;
        gameObject.transform.rotation = rotation;
        gameObject.transform.localScale = scale;
        if (parent != null)
        {
            gameObject.transform.SetParent(parent, true);
        }

        var renderer = gameObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
        }

        return gameObject;
    }

    private static GameObject CreateText(string name, Transform parent, string text, Vector3 position, Quaternion rotation, float fontSize, Color color, TextAlignmentOptions alignment)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.position = position;
        gameObject.transform.rotation = rotation;
        gameObject.transform.SetParent(parent, true);
        var textMesh = gameObject.AddComponent<TextMeshPro>();
        textMesh.text = text;
        textMesh.fontSize = fontSize;
        textMesh.color = color;
        textMesh.alignment = alignment;
        textMesh.enableWordWrapping = true;
        textMesh.rectTransform.sizeDelta = name == "DescriptionText" ? new Vector2(1.05f, 0.38f) : new Vector2(1.3f, 0.35f);
        return gameObject;
    }

    private static GameObject CreatePrimitiveLocal(string name, PrimitiveType primitiveType, Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Material material)
    {
        var gameObject = GameObject.CreatePrimitive(primitiveType);
        gameObject.name = name;
        gameObject.transform.SetParent(parent, false);
        gameObject.transform.localPosition = localPosition;
        gameObject.transform.localRotation = localRotation;
        gameObject.transform.localScale = localScale;

        var renderer = gameObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
        }

        return gameObject;
    }

    private static GameObject CreateTextLocal(string name, Transform parent, string text, Vector3 localPosition, Quaternion localRotation, float fontSize, Color color, TextAlignmentOptions alignment)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        gameObject.transform.localPosition = localPosition;
        gameObject.transform.localRotation = localRotation;
        gameObject.transform.localScale = Vector3.one;
        var textMesh = gameObject.AddComponent<TextMeshPro>();
        textMesh.text = text;
        textMesh.fontSize = fontSize;
        textMesh.color = color;
        textMesh.alignment = alignment;
        textMesh.enableWordWrapping = true;
        textMesh.rectTransform.sizeDelta = name == "DescriptionText" ? new Vector2(1.05f, 0.38f) : new Vector2(1.3f, 0.35f);
        return gameObject;
    }

    private static Transform FindRequiredChild(Transform parent, string childName)
    {
        var child = parent.Find(childName);
        if (child == null)
        {
            throw new MissingReferenceException($"Missing required child '{childName}' under '{parent.name}'.");
        }

        return child;
    }

    private static void SetRendererMaterial(Transform transform, Material material)
    {
        var renderer = transform.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
    }

    private static void SetText(Transform transform, string value, float fontSize, Vector2 size)
    {
        var textMesh = transform.GetComponent<TextMeshPro>();
        if (textMesh == null)
        {
            throw new MissingReferenceException($"Missing TextMeshPro component on '{transform.name}'.");
        }

        textMesh.text = value;
        textMesh.fontSize = fontSize;
        textMesh.color = Color.white;
        textMesh.alignment = TextAlignmentOptions.Center;
        textMesh.enableWordWrapping = true;
        textMesh.rectTransform.sizeDelta = size;
        EditorUtility.SetDirty(textMesh);
        PrefabUtility.RecordPrefabInstancePropertyModifications(textMesh);
    }

    private static Quaternion LookAtYaw(Vector3 from, Vector3 to)
    {
        var direction = to - from;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return Quaternion.identity;
        }

        return Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    private static string CompactName(string name)
    {
        return name.Replace(" ", string.Empty);
    }

    private enum ExhibitLane
    {
        Center,
        Left,
        Right
    }

    private readonly struct ExhibitSpec
    {
        public ExhibitSpec(string id, string name, string semanticRole, Vector3 position, Vector3 viewPoint, Vector3 gazeTarget, string[] tags, string[] relatedExhibits, string description, ExhibitLane lane)
        {
            Id = id;
            Name = name;
            SemanticRole = semanticRole;
            Position = position;
            ViewPoint = viewPoint;
            GazeTarget = gazeTarget;
            Tags = tags;
            RelatedExhibits = relatedExhibits;
            Description = description;
            Lane = lane;
        }

        public string Id { get; }
        public string Name { get; }
        public string SemanticRole { get; }
        public Vector3 Position { get; }
        public Vector3 ViewPoint { get; }
        public Vector3 GazeTarget { get; }
        public string[] Tags { get; }
        public string[] RelatedExhibits { get; }
        public string Description { get; }
        public ExhibitLane Lane { get; }
    }
}
