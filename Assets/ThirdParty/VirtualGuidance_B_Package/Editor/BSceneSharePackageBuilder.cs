using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// 将 B 部分的场馆环境、装饰和展柜视觉层整理为独立 Prefab，并导出可分享的 UnityPackage。
/// </summary>
public static class BSceneSharePackageBuilder
{
    private const string PackageFolder = "Assets/VirtualGuidance_B_Package";
    private const string PrefabFolder = PackageFolder + "/Prefabs";
    private const string PrefabPath = PrefabFolder + "/VirtualGuidance_B_Root.prefab";
    private const string ReadmePath = PackageFolder + "/README_B_Scene_Package.txt";
    private const string PackageRootName = "VirtualGuidance_B_Root";
    private const string ExportFolderName = "SharePackages/VirtualGuidance_B_ScenePackage";
    private const string ExportFileName = "VirtualGuidance_B_ScenePackage.unitypackage";
    private const string AutoBuildMarkerPath = "Temp/BuildBSceneSharePackage.flag";
    private static readonly Regex ExhibitIdPattern = new Regex(@"(?<![A-Za-z0-9])E[1-8](?![A-Za-z0-9])", RegexOptions.Compiled);

    [InitializeOnLoadMethod]
    private static void BuildWhenRequested()
    {
        if (!File.Exists(AutoBuildMarkerPath))
        {
            return;
        }

        File.Delete(AutoBuildMarkerPath);
        EditorApplication.delayCall += BuildSharePackage;
    }

    /// <summary>
    /// 从当前场景提取 B 部分视觉内容，并导出完整依赖包。
    /// </summary>
    [MenuItem("VirtualGuidance/B 部分/生成独立分享包")]
    public static void BuildSharePackage()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
        {
            Debug.LogError("[BSceneShare] 当前没有可用场景。");
            return;
        }

        EnsureAssetFolder(PackageFolder);
        EnsureAssetFolder(PrefabFolder);

