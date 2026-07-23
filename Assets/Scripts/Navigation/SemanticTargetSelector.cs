using UnityEngine;

/// <summary>
/// 表示语义候选相对最新物理风险的可执行状态。
/// </summary>
public enum CandidateSafetyState
{
    /// <summary>方向未达到 safe 或 unsafe 阈值，不能默认选中。</summary>
    Unknown,
    /// <summary>方向已达到 safe 阈值，可以用于重规划。</summary>
    Eligible,
    /// <summary>方向已进入 unsafe 阈值，禁止选中。</summary>
    Blocked
}

/// <summary>
/// 将任务的语义下一目标与当前物理安全方向结合，选择本次 guidance 的可执行展品目标。
/// 候选来自当前展品的 related exhibits，结果提供给 GuidanceCoordinator。
/// </summary>
public sealed class SemanticTargetSelector : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入用于查询目标、关联展品及方向的导航索引。</summary>
    public void Configure(NavigationTargetManager targetManager)
    {
        if (targetManager != null)
        {
            navigationTargets = targetManager;
        }
    }

    /// <summary>
    /// 优先保留路线目标；其方向危险时依次选择明确安全、再选择未标危险的相关展品。
    /// </summary>
    /// <returns>可用于请求的展品 ID；无可接受候选时返回空字符串。</returns>
    public string SelectNextTarget(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        return SelectNextTarget(runtimeState, riskState, ExperimentConditionMatrix.GetPolicy("ours_full"));
    }

    /// <summary>
    /// 按 Condition Policy 决定是否在实验决策层应用安全候选筛选。
    /// </summary>
    /// <returns>策略允许筛选时返回安全候选；否则原样返回路线目标。</returns>
    public string SelectNextTarget(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        var target = SelectNextGuidanceTarget(runtimeState, riskState, policy);
        return target != null ? target.TargetId : string.Empty;
    }

    /// <summary>
    /// 返回带明确 TargetRole 的 Guidance 目标，避免 TurnCueAnchor 与 NavigableDetour 混用。
    /// </summary>
    public GuidanceTarget SelectNextGuidanceTarget(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        ResolveReferences();
        if (runtimeState == null)
        {
            return null;
        }

        var preferred = runtimeState.nextTargetId;
        var preferredRole = string.Equals(preferred, runtimeState.finalTargetId, System.StringComparison.Ordinal)
            ? NavigationTargetRole.FinalTarget
            : NavigationTargetRole.RouteSubgoal;
        if (policy == null || policy.UseSafeCandidateFilter != ConditionCapabilityMode.Enabled)
        {
            return new GuidanceTarget(preferred, preferredRole);
        }

        if (navigationTargets == null || riskState == null || string.IsNullOrWhiteSpace(preferred))
        {
            return null;
        }

        if (GetCandidateSafetyState(preferred, riskState) == CandidateSafetyState.Eligible)
        {
            return new GuidanceTarget(preferred, preferredRole);
        }

        var related = navigationTargets.GetRelatedTargetIds(runtimeState.currentExhibitId);
        // 明确列入 safe_directions 的候选优先于仅未被标记为危险的候选。
        foreach (var candidate in related)
        {
            var role = navigationTargets.GetSafetyTargetRole(candidate);
            if (navigationTargets.HasTarget(candidate) &&
                CanUseSafetyTarget(runtimeState, candidate, role) &&
                GetCandidateSafetyState(candidate, riskState) == CandidateSafetyState.Eligible)
            {
                return new GuidanceTarget(candidate, role);
            }
        }

        return null;
    }

    /// <summary>
    /// 根据风险计算器已有 safe/unsafe 阈值带判定候选状态；Unknown 不视为可执行。
    /// </summary>
    public CandidateSafetyState GetCandidateSafetyState(string targetId, PhysicalRiskState riskState)
    {
        ResolveReferences();
        if (navigationTargets == null ||
            riskState == null ||
            !navigationTargets.HasTarget(targetId))
        {
            return CandidateSafetyState.Unknown;
        }

        var direction = navigationTargets.GetTargetDirection(targetId);
        if (!IsNaturalDirection(direction))
        {
            return CandidateSafetyState.Blocked;
        }

        if (IsDirectionUnsafe(direction, riskState))
        {
            return CandidateSafetyState.Blocked;
        }

        return IsDirectionSafe(direction, riskState)
            ? CandidateSafetyState.Eligible
            : CandidateSafetyState.Unknown;
    }

    private void ResolveReferences()
    {
        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
    }

    private static bool IsDirectionUnsafe(string direction, PhysicalRiskState riskState)
    {
        return Contains(riskState != null ? riskState.unsafe_directions : null, direction);
    }

    private static bool IsDirectionSafe(string direction, PhysicalRiskState riskState)
    {
        return Contains(riskState != null ? riskState.safe_directions : null, direction);
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<string> values, string value)
    {
        if (values == null || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var item in values)
        {
            if (item == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNaturalDirection(string direction)
    {
        return direction == "forward" || direction == "left" || direction == "right";
    }

    private static bool CanUseSafetyTarget(
        GuidanceRuntimeState runtimeState,
        string targetId,
        NavigationTargetRole role)
    {
        if (role == NavigationTargetRole.TurnCueAnchor)
        {
            return !runtimeState.HasCompletedTurnCue(targetId);
        }

        if (role != NavigationTargetRole.NavigableDetour)
        {
            return true;
        }

        var sameActiveDetour = string.Equals(
            runtimeState.activeDetourId,
            targetId,
            System.StringComparison.Ordinal);
        return sameActiveDetour ||
               (!runtimeState.HasVisitedDetour(targetId) &&
                runtimeState.consecutiveDetourCount < runtimeState.maxConsecutiveDetours);
    }
}
