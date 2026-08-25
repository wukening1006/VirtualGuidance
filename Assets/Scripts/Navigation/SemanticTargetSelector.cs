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
/// 按 T2 Candidate Policy 从任务、虚拟空间拓扑、角色和访问历史构建候选集合。
/// 候选集合来自场景中全部展品，再由任务策略和运行时安全事实筛选。
/// </summary>
public sealed class SemanticTargetSelector : MonoBehaviour
{
    [SerializeField] private NavigationTargetManager navigationTargets;
    [SerializeField] private PhysicalRiskEvaluator riskEvaluator;
    private readonly GuidanceActionPlanner actionPlanner = new GuidanceActionPlanner();

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>注入用于查询目标、关联展品及方向的导航索引。</summary>
    public void Configure(NavigationTargetManager targetManager, PhysicalRiskEvaluator evaluator = null)
    {
        if (targetManager != null)
        {
            navigationTargets = targetManager;
        }
        if (evaluator != null)
        {
            riskEvaluator = evaluator;
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
        var useSafeFilter = policy != null &&
                            policy.UseSafeCandidateFilter == ConditionCapabilityMode.Enabled;
        var source = useSafeFilter && candidateSet.AllowedCandidateIds.Count > 0
            ? candidateSet.AllowedCandidateIds
            : candidateSet.ModelCandidateIds;
        var targetId = source[0];
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
    /// 构建 T2 的 Base、Model、Assessment 和 Executable Candidate Set。
    /// </summary>
    public NavigationCandidateSet BuildCandidateSet(
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy,
        bool allowPausedForReset = false)
    {
        ResolveReferences();
        var canInspectCandidates = runtimeState != null &&
                                   (runtimeState.IsNavigating ||
                                    (allowPausedForReset &&
                                     runtimeState.phase == GuidanceRuntimePhase.PausedForReset));
        if (!canInspectCandidates || navigationTargets == null ||
            !ExperimentConditionMatrix.IsFormalTaskSupported(runtimeState.taskId))
        {
            return new NavigationCandidateSet(null, null, null, null, 0);
        }

        var candidates = new List<NavigationCandidate>();
        var assessments = new List<CandidateAssessment>();
        if (runtimeState.CanSelectFinalGoal &&
            !string.IsNullOrWhiteSpace(runtimeState.finalTargetId))
        {
            TryAddCandidate(
                candidates,
                assessments,
                runtimeState,
                riskState,
                runtimeState.finalTargetId,
                NavigationTargetRole.FinalTarget);
        }
        var graph = navigationTargets.SemanticGraph;
        if (!runtimeState.MustSelectFinalGoal && graph != null)
        {
            var routeRole = policy != null &&
                            policy.UseRdwAffordance == ConditionCapabilityMode.Enabled
                ? NavigationTargetRole.RouteSubgoal
                : NavigationTargetRole.NavigableDetour;
            foreach (var exhibit in graph.GetAllExhibits())
            {
                if (exhibit == null ||
                    (!string.IsNullOrWhiteSpace(runtimeState.currentExhibitId) &&
                     string.Equals(exhibit.id, runtimeState.currentExhibitId, StringComparison.Ordinal)) ||
                    string.Equals(exhibit.id, runtimeState.finalTargetId, StringComparison.Ordinal))
                {
                    continue;
                }
                TryAddCandidate(
                    candidates,
                    assessments,
                    runtimeState,
                    riskState,
                    exhibit.id,
                    routeRole);
            }
        }

        var candidateIds = new List<string>(candidates.Count);
        var allowedCandidateIds = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            candidateIds.Add(candidate.TargetId);
            var assessment = FindAssessment(assessments, candidate.TargetId);
            if (assessment != null && assessment.IsExecutable)
            {
                allowedCandidateIds.Add(candidate.TargetId);
            }
        }

        // 正式 OURS 先把完整基础候选交给 RDW Top-K 规划；
        // 其他启用安全过滤的条件仍在模型入口提前裁剪。
        var useSafeFilter = policy != null && policy.UseSafeCandidateFilter == ConditionCapabilityMode.Enabled;
        var useRdwTopKSelection = UsesRdwTopKCandidateSelection(policy);
        var modelCandidateIds = useRdwTopKSelection
            ? candidateIds
            : useSafeFilter ? allowedCandidateIds : candidateIds;
        return new NavigationCandidateSet(
            candidateIds,
            modelCandidateIds,
            allowedCandidateIds,
            candidates,
            navigationTargets.RegistryRevision,
            assessments);
    }

    /// <summary>
    /// 判断当前条件是否由 RDW Top-K 规划负责候选筛选。
    /// </summary>
    private static bool UsesRdwTopKCandidateSelection(ExperimentConditionPolicy policy)
    {
        if (policy == null)
        {
            return false;
        }

        var profile = ExperimentConditionMatrix.GetProfile(policy.Condition);
        return profile != null && profile.plannerMode == ExperimentPlannerMode.RdwGroundedTopK;
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
    /// 使用最新风险重验模型选中的单个冻结候选，不要求其他候选仍与请求时完全一致。
    /// </summary>
    /// <param name="reason">候选失效时返回可记录的具体原因；有效时为空。</param>
    /// <returns>请求上下文稳定，且所选候选的角色、方向、资格和 clearance 均仍安全时返回 true。</returns>
    public bool IsSelectedCandidateCurrent(
        CandidateSetReservation reservation,
        string selectedTargetId,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        ExperimentConditionPolicy policy,
        PhysicalRiskEvaluator riskEvaluator,
        out string reason)
    {
        ResolveReferences();
        reason = string.Empty;
        if (reservation == null || runtimeState == null || riskState == null ||
            navigationTargets == null || riskEvaluator == null)
        {
            reason = "Selected candidate revalidation dependencies are unavailable.";
            return false;
        }

        if (reservation.Phase != CandidateSetReservationPhase.Reserved ||
            reservation.TaskStateVersion != runtimeState.stateVersion ||
            !string.Equals(reservation.TaskId, runtimeState.taskId, StringComparison.Ordinal) ||
            !string.Equals(reservation.FinalTargetId, runtimeState.finalTargetId, StringComparison.Ordinal) ||
            !string.Equals(reservation.CalibrationId, riskState.calibration_id, StringComparison.Ordinal) ||
            !string.Equals(reservation.Condition, policy != null ? policy.Condition : string.Empty, StringComparison.Ordinal))
        {
            reason = "Selected candidate no longer matches the active task or calibration context.";
            return false;
        }

        if (reservation.RegistryRevision != navigationTargets.RegistryRevision ||
            !reservation.ContainsModelCandidate(selectedTargetId) ||
            !reservation.TryGetCandidate(selectedTargetId, out var frozenCandidate))
        {
            reason = "Selected candidate is not part of the active frozen candidate set.";
            return false;
        }

        var current = BuildCandidateSet(runtimeState, riskState, policy);
        NavigationCandidate currentCandidate = null;
        if (current != null)
        {
            foreach (var candidate in current.Candidates)
            {
                if (candidate != null && string.Equals(candidate.TargetId, selectedTargetId, StringComparison.Ordinal))
                {
                    currentCandidate = candidate;
                    break;
                }
            }
        }

        if (currentCandidate == null ||
            currentCandidate.TargetRole != frozenCandidate.TargetRole)
        {
            reason = "Selected candidate identity or role changed after the request.";
            return false;
        }

        var assessment = AssessCandidate(currentCandidate, riskState);
        if (!assessment.IsExecutable)
        {
            reason = $"Selected candidate is not executable under the latest physical risk snapshot: {assessment.Reason}.";
            return false;
        }

        return true;
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

        if (!TryBuildCandidate(targetId, NavigationTargetRole.FinalTarget, null, out var candidate))
        {
            return CandidateSafetyState.Unknown;
        }

        var state = AssessCandidate(candidate, riskState).ExecutionState;
        return state == CandidateExecutionState.Executable
            ? CandidateSafetyState.Eligible
            : state == CandidateExecutionState.Blocked
                ? CandidateSafetyState.Blocked
                : CandidateSafetyState.Unknown;
    }

    /// <summary>
    /// 使用最新位姿和风险重新评估指定候选。
    /// </summary>
    public bool TryAssessCandidate(
        string targetId,
        NavigationTargetRole role,
        PhysicalRiskState riskState,
        out CandidateAssessment assessment)
    {
        assessment = null;
        return TryBuildCandidate(targetId, role, null, out var candidate) &&
               (assessment = AssessCandidate(candidate, riskState)) != null;
    }

    private void ResolveReferences()
    {
        if (navigationTargets == null)
        {
            navigationTargets = FindObjectOfType<NavigationTargetManager>();
        }
        if (riskEvaluator == null)
        {
            riskEvaluator = FindObjectOfType<PhysicalRiskEvaluator>();
        }
    }

    private void TryAddCandidate(
        List<NavigationCandidate> candidates,
        List<CandidateAssessment> assessments,
        GuidanceRuntimeState runtimeState,
        PhysicalRiskState riskState,
        string targetId,
        NavigationTargetRole role)
    {
        if (!TryBuildCandidate(targetId, role, runtimeState != null ? runtimeState.currentExhibitId : null, out var candidate))
        {
            return;
        }

        var assessment = AssessCandidate(candidate, riskState);
        var exclusionReason = GetSemanticExclusionReason(runtimeState, targetId, role);
        if (!string.IsNullOrEmpty(exclusionReason))
        {
            assessments.Add(new CandidateAssessment(
                assessment.Candidate,
                assessment.Bearing,
                assessment.SuggestedAction,
                assessment.ExecutionState,
                assessment.Reason,
                false,
                exclusionReason));
            return;
        }

        candidates.Add(candidate);
        assessments.Add(assessment);
    }

    private bool TryBuildCandidate(
        string targetId,
        NavigationTargetRole role,
        string contextNodeId,
        out NavigationCandidate candidate)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(targetId) ||
            !navigationTargets.TryGetTarget(targetId, out var metadata) ||
            metadata == null ||
            !navigationTargets.TryGetTargetBearing(targetId, out var bearing) ||
            !navigationTargets.TryGetDefinition(targetId, out var definition))
        {
            return false;
        }

        candidate = new NavigationCandidate(
            targetId,
            role,
            BearingLabelProtocolMapper.ToProtocol(bearing.DisplayLabel),
            definition,
            GetSemanticEdges(contextNodeId, targetId),
            bearing.RelativeYawDegrees,
            bearing.DistanceMeters,
            bearing.DisplayLabel,
            bearing.PoseRevision);
        return true;
    }

