using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 将原 MainScene 的展品与演示路径整体嵌入 room 展厅。
/// </summary>
[InitializeOnLoad]
public static class RoomIntegrationBuilder
{
    private const string RoomSourceScenePath = "Assets/ThirdParty/Display room/Scene.unity";
    private const string ArchiveScenePath = "Assets/Scenes/Archives/MainScene_Before_B_V1.unity";
    private const string MainScenePath = "Assets/Scenes/MainScene.unity";
    private const string RoomPrefabPath = "Assets/Prefabs/Exhibits/Room.prefab";
    private const string ReportPath = "Assets/Documentation/RoomIntegrationMeasurements.txt";
    private const string RunMarkerPath = "Temp/RunRoomIntegration.flag";
    private const string SceneViewMarkerPath = "Temp/FocusRoomSceneView.flag";
    private const string AnalyzeCenterWallMarkerPath = "Temp/AnalyzeCenterWall.flag";
    private const string RemoveCenterWallMarkerPath = "Temp/RemoveCenterWall.flag";
    private const string ApplyShowroomStyleMarkerPath = "Temp/ApplyShowroomStyle.flag";
    private const string ApplyEnvironmentDressingMarkerPath = "Temp/ApplyEnvironmentDressing.flag";
    private const string BeforeWallRemovalScenePath = "Assets/Scenes/Archives/MainScene_Before_CenterWall_Removal.unity";
    private const string BeforeShowroomStyleScenePath = "Assets/Scenes/Archives/MainScene_Before_ShowroomStyle.unity";
    private const string BeforeEnvironmentDressingScenePath = "Assets/Scenes/Archives/MainScene_Before_EnvironmentDressing.unity";
    private const string CenterWallRemovedMeshPath = "Assets/Meshes/Room/Object004_NoCenterWall.asset";
    private const string ShowroomWallMaterialPath = "Assets/Showroom Environment - gallery/models/Materials/wall.mat";
    private const string ShowroomFloorMaterialPath = "Assets/Showroom Environment - gallery/models/Materials/floor.mat";
    private const string ShowroomBenchPrefabPath = "Assets/Showroom Environment - gallery/models/prefab/bench.prefab";
    private const string ShowroomFlowerPrefabPath = "Assets/Showroom Environment - gallery/models/prefab/flower.prefab";
    private const string ShowroomEnhancementRootName = "Room_Showroom_Enhancements";
    private const string LampPrefabPath = "Assets/ThirdParty/Display room/Prefabs/3D models/Lamps/Prefabs/Lamp 7.prefab";
    private const string ShelfPrefabPath = "Assets/Low Poly Factory/Meshes/Office/SM_Shelf_01.fbx";
    private const string TreePrefabPath = "Assets/Low Poly Factory/Meshes/Tree/SM_Tree_04.fbx";
    private const string EnvironmentDressingRootName = "Room_Environment_Dressing";
    private const string PreviewFileName = "Preview_Room_Integrated.png";
    private const string ShowroomPreviewFileName = "Preview_Room_ShowroomStyle.png";
    private const string LoungePreviewFileName = "Preview_Room_ShowroomLounge.png";
    private const string InteriorDressingPreviewFileName = "Preview_Room_Interior_Dressing.png";
    private const string ExteriorTreesPreviewFileName = "Preview_Room_Exterior_Trees.png";
    private const string ShelfDetailPreviewFileName = "Preview_Room_Shelf_Detail.png";
    private const string TreeDetailPreviewFileName = "Preview_Room_Tree_Detail.png";

    static RoomIntegrationBuilder()
    {
        if (File.Exists(RunMarkerPath))
        {
            File.Delete(RunMarkerPath);
            EditorApplication.delayCall += BuildIntegratedScene;
        }
        else if (File.Exists(SceneViewMarkerPath))
        {
            File.Delete(SceneViewMarkerPath);
            EditorApplication.delayCall += FocusRoomEntranceInSceneView;
        }
        else if (File.Exists(AnalyzeCenterWallMarkerPath))
        {
            File.Delete(AnalyzeCenterWallMarkerPath);
            EditorApplication.delayCall += AnalyzeCenterWall;
        }
        else if (File.Exists(RemoveCenterWallMarkerPath))
        {
            File.Delete(RemoveCenterWallMarkerPath);
            EditorApplication.delayCall += RemoveCenterWallAndTrimFloor;
        }
        else if (File.Exists(ApplyShowroomStyleMarkerPath))
        {
            File.Delete(ApplyShowroomStyleMarkerPath);
            EditorApplication.delayCall += ApplyShowroomStyle;
        }
        else if (File.Exists(ApplyEnvironmentDressingMarkerPath))
        {
            File.Delete(ApplyEnvironmentDressingMarkerPath);
            EditorApplication.delayCall += ApplyEnvironmentDressing;
        }
        else if (!File.Exists(ReportPath))
        {
            EditorApplication.delayCall += MeasureRoomAndOriginalScene;
        }
    }