        GameObject packageRoot = null;
        try
        {
            packageRoot = BuildPackageRoot(scene);
            PrefabUtility.SaveAsPrefabAsset(packageRoot, PrefabPath, out bool prefabSaved);
            if (!prefabSaved)
            {
                throw new InvalidOperationException($"无法保存根 Prefab：{PrefabPath}");
            }

            WriteReadme();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string exportDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExportFolderName));
            Directory.CreateDirectory(exportDirectory);
            string exportPath = Path.Combine(exportDirectory, ExportFileName);

            AssetDatabase.ExportPackage(
                new[] { PackageFolder },
                exportPath,
                ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log($"[BSceneShare] 分享包生成完成：{exportPath}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            if (packageRoot != null)
            {
                Object.DestroyImmediate(packageRoot);
            }
        }
    }

    /// <summary>
    /// 将分享包根 Prefab 放入当前场景，不覆盖已有实验逻辑。
    /// </summary>
    [MenuItem("VirtualGuidance/B 部分/将独立视觉包放入当前场景")]
    public static void InstallPackageRoot()
    {
        GameObject existingRoot = GameObject.Find(PackageRootName);
        if (existingRoot != null)
        {
            Selection.activeGameObject = existingRoot;
            EditorGUIUtility.PingObject(existingRoot);
            Debug.Log("[BSceneShare] 当前场景已经存在独立视觉包根物体。");
            return;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[BSceneShare] 找不到根 Prefab：{PrefabPath}");
            return;
        }

        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
        if (instance == null)
        {
            Debug.LogError("[BSceneShare] 根 Prefab 实例化失败。");
            return;
        }

        Undo.RegisterCreatedObjectUndo(instance, "安装 B 部分独立视觉包");
        instance.name = PackageRootName;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = instance;
        Debug.Log("[BSceneShare] 已将独立视觉包放入当前场景。");
    }

    private static GameObject BuildPackageRoot(Scene scene)
    {
        GameObject packageRoot = new GameObject(PackageRootName);
        packageRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        Transform environmentRoot = CreateGroup(packageRoot.transform, "Environment");
        Transform exhibitRoot = CreateGroup(packageRoot.transform, "ExhibitVisuals");

        CopySceneRoot(scene, "Room_Environment", environmentRoot);
        CopySceneRoot(scene, "Room_Environment_Dressing", environmentRoot);
        CopySceneRoot(scene, "B_Scene_V1", environmentRoot);

        List<Transform> showcaseRoots = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Where(transform => transform.name == "ShowcaseVisual")
            .OrderBy(transform => FindExhibitId(transform))
            .ThenBy(transform => transform.position.z)
            .ToList();

        if (showcaseRoots.Count != 8)
        {
            Debug.LogWarning($"[BSceneShare] 预期 8 个展柜视觉层，实际找到 {showcaseRoots.Count} 个。");
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Transform showcaseRoot in showcaseRoots)
        {
            string exhibitId = FindExhibitId(showcaseRoot);
            string anchorName = string.IsNullOrEmpty(exhibitId) ? "ExhibitVisual" : $"{exhibitId}_Visual";
            anchorName = MakeUniqueName(anchorName, usedNames);

            Transform anchor = CreateGroup(exhibitRoot, anchorName);
            anchor.SetPositionAndRotation(showcaseRoot.position, showcaseRoot.rotation);
            anchor.localScale = showcaseRoot.lossyScale;

            GameObject showcaseCopy = Object.Instantiate(showcaseRoot.gameObject);
            showcaseCopy.name = "ShowcaseVisual";
            showcaseCopy.transform.SetParent(anchor, false);
            showcaseCopy.transform.localPosition = Vector3.zero;
            showcaseCopy.transform.localRotation = Quaternion.identity;
            showcaseCopy.transform.localScale = Vector3.one;
        }

        return packageRoot;
    }

    private static void CopySceneRoot(Scene scene, string rootName, Transform destination)
    {
        GameObject source = scene.GetRootGameObjects().FirstOrDefault(root => root.name == rootName);
        if (source == null)
        {
            return;
        }

        GameObject copy = Object.Instantiate(source);
        copy.name = source.name;
        copy.transform.SetParent(destination, true);
    }

    private static string FindExhibitId(Transform source)
    {
        Transform current = source;
        while (current != null)
        {
            Match match = ExhibitIdPattern.Match(current.name);
            if (match.Success)
            {
                return match.Value;
            }

            current = current.parent;
        }

        return string.Empty;
    }

    private static string MakeUniqueName(string preferredName, ISet<string> usedNames)
    {
        if (usedNames.Add(preferredName))
        {
            return preferredName;
        }

        int suffix = 2;
        while (!usedNames.Add($"{preferredName}_{suffix}"))
        {
            suffix++;
        }

        return $"{preferredName}_{suffix}";
    }

    private static Transform CreateGroup(Transform parent, string name)
    {
        var group = new GameObject(name);
        group.transform.SetParent(parent, false);
        return group.transform;
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        string[] segments = folderPath.Split('/');
        string currentPath = segments[0];
        for (int index = 1; index < segments.Length; index++)
        {
            string nextPath = $"{currentPath}/{segments[index]}";
            if (!AssetDatabase.IsValidFolder(nextPath))
            {
                AssetDatabase.CreateFolder(currentPath, segments[index]);
            }

            currentPath = nextPath;
        }
    }

    private static void WriteReadme()
    {
        var content = new StringBuilder()
            .AppendLine("VirtualGuidance B 部分独立场景视觉包")
            .AppendLine("====================================")
            .AppendLine()
            .AppendLine("内容：")
            .AppendLine("1. VirtualGuidance_B_Root.prefab：统一根物体。")
            .AppendLine("2. Environment：room 场馆、灯具、书架、植物和休息区。")
            .AppendLine("3. ExhibitVisuals：E1-E8 玻璃展柜、实体模型和中文铭牌。")
            .AppendLine()
            .AppendLine("导入：")
            .AppendLine("1. 在目标项目中导入 VirtualGuidance_B_ScenePackage.unitypackage。")
            .AppendLine("2. 将 Prefabs/VirtualGuidance_B_Root.prefab 拖入场景根层级。")
            .AppendLine("3. 或使用菜单 VirtualGuidance > B 部分 > 将独立视觉包放入当前场景。")
            .AppendLine()
            .AppendLine("兼容说明：")
            .AppendLine("- 本包只提供视觉内容，不复制后端、导航、RDW、到达检测和实验状态逻辑。")
            .AppendLine("- 如目标场景已有 room 或旧展板，请先禁用对应旧视觉物体，避免重叠。")
            .AppendLine("- E1-E8 的展柜视觉保持原世界坐标，可与原实验节点对齐。")
            .ToString();

        File.WriteAllText(ReadmePath, content, new UTF8Encoding(true));
    }
}