    private IReadOnlyList<SemanticEdgeDefinition> GetSemanticEdges(string contextNodeId, string targetId)
    {
        var graph = navigationTargets != null ? navigationTargets.SemanticGraph : null;
        if (graph == null || string.IsNullOrWhiteSpace(contextNodeId)) return Array.Empty<SemanticEdgeDefinition>();
        var result = new List<SemanticEdgeDefinition>();
        foreach (var edge in graph.GetOutgoingSemanticEdges(contextNodeId))
        {
            if (string.Equals(edge.source, targetId, StringComparison.Ordinal) ||
                string.Equals(edge.target, targetId, StringComparison.Ordinal)) result.Add(edge);
        }
        return result;
    }

    private CandidateAssessment AssessCandidate(
        NavigationCandidate candidate,
        PhysicalRiskState riskState)
    {
        var bearing = new TargetBearing(
            candidate.TargetId,
            candidate.RelativeYawDegrees,
            candidate.DistanceMeters,
            candidate.DisplayLabel,
            candidate.PoseRevision);
        var action = actionPlanner.Plan(bearing);
        var executionState = EvaluateExactTargetDirection(candidate, riskState, out var reason);
        return new CandidateAssessment(candidate, bearing, action, executionState, reason);
    }

    private CandidateExecutionState EvaluateExactTargetDirection(
        NavigationCandidate candidate,
        PhysicalRiskState riskState,
        out string reason)
    {
        if (candidate == null || riskState == null || riskEvaluator == null ||
            !riskEvaluator.IsRiskIdentityCurrent(riskState))
        {
            reason = "exact_direction_facts_unavailable";
            return CandidateExecutionState.Unknown;
        }

        // Candidate Safety 始终评估目标的连续 physical direction，不读取离散 Profile 或方向标签。
        var physicalDirection = RotatePhysicalForward(
            riskState.user_physical_forward,
            candidate.RelativeYawDegrees);
        if (!riskEvaluator.TryEvaluateAbsoluteHeading(riskState, physicalDirection, out var heading))
        {
            reason = "exact_direction_evaluation_failed";
            return CandidateExecutionState.Unknown;
        }

        if (PhysicalRiskEvaluator.IsMovementExecutable(heading))
        {
            reason = string.Empty;
            return CandidateExecutionState.Executable;
        }

        reason = "exact_direction_physical_facts_invalid";
        return CandidateExecutionState.Blocked;
    }

