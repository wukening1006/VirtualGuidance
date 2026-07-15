using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 将场景中的 ExhibitMetadata 按 ID 建立索引，为语义选点、到达检测和本地安全校验提供查询。
/// 目标方向按展品观看点的世界 X 位置归类为 left、forward 或 right。
/// </summary>
public sealed class NavigationTargetManager : MonoBehaviour
{
    [SerializeField] private bool rebuildOnAwake = true;

    private readonly Dictionary<string, ExhibitMetadata> targetsById = new Dictionary<string, ExhibitMetadata>();

    private void Awake()
    {
        if (rebuildOnAwake)
        {
            RebuildIndex();
        }
    }

    /// <summary>
    /// 重新扫描当前已加载场景中的展品元数据；重复 ID 以后扫描到的对象为准。
    /// </summary>
    public void RebuildIndex()
    {
        targetsById.Clear();
        foreach (var exhibit in FindObjectsOfType<ExhibitMetadata>())
        {
            if (exhibit != null && !string.IsNullOrWhiteSpace(exhibit.Id))
            {
                targetsById[exhibit.Id] = exhibit;
            }
        }
    }

    /// <summary>检查目标 ID 是否存在；索引为空时会先自动重建。</summary>
    public bool HasTarget(string targetId)
    {
        EnsureIndex();
        return !string.IsNullOrWhiteSpace(targetId) && targetsById.ContainsKey(targetId);
    }

    /// <summary>
    /// 按 ID 获取展品元数据；索引为空时会先自动重建。
    /// </summary>
    /// <returns>找到目标时返回 true。</returns>
    public bool TryGetTarget(string targetId, out ExhibitMetadata target)
    {
        EnsureIndex();
        return targetsById.TryGetValue(targetId, out target);
    }

    /// <summary>
    /// 获取展品配置的相关目标 ID；目标不存在或未配置时返回空数组。
    /// </summary>
    public string[] GetRelatedTargetIds(string targetId)
    {
        return TryGetTarget(targetId, out var target) && target.RelatedExhibits != null
            ? target.RelatedExhibits
            : Array.Empty<string>();
    }

    /// <summary>
    /// 根据观看点世界 X 坐标返回 left、forward 或 right；未知目标降级为 forward。
    /// </summary>
    /// <returns>供 RDW 风险标签匹配使用的英文方向值。</returns>
    public string GetTargetDirection(string targetId)
    {
        // 保持 forward 回退可兼容缺少元数据的旧场景，最终仍由本地安全门校验该方向。
        if (!TryGetTarget(targetId, out var target) || target == null)
        {
            return "forward";
        }

        var anchor = target.ViewPoint != null ? target.ViewPoint : target.transform;
        if (anchor.position.x < -0.5f)
        {
            return "left";
        }

        if (anchor.position.x > 0.5f)
        {
            return "right";
        }

        return "forward";
    }

    private void EnsureIndex()
    {
        if (targetsById.Count == 0)
        {
            RebuildIndex();
        }
    }
}
