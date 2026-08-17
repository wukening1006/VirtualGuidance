using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 将静态语义图与 MainScene Exhibit Anchor 按 ID 组合，为导航和本地安全校验提供查询。
/// 目标方向基于用户当前 Virtual Position/Forward 计算，不依赖世界坐标 X。
/// </summary>
public sealed class NavigationTargetManager : MonoBehaviour
{
    [SerializeField] private bool rebuildOnAwake = true;
    [Tooltip("提供当前用户 Virtual Position/Forward；未配置时自动查找。")]
    [SerializeField] private UserPoseTracker userPoseTracker;
    [Tooltip("Forward 方向允许的半角，单位：度。")]
    [SerializeField, Range(1f, 89f)] private float forwardHalfAngleDegrees = 30f;
    [Tooltip("目标进入 Forward 标签所需的半角，单位：度。")]
    [SerializeField, Range(1f, 89f)] private float forwardEnterHalfAngleDegrees = 25f;
    [Tooltip("目标退出 Forward 标签所需的半角，单位：度；必须大于进入阈值。")]
    [SerializeField, Range(1f, 89f)] private float forwardExitHalfAngleDegrees = 35f;
    [Tooltip("超过该绝对角度后归类为 Behind，单位：度。")]
    [SerializeField, Range(91f, 179f)] private float behindThresholdDegrees = 150f;

    private readonly Dictionary<string, ExhibitMetadata> targetsById = new Dictionary<string, ExhibitMetadata>();
    private StaticSceneSemanticGraph semanticGraph;
    private readonly Dictionary<string, BearingLabel> displayLabelsByTarget = new Dictionary<string, BearingLabel>();
    private string registrySignature = string.Empty;
    private int registryRevision;
    private int poseRevision;
    private Vector3 lastVirtualPosition;
    private Vector3 lastVirtualForward = Vector3.forward;
    private bool hasPoseSample;

    /// <summary>场景导航 Target Registry 内容发生变化时递增的修订号。</summary>
    public int RegistryRevision => registryRevision;

    private void Awake()
    {
        ResolveReferences();
        if (rebuildOnAwake)
        {
            RebuildIndex();
        }
    }

    /// <summary>注入用户虚拟位姿来源，供所有目标方向查询共享。</summary>
    public void Configure(UserPoseTracker poseTracker, StaticSceneSemanticGraph graph = null)
    {
        if (poseTracker != null)
        {
            userPoseTracker = poseTracker;
        }
        if (graph != null)
        {
            semanticGraph = graph;
        }
    }

    /// <summary>注入唯一静态语义图；不从 Anchor Inspector 读取内容语义。</summary>
    public void ConfigureSemanticGraph(StaticSceneSemanticGraph graph)
    {
        semanticGraph = graph;
    }

    /// <summary>当前使用的静态语义图。</summary>
    public StaticSceneSemanticGraph SemanticGraph => semanticGraph;