    /// <summary>
    /// 打开 room 包自带的参考场景，用于确认正门和内部朝向。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/打开 room 原始场景 _F8")]
    public static void OpenRoomSourceScene()
    {
        EditorSceneManager.OpenScene(RoomSourceScenePath, OpenSceneMode.Single);
        Debug.Log("[RoomIntegration] 已打开 room 原始参考场景。");
    }

    /// <summary>
    /// 测量 room 和原始展品的世界边界，供整体对齐使用。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/测量 room 与原场景 _F6")]
    public static void MeasureRoomAndOriginalScene()
    {
        var builder = new StringBuilder();

        var roomScene = EditorSceneManager.OpenScene(RoomSourceScenePath, OpenSceneMode.Single);
        var roomRoot = roomScene.GetRootGameObjects().FirstOrDefault(root => root.name == "Room");
        var roomCamera = roomScene.GetRootGameObjects().FirstOrDefault(root => root.name == "Main Camera");
        AppendObjectMeasurement(builder, "ROOM", roomRoot);
        AppendTransform(builder, "ROOM_CAMERA", roomCamera == null ? null : roomCamera.transform);

        var originalScene = EditorSceneManager.OpenScene(ArchiveScenePath, OpenSceneMode.Single);
        var exhibitsRoot = originalScene.GetRootGameObjects().FirstOrDefault(root => root.name == "Exhibits");
        var environmentRoot = originalScene.GetRootGameObjects().FirstOrDefault(root => root.name == "Environment");
        var xrOrigin = originalScene.GetRootGameObjects().FirstOrDefault(root => root.name.Contains("XR Origin"));
        AppendObjectMeasurement(builder, "ORIGINAL_EXHIBITS", exhibitsRoot);
        AppendObjectMeasurement(builder, "ORIGINAL_ENVIRONMENT", environmentRoot);
        AppendTransform(builder, "ORIGINAL_XR_ORIGIN", xrOrigin == null ? null : xrOrigin.transform);

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, builder.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();
        EditorSceneManager.OpenScene(RoomSourceScenePath, OpenSceneMode.Single);
        Debug.Log($"[RoomIntegration] 测量完成：{ReportPath}");
    }

