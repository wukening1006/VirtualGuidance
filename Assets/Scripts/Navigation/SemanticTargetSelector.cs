using System;
using System.Collections.Generic;
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
/// 按 T2 Candidate Policy 从任务、Target Registry、角色和访问历史构建候选集合。
/// related_exhibits 只附加为模型语义元数据，不参与候选资格判定。
/// </summary>
public sealed class SemanticTargetSelector : MonoBehaviour
{
    private static readonly string[] T2DetourTargetIds = { "E3", "E4" };

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

    /// <summary>兼容诊断调用：构建完整候选后返回第一个模型可见目标。</summary>
    public string SelectNextTarget(GuidanceRuntimeState runtimeState, PhysicalRiskState riskState)
    {
        return SelectNextTarget(runtimeState, riskState, ExperimentConditionMatrix.GetPolicy("ours_full"));
    }

    /// <summary>兼容诊断调用：按 Condition 构建候选后返回第一个模型可见目标。</summary>
    public string SelectNextTarget(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        var target = SelectNextGuidanceTarget(runtimeState, riskState, policy);
        return target != null ? target.TargetId : string.Empty;
    }

    /// <summary>兼容运行期安全探针：返回第一个模型可见候选及其职责。</summary>
    public GuidanceTarget SelectNextGuidanceTarget(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        var candidateSet = BuildCandidateSet(runtimeState, riskState, policy);
        if (candidateSet == null || !candidateSet.HasModelCandidates) return null;
        var targetId = candidateSet.ModelCandidateIds[0];
        foreach (var candidate in candidateSet.Candidates)
        {
            if (candidate != null && string.Equals(candidate.TargetId, targetId, StringComparison.Ordinal))
            {
                return new GuidanceTarget(candidate.TargetId, candidate.TargetRole);
            }
        }
        return null;
    }

    /// <summary>
    /// 构建 T2 的 Base、Model 和 Allowed Candidate Set；E3/E4 是唯一临时 Detour，E5 始终为 Final Goal。
    /// </summary>
    public NavigationCandidateSet BuildCandidateSet(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        ResolveReferences();
        if (runtimeState == null || navigationTargets == null || !runtimeState.IsNavigating ||
            !string.Equals(runtimeState.taskId, "T2_main_target", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(runtimeState.finalTargetId))
        {
            return new NavigationCandidateSet(null, null, null, null, 0);
        }

        var candidates = new List<NavigationCandidate>();
        TryAddCandidate(candidates, runtimeState, runtimeState.finalTargetId, NavigationTargetRole.FinalTarget);
        foreach (var detourId in T2DetourTargetIds)
        {
            TryAddCandidate(candidates, runtimeState, detourId, navigationTargets.GetSafetyTargetRole(detourId));
        }

        var candidateIds = new List<string>(candidates.Count);
        var allowedCandidateIds = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            candidateIds.Add(candidate.TargetId);
            if (GetCandidateSafetyState(candidate.TargetId, riskState) == CandidateSafetyState.Eligible)
            {
                allowedCandidateIds.Add(candidate.TargetId);
            }
        }

        var useSafeFilter = policy != null &&
                            policy.UseSafeCandidateFilter == ConditionCapabilityMode.Enabled;
        var modelCandidateIds = useSafeFilter
            ? allowedCandidateIds
            : candidateIds;
        return new NavigationCandidateSet(
            candidateIds,
            modelCandidateIds,
            useSafeFilter ? allowedCandidateIds : Array.Empty<string>(),
            candidates,
            navigationTargets.RegistryRevision);
    }

    /// <summary>重新构建候选并确认冻结 Reservation 的成员、角色和方向仍然一致。</summary>
    public bool IsReservationCurrent(
        CandidateSetReservation reservation,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy)
    {
        if (reservation == null || runtimeState == null || riskState == null ||
            reservation.TaskStateVersion != runtimeState.stateVersion ||
            reservation.RiskRevision != riskState.risk_revision ||
            !string.Equals(reservation.CalibrationId, riskState.calibration_id, StringComparison.Ordinal) ||
            !string.Equals(reservation.Condition, policy != null ? policy.Condition : string.Empty, StringComparison.Ordinal))
        {
            return false;
        }

        var current = BuildCandidateSet(runtimeState, riskState, policy);
        return current != null &&
               reservation.RegistryRevision == current.RegistryRevision &&
               SameValues(reservation.CandidateIds, current.CandidateIds) &&
               SameValues(reservation.ModelCandidateIds, current.ModelCandidateIds) &&
               SameValues(reservation.AllowedCandidateIds, current.AllowedCandidateIds) &&
               SameCandidates(reservation.Candidates, current.Candidates);
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

    private void TryAddCandidate(
        List<NavigationCandidate> candidates,
        GuidanceRuntimeState runtimeState,
        string targetId,
        NavigationTargetRole role)
    {
        if (string.IsNullOrWhiteSpace(targetId) || !navigationTargets.TryGetTarget(targetId, out var metadata) ||
            metadata == null || !CanUseSafetyTarget(runtimeState, targetId, role) ||
            !navigationTargets.TryGetTargetDirection(targetId, out var direction) || !IsNaturalDirection(direction))
        {
            return;
        }

        candidates.Add(new NavigationCandidate(
            targetId,
            role,
            direction,
            metadata.ExhibitName,
            metadata.Description,
            metadata.SemanticRole,
            metadata.Tags,
            metadata.RelatedExhibits));
    }

    private static bool SameCandidates(
        System.Collections.Generic.IReadOnlyList<NavigationCandidate> left,
        System.Collections.Generic.IReadOnlyList<NavigationCandidate> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] == null || right[i] == null ||
                !string.Equals(left[i].TargetId, right[i].TargetId, StringComparison.Ordinal) ||
                left[i].TargetRole != right[i].TargetRole ||
                !string.Equals(left[i].RelativeDirection, right[i].RelativeDirection, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SameValues(
        System.Collections.Generic.IReadOnlyList<string> left,
        System.Collections.Generic.IReadOnlyList<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }
        return true;
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
