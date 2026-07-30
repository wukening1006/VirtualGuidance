using System;
using System.Collections.Generic;

/// <summary>
/// 仅用于界面和兼容协议的目标方位标签；连续偏航角才是规划事实。
/// </summary>
public enum BearingLabel
{
    /// <summary>目标位于当前朝向前方。</summary>
    Forward,
    /// <summary>目标位于当前朝向左侧。</summary>
    Left,
    /// <summary>目标位于当前朝向右侧。</summary>
    Right,
    /// <summary>目标位于当前朝向后方。</summary>
    Behind
}

/// <summary>
/// 保存目标相对最新虚拟位姿的连续方位事实。
/// </summary>
public sealed class TargetBearing
{
    /// <summary>目标 ID。</summary>
    public string TargetId { get; }
    /// <summary>相对用户当前 Virtual Forward 的有符号偏航角，单位：度，范围 [-180, 180]。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>用户到目标锚点的水平距离，单位：米。</summary>
    public float DistanceMeters { get; }
    /// <summary>仅用于 UI 和兼容协议的离散标签。</summary>
    public BearingLabel DisplayLabel { get; }
    /// <summary>生成该事实时的虚拟位姿修订号。</summary>
    public int PoseRevision { get; }

    /// <summary>创建一项不可变目标方位事实。</summary>
    public TargetBearing(
        string targetId,
        float relativeYawDegrees,
        float distanceMeters,
        BearingLabel displayLabel,
        int poseRevision)
    {
        TargetId = targetId ?? string.Empty;
        RelativeYawDegrees = relativeYawDegrees;
        DistanceMeters = Math.Max(0f, distanceMeters);
        DisplayLabel = displayLabel;
        PoseRevision = Math.Max(0, poseRevision);
    }
}

/// <summary>
/// 统一管理内部方位标签与旧版方向字符串之间的协议映射。
/// </summary>
public static class BearingLabelProtocolMapper
{
    /// <summary>将内部方位标签映射为旧版 forward/left/right/behind 字符串。</summary>
    public static string ToProtocol(BearingLabel label)
    {
        switch (label)
        {
            case BearingLabel.Left: return "left";
            case BearingLabel.Right: return "right";
            case BearingLabel.Behind: return "behind";
            default: return "forward";
        }
    }
}

/// <summary>
/// 表示候选对应的原子动作在最新物理事实下的执行资格。
/// </summary>
public enum CandidateExecutionState
{
    /// <summary>缺少足够的物理事实，不能执行但可以继续规划。</summary>
    Unknown,
    /// <summary>当前动作满足物理安全约束。</summary>
    Executable,
    /// <summary>当前动作需要保持并等待新位姿或风险样本。</summary>
    TemporarilyBlocked,
    /// <summary>当前动作方向已明确不安全。</summary>
    Blocked
}

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
/// 保存一个已通过 T2 Candidate Policy 的导航候选及其模型语义元数据。
/// </summary>
public sealed class NavigationCandidate
{
    /// <summary>候选目标 ID。</summary>
    public string TargetId { get; }
    /// <summary>候选在当前任务决策中的职责。</summary>
    public NavigationTargetRole TargetRole { get; }
    /// <summary>候选相对用户当前 Virtual Forward 的方向。</summary>
    public string RelativeDirection { get; }
    /// <summary>候选相对请求位姿的连续偏航角，单位：度。</summary>
    public float RelativeYawDegrees { get; }
    /// <summary>候选距离请求位姿的水平距离，单位：米。</summary>
    public float DistanceMeters { get; }
    /// <summary>候选方向的显示标签。</summary>
    public BearingLabel DisplayLabel { get; }
    /// <summary>生成候选时的虚拟位姿修订号。</summary>
    public int PoseRevision { get; }
    /// <summary>候选显示名称。</summary>
    public string Name { get; }
    /// <summary>候选语义描述。</summary>
    public string Description { get; }
    /// <summary>候选在场景语义中的角色。</summary>
    public string SemanticRole { get; }
    /// <summary>候选语义标签的只读副本。</summary>
    public IReadOnlyList<string> Tags { get; }
    /// <summary>仅供模型理解的相关展品元数据，不参与候选资格判定。</summary>
    public IReadOnlyList<string> RelatedExhibits { get; }