    /// <summary>
    /// 恢复存档中的原场景，并将其完整放入 room 正门后的主厅。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/恢复并嵌入原场景布局 _F9")]
    public static void BuildIntegratedScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[RoomIntegration] 请先退出播放模式再执行嵌入。");
            return;
        }

        var archiveScene = EditorSceneManager.OpenScene(ArchiveScenePath, OpenSceneMode.Single);
        if (!EditorSceneManager.SaveScene(archiveScene, MainScenePath, true))
        {
            Debug.LogError("[RoomIntegration] 恢复 MainScene 失败。");
            return;
        }

        var mainScene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        RemoveExistingRoom(mainScene);

        var roomPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath);
        var roomInstance = PrefabUtility.InstantiatePrefab(roomPrefab, mainScene) as GameObject;
        if (roomInstance == null)
        {
            Debug.LogError($"[RoomIntegration] 未找到 room 预制体：{RoomPrefabPath}");
            return;
        }

        roomInstance.name = "Room_Environment";
        roomInstance.transform.SetPositionAndRotation(
            new Vector3(1.128f, 1.8465f, 5.358f),
            Quaternion.identity);
        roomInstance.transform.localScale = Vector3.one;

        DisablePlaceholderGroup(roomInstance.transform, "hangers");
        DisablePlaceholderGroup(roomInstance.transform, "Show cubes");
        HideCollisionRenderers(roomInstance.transform.Find("Collisions"));
        ApplyCenterWallCleanup(roomInstance);
        ApplyShowroomStyleToScene(mainScene, roomInstance);
        ApplyEnvironmentDressingToScene(mainScene);
        ExhibitShowcaseBuilder.ApplyToScene(mainScene);
        HideOriginalEnvironmentMeshes(mainScene);

        EditorSceneManager.MarkSceneDirty(mainScene);
        EditorSceneManager.SaveScene(mainScene);
        CaptureEntrancePreview();
        Selection.activeGameObject = roomInstance;
        EditorGUIUtility.PingObject(roomInstance);
        Debug.Log("[RoomIntegration] 已恢复原展品布局并嵌入 room，原演示坐标和路径保持不变。");
    }

    /// <summary>
    /// 将 Scene 视图定位到 room 正门，避免视角落在墙体内部只看到白色表面。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/聚焦 room 正门视角 _F5")]
    public static void FocusRoomEntranceInSceneView()
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
        {
            return;
        }

        sceneView.in2DMode = false;
        sceneView.orthographic = false;
        var viewAnchor = new GameObject("__RoomSceneViewAnchor");
        viewAnchor.hideFlags = HideFlags.HideAndDontSave;
        viewAnchor.transform.position = new Vector3(0f, 1.65f, -2.15f);
        viewAnchor.transform.rotation = Quaternion.LookRotation(
            new Vector3(0f, 1.15f, 6.5f) - viewAnchor.transform.position,
            Vector3.up);
        sceneView.AlignViewToObject(viewAnchor.transform);
        Object.DestroyImmediate(viewAnchor);
        sceneView.Repaint();
        Debug.Log("[RoomIntegration] Scene 视图已定位到 room 正门入口。");
    }

    /// <summary>
    /// 分析与中央碰撞墙重叠的 room 建筑网格，避免误删其他结构。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/验证入口通行")]
    private static void AnalyzeCenterWall()
    {
        var roomRoot = GameObject.Find("Room_Environment");
        var architecture = roomRoot == null ? null : roomRoot.transform.Find("architecture");
        var centerCollision = roomRoot == null ? null : roomRoot.transform.Find("Collisions/center");
        if (architecture == null || centerCollision == null)
        {
            File.WriteAllText("Temp/CenterWallCandidates.txt", "missing room hierarchy");
            return;
        }

        var collisionRenderer = centerCollision.GetComponent<Renderer>();
        var collisionCollider = centerCollision.GetComponent<Collider>();
        var collisionBounds = collisionCollider != null ? collisionCollider.bounds : collisionRenderer.bounds;
        collisionBounds.Expand(new Vector3(0.4f, 0.2f, 0.4f));

        var builder = new StringBuilder();
        builder.AppendLine($"collisionCenter={Format(collisionBounds.center)}");
        builder.AppendLine($"collisionSize={Format(collisionBounds.size)}");
        foreach (var renderer in architecture.GetComponentsInChildren<Renderer>(true))
        {
            var minimum = Vector3.Max(renderer.bounds.min, collisionBounds.min);
            var maximum = Vector3.Min(renderer.bounds.max, collisionBounds.max);
            var overlap = maximum - minimum;
            if (overlap.x <= 0f || overlap.y <= 0f || overlap.z <= 0f)
            {
                continue;
            }

            builder.AppendLine($"{GetPath(renderer.transform, architecture)} | center={Format(renderer.bounds.center)} | size={Format(renderer.bounds.size)} | overlap={Format(overlap)}");
        }

        var rayOrigin = new Vector3(0f, 2.4f, -2.15f);
        var ray = new Ray(rayOrigin, (collisionBounds.center - rayOrigin).normalized);
        builder.AppendLine("rayHits:");
        foreach (var meshFilter in architecture.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter.sharedMesh == null)
            {
                continue;
            }

            var meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = meshFilter.sharedMesh;
            if (meshCollider.Raycast(ray, out var hit, 100f))
            {
                builder.AppendLine($"{GetPath(meshFilter.transform, architecture)} | distance={hit.distance:F4} | point={Format(hit.point)}");
            }

            Object.DestroyImmediate(meshCollider);
        }

        File.WriteAllText("Temp/CenterWallCandidates.txt", builder.ToString(), Encoding.UTF8);
        Debug.Log("[RoomIntegration] 中央墙候选网格分析完成。");
    }

    /// <summary>
    /// 移除入口正前方的中央分隔墙及碰撞体，并将超大地面收缩到展厅范围。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/移除中央挡路墙")]
    public static void RemoveCenterWallAndTrimFloor()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScenePath)
        {
            scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        }

        if (!File.Exists(BeforeWallRemovalScenePath))
        {
            EditorSceneManager.SaveScene(scene, BeforeWallRemovalScenePath, true);
        }

        var roomRoot = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "Room_Environment");
        if (roomRoot == null)
        {
            Debug.LogError("[RoomIntegration] MainScene 中未找到 Room_Environment。");
            return;
        }

        ApplyCenterWallCleanup(roomRoot);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        CaptureEntrancePreview();
        FocusRoomEntranceInSceneView();
        Selection.activeObject = null;
        Debug.Log("[RoomIntegration] 已移除中央挡路墙和碰撞体，并替换超大地面。");
    }

    /// <summary>
    /// 恢复顶部建筑细节，并使用 Showroom 资源完成墙面、地板和后部休息区布置。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/应用 Showroom 风格")]
    public static void ApplyShowroomStyle()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScenePath)
        {
            scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        }

        if (!File.Exists(BeforeShowroomStyleScenePath))
        {
            EditorSceneManager.SaveScene(scene, BeforeShowroomStyleScenePath, true);
        }

        var roomRoot = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "Room_Environment");
        if (roomRoot == null)
        {
            Debug.LogError("[RoomIntegration] MainScene 中未找到 Room_Environment。");
            return;
        }

        var centerCollision = roomRoot.transform.Find("Collisions/center");
        ReplaceCenterWallMesh(roomRoot.transform.Find("architecture/Object004"), GetActiveBounds(centerCollision));
        ApplyShowroomStyleToScene(scene, roomRoot);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        CaptureEntrancePreview();
        CapturePreview(ShowroomPreviewFileName, new Vector3(0f, 1.7f, -2.15f), new Vector3(0f, 1.2f, 10.5f), 72f);
        CapturePreview(LoungePreviewFileName, new Vector3(0f, 2.2f, 11.8f), new Vector3(0f, 1.05f, 17.2f), 72f);
        FocusRoomEntranceInSceneView();
        Selection.activeObject = null;
        Debug.Log("[RoomIntegration] Showroom 风格墙面、地板和休息区布置完成。");
    }

    /// <summary>
    /// 在展厅顶部布置吊灯、沿展板附近墙面布置书架，并在建筑外围布置树木。
    /// </summary>
    [MenuItem("VirtualGuidance/Room 嵌入/布置灯具书架与外部绿化")]
    public static void ApplyEnvironmentDressing()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScenePath)
        {
            scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        }

        if (!File.Exists(BeforeEnvironmentDressingScenePath))
        {
            EditorSceneManager.SaveScene(scene, BeforeEnvironmentDressingScenePath, true);
        }

        ApplyEnvironmentDressingToScene(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        CapturePreview(InteriorDressingPreviewFileName, new Vector3(0f, 1.7f, -2.15f), new Vector3(0f, 1.7f, 8.2f), 72f);
        CapturePreview(ExteriorTreesPreviewFileName, new Vector3(17f, 7.5f, -11f), new Vector3(0.4f, 2.2f, 12f), 62f);
        CapturePreview(ShelfDetailPreviewFileName, new Vector3(-1.6f, 1.65f, 4.3f), new Vector3(-4.3f, 1.15f, 6.5f), 58f);
        CapturePreview(TreeDetailPreviewFileName, new Vector3(13.5f, 2.2f, -4.5f), new Vector3(8.8f, 1.7f, -2f), 57f);
        FocusRoomEntranceInSceneView();
        Selection.activeObject = null;
        Debug.Log("[RoomIntegration] 6 盏吊灯、6 组沿墙书架与 12 株场馆外部树木布置完成。");
    }

    private static void ApplyEnvironmentDressingToScene(Scene scene)
    {
        var existingRoot = scene.GetRootGameObjects().FirstOrDefault(root => root.name == EnvironmentDressingRootName);
        if (existingRoot != null)
        {
            Object.DestroyImmediate(existingRoot);
        }

        var dressingRoot = new GameObject(EnvironmentDressingRootName);
        SceneManager.MoveGameObjectToScene(dressingRoot, scene);
        var lampRoot = CreateGroup(dressingRoot.transform, "Ceiling_Lamps");
        var shelfRoot = CreateGroup(dressingRoot.transform, "Wall_Shelves");
        var treeRoot = CreateGroup(dressingRoot.transform, "Exterior_Trees");

        var lampPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LampPrefabPath);
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Left_Entrance", new Vector3(-2.25f, 4.35f, 2.7f));
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Right_Entrance", new Vector3(2.25f, 4.35f, 2.7f));
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Left_Middle", new Vector3(-2.25f, 4.35f, 6.4f));
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Right_Middle", new Vector3(2.25f, 4.35f, 6.4f));
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Left_Back", new Vector3(-2.25f, 4.35f, 10.1f));
        CreateLamp(lampPrefab, scene, lampRoot, "Lamp_Right_Back", new Vector3(2.25f, 4.35f, 10.1f));

        var shelfPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShelfPrefabPath);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Left_Entrance", new Vector3(-4.32f, 0f, 2.2f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Right_Entrance", new Vector3(5.12f, 0f, 2.2f), new Vector3(0f, -90f, 0f), Vector3.one);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Left_Middle", new Vector3(-4.32f, 0f, 6.5f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Right_Middle", new Vector3(5.12f, 0f, 6.5f), new Vector3(0f, -90f, 0f), Vector3.one);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Left_Back", new Vector3(-4.32f, 0f, 10.7f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateDecorPrefab(shelfPrefab, scene, shelfRoot, "Shelf_Right_Back", new Vector3(5.12f, 0f, 10.7f), new Vector3(0f, -90f, 0f), Vector3.one);

        var treePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TreePrefabPath);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Front_Left", new Vector3(-8.2f, 0f, -1f), new Vector3(0f, 20f, 0f), Vector3.one);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Front_Right", new Vector3(8.8f, 0f, -2f), new Vector3(0f, -35f, 0f), Vector3.one * 1.15f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Left_01", new Vector3(-9.5f, 0f, 5f), new Vector3(0f, 70f, 0f), Vector3.one * 1.25f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Right_01", new Vector3(10f, 0f, 6.5f), new Vector3(0f, 120f, 0f), Vector3.one * 0.95f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Left_02", new Vector3(-8.4f, 0f, 12.5f), new Vector3(0f, -60f, 0f), Vector3.one * 1.1f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Right_02", new Vector3(9.2f, 0f, 14f), new Vector3(0f, 45f, 0f), Vector3.one * 1.3f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Left_03", new Vector3(-10.2f, 0f, 21f), new Vector3(0f, 15f, 0f), Vector3.one * 0.95f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Right_03", new Vector3(10.4f, 0f, 22.2f), new Vector3(0f, -100f, 0f), Vector3.one * 1.15f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Back_Left", new Vector3(-7.8f, 0f, 28f), new Vector3(0f, 80f, 0f), Vector3.one * 1.3f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Back_Right", new Vector3(8.4f, 0f, 28.8f), new Vector3(0f, -20f, 0f), Vector3.one);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Back_Center_Left", new Vector3(-2.8f, 0f, 29.2f), new Vector3(0f, 35f, 0f), Vector3.one * 1.1f);
        CreateDecorPrefab(treePrefab, scene, treeRoot, "Tree_Back_Center_Right", new Vector3(3.7f, 0f, 29.8f), new Vector3(0f, -50f, 0f), Vector3.one * 1.25f);
    }

    private static Transform CreateGroup(Transform parent, string groupName)
    {
        var group = new GameObject(groupName);
        group.transform.SetParent(parent, false);
        return group.transform;
    }

    private static GameObject CreateDecorPrefab(GameObject prefab, Scene scene, Transform parent, string objectName, Vector3 position, Vector3 eulerAngles, Vector3 scale)
    {
        if (prefab == null)
        {
            Debug.LogWarning($"[RoomIntegration] 未找到布景资源：{objectName}");
            return null;
        }

        var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
        if (instance == null)
        {
            return null;
        }

        instance.name = objectName;
        instance.transform.SetParent(parent, true);
        instance.transform.SetPositionAndRotation(position, Quaternion.Euler(eulerAngles));
        instance.transform.localScale = scale;
        GameObjectUtility.SetStaticEditorFlags(instance, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
        AddDecorCollider(instance, objectName);
        return instance;
    }

    private static void AddDecorCollider(GameObject instance, string objectName)
    {
        if (instance.GetComponentInChildren<Collider>() != null)
        {
            return;
        }

        var collider = instance.AddComponent<BoxCollider>();
        if (objectName.StartsWith("Shelf_"))
        {
            collider.center = new Vector3(0f, 1.0556f, 0.0109f);
            collider.size = new Vector3(1.8133f, 2.1145f, 0.5933f);
        }
        else if (objectName.StartsWith("Tree_"))
        {
            collider.center = new Vector3(0f, 1.6767f, 0.1202f);
            collider.size = new Vector3(1.4757f, 3.3534f, 1.469f);
        }
    }

    private static void CreateLamp(GameObject prefab, Scene scene, Transform parent, string objectName, Vector3 position)
    {
        var lamp = CreateDecorPrefab(prefab, scene, parent, objectName, position, Vector3.zero, Vector3.one);
        if (lamp == null)
        {
            return;
        }

        foreach (var lampLight in lamp.GetComponentsInChildren<Light>(true))
        {
            lampLight.color = new Color(1f, 0.88f, 0.72f);
            lampLight.intensity = 1.25f;
            lampLight.range = 4.5f;
            lampLight.shadows = LightShadows.None;
        }
    }

    private static void ApplyShowroomStyleToScene(Scene scene, GameObject roomRoot)
    {
        var existingRoot = scene.GetRootGameObjects().FirstOrDefault(root => root.name == ShowroomEnhancementRootName);
        if (existingRoot != null)
        {
            Object.DestroyImmediate(existingRoot);
        }

        var enhancementRoot = new GameObject(ShowroomEnhancementRootName);
        SceneManager.MoveGameObjectToScene(enhancementRoot, scene);

        var wallMaterial = AssetDatabase.LoadAssetAtPath<Material>(ShowroomWallMaterialPath);
        var floorMaterial = AssetDatabase.LoadAssetAtPath<Material>(ShowroomFloorMaterialPath);
        CreateWallPanel(enhancementRoot.transform, "Showroom_Wall_Left", new Vector3(-4.78f, 2.45f, 11.65f), new Vector3(0.18f, 4.9f, 28.7f), wallMaterial);
        CreateWallPanel(enhancementRoot.transform, "Showroom_Wall_Right", new Vector3(5.58f, 2.45f, 11.65f), new Vector3(0.18f, 4.9f, 28.7f), wallMaterial);

        var boundedFloor = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "Room_Floor_Bounded");
        var floorRenderer = boundedFloor == null ? null : boundedFloor.GetComponent<Renderer>();
        if (floorRenderer != null && floorMaterial != null)
        {
            floorRenderer.sharedMaterial = floorMaterial;
        }

        var benchPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShowroomBenchPrefabPath);
        var flowerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShowroomFlowerPrefabPath);
        CreateShowroomPrefab(benchPrefab, scene, enhancementRoot.transform, "Lounge_Bench_Left_Front", new Vector3(-3.45f, 0f, 14.7f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateShowroomPrefab(benchPrefab, scene, enhancementRoot.transform, "Lounge_Bench_Right_Front", new Vector3(4.25f, 0f, 14.7f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateShowroomPrefab(benchPrefab, scene, enhancementRoot.transform, "Lounge_Bench_Left_Back", new Vector3(-3.45f, 0f, 18.6f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateShowroomPrefab(benchPrefab, scene, enhancementRoot.transform, "Lounge_Bench_Right_Back", new Vector3(4.25f, 0f, 18.6f), new Vector3(0f, 90f, 0f), Vector3.one);
        CreateShowroomPrefab(flowerPrefab, scene, enhancementRoot.transform, "Lounge_Plant_Left_Front", new Vector3(-4.15f, 0f, 12.9f), new Vector3(0f, 25f, 0f), Vector3.one * 0.85f);
        CreateShowroomPrefab(flowerPrefab, scene, enhancementRoot.transform, "Lounge_Plant_Right_Front", new Vector3(4.95f, 0f, 12.9f), new Vector3(0f, -25f, 0f), Vector3.one * 0.85f);
        CreateShowroomPrefab(flowerPrefab, scene, enhancementRoot.transform, "Lounge_Plant_Left_Back", new Vector3(-4.15f, 0f, 20.5f), new Vector3(0f, 155f, 0f), Vector3.one);
        CreateShowroomPrefab(flowerPrefab, scene, enhancementRoot.transform, "Lounge_Plant_Right_Back", new Vector3(4.95f, 0f, 20.5f), new Vector3(0f, -155f, 0f), Vector3.one);
    }

    private static void CreateWallPanel(Transform parent, string objectName, Vector3 position, Vector3 scale, Material material)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = objectName;
        wall.transform.SetParent(parent, false);
        wall.transform.SetPositionAndRotation(position, Quaternion.identity);
        wall.transform.localScale = scale;
        wall.isStatic = true;
        if (material != null)
        {
            wall.GetComponent<Renderer>().sharedMaterial = material;
        }
    }

    private static void CreateShowroomPrefab(GameObject prefab, Scene scene, Transform parent, string objectName, Vector3 position, Vector3 eulerAngles, Vector3 scale)
    {
        if (prefab == null)
        {
            return;
        }

        var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
        if (instance == null)
        {
            return;
        }

        instance.name = objectName;
        instance.transform.SetParent(parent, true);
        instance.transform.SetPositionAndRotation(position, Quaternion.Euler(eulerAngles));
        instance.transform.localScale = scale;
        GameObjectUtility.SetStaticEditorFlags(instance, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
    }

    private static void ApplyCenterWallCleanup(GameObject roomRoot)
    {
        var centerCollision = roomRoot.transform.Find("Collisions/center");
        var collisionBounds = GetActiveBounds(centerCollision);
        ReplaceCenterWallMesh(roomRoot.transform.Find("architecture/Object004"), collisionBounds);
        SetInactive(centerCollision);

        var oversizedFloor = roomRoot.transform.Find("architecture/Plane003");
        var floorMaterial = oversizedFloor == null ? null : oversizedFloor.GetComponent<Renderer>()?.sharedMaterial;
        SetInactive(oversizedFloor);

        var existingFloor = GameObject.Find("Room_Floor_Bounded");
        if (existingFloor != null)
        {
            Object.DestroyImmediate(existingFloor);
        }

        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Room_Floor_Bounded";
        floor.transform.SetPositionAndRotation(new Vector3(0.4f, -0.025f, 11.63f), Quaternion.identity);
        floor.transform.localScale = new Vector3(10.3f, 0.05f, 28.85f);
        floor.isStatic = true;
        if (floorMaterial != null)
        {
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
        }
    }

    private static Bounds GetActiveBounds(Transform target)
    {
        if (target == null)
        {
            return new Bounds();
        }

        var wasActive = target.gameObject.activeSelf;
        target.gameObject.SetActive(true);
        var collider = target.GetComponent<Collider>();
        var renderer = target.GetComponent<Renderer>();
        var bounds = collider != null ? collider.bounds : renderer.bounds;
        target.gameObject.SetActive(wasActive);
        bounds.Expand(new Vector3(0.12f, 0.12f, 0.12f));
        return bounds;
    }

    private static void ReplaceCenterWallMesh(Transform wallObject, Bounds collisionBounds)
    {
        if (wallObject == null || collisionBounds.size == Vector3.zero)
        {
            return;
        }

        wallObject.gameObject.SetActive(true);
        var meshFilter = wallObject.GetComponent<MeshFilter>();
        var roomPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath);
        var sourceWall = roomPrefab == null ? null : roomPrefab.transform.Find("architecture/Object004");
        var sourceMesh = sourceWall == null ? null : sourceWall.GetComponent<MeshFilter>()?.sharedMesh;
        if (meshFilter == null || sourceMesh == null)
        {
            return;
        }

        var cleanedMesh = Object.Instantiate(sourceMesh);
        cleanedMesh.name = "Object004_NoCenterWall";
        var vertices = sourceMesh.vertices;
        var removedTriangleCount = 0;
        for (var subMeshIndex = 0; subMeshIndex < sourceMesh.subMeshCount; subMeshIndex++)
        {
            var sourceTriangles = sourceMesh.GetTriangles(subMeshIndex);
            var keptTriangles = new List<int>(sourceTriangles.Length);
            for (var index = 0; index < sourceTriangles.Length; index += 3)
            {
                var first = wallObject.TransformPoint(vertices[sourceTriangles[index]]);
                var second = wallObject.TransformPoint(vertices[sourceTriangles[index + 1]]);
                var third = wallObject.TransformPoint(vertices[sourceTriangles[index + 2]]);
                if (IsCenterWallTriangle(first, second, third, collisionBounds))
                {
                    removedTriangleCount++;
                    continue;
                }

                keptTriangles.Add(sourceTriangles[index]);
                keptTriangles.Add(sourceTriangles[index + 1]);
                keptTriangles.Add(sourceTriangles[index + 2]);
            }

            cleanedMesh.SetTriangles(keptTriangles, subMeshIndex);
        }

        cleanedMesh.RecalculateBounds();
        Directory.CreateDirectory(Path.GetDirectoryName(CenterWallRemovedMeshPath));
        AssetDatabase.DeleteAsset(CenterWallRemovedMeshPath);
        AssetDatabase.CreateAsset(cleanedMesh, CenterWallRemovedMeshPath);
        meshFilter.sharedMesh = cleanedMesh;
        Debug.Log($"[RoomIntegration] 中央墙网格裁剪完成，移除三角面 {removedTriangleCount} 个。");
    }

    private static bool IsCenterWallTriangle(Vector3 first, Vector3 second, Vector3 third, Bounds collisionBounds)
    {
        var triangleBounds = new Bounds(first, Vector3.zero);
        triangleBounds.Encapsulate(second);
        triangleBounds.Encapsulate(third);
        if (!triangleBounds.Intersects(collisionBounds))
        {
            return false;
        }

        var triangleNormal = Vector3.Cross(second - first, third - first).normalized;
        var isVerticalWallSurface = triangleBounds.size.y > 0.08f && Mathf.Abs(triangleNormal.y) < 0.35f;
        var isFloorLevelWallCap = triangleBounds.center.y < 0.35f && Mathf.Abs(triangleNormal.y) >= 0.35f;
        return triangleBounds.size.x < 1.4f &&
               (isVerticalWallSurface || isFloorLevelWallCap) &&
               triangleBounds.center.x >= collisionBounds.min.x &&
               triangleBounds.center.x <= collisionBounds.max.x &&
               triangleBounds.center.z >= collisionBounds.min.z &&
               triangleBounds.center.z <= collisionBounds.max.z;
    }

    private static void SetInactive(Transform target)
    {
        if (target != null)
        {
            target.gameObject.SetActive(false);
        }
    }

    private static void RemoveExistingRoom(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == "Room_Environment")
            {
                Object.DestroyImmediate(root);
            }
        }
    }

    private static void DisablePlaceholderGroup(Transform roomRoot, string childName)
    {
        var child = roomRoot.Find(childName);
        if (child != null)
        {
            child.gameObject.SetActive(false);
        }
    }

    private static void HideCollisionRenderers(Transform collisionsRoot)
    {
        if (collisionsRoot == null)
        {
            return;
        }

        foreach (var renderer in collisionsRoot.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = false;
        }
    }

    private static void HideOriginalEnvironmentMeshes(Scene scene)
    {
        var environment = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "Environment");
        if (environment == null)
        {
            return;
        }

        foreach (var renderer in environment.GetComponentsInChildren<MeshRenderer>(true))
        {
            renderer.enabled = false;
        }

        var oldDirectionalLight = environment.transform.Find("Directional Light");
        if (oldDirectionalLight != null)
        {
            oldDirectionalLight.gameObject.SetActive(false);
        }
    }

    private static void CaptureEntrancePreview()
    {
        CapturePreview(PreviewFileName, new Vector3(0f, 1.65f, -2.15f), new Vector3(0f, 1.15f, 6.5f), 68f);
    }

    private static void CapturePreview(string fileName, Vector3 cameraPosition, Vector3 lookTarget, float fieldOfView)
    {
        var previewCameraObject = new GameObject("__RoomIntegrationPreviewCamera");
        var previewCamera = previewCameraObject.AddComponent<Camera>();
        previewCamera.transform.position = cameraPosition;
        previewCamera.transform.LookAt(lookTarget);
        previewCamera.fieldOfView = fieldOfView;
        previewCamera.nearClipPlane = 0.05f;
        previewCamera.farClipPlane = 80f;

        var renderTexture = new RenderTexture(1600, 900, 24);
        var texture = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        var previousActive = RenderTexture.active;
        previewCamera.targetTexture = renderTexture;
        previewCamera.Render();
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
        texture.Apply();

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        File.WriteAllBytes(Path.Combine(projectRoot, fileName), texture.EncodeToPNG());

        previewCamera.targetTexture = null;
        RenderTexture.active = previousActive;
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(renderTexture);
        Object.DestroyImmediate(previewCameraObject);
    }

    private static void AppendObjectMeasurement(StringBuilder builder, string label, GameObject target)
    {
        builder.AppendLine($"[{label}]");
        if (target == null)
        {
            builder.AppendLine("missing=true");
            return;
        }

        AppendTransform(builder, "transform", target.transform);
        builder.AppendLine("children=" + string.Join(", ", Enumerable.Range(0, target.transform.childCount).Select(index => target.transform.GetChild(index).name)));
        var renderers = target.GetComponentsInChildren<Renderer>(true);
        builder.AppendLine($"rendererCount={renderers.Length}");
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1))
            {
                bounds.Encapsulate(renderer.bounds);
            }

            builder.AppendLine($"boundsCenter={Format(bounds.center)}");
            builder.AppendLine($"boundsSize={Format(bounds.size)}");
            builder.AppendLine($"boundsMin={Format(bounds.min)}");
            builder.AppendLine($"boundsMax={Format(bounds.max)}");
            foreach (var renderer in renderers.OrderByDescending(item => item.bounds.size.sqrMagnitude).Take(80))
            {
                builder.AppendLine($"renderer={GetPath(renderer.transform, target.transform)} | center={Format(renderer.bounds.center)} | size={Format(renderer.bounds.size)}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendTransform(StringBuilder builder, string label, Transform target)
    {
        if (target == null)
        {
            builder.AppendLine($"{label}=missing");
            return;
        }

        builder.AppendLine($"{label}.position={Format(target.position)}");
        builder.AppendLine($"{label}.rotation={Format(target.eulerAngles)}");
        builder.AppendLine($"{label}.scale={Format(target.lossyScale)}");
    }

    private static string Format(Vector3 value)
    {
        return $"({value.x:F4}, {value.y:F4}, {value.z:F4})";
    }

    private static string GetPath(Transform target, Transform root)
    {
        var path = target.name;
        while (target.parent != null && target.parent != root)
        {
            target = target.parent;
            path = target.name + "/" + path;
        }

        return path;
    }
}