    private static Vector2 RotatePhysicalForward(Vector2 forward, float yawDegrees)
    {
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector2.up;
        var radians = yawDegrees * Mathf.Deg2Rad;
        var sin = Mathf.Sin(radians);
        var cos = Mathf.Cos(radians);
        return new Vector2(
            forward.x * cos + forward.y * sin,
            -forward.x * sin + forward.y * cos).normalized;
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
                left[i].TargetRole != right[i].TargetRole)
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

    private static CandidateAssessment FindAssessment(
        IReadOnlyList<CandidateAssessment> assessments,
        string targetId)
    {
        foreach (var assessment in assessments)
        {
            if (assessment?.Candidate != null &&
                string.Equals(assessment.Candidate.TargetId, targetId, StringComparison.Ordinal))
            {
                return assessment;
            }
        }

        return null;
    }

    private static string GetSemanticExclusionReason(
        GuidanceRuntimeState runtimeState,
        string targetId,
        NavigationTargetRole role)
    {
        if (role == NavigationTargetRole.TurnCueAnchor)
        {
            return runtimeState.HasCompletedTurnCue(targetId) ? "turn_cue_completed" : string.Empty;
        }

        if (role != NavigationTargetRole.NavigableDetour)
        {
            if (role == NavigationTargetRole.RouteSubgoal &&
                runtimeState.visitedExhibitIds != null)
            {
                foreach (var visitedId in runtimeState.visitedExhibitIds)
                {
                    if (string.Equals(visitedId, targetId, StringComparison.Ordinal))
                    {
                        return "exhibit_visited";
                    }
                }
            }
            return string.Empty;
        }

        var sameActiveDetour = string.Equals(
            runtimeState.activeDetourId,
            targetId,
            System.StringComparison.Ordinal);
        if (sameActiveDetour) return string.Empty;
        if (runtimeState.HasVisitedDetour(targetId)) return "detour_visited";
        if (runtimeState.IsDetourCoolingDown(targetId)) return "detour_cooldown";
        return runtimeState.consecutiveDetourCount >= runtimeState.maxConsecutiveDetours
            ? "detour_limit_reached"
            : string.Empty;
    }
}
