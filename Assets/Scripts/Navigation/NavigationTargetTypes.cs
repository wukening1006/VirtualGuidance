using System;

/// <summary>
/// 区分最终目标、原路线节点、只转向锚点和需要实际到达的安全绕行点。
/// </summary>
public enum NavigationTargetRole
{
    /// <summary>任务最终目标。</summary>
    FinalTarget,
    /// <summary>原始任务路线中的普通子目标。</summary>
    RouteSubgoal,
    /// <summary>只要求用户看向或转向，不触发普通到达进度。</summary>
    TurnCueAnchor,
    /// <summary>因物理安全需要临时实际到达，到达后重新规划原路线。</summary>
    NavigableDetour
}

/// <summary>
/// 表示当前真正用于 Guidance 的目标及其导航职责。
/// </summary>
[Serializable]
public sealed class GuidanceTarget
{
    /// <summary>目标展品或 Anchor ID。</summary>
    public string TargetId { get; }
    /// <summary>目标在当前决策中的职责。</summary>
    public NavigationTargetRole TargetRole { get; }

    /// <summary>创建一项不可变 Guidance Target。</summary>
    public GuidanceTarget(string targetId, NavigationTargetRole targetRole)
    {
        TargetId = targetId;
        TargetRole = targetRole;
    }
}
