using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 加载并切换 physical tracking space 配置，向风险计算和可视化模块提供当前空间。
/// </summary>
public sealed class PhysicalSpaceManager : MonoBehaviour
{
    [Header("默认空间")]
    [Tooltip("场景启动后默认切换到的 physical space ID，例如 wide_room。")]
    [SerializeField] private string defaultSpaceId = "wide_room";
    [Tooltip("配置文件夹相对于 Assets 的路径，例如 Configs。")]
    [SerializeField] private string configFolderRelativeToAssets = "Configs";
    [Tooltip("需要加载的 physical space JSON 文件名列表。")]
    [SerializeField] private string[] configFileNames =
    {
        "physical_wide_room.json",
        "physical_narrow_room.json",
        "physical_obstacle_room.json",
        "physical_l_shaped_room.json"
    };

    // 使用 ID 索引空间配置，保证 dashboard 或调试热键能稳定复现实验条件。
    private readonly Dictionary<string, PhysicalSpaceConfig> spacesById = new Dictionary<string, PhysicalSpaceConfig>();
    private PhysicalSpaceConfig currentSpace;

    /// <summary>
    /// 当前 physical space 切换后触发，供风险计算和边界可视化同步状态。
    /// </summary>
    public event Action<PhysicalSpaceConfig> CurrentSpaceChanged;

    /// <summary>
    /// 获取当前 physical tracking space 的 ID，未加载时返回空字符串。
    /// </summary>
    public string CurrentSpaceId => currentSpace != null ? currentSpace.id : string.Empty;

    /// <summary>
    /// 获取当前 physical tracking space 的完整配置。
    /// </summary>
    public PhysicalSpaceConfig CurrentSpace => currentSpace;

    /// <summary>
    /// 获取已加载空间配置的只读索引，供 dashboard 枚举可选实验条件。
    /// </summary>
    public IReadOnlyDictionary<string, PhysicalSpaceConfig> SpacesById => spacesById;

    private void Awake()
    {
        LoadSpaces();
        if (!string.IsNullOrWhiteSpace(defaultSpaceId))
        {
            if (!SetPhysicalSpace(defaultSpaceId))
            {
                Debug.LogWarning($"Default physical space could not be loaded: {defaultSpaceId}", this);
            }
        }
    }

    /// <summary>
    /// 从 Assets/Configs 读取所有 physical space JSON，作为后续实验模式切换的数据源。
    /// </summary>
    public void LoadSpaces()
    {
        spacesById.Clear();

        foreach (var fileName in configFileNames)
        {
            // 配置文件保存在 Assets 下，方便 Unity 和后端共享同一套实验空间定义。
            var path = Path.Combine(Application.dataPath, configFolderRelativeToAssets, fileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"Physical space config not found: {path}", this);
                continue;
            }

            var json = File.ReadAllText(path);
            if (!PhysicalSpaceConfigParser.TryParse(json, out var config, out var error))
            {
                Debug.LogWarning($"Failed to parse physical space config '{path}': {error}", this);
                continue;
            }

            spacesById[config.id] = config;
        }

        if (spacesById.Count == 0)
        {
            Debug.LogWarning("No physical space configs loaded.", this);
        }
    }

    /// <summary>
    /// 切换当前 physical tracking space，并通知依赖模块重新计算风险和可视化。
    /// </summary>
    /// <param name="id">目标 physical space ID。</param>
    /// <returns>切换成功返回 true；未知 ID 或配置未加载时返回 false。</returns>
    public bool SetPhysicalSpace(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (spacesById.Count == 0)
        {
            LoadSpaces();
        }

        if (!spacesById.TryGetValue(id, out var nextSpace))
        {
            Debug.LogWarning($"Unknown physical space id: {id}", this);
            return false;
        }

        currentSpace = nextSpace;
        CurrentSpaceChanged?.Invoke(currentSpace);
        return true;
    }

    /// <summary>
    /// 设置默认 physical space，并立即尝试切换，供测试或场景构建脚本初始化使用。
    /// </summary>
    /// <param name="id">新的默认 physical space ID。</param>
    public void ConfigureDefaultSpace(string id)
    {
        defaultSpaceId = id;
        SetPhysicalSpace(defaultSpaceId);
    }
}
