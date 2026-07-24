using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// 生成 B 部分第一版内容场景：复用 room 建筑，保留系统对象和关键标识。
/// </summary>
public static class BSceneV1Builder
{
    private const string MainScenePath = "Assets/Scenes/MainScene.unity";
    private const string MainSceneArchivePath = "Assets/Scenes/Archives/MainScene_Before_B_V1.unity";
    private const string RoomPrefabPath = "Assets/Room（ours）.prefab";
    private const string BenchPrefabPath = "Assets/Showroom Environment - gallery/models/prefab/bench.prefab";
    private const string FlowerPrefabPath = "Assets/Showroom Environment - gallery/models/prefab/flower.prefab";
    private const string PackageFloorMaterialPath = "Assets/Display room/Materials/Architecture/Ground_Wood.mat";
    private const string PackageWallMaterialPath = "Assets/Display room/Materials/Architecture/Walls 2.mat";
    private const string PackageWhiteMaterialPath = "Assets/Display room/Materials/Architecture/white.mat";
    private const string ContentRootName = "B_Content_V1";
    private const string ContentPrefabPath = "Assets/Prefabs/BContent/Environment_V1.prefab";
    private const string PreviewPath = "Preview_BScene_V1.png";

    private static readonly Color DeepNavy = new Color(0.035f, 0.075f, 0.12f, 1f);
    private static readonly Color Teal = new Color(0.05f, 0.46f, 0.50f, 1f);
    private static readonly Color WarmGold = new Color(1f, 0.69f, 0.25f, 1f);
    private static readonly Color WarmWhite = new Color(0.92f, 0.90f, 0.84f, 1f);
    private static readonly Color Stone = new Color(0.34f, 0.37f, 0.39f, 1f);

    /// <summary>
    /// 构建并保存第一版 B 内容场景。
    /// </summary>
    [MenuItem("VirtualGuidance/B 部分/生成场景美化 V1 _F6")]
    public static void BuildSceneV1()
    {
        var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        CreateSceneArchive();
        RemovePreviousContent();
        PreserveSystemGeometry();

        var contentRoot = new GameObject(ContentRootName);
        var materials = CreateMaterials();

        BuildRoomShell(contentRoot.transform);
        BuildFloorAndRoute(contentRoot.transform, materials);
        BuildGalleryShell(contentRoot.transform, materials);
        BuildLighting(contentRoot.transform, materials);
        ArrangeExhibitsForGallery();
        BuildExhibitAccents(contentRoot.transform, materials);
        BuildFurniture(contentRoot.transform);
        BuildEntranceIdentity(contentRoot.transform, materials);
        HideGuideAvatarVisuals();
        StyleParticipantUi();
        ConfigureRenderSettings();

        SaveContentPrefab(contentRoot);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = contentRoot;
        Debug.Log("[BSceneV1] 场景美化 V1 已生成并保存。");
    }

    /// <summary>
    /// 从参与者视角生成 1600x900 预览图。
    /// </summary>
    [MenuItem("VirtualGuidance/B 部分/生成场景 V1 预览图 _F7")]
    public static void CapturePreview()
    {
        EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);

