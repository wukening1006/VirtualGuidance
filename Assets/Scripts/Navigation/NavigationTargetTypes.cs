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

/// <summary>
/// Guidance Target 预留的生命周期状态。
/// </summary>
public enum TargetReservationPhase
{
    Reserved,
    Committed,
    Completed,
    Abandoned
}

/// <summary>
/// 将一次目标决策绑定到任务版本和代际，防止旧 Arrival 推进新决策。
/// </summary>
[Serializable]
public sealed class TargetReservation
{
    /// <summary>预留目标 ID。</summary>
    public string TargetId { get; }
    /// <summary>目标在本次决策中的职责。</summary>
    public NavigationTargetRole TargetRole { get; }
    /// <summary>创建预留的 Guidance 决策 ID。</summary>
    public string DecisionId { get; }
    /// <summary>预留创建时的任务状态版本。</summary>
    public int TaskStateVersion { get; }
    /// <summary>同一任务中的单调递增预留代际。</summary>
    public int Generation { get; }
    /// <summary>当前预留生命周期状态。</summary>
    public TargetReservationPhase Phase { get; private set; }

    /// <summary>创建一项新的 Reserved 目标预留。</summary>
    public TargetReservation(
        string targetId,
        NavigationTargetRole targetRole,
        string decisionId,
        int taskStateVersion,
        int generation)
    {
        TargetId = targetId;
        TargetRole = targetRole;
        DecisionId = decisionId;
        TaskStateVersion = taskStateVersion;
        Generation = generation;
        Phase = TargetReservationPhase.Reserved;
    }

    /// <summary>检查另一 token 是否代表同一次目标预留。</summary>
    public bool Matches(TargetReservation other)
    {
        return other != null &&
               string.Equals(TargetId, other.TargetId, StringComparison.Ordinal) &&
               TargetRole == other.TargetRole &&
               string.Equals(DecisionId, other.DecisionId, StringComparison.Ordinal) &&
               TaskStateVersion == other.TaskStateVersion &&
               Generation == other.Generation;
    }

    internal void Commit() => Phase = TargetReservationPhase.Committed;
    internal void Complete() => Phase = TargetReservationPhase.Completed;
    internal void Abandon() => Phase = TargetReservationPhase.Abandoned;
}