    /// <summary>
    /// 重新扫描当前已加载场景中的 Exhibit Anchor；重复 ID 会被启动校验报告。
    /// </summary>
    public void RebuildIndex()
    {
        targetsById.Clear();
        displayLabelsByTarget.Clear();
        foreach (var exhibit in FindObjectsOfType<ExhibitMetadata>())
        {
            if (exhibit != null && !string.IsNullOrWhiteSpace(exhibit.Id))
            {
                targetsById[exhibit.Id] = exhibit;
            }
        }

        var nextSignature = BuildRegistrySignature();
        if (!string.Equals(nextSignature, registrySignature, StringComparison.Ordinal))
        {
            registrySignature = nextSignature;
            registryRevision++;
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
    /// 从唯一静态语义图获取展品定义。
    /// </summary>
    public bool TryGetDefinition(string targetId, out SceneExhibitDefinition definition)
    {
        definition = null;
        return semanticGraph != null && semanticGraph.TryGetExhibit(targetId, out definition);
    }

    /// <summary>
    /// 根据用户当前 Virtual Forward 返回 left、forward、right 或 behind；无法解析时返回空字符串。
    /// </summary>
    /// <returns>供 RDW 风险标签匹配使用的英文方向值。</returns>
    public string GetTargetDirection(string targetId)
    {
        return TryGetTargetBearing(targetId, out var bearing)
            ? BearingLabelProtocolMapper.ToProtocol(bearing.DisplayLabel)
            : string.Empty;
    }

    /// <summary>返回当前注册目标 ID 的稳定排序副本。</summary>
    public string[] GetRegisteredTargetIds()
    {
        EnsureIndex();
        var ids = new List<string>(targetsById.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids.ToArray();
    }

    /// <summary>
    /// 尝试依据用户当前 Virtual Position/Forward 解析目标相对方向。
    /// </summary>
    /// <returns>目标、用户位姿与水平目标向量均有效时返回 true。</returns>
    public bool TryGetTargetDirection(string targetId, out string direction)
    {
        direction = string.Empty;
        if (!TryGetTargetBearing(targetId, out var bearing))
        {
            return false;
        }

        direction = BearingLabelProtocolMapper.ToProtocol(bearing.DisplayLabel);
        return true;
    }

    /// <summary>
    /// 尝试读取目标的连续偏航、水平距离、显示标签和虚拟位姿修订号。
    /// </summary>
    /// <returns>目标、用户位姿与水平目标向量均有效时返回 true。</returns>
    public bool TryGetTargetBearing(string targetId, out TargetBearing bearing)
    {
        bearing = null;
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

        UpdatePoseRevision(userPosition, userForward);
        var relativeYawDegrees = Vector3.SignedAngle(userForward.normalized, toTarget.normalized, Vector3.up);
        var previousLabel = displayLabelsByTarget.TryGetValue(targetId, out var cachedLabel)
            ? cachedLabel
            : ClassifyBearingLabel(relativeYawDegrees, BearingLabel.Forward, false);
        var displayLabel = ClassifyBearingLabel(relativeYawDegrees, previousLabel, true);
        displayLabelsByTarget[targetId] = displayLabel;
        bearing = new TargetBearing(
            targetId,
            relativeYawDegrees,
            toTarget.magnitude,
            displayLabel,
            poseRevision);
        return true;
    }

    /// <summary>
    /// 尝试计算目标相对用户当前 Virtual Forward 的有符号水平偏航，单位：度。
    /// </summary>
    /// <returns>目标、用户位姿与水平目标向量均有效时返回 true。</returns>
    public bool TryGetTargetRelativeYawDegrees(string targetId, out float relativeYawDegrees)
    {
        var result = TryGetTargetBearing(targetId, out var bearing);
        relativeYawDegrees = result ? bearing.RelativeYawDegrees : 0f;
        return result;
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
        return BearingLabelProtocolMapper.ToProtocol(
            ClassifyBearingLabel(angle, BearingLabel.Forward, false));
    }

    /// <summary>
    /// 使用 Forward 进入/退出滞回阈值生成显示标签，避免头部抖动造成标签反复切换。
    /// </summary>
    public BearingLabel ClassifyBearingLabel(
        float relativeYawDegrees,
        BearingLabel previousLabel,
        bool useHysteresis)
    {
        var absoluteAngle = Mathf.Abs(Mathf.DeltaAngle(0f, relativeYawDegrees));
        var forwardThreshold = useHysteresis && previousLabel == BearingLabel.Forward
            ? Mathf.Max(forwardEnterHalfAngleDegrees, forwardExitHalfAngleDegrees)
            : useHysteresis
                ? Mathf.Min(forwardEnterHalfAngleDegrees, forwardExitHalfAngleDegrees)
                : forwardHalfAngleDegrees;
        if (absoluteAngle <= forwardThreshold)
        {
            return BearingLabel.Forward;
        }

        if (absoluteAngle >= behindThresholdDegrees)
        {
            return BearingLabel.Behind;
        }

        return relativeYawDegrees < 0f ? BearingLabel.Left : BearingLabel.Right;
    }

    /// <summary>返回运行时调用方提供的默认目标职责；静态图不声明导航角色。</summary>
    public NavigationTargetRole GetSafetyTargetRole(string targetId)
    {
        return HasTarget(targetId) ? NavigationTargetRole.RouteSubgoal : NavigationTargetRole.NavigableDetour;
    }

    private string BuildRegistrySignature()
    {
        var ids = new List<string>(targetsById.Keys);
        ids.Sort(StringComparer.Ordinal);
        var entries = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            var target = targetsById[id];
            entries.Add(string.Join("~", id, target != null ? target.HierarchyPath : string.Empty));
        }
        return string.Join("|", entries);
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

    private void UpdatePoseRevision(Vector3 position, Vector3 forward)
    {
        var normalizedForward = forward.normalized;
        if (!hasPoseSample ||
            (position - lastVirtualPosition).sqrMagnitude > 0.000001f ||
            Vector3.Angle(lastVirtualForward, normalizedForward) > 0.01f)
        {
            poseRevision++;
            lastVirtualPosition = position;
            lastVirtualForward = normalizedForward;
            hasPoseSample = true;
        }
    }
}