        var cameraObject = new GameObject("B_PreviewCamera_Temporary");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 2.15f, -1.8f);
        camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.55f, 6.3f) - camera.transform.position);
        camera.fieldOfView = 62f;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 120f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.allowHDR = true;

        var renderTexture = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = renderTexture;
        camera.Render();

        RenderTexture.active = renderTexture;
        var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
        texture.Apply();

        var outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", PreviewPath));
        File.WriteAllBytes(outputPath, texture.EncodeToPNG());

        camera.targetTexture = null;
        RenderTexture.active = null;
        UnityEngine.Object.DestroyImmediate(texture);
        UnityEngine.Object.DestroyImmediate(renderTexture);
        UnityEngine.Object.DestroyImmediate(cameraObject);
        Debug.Log($"[BSceneV1] 预览图已保存：{outputPath}");
    }

    private static void CreateSceneArchive()
    {
        EnsureAssetFolder("Assets/Scenes/Archives");
        if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(MainSceneArchivePath))
        {
            AssetDatabase.CopyAsset(MainScenePath, MainSceneArchivePath);
            AssetDatabase.SaveAssets();
        }
    }

    private static void RemovePreviousContent()
    {
        var previous = GameObject.Find(ContentRootName);
        if (previous != null)
        {
            UnityEngine.Object.DestroyImmediate(previous);
        }
    }

    private static void PreserveSystemGeometry()
    {
        var environment = GameObject.Find("Environment");
        if (environment == null)
        {
            return;
        }

        foreach (var renderer in environment.GetComponentsInChildren<MeshRenderer>(true))
        {
            renderer.enabled = false;
        }

        var legacyLight = environment.GetComponentInChildren<Light>(true);
        if (legacyLight != null)
        {
            legacyLight.enabled = false;
        }
    }

    private static void BuildRoomShell(Transform parent)
    {
        var roomPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath);
        if (roomPrefab == null)
        {
            Debug.LogError($"[BSceneV1] 找不到 room 预制体：{RoomPrefabPath}");
            return;
        }

        var roomShell = UnityEngine.Object.Instantiate(roomPrefab, parent);
        roomShell.name = "Room_Visual_From_Package";
        roomShell.transform.localPosition = Vector3.zero;
        roomShell.transform.localRotation = Quaternion.identity;
        roomShell.transform.localScale = Vector3.one;

        DisableImportedRuntimeComponents(roomShell);
        NormalizeToMainArea(roomShell, new Vector3(0f, 0f, 8.65f), 7.6f, 5.2f);
    }

    private static void DisableImportedRuntimeComponents(GameObject root)
    {
        foreach (var item in root.GetComponentsInChildren<Transform>(true))
        {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(item.gameObject);
        }

        foreach (var camera in root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (var canvas in root.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
        foreach (var light in root.GetComponentsInChildren<Light>(true)) light.enabled = false;
        foreach (var audioSource in root.GetComponentsInChildren<AudioSource>(true)) audioSource.enabled = false;
        foreach (var player in root.GetComponentsInChildren<VideoPlayer>(true)) player.enabled = false;
        foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null) behaviour.enabled = false;
        }
        foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
    }

    private static void NormalizeToMainArea(GameObject root, Vector3 targetCenter, float targetWidth, float targetDepth)
    {
        if (!TryGetRendererBounds(root, out var bounds))
        {
            return;
        }

        var scaleX = targetWidth / Mathf.Max(0.01f, bounds.size.x);
        var scaleZ = targetDepth / Mathf.Max(0.01f, bounds.size.z);
        var uniformScale = Mathf.Min(scaleX, scaleZ);
        root.transform.localScale = Vector3.one * uniformScale;

        if (!TryGetRendererBounds(root, out bounds))
        {
            return;
        }

        var offset = targetCenter - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        root.transform.position += offset;
    }

    private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
    {
        bounds = new Bounds();
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        var hasBounds = false;
        foreach (var renderer in renderers)
        {
            if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer || renderer is LineRenderer)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private static Dictionary<string, Material> CreateMaterials()
    {
        EnsureAssetFolder("Assets/Materials/BSceneV1");
        var materials = new Dictionary<string, Material>
        {
            ["floor"] = CreateOrUpdateMaterial("B_Floor_Stone", new Color(0.23f, 0.25f, 0.27f), 0.12f, 0.42f),
            ["route"] = CreateOrUpdateMaterial("B_Route_Teal", new Color(0.035f, 0.23f, 0.26f), 0.25f, 0.58f),
            ["gold"] = CreateOrUpdateMaterial("B_Accent_Gold", WarmGold, 0.55f, 0.72f, WarmGold * 0.45f),
            ["stone"] = CreateOrUpdateMaterial("B_Plinth_Stone", Stone, 0.08f, 0.35f),
            ["emissive"] = CreateOrUpdateMaterial("B_Ceiling_Emissive", WarmWhite, 0.05f, 0.48f, WarmWhite * 1.25f),
            ["navy"] = CreateOrUpdateMaterial("B_Entrance_Navy", DeepNavy, 0.18f, 0.55f)
        };

        materials["packageFloor"] = AssetDatabase.LoadAssetAtPath<Material>(PackageFloorMaterialPath) ?? materials["floor"];
        materials["packageWall"] = AssetDatabase.LoadAssetAtPath<Material>(PackageWallMaterialPath) ?? materials["stone"];
        materials["packageWhite"] = AssetDatabase.LoadAssetAtPath<Material>(PackageWhiteMaterialPath) ?? materials["emissive"];
        return materials;
    }

    private static Material CreateOrUpdateMaterial(string name, Color color, float metallic, float smoothness, Color? emission = null)
    {
        var path = $"Assets/Materials/BSceneV1/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Glossiness", smoothness);
        if (emission.HasValue)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emission.Value);
        }
        else
        {
            material.DisableKeyword("_EMISSION");
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    private static void BuildFloorAndRoute(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var floor = CreateCube("B_Floor_Surface", parent, new Vector3(0f, -0.035f, 6f), new Vector3(12f, 0.07f, 12f), materials["packageFloor"]);
        floor.isStatic = true;
        RemoveCollider(floor);

        var route = CreateCube("Main_Demo_Route", parent, new Vector3(0f, 0.012f, 6f), new Vector3(2.25f, 0.024f, 10.8f), materials["route"]);
        route.isStatic = true;
        RemoveCollider(route);

        CreateCube("Route_Edge_Left", parent, new Vector3(-1.18f, 0.03f, 6f), new Vector3(0.035f, 0.035f, 10.8f), materials["gold"]);
        CreateCube("Route_Edge_Right", parent, new Vector3(1.18f, 0.03f, 6f), new Vector3(0.035f, 0.035f, 10.8f), materials["gold"]);
    }

    private static void BuildGalleryShell(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var shellRoot = new GameObject("B_Gallery_Architectural_Shell").transform;
        shellRoot.SetParent(parent, false);

        CreateVisualBlock("Wall_Left", shellRoot, new Vector3(-5.92f, 1.7f, 6f), new Vector3(0.16f, 3.4f, 12f), materials["packageWall"]);
        CreateVisualBlock("Wall_Right", shellRoot, new Vector3(5.92f, 1.7f, 6f), new Vector3(0.16f, 3.4f, 12f), materials["packageWall"]);
        CreateVisualBlock("Wall_Back", shellRoot, new Vector3(0f, 1.7f, 11.92f), new Vector3(12f, 3.4f, 0.16f), materials["packageWall"]);
        CreateVisualBlock("Entrance_Wall_Left", shellRoot, new Vector3(-4.75f, 1.7f, 0.08f), new Vector3(2.35f, 3.4f, 0.16f), materials["packageWall"]);
        CreateVisualBlock("Entrance_Wall_Right", shellRoot, new Vector3(4.75f, 1.7f, 0.08f), new Vector3(2.35f, 3.4f, 0.16f), materials["packageWall"]);

        CreateVisualBlock("Ceiling_Front", shellRoot, new Vector3(0f, 3.38f, 2.3f), new Vector3(12f, 0.12f, 4.6f), materials["packageWhite"]);
        CreateVisualBlock("Ceiling_Back", shellRoot, new Vector3(0f, 3.38f, 9.7f), new Vector3(12f, 0.12f, 4.6f), materials["packageWhite"]);

        CreateVisualBlock("Entrance_Column_Left", shellRoot, new Vector3(-3.55f, 1.72f, 0.34f), new Vector3(0.34f, 3.45f, 0.42f), materials["navy"]);
        CreateVisualBlock("Entrance_Column_Right", shellRoot, new Vector3(3.55f, 1.72f, 0.34f), new Vector3(0.34f, 3.45f, 0.42f), materials["navy"]);
        CreateVisualBlock("Entrance_Header", shellRoot, new Vector3(0f, 3.12f, 0.34f), new Vector3(7.45f, 0.34f, 0.42f), materials["navy"]);

        for (var section = 0; section < 3; section++)
        {
            var z = 2f + section * 4f;
            CreateVisualBlock($"Column_Left_{section + 1}", shellRoot, new Vector3(-5.52f, 1.62f, z), new Vector3(0.28f, 3.22f, 0.42f), materials["navy"]);
            CreateVisualBlock($"Column_Right_{section + 1}", shellRoot, new Vector3(5.52f, 1.62f, z), new Vector3(0.28f, 3.22f, 0.42f), materials["navy"]);
        }

        CreateVisualBlock("Gold_Baseboard_Left", shellRoot, new Vector3(-5.78f, 0.12f, 6f), new Vector3(0.035f, 0.12f, 11.5f), materials["gold"]);
        CreateVisualBlock("Gold_Baseboard_Right", shellRoot, new Vector3(5.78f, 0.12f, 6f), new Vector3(0.035f, 0.12f, 11.5f), materials["gold"]);
    }

    private static void CreateVisualBlock(string objectName, Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        var block = CreateCube(objectName, parent, position, scale, material);
        block.isStatic = true;
        RemoveCollider(block);
    }

    private static void BuildLighting(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var lightingRoot = new GameObject("B_Soft_Lighting").transform;
        lightingRoot.SetParent(parent, false);

        var keyObject = new GameObject("Key_Directional_Light");
        keyObject.transform.SetParent(lightingRoot, false);
        keyObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        var keyLight = keyObject.AddComponent<Light>();
        keyLight.type = LightType.Directional;
        keyLight.color = new Color(1f, 0.92f, 0.80f);
        keyLight.intensity = 0.72f;
        keyLight.shadows = LightShadows.Soft;
        keyLight.shadowStrength = 0.55f;

        var positions = new[]
        {
            new Vector3(-3.5f, 3.15f, 3f),
            new Vector3(3.5f, 3.15f, 3f),
            new Vector3(-3.5f, 3.15f, 9f),
            new Vector3(3.5f, 3.15f, 9f)
        };

        for (var index = 0; index < positions.Length; index++)
        {
            var panel = CreateCube($"Ceiling_Softbox_{index + 1}", lightingRoot, positions[index], new Vector3(2.4f, 0.035f, 0.52f), materials["emissive"]);
            RemoveCollider(panel);

            var lightObject = new GameObject($"Soft_Point_{index + 1}");
            lightObject.transform.SetParent(lightingRoot, false);
            lightObject.transform.position = positions[index] + Vector3.down * 0.18f;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.80f, 0.62f);
            light.intensity = 0.65f;
            light.range = 5.2f;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.Auto;
        }
    }

    private static void BuildExhibitAccents(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var exhibits = GameObject.Find("Exhibits");
        if (exhibits == null)
        {
            return;
        }

        var accentsRoot = new GameObject("B_Exhibit_Accents").transform;
        accentsRoot.SetParent(parent, false);
        var exhibitIndex = 0;

        foreach (Transform exhibit in exhibits.transform)
        {
            if (!TryGetRendererBounds(exhibit.gameObject, out var bounds))
            {
                continue;
            }

            exhibitIndex++;
            var width = Mathf.Clamp(bounds.size.x + 0.42f, 0.9f, 2.2f);
            var depth = Mathf.Clamp(bounds.size.z + 0.42f, 0.7f, 1.8f);
            var topY = Mathf.Max(0.09f, bounds.min.y - 0.01f);

            var plinth = CreateCube($"Exhibit_Plinth_{exhibitIndex:00}", accentsRoot, new Vector3(bounds.center.x, topY - 0.09f, bounds.center.z), new Vector3(width, 0.18f, depth), materials["stone"]);
            RemoveCollider(plinth);

            var halo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            halo.name = $"Exhibit_Halo_{exhibitIndex:00}";
            halo.transform.SetParent(accentsRoot, false);
            halo.transform.position = new Vector3(bounds.center.x, 0.035f, bounds.center.z);
            halo.transform.localScale = new Vector3(width * 0.58f, 0.018f, depth * 0.58f);
            halo.GetComponent<Renderer>().sharedMaterial = materials["gold"];
            RemoveCollider(halo);
        }
    }

    private static void ArrangeExhibitsForGallery()
    {
        var exhibits = GameObject.Find("Exhibits");
        if (exhibits == null)
        {
            return;
        }

        var positions = new[]
        {
            new Vector3(-3.65f, 0f, 2.1f),
            new Vector3(3.65f, 0f, 2.1f),
            new Vector3(-3.85f, 0f, 4.85f),
            new Vector3(3.85f, 0f, 4.85f),
            new Vector3(-3.85f, 0f, 7.55f),
            new Vector3(3.85f, 0f, 7.55f),
            new Vector3(-3.45f, 0f, 10.15f),
            new Vector3(3.45f, 0f, 10.15f)
        };

        var target = new Vector3(0f, 1.1f, 6.1f);
        for (var index = 0; index < exhibits.transform.childCount && index < positions.Length; index++)
        {
            var exhibit = exhibits.transform.GetChild(index);
            exhibit.position = positions[index];
            var lookDirection = target - exhibit.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.01f)
            {
                exhibit.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }
        }
    }

    private static void BuildFurniture(Transform parent)
    {
        var furnitureRoot = new GameObject("B_Gallery_Furniture").transform;
        furnitureRoot.SetParent(parent, false);

        AddPrefabAccent(BenchPrefabPath, "Bench_Left", furnitureRoot, new Vector3(-4.15f, 0f, 5.9f), Quaternion.Euler(0f, 90f, 0f), 0.9f);
        AddPrefabAccent(BenchPrefabPath, "Bench_Right", furnitureRoot, new Vector3(4.15f, 0f, 5.9f), Quaternion.Euler(0f, -90f, 0f), 0.9f);
        AddPrefabAccent(FlowerPrefabPath, "Plant_Entrance_Left", furnitureRoot, new Vector3(-4.75f, 0f, 1.25f), Quaternion.identity, 0.72f);
        AddPrefabAccent(FlowerPrefabPath, "Plant_Entrance_Right", furnitureRoot, new Vector3(4.75f, 0f, 1.25f), Quaternion.identity, 0.72f);
    }

    private static void AddPrefabAccent(string assetPath, string objectName, Transform parent, Vector3 position, Quaternion rotation, float scale)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[BSceneV1] 可选装饰缺失：{assetPath}");
            return;
        }

        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (instance == null)
        {
            return;
        }

        instance.name = objectName;
        instance.transform.SetParent(parent, true);
        instance.transform.position = position;
        instance.transform.rotation = rotation;
        instance.transform.localScale = Vector3.one * scale;
        foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
    }

    private static void BuildEntranceIdentity(Transform parent, IReadOnlyDictionary<string, Material> materials)
    {
        var entranceRoot = new GameObject("B_Entrance_Identity").transform;
        entranceRoot.SetParent(parent, false);
        CreateCube("Entrance_Backdrop", entranceRoot, new Vector3(0f, 1.55f, 11.72f), new Vector3(5.8f, 2.45f, 0.08f), materials["navy"]);

        var titleObject = new GameObject("VirtualGuidance_Title");
        titleObject.transform.SetParent(entranceRoot, false);
        titleObject.transform.position = new Vector3(0f, 1.92f, 11.64f);
        titleObject.transform.rotation = Quaternion.identity;
        var title = titleObject.AddComponent<TextMeshPro>();
        title.text = "VIRTUAL GUIDANCE";
        title.alignment = TextAlignmentOptions.Center;
        title.fontSize = 3.1f;
        title.color = WarmWhite;
        title.enableWordWrapping = false;
        title.rectTransform.sizeDelta = new Vector2(6.4f, 1f);

        var subtitleObject = new GameObject("VirtualGuidance_Subtitle");
        subtitleObject.transform.SetParent(entranceRoot, false);
        subtitleObject.transform.position = new Vector3(0f, 1.28f, 11.63f);
        subtitleObject.transform.rotation = Quaternion.identity;
        var subtitle = subtitleObject.AddComponent<TextMeshPro>();
        subtitle.text = "IMMERSIVE EXHIBITION  ·  SAFE EXPLORATION";
        subtitle.alignment = TextAlignmentOptions.Center;
        subtitle.fontSize = 1.05f;
        subtitle.color = WarmGold;
        subtitle.enableWordWrapping = false;
        subtitle.rectTransform.sizeDelta = new Vector2(7.2f, 0.6f);
    }

    private static void HideGuideAvatarVisuals()
    {
        var guideAvatar = GameObject.Find("GuideAvatar");
        if (guideAvatar == null)
        {
            return;
        }

        foreach (var renderer in guideAvatar.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = false;
        }
    }

    private static void StyleParticipantUi()
    {
        var participantCanvas = GameObject.Find("ParticipantCanvas");
        if (participantCanvas == null)
        {
            Debug.LogWarning("[BSceneV1] 未找到 ParticipantCanvas，跳过 UI 样式优化。");
            return;
        }

        StylePanel("GuidancePanel", new Color(0.025f, 0.07f, 0.11f, 0.88f), Teal);
        StylePanel("SafetyPanel", new Color(0.35f, 0.055f, 0.045f, 0.92f), WarmGold);
        StyleText("GuidanceTextPanel_Text", WarmWhite, 30f, FontStyles.Normal);
        StyleText("MovementHintPanel_Text", new Color(0.35f, 0.92f, 0.88f), 25f, FontStyles.Bold);
        StyleText("GazeCuePanel_Text", WarmGold, 24f, FontStyles.Bold);
        StyleText("SafetyText", Color.white, 30f, FontStyles.Bold);
        StyleText("TaskPanel_Text", new Color(0.82f, 0.88f, 0.92f), 22f, FontStyles.Normal);
    }

    private static void StylePanel(string objectName, Color background, Color outlineColor)
    {
        var panel = GameObject.Find(objectName);
        if (panel == null)
        {
            return;
        }

        var image = panel.GetComponent<Image>();
        if (image != null)
        {
            image.color = background;
        }

        var outline = panel.GetComponent<Outline>();
        if (outline == null)
        {
            outline = panel.AddComponent<Outline>();
        }

        outline.effectColor = outlineColor;
        outline.effectDistance = new Vector2(2f, -2f);
    }

    private static void StyleText(string objectName, Color color, float fontSize, FontStyles style)
    {
        var textObject = GameObject.Find(objectName);
        var text = textObject != null ? textObject.GetComponent<TMP_Text>() : null;
        if (text == null)
        {
            return;
        }

        text.color = color;
        text.fontSize = fontSize;
        text.fontStyle = style;
    }

    private static void ConfigureRenderSettings()
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.26f, 0.33f, 0.40f);
        RenderSettings.ambientEquatorColor = new Color(0.16f, 0.20f, 0.23f);
        RenderSettings.ambientGroundColor = new Color(0.065f, 0.075f, 0.085f);
        RenderSettings.ambientIntensity = 0.78f;
        RenderSettings.fog = true;
        RenderSettings.fogColor = new Color(0.08f, 0.10f, 0.12f);
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 18f;
        RenderSettings.fogEndDistance = 48f;
    }

    private static GameObject CreateCube(string objectName, Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = objectName;
        cube.transform.SetParent(parent, false);
        cube.transform.position = position;
        cube.transform.localScale = scale;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        return cube;
    }

    private static void RemoveCollider(GameObject target)
    {
        var collider = target.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }
    }

    private static void SaveContentPrefab(GameObject contentRoot)
    {
        EnsureAssetFolder("Assets/Prefabs/BContent");
        PrefabUtility.SaveAsPrefabAssetAndConnect(contentRoot, ContentPrefabPath, InteractionMode.AutomatedAction);
        AssetDatabase.SaveAssets();
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        var parts = folderPath.Split('/');
        var current = parts[0];
        for (var index = 1; index < parts.Length; index++)
        {
            var next = $"{current}/{parts[index]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[index]);
            }

            current = next;
        }
    }
}