    /// <summary>创建一项不可变导航候选。</summary>
    public NavigationCandidate(
        string targetId,
        NavigationTargetRole targetRole,
        string relativeDirection,
        string name,
        string description,
        string semanticRole,
        IEnumerable<string> tags,
        IEnumerable<string> relatedExhibits,
        float relativeYawDegrees = 0f,
        float distanceMeters = 0f,
        BearingLabel displayLabel = BearingLabel.Forward,
        int poseRevision = 0)
    {
        TargetId = targetId ?? string.Empty;
        TargetRole = targetRole;
        RelativeDirection = relativeDirection ?? string.Empty;
        Name = name ?? string.Empty;
        Description = description ?? string.Empty;
        SemanticRole = semanticRole ?? string.Empty;
        Tags = ReadOnlyCopy(tags);
        RelatedExhibits = ReadOnlyCopy(relatedExhibits);
        RelativeYawDegrees = relativeYawDegrees;
        DistanceMeters = Math.Max(0f, distanceMeters);
        DisplayLabel = displayLabel;
        PoseRevision = Math.Max(0, poseRevision);
    }

    private static IReadOnlyList<string> ReadOnlyCopy(IEnumerable<string> values)
    {
        return Array.AsReadOnly(new List<string>(values ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>
/// 将语义候选、连续方位、建议动作和当前执行资格组合为只读评估结果。
/// </summary>
public sealed class CandidateAssessment
{
    /// <summary>被评估的语义候选。</summary>
    public NavigationCandidate Candidate { get; }
    /// <summary>基于最新虚拟位姿的连续目标方位。</summary>
    public TargetBearing Bearing { get; }
    /// <summary>本地规划器生成的原子动作。</summary>
    public GuidanceAction SuggestedAction { get; }
    /// <summary>最新物理事实下的动作执行资格。</summary>
    public CandidateExecutionState ExecutionState { get; }
    /// <summary>不可执行或未知时的稳定原因码。</summary>
    public string Reason { get; }
    /// <summary>候选是否通过当前任务历史与角色规则。</summary>
    public bool IsSemanticallyEligible { get; }
    /// <summary>未通过语义资格时的稳定排除原因码。</summary>
    public string SemanticExclusionReason { get; }
    /// <summary>当前是否可立即执行建议动作。</summary>
    public bool IsExecutable => ExecutionState == CandidateExecutionState.Executable;

    /// <summary>创建一项不可变候选评估。</summary>
    public CandidateAssessment(
        NavigationCandidate candidate,
        TargetBearing bearing,
        GuidanceAction suggestedAction,
        CandidateExecutionState executionState,
        string reason,
        bool isSemanticallyEligible = true,
        string semanticExclusionReason = "")
    {
        Candidate = candidate;
        Bearing = bearing;
        SuggestedAction = suggestedAction;
        ExecutionState = executionState;
        Reason = reason ?? string.Empty;
        IsSemanticallyEligible = isSemanticallyEligible;
        SemanticExclusionReason = semanticExclusionReason ?? string.Empty;
    }
}

/// <summary>
/// 表示一次 T2 Candidate Builder 的不可变输出，不包含请求身份或生命周期状态。
/// </summary>
public sealed class NavigationCandidateSet
{
    /// <summary>完整基础候选 ID。</summary>
    public IReadOnlyList<string> CandidateIds { get; }
    /// <summary>当前 Condition 可交给模型选择的候选 ID。</summary>
    public IReadOnlyList<string> ModelCandidateIds { get; }
    /// <summary>应用 Unity RiskSnapshot 后允许执行的候选 ID。</summary>
    public IReadOnlyList<string> AllowedCandidateIds { get; }
    /// <summary>完整候选及其语义元数据。</summary>
    public IReadOnlyList<NavigationCandidate> Candidates { get; }
    /// <summary>每个基础候选的动作与当前执行资格。</summary>
    public IReadOnlyList<CandidateAssessment> Assessments { get; }
    /// <summary>构建候选时的场景 Target Registry 修订号。</summary>
    public int RegistryRevision { get; }

    /// <summary>创建 Candidate Builder 的冻结输出。</summary>
    public NavigationCandidateSet(
        IEnumerable<string> candidateIds,
        IEnumerable<string> modelCandidateIds,
        IEnumerable<string> allowedCandidateIds,
        IEnumerable<NavigationCandidate> candidates,
        int registryRevision,
        IEnumerable<CandidateAssessment> assessments = null)
    {
        CandidateIds = ReadOnlyCopy(candidateIds);
        ModelCandidateIds = ReadOnlyCopy(modelCandidateIds);
        AllowedCandidateIds = ReadOnlyCopy(allowedCandidateIds);
        Candidates = Array.AsReadOnly(new List<NavigationCandidate>(
            candidates ?? Array.Empty<NavigationCandidate>()).ToArray());
        Assessments = Array.AsReadOnly(new List<CandidateAssessment>(
            assessments ?? Array.Empty<CandidateAssessment>()).ToArray());
        RegistryRevision = Math.Max(0, registryRevision);
    }

    /// <summary>Candidate Builder 是否生成了可供模型选择的候选。</summary>
    public bool HasModelCandidates => ModelCandidateIds.Count > 0;

    private static IReadOnlyList<string> ReadOnlyCopy(IEnumerable<string> values)
    {
        return Array.AsReadOnly(new List<string>(values ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>Candidate-Set Reservation 的不可变生命周期状态。</summary>
public enum CandidateSetReservationPhase
{
    Reserved,
    Validated,
    Committed,
    Abandoned
}

/// <summary>
/// 冻结一次模型选点的候选、任务、校准、风险和请求身份；状态转换会创建新实例。
/// </summary>
public sealed class CandidateSetReservation
{
    /// <summary>冻结候选集合的唯一 ID。</summary>
    public string CandidateSetId { get; }
    /// <summary>绑定的单次请求 ID。</summary>
    public string RequestId { get; }
    /// <summary>绑定的规划决策 ID。</summary>
    public string DecisionId { get; }
    /// <summary>绑定的正式任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>预留创建时的任务状态版本。</summary>
    public int TaskStateVersion { get; }
    /// <summary>冻结的实验 Condition。</summary>
    public string Condition { get; }
    /// <summary>冻结的物理空间校准 ID。</summary>
    public string CalibrationId { get; }
    /// <summary>请求携带的 Unity RiskSnapshot ID。</summary>
    public string RiskSnapshotId { get; }
    /// <summary>执行相关风险修订号。</summary>
    public int RiskRevision { get; }
    /// <summary>T2 不变的最终目标 ID。</summary>
    public string FinalTargetId { get; }
    /// <summary>完整基础候选成员。</summary>
    public IReadOnlyList<string> CandidateIds { get; }
    /// <summary>本 Condition 可见的模型候选成员。</summary>
    public IReadOnlyList<string> ModelCandidateIds { get; }
    /// <summary>Unity 风险层允许执行的候选成员。</summary>
    public IReadOnlyList<string> AllowedCandidateIds { get; }
    /// <summary>候选角色、方向和语义元数据的冻结副本。</summary>
    public IReadOnlyList<NavigationCandidate> Candidates { get; }
    /// <summary>包含物理资格与语义排除原因的冻结诊断副本。</summary>
    public IReadOnlyList<CandidateAssessment> Assessments { get; }
    /// <summary>当前任务内的 Candidate Reservation 代际。</summary>
    public int Generation { get; }
    /// <summary>构建候选时的 Target Registry 修订号。</summary>
    public int RegistryRevision { get; }
    /// <summary>预留创建的 UTC ISO 8601 时间。</summary>
    public string CreatedAtUtc { get; }
    /// <summary>不可变生命周期实例的当前阶段。</summary>
    public CandidateSetReservationPhase Phase { get; }

    /// <summary>创建一项 Reserved Candidate-Set Reservation。</summary>
    public CandidateSetReservation(
        string candidateSetId,
        string requestId,
        string decisionId,
        string taskId,
        int taskStateVersion,
        string condition,
        string calibrationId,
        string riskSnapshotId,
        int riskRevision,
        string finalTargetId,
        NavigationCandidateSet candidateSet,
        int generation,
        string createdAtUtc)
        : this(
            candidateSetId,
            requestId,
            decisionId,
            taskId,
            taskStateVersion,
            condition,
            calibrationId,
            riskSnapshotId,
            riskRevision,
            finalTargetId,
            candidateSet != null ? candidateSet.CandidateIds : null,
            candidateSet != null ? candidateSet.ModelCandidateIds : null,
            candidateSet != null ? candidateSet.AllowedCandidateIds : null,
            candidateSet != null ? candidateSet.Candidates : null,
            candidateSet != null ? candidateSet.Assessments : null,
            generation,
            candidateSet != null ? candidateSet.RegistryRevision : 0,
            createdAtUtc,
            CandidateSetReservationPhase.Reserved)
    {
    }

    private CandidateSetReservation(
        string candidateSetId,
        string requestId,
        string decisionId,
        string taskId,
        int taskStateVersion,
        string condition,
        string calibrationId,
        string riskSnapshotId,
        int riskRevision,
        string finalTargetId,
        IEnumerable<string> candidateIds,
        IEnumerable<string> modelCandidateIds,
        IEnumerable<string> allowedCandidateIds,
        IEnumerable<NavigationCandidate> candidates,
        IEnumerable<CandidateAssessment> assessments,
        int generation,
        int registryRevision,
        string createdAtUtc,
        CandidateSetReservationPhase phase)
    {
        CandidateSetId = candidateSetId ?? string.Empty;
        RequestId = requestId ?? string.Empty;
        DecisionId = decisionId ?? string.Empty;
        TaskId = taskId ?? string.Empty;
        TaskStateVersion = Math.Max(0, taskStateVersion);
        Condition = condition ?? string.Empty;
        CalibrationId = calibrationId ?? string.Empty;
        RiskSnapshotId = riskSnapshotId ?? string.Empty;
        RiskRevision = Math.Max(0, riskRevision);
        FinalTargetId = finalTargetId ?? string.Empty;
        CandidateIds = ReadOnlyCopy(candidateIds);
        ModelCandidateIds = ReadOnlyCopy(modelCandidateIds);
        AllowedCandidateIds = ReadOnlyCopy(allowedCandidateIds);
        Candidates = Array.AsReadOnly(new List<NavigationCandidate>(
            candidates ?? Array.Empty<NavigationCandidate>()).ToArray());
        Assessments = Array.AsReadOnly(new List<CandidateAssessment>(
            assessments ?? Array.Empty<CandidateAssessment>()).ToArray());
        Generation = Math.Max(0, generation);
        RegistryRevision = Math.Max(0, registryRevision);
        CreatedAtUtc = createdAtUtc ?? string.Empty;
        Phase = phase;
    }

    /// <summary>返回同一冻结事实的下一生命周期实例。</summary>
    public CandidateSetReservation WithPhase(CandidateSetReservationPhase phase)
    {
        return new CandidateSetReservation(
            CandidateSetId,
            RequestId,
            DecisionId,
            TaskId,
            TaskStateVersion,
            Condition,
            CalibrationId,
            RiskSnapshotId,
            RiskRevision,
            FinalTargetId,
            CandidateIds,
            ModelCandidateIds,
            AllowedCandidateIds,
            Candidates,
            Assessments,
            Generation,
            RegistryRevision,
            CreatedAtUtc,
            phase);
    }

    /// <summary>检查另一实例是否代表同一次候选预留。</summary>
    public bool Matches(CandidateSetReservation other)
    {
        return other != null &&
               string.Equals(CandidateSetId, other.CandidateSetId, StringComparison.Ordinal) &&
               string.Equals(RequestId, other.RequestId, StringComparison.Ordinal) &&
               string.Equals(DecisionId, other.DecisionId, StringComparison.Ordinal) &&
               TaskStateVersion == other.TaskStateVersion &&
               Generation == other.Generation;
    }

    /// <summary>检查目标是否属于冻结的模型候选集合。</summary>
    public bool ContainsModelCandidate(string targetId) => Contains(ModelCandidateIds, targetId);

    /// <summary>按 ID 查询冻结的候选元数据。</summary>
    public bool TryGetCandidate(string targetId, out NavigationCandidate candidate)
    {
        foreach (var item in Candidates)
        {
            if (item != null && string.Equals(item.TargetId, targetId, StringComparison.Ordinal))
            {
                candidate = item;
                return true;
            }
        }

        candidate = null;
        return false;
    }

    private static bool Contains(IReadOnlyList<string> values, string expected)
    {
        if (values == null || string.IsNullOrWhiteSpace(expected)) return false;
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IReadOnlyList<string> ReadOnlyCopy(IEnumerable<string> values)
    {
        return Array.AsReadOnly(new List<string>(values ?? Array.Empty<string>()).ToArray());
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
