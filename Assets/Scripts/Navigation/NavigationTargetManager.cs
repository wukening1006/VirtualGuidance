using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 将场景中的 ExhibitMetadata 按 ID 建立索引，为语义选点、到达检测和本地安全校验提供查询。
/// 目标方向基于用户当前 Virtual Position/Forward 计算，不依赖世界坐标 X。
/// </summary>
public sealed class NavigationTargetManager : MonoBehaviour
{
    [SerializeField] private bool rebuildOnAwake = true;
    [Tooltip("提供当前用户 Virtual Position/Forward；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("Forward 方向允许的半角，单位：度。")]
    [SerializeField, Range(1f, 89f)] private float forwardHalfAngleDegrees = 30f;
    [Tooltip("超过该绝对角度后归类为 Behind，单位：度。")]
    [SerializeField, Range(91f, 179f)] private float behindThresholdDegrees = 150f;

    private readonly Dictionary<string, ExhibitMetadata> targetsById = new Dictionary<string, ExhibitMetadata>();

    private void Awake()
    {
        ResolveReferences();
        if (rebuildOnAwake)
        {
            RebuildIndex();
        }
    }

    /// <summary>注入用户虚拟位姿来源，供所有目标方向查询共享。</summary>
    public void Configure(UserPoseTracker poseTracker)
    {
        if (poseTracker != null)
        {
            userPoseTracker = poseTracker;
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
    /// 根据用户当前 Virtual Forward 返回 left、forward、right 或 behind；无法解析时返回空字符串。
    /// </summary>
    /// <returns>供 RDW 风险标签匹配使用的英文方向值。</returns>
    public string GetTargetDirection(string targetId)
    {
        return TryGetTargetDirection(targetId, out var direction) ? direction : string.Empty;
    }

    /// <summary>
    /// 尝试依据用户当前 Virtual Position/Forward 解析目标相对方向。
    /// </summary>
    /// <returns>目标、用户位姿与水平目标向量均有效时返回 true。</returns>
    public bool TryGetTargetDirection(string targetId, out string direction)
    {
        direction = string.Empty;
        if (!TryGetTarget(targetId, out var target) || target == null)
        {
            return false;
        }

        ResolveReferences();
        if (userPoseTracker == null)
        {
            return false;
        }

        var anchor = target.ViewPoint != null ? target.ViewPoint : target.transform;
        var userPosition = userPoseTracker.UserVirtualPosition;
        var userForward = Vector3.ProjectOnPlane(userPoseTracker.UserVirtualForward, Vector3.up);
        var toTarget = Vector3.ProjectOnPlane(anchor.position - userPosition, Vector3.up);
        if (userForward.sqrMagnitude < 0.0001f || toTarget.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        direction = ClassifyRelativeDirection(userPosition, userForward, anchor.position);
        return !string.IsNullOrWhiteSpace(direction);
    }

    /// <summary>
    /// 使用水平面 SignedAngle 将目标归类为 forward、left、right 或 behind。
    /// </summary>
    public string ClassifyRelativeDirection(Vector3 userPosition, Vector3 userForward, Vector3 targetPosition)
    {
        var forward = Vector3.ProjectOnPlane(userForward, Vector3.up);
        var toTarget = Vector3.ProjectOnPlane(targetPosition - userPosition, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f || toTarget.sqrMagnitude < 0.0001f)
        {
            return "forward";
        }

        var angle = Vector3.SignedAngle(forward.normalized, toTarget.normalized, Vector3.up);
        var absoluteAngle = Mathf.Abs(angle);
        if (absoluteAngle <= forwardHalfAngleDegrees)
        {
            return "forward";
        }

        if (absoluteAngle >= behindThresholdDegrees)
        {
            return "behind";
        }

        return angle < 0f ? "left" : "right";
    }

    /// <summary>返回安全候选在场景元数据中声明的 TargetRole。</summary>
    public NavigationTargetRole GetSafetyTargetRole(string targetId)
    {
        return TryGetTarget(targetId, out var target) && target != null
            ? target.SafetyTargetRole
            : NavigationTargetRole.NavigableDetour;
    }

    private void EnsureIndex()
    {
        if (targetsById.Count == 0)
        {
            RebuildIndex();
        }
    }

    private void ResolveReferences()
    {
        if (userPoseTracker == null)
        {
            userPoseTracker = FindObjectOfType<UserPoseTracker>();
        }
    }
}
