using System;

[Serializable]
/// <summary>
/// 保存导览任务的当前阶段和路线位置，由任务控制器推进并提供给请求与日志模块。
/// 每次有效状态转换都会递增版本号，以便拒绝基于旧状态生成的响应。
/// </summary>
public sealed class GuidanceRuntimeState
{
    private const double DetourCooldownSeconds = 3d;

    /// <summary>当前任务运行阶段。</summary>
    public GuidanceRuntimePhase phase = GuidanceRuntimePhase.Idle;
    /// <summary>当前任务 ID，必须与实验条件和后端 schema 使用相同取值。</summary>
    public string taskId = "T2_main_target";
    /// <summary>用户当前已到达的展品 ID。</summary>
    public string currentExhibitId = "E1";
    /// <summary>路线中的下一目标 ID；为空表示路线已完成。</summary>
    public string nextTargetId = "E5";
    /// <summary>本次任务的最终目标展品 ID。</summary>
    public string finalTargetId = "E5";
    /// <summary>状态转换版本，用于检测请求与响应之间的竞态。</summary>
    public int stateVersion;
    /// <summary>当前 Guidance Target ID。</summary>
    public string guidanceTargetId;
    /// <summary>当前 Guidance Target 的职责。</summary>
    public NavigationTargetRole guidanceTargetRole = NavigationTargetRole.RouteSubgoal;
    /// <summary>当前尚未完成的 NavigableDetour ID。</summary>
    public string activeDetourId;
    /// <summary>本轮路线决策中已经到达的 Detour ID。</summary>
    public string[] visitedDetourIds = Array.Empty<string>();
    /// <summary>本轮路线决策中已完成的 TurnCueAnchor ID。</summary>
    public string[] completedTurnCueAnchorIds = Array.Empty<string>();
    /// <summary>尚未回到原路线前连续使用的 Detour 数量。</summary>
    public int consecutiveDetourCount;
    /// <summary>允许的最大连续 Detour 数量。</summary>
    public int maxConsecutiveDetours = 2;
    /// <summary>当前处于短暂冷却的 Detour ID。</summary>
    public string[] coolingDownDetourIds = Array.Empty<string>();
    /// <summary>与 coolingDownDetourIds 对齐的 UTC 到期时刻，单位：Ticks。</summary>
    public long[] detourCooldownUntilUtcTicks = Array.Empty<long>();
    /// <summary>最近一次 Guidance Target 决策对应的状态版本。</summary>
    public int decisionStateVersion;
    /// <summary>当前任务中已创建的 Target Reservation 代际。</summary>
    public int guidanceGeneration;
    /// <summary>当前任务中已创建的 Candidate-Set Reservation 代际。</summary>
    public int candidateSetGeneration;

    [NonSerialized] private TargetReservation activeReservation;
    [NonSerialized] private CandidateSetReservation activeCandidateSetReservation;

    /// <summary>当前任务是否已经到达完成阶段。</summary>
    public bool IsCompleted => phase == GuidanceRuntimePhase.Completed;

    /// <summary>任务当前是否允许请求 guidance 或接受到达事件。</summary>
    public bool IsNavigating => phase == GuidanceRuntimePhase.Navigating;
    /// <summary>当前 Guidance Target；未选择时返回 null。</summary>
    public GuidanceTarget CurrentGuidanceTarget => string.IsNullOrWhiteSpace(guidanceTargetId)
        ? null
        : new GuidanceTarget(guidanceTargetId, guidanceTargetRole);
    /// <summary>当前有效的 Guidance Target Reservation。</summary>
    public TargetReservation ActiveReservation => activeReservation;
    /// <summary>当前是否存在尚未完成的语义目标锁。</summary>
    public bool HasLockedSemanticGoal => activeReservation != null &&
                                         activeReservation.Phase != TargetReservationPhase.Abandoned &&
                                         activeReservation.Phase != TargetReservationPhase.Completed &&
                                         !string.IsNullOrWhiteSpace(guidanceTargetId);
    /// <summary>当前是否存在尚未提交的语义候选集规划。</summary>
    public bool HasActiveSemanticPlanning => activeCandidateSetReservation != null &&
                                             (activeCandidateSetReservation.Phase == CandidateSetReservationPhase.Reserved ||
                                              activeCandidateSetReservation.Phase == CandidateSetReservationPhase.Validated);
    /// <summary>最近一次 Candidate-Set Reservation 及其不可变生命周期实例。</summary>
    public CandidateSetReservation ActiveCandidateSetReservation => activeCandidateSetReservation;

    /// <summary>清空运行任务并回到 Idle；仅在状态实际变化时递增版本。</summary>
    public void Reset()
    {
        if (phase == GuidanceRuntimePhase.Idle)
        {
            return;
        }

        AbandonActiveReservationWithoutVersion();
        phase = GuidanceRuntimePhase.Idle;
        stateVersion++;
    }

    /// <summary>
    /// 使用任务与路线起点初始化运行状态，并进入 Navigating 阶段。
    /// </summary>
    public void Begin(
        string nextTaskId,
        string startExhibitId,
        string firstTargetId,
        string finalTarget,
        int detourLimit = 2)
    {
        taskId = EmptyFallback(nextTaskId, taskId);
        currentExhibitId = EmptyFallback(startExhibitId, "E1");
        nextTargetId = EmptyFallback(firstTargetId, finalTarget);
        finalTargetId = EmptyFallback(finalTarget, nextTargetId);
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
        visitedDetourIds = Array.Empty<string>();
        completedTurnCueAnchorIds = Array.Empty<string>();
        consecutiveDetourCount = 0;
        maxConsecutiveDetours = Math.Max(0, detourLimit);
        coolingDownDetourIds = Array.Empty<string>();
        detourCooldownUntilUtcTicks = Array.Empty<long>();
        decisionStateVersion = 0;
        guidanceGeneration = 0;
        candidateSetGeneration = 0;
        activeReservation = null;
        activeCandidateSetReservation = null;
        phase = GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    /// <summary>
    /// 接受已确认到达的目标并推进路线；没有后续目标时进入 Completed。
    /// </summary>
    public void AdvanceTo(string arrivedTargetId, string followingTargetId)
    {
        CompleteActiveReservation();
        currentExhibitId = EmptyFallback(arrivedTargetId, currentExhibitId);
        nextTargetId = followingTargetId;
        ClearRedirectionHistory();
        phase = string.IsNullOrWhiteSpace(nextTargetId) ? GuidanceRuntimePhase.Completed : GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    /// <summary>
    /// 将任务标记为等待物理 reset，使在途 guidance 响应因版本变化而失效。
    /// </summary>
    public void PauseForReset()
    {
        if (phase == GuidanceRuntimePhase.PausedForReset || phase == GuidanceRuntimePhase.Ended)
        {
            return;
        }

        AbandonActiveReservationWithoutVersion();
        phase = GuidanceRuntimePhase.PausedForReset;
        stateVersion++;
    }

    /// <summary>
    /// 设置当前 Guidance Target，并执行 Detour 防重复与连续次数检查。
    /// </summary>
    /// <returns>目标可用于当前决策时返回 true。</returns>
    public bool TrySetGuidanceTarget(GuidanceTarget target)
    {
        return TryReserveGuidanceTarget(target, $"local-{guidanceGeneration + 1}");
    }

    /// <summary>使用决策 ID 创建新的 Guidance Target Reservation。</summary>
    public bool TryReserveGuidanceTarget(GuidanceTarget target, string decisionId)
    {
        if (target == null || string.IsNullOrWhiteSpace(target.TargetId) ||
            string.IsNullOrWhiteSpace(decisionId) || !IsNavigating)
        {
            return false;
        }

        if (activeReservation != null &&
            string.Equals(guidanceTargetId, target.TargetId, StringComparison.Ordinal) &&
            guidanceTargetRole == target.TargetRole &&
            string.Equals(activeReservation.DecisionId, decisionId, StringComparison.Ordinal))
        {
            return true;
        }

        if (HasLockedSemanticGoal || HasActiveSemanticPlanning)
        {
            return false;
        }

        if (target.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            var sameActiveDetour = string.Equals(activeDetourId, target.TargetId, StringComparison.Ordinal);
            if (!sameActiveDetour &&
                (HasVisitedDetour(target.TargetId) ||
                 IsDetourCoolingDown(target.TargetId) ||
                 consecutiveDetourCount >= maxConsecutiveDetours))
            {
                return false;
            }

            if (!sameActiveDetour)
            {
                activeDetourId = target.TargetId;
            }
        }
        else if (target.TargetRole == NavigationTargetRole.TurnCueAnchor &&
                 HasCompletedTurnCue(target.TargetId))
        {
            return false;
        }

        guidanceTargetId = target.TargetId;
        guidanceTargetRole = target.TargetRole;
        stateVersion++;
        decisionStateVersion = stateVersion;
        guidanceGeneration++;
        activeReservation = new TargetReservation(
            target.TargetId,
            target.TargetRole,
            decisionId,
            stateVersion,
            guidanceGeneration);
        return true;
    }

    /// <summary>在不修改 Current Subgoal 的前提下创建新的 Candidate-Set Reservation。</summary>
    public bool TryReserveCandidateSet(
        NavigationCandidateSet candidateSet,
        string candidateSetId,
        string requestId,
        string decisionId,
        string condition,
        PhysicalRiskState riskState,
        out CandidateSetReservation reservation)
    {
        reservation = null;
        if (HasLockedSemanticGoal || HasActiveSemanticPlanning ||
            !IsNavigating || candidateSet == null || !candidateSet.HasModelCandidates || riskState == null ||
            string.IsNullOrWhiteSpace(candidateSetId) || string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(decisionId) || string.IsNullOrWhiteSpace(condition) ||
            string.IsNullOrWhiteSpace(riskState.calibration_id) ||
            string.IsNullOrWhiteSpace(riskState.risk_snapshot_id) || riskState.risk_revision < 1)
        {
            return false;
        }

        if (activeCandidateSetReservation != null &&
            activeCandidateSetReservation.Phase != CandidateSetReservationPhase.Committed)
        {
            activeCandidateSetReservation = activeCandidateSetReservation.WithPhase(
                CandidateSetReservationPhase.Abandoned);
        }

        candidateSetGeneration++;
        activeCandidateSetReservation = new CandidateSetReservation(
            candidateSetId,
            requestId,
            decisionId,
            taskId,
            stateVersion,
            condition,
            riskState.calibration_id,
            riskState.risk_snapshot_id,
            riskState.risk_revision,
            finalTargetId,
            candidateSet,
            candidateSetGeneration,
            DateTime.UtcNow.ToString("o"));
        reservation = activeCandidateSetReservation;
        return true;
    }

    /// <summary>确认模型选择属于当前冻结 Candidate Set，但仍不修改 Current Subgoal。</summary>
    public bool TryValidateCandidateSelection(
        CandidateSetReservation reservation,
        string selectedTargetId,
        out NavigationCandidate selectedCandidate)
    {
        selectedCandidate = null;
        if (!MatchesActiveCandidateSetReservation(reservation) ||
            activeCandidateSetReservation.Phase != CandidateSetReservationPhase.Reserved ||
            stateVersion != activeCandidateSetReservation.TaskStateVersion ||
            !activeCandidateSetReservation.ContainsModelCandidate(selectedTargetId) ||
            !activeCandidateSetReservation.TryGetCandidate(selectedTargetId, out selectedCandidate))
        {
            return false;
        }

        activeCandidateSetReservation = activeCandidateSetReservation.WithPhase(
            CandidateSetReservationPhase.Validated);
        return true;
    }

    /// <summary>
    /// 将已验证候选原子提交为 Current Subgoal，并创建提交后的 Arrival Target Reservation。
    /// </summary>
    public bool CommitCurrentSubgoal(
        CandidateSetReservation reservation,
        NavigationCandidate selectedCandidate,
        out TargetReservation targetReservation)
    {
        targetReservation = null;
        if (selectedCandidate == null || !MatchesActiveCandidateSetReservation(reservation) ||
            activeCandidateSetReservation.Phase != CandidateSetReservationPhase.Validated ||
            stateVersion != activeCandidateSetReservation.TaskStateVersion ||
            !activeCandidateSetReservation.ContainsModelCandidate(selectedCandidate.TargetId))
        {
            return false;
        }

        if (selectedCandidate.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            var sameActiveDetour = string.Equals(activeDetourId, selectedCandidate.TargetId, StringComparison.Ordinal);
            if (!sameActiveDetour &&
                (HasVisitedDetour(selectedCandidate.TargetId) ||
                 IsDetourCoolingDown(selectedCandidate.TargetId) ||
                 consecutiveDetourCount >= maxConsecutiveDetours))
            {
                return false;
            }
        }
        else if (selectedCandidate.TargetRole == NavigationTargetRole.TurnCueAnchor &&
                 HasCompletedTurnCue(selectedCandidate.TargetId))
        {
            return false;
        }

        if (selectedCandidate.TargetRole == NavigationTargetRole.NavigableDetour &&
            !string.Equals(activeDetourId, selectedCandidate.TargetId, StringComparison.Ordinal))
        {
            activeDetourId = selectedCandidate.TargetId;
        }

        guidanceTargetId = selectedCandidate.TargetId;
        guidanceTargetRole = selectedCandidate.TargetRole;
        stateVersion++;
        decisionStateVersion = stateVersion;
        guidanceGeneration++;
        activeReservation = new TargetReservation(
            selectedCandidate.TargetId,
            selectedCandidate.TargetRole,
            activeCandidateSetReservation.DecisionId,
            stateVersion,
            guidanceGeneration);
        activeReservation.Commit();
        activeCandidateSetReservation = activeCandidateSetReservation.WithPhase(
            CandidateSetReservationPhase.Committed);
        targetReservation = activeReservation;
        return true;
    }

    /// <summary>检查 token 是否仍代表当前 Candidate-Set Reservation。</summary>
    public bool MatchesActiveCandidateSetReservation(CandidateSetReservation reservation)
    {
        return activeCandidateSetReservation != null &&
               activeCandidateSetReservation.Matches(reservation) &&
               activeCandidateSetReservation.Phase != CandidateSetReservationPhase.Abandoned;
    }

    /// <summary>废弃未提交 Candidate Set；Current Subgoal 保持不变。</summary>
    public bool AbandonCandidateSetReservation()
    {
        if (activeCandidateSetReservation == null ||
            activeCandidateSetReservation.Phase == CandidateSetReservationPhase.Abandoned ||
            activeCandidateSetReservation.Phase == CandidateSetReservationPhase.Committed)
        {
            return false;
        }

        activeCandidateSetReservation = activeCandidateSetReservation.WithPhase(
            CandidateSetReservationPhase.Abandoned);
        return true;
    }

    /// <summary>确认 Backend 响应对应的目标预留可以进入执行。</summary>
    public bool CommitReservation(TargetReservation reservation)
    {
        if (!MatchesActiveReservation(reservation) || activeReservation.Phase != TargetReservationPhase.Reserved)
        {
            return false;
        }

        activeReservation.Commit();
        return true;
    }

    /// <summary>检查 token 是否仍是当前活动目标预留。</summary>
    public bool MatchesActiveReservation(TargetReservation reservation)
    {
        return activeReservation != null && activeReservation.Matches(reservation) &&
               activeReservation.Phase != TargetReservationPhase.Abandoned &&
               activeReservation.Phase != TargetReservationPhase.Completed;
    }

    /// <summary>放弃当前目标预留，使旧 Arrival 立即失效。</summary>
    public bool AbandonReservation()
    {
        if (activeReservation == null)
        {
            return false;
        }

        activeReservation.Abandon();
        activeReservation = null;
        AbandonActiveCandidateSetReservation();
        StartActiveDetourCooldown();
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
        stateVersion++;
        return true;
    }

    /// <summary>
    /// 完成当前 NavigableDetour；更新当前位置但保留原 RouteSubgoal。
    /// </summary>
    public bool CompleteDetour(string detourId)
    {
        if (!IsNavigating || string.IsNullOrWhiteSpace(detourId) ||
            !string.Equals(activeDetourId, detourId, StringComparison.Ordinal))
        {
            return false;
        }

        CompleteActiveReservation();
        currentExhibitId = detourId;
        visitedDetourIds = AppendUnique(visitedDetourIds, detourId);
        consecutiveDetourCount++;
        RemoveDetourCooldown(detourId);
        activeDetourId = string.Empty;
        guidanceTargetId = string.Empty;
        stateVersion++;
        return true;
    }

    /// <summary>
    /// 完成只转向 Anchor；不改变当前位置或原 RouteSubgoal。
    /// </summary>
    public bool CompleteTurnCue(string anchorId)
    {
        if (!IsNavigating || guidanceTargetRole != NavigationTargetRole.TurnCueAnchor ||
            !string.Equals(guidanceTargetId, anchorId, StringComparison.Ordinal))
        {
            return false;
        }

        CompleteActiveReservation();
        completedTurnCueAnchorIds = AppendUnique(completedTurnCueAnchorIds, anchorId);
        guidanceTargetId = string.Empty;
        stateVersion++;
        return true;
    }

    /// <summary>检查 Detour 是否已经在本轮路线决策中到达。</summary>
    public bool HasVisitedDetour(string detourId)
    {
        return Contains(visitedDetourIds, detourId);
    }

    /// <summary>检查指定 Detour 是否仍处于放弃后的短暂冷却。</summary>
    public bool IsDetourCoolingDown(string detourId)
    {
        return IsDetourCoolingDown(detourId, DateTime.UtcNow);
    }

    /// <summary>按给定 UTC 时刻检查 Detour 冷却，供确定性测试与日志复验使用。</summary>
    public bool IsDetourCoolingDown(string detourId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(detourId) || coolingDownDetourIds == null ||
            detourCooldownUntilUtcTicks == null)
        {
            return false;
        }

        var count = Math.Min(coolingDownDetourIds.Length, detourCooldownUntilUtcTicks.Length);
        var nowTicks = utcNow.ToUniversalTime().Ticks;
        for (var index = 0; index < count; index++)
        {
            if (string.Equals(coolingDownDetourIds[index], detourId, StringComparison.Ordinal))
            {
                return detourCooldownUntilUtcTicks[index] > nowTicks;
            }
        }

        return false;
    }

    /// <summary>检查 TurnCueAnchor 是否已经在本轮路线决策中完成。</summary>
    public bool HasCompletedTurnCue(string anchorId)
    {
        return Contains(completedTurnCueAnchorIds, anchorId);
    }

    /// <summary>研究员暂停正在导航的任务。</summary>
    public void PauseForResearcher()
    {
        if (phase != GuidanceRuntimePhase.Navigating)
        {
            return;
        }

        AbandonActiveReservationWithoutVersion();
        phase = GuidanceRuntimePhase.PausedByResearcher;
        stateVersion++;
    }

    /// <summary>
    /// 仅从 PausedForReset 恢复，并依据是否仍有下一目标返回导航或完成阶段。
    /// </summary>
    public void Resume()
    {
        if (phase != GuidanceRuntimePhase.PausedForReset && phase != GuidanceRuntimePhase.PausedByResearcher)
        {
            return;
        }

        phase = string.IsNullOrWhiteSpace(nextTargetId) ? GuidanceRuntimePhase.Completed : GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    /// <summary>终止任务并使所有在途响应因版本变化失效。</summary>
    public void End()
    {
        if (phase == GuidanceRuntimePhase.Ended)
        {
            return;
        }

        AbandonActiveReservationWithoutVersion();
        phase = GuidanceRuntimePhase.Ended;
        stateVersion++;
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void CompleteActiveReservation()
    {
        activeReservation?.Complete();
        activeReservation = null;
        activeCandidateSetReservation = null;
    }

    private void AbandonActiveReservationWithoutVersion()
    {
        activeReservation?.Abandon();
        activeReservation = null;
        AbandonActiveCandidateSetReservation();
        StartActiveDetourCooldown();
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
    }

    private void AbandonActiveCandidateSetReservation()
    {
        if (activeCandidateSetReservation == null ||
            activeCandidateSetReservation.Phase == CandidateSetReservationPhase.Abandoned)
        {
            return;
        }

        activeCandidateSetReservation = activeCandidateSetReservation.WithPhase(
            CandidateSetReservationPhase.Abandoned);
    }

    private void ClearRedirectionHistory()
    {
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
        visitedDetourIds = Array.Empty<string>();
        completedTurnCueAnchorIds = Array.Empty<string>();
        consecutiveDetourCount = 0;
        coolingDownDetourIds = Array.Empty<string>();
        detourCooldownUntilUtcTicks = Array.Empty<long>();
        decisionStateVersion = 0;
    }

    private void StartActiveDetourCooldown()
    {
        if (guidanceTargetRole != NavigationTargetRole.NavigableDetour ||
            string.IsNullOrWhiteSpace(activeDetourId))
        {
            return;
        }

        var untilTicks = DateTime.UtcNow.AddSeconds(DetourCooldownSeconds).Ticks;
        var ids = coolingDownDetourIds ?? Array.Empty<string>();
        var ticks = detourCooldownUntilUtcTicks ?? Array.Empty<long>();
        var count = Math.Min(ids.Length, ticks.Length);
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(ids[index], activeDetourId, StringComparison.Ordinal))
            {
                continue;
            }

            ticks[index] = untilTicks;
            return;
        }

        var nextIds = new string[count + 1];
        var nextTicks = new long[count + 1];
        Array.Copy(ids, nextIds, count);
        Array.Copy(ticks, nextTicks, count);
        nextIds[count] = activeDetourId;
        nextTicks[count] = untilTicks;
        coolingDownDetourIds = nextIds;
        detourCooldownUntilUtcTicks = nextTicks;
    }

    private void RemoveDetourCooldown(string detourId)
    {
        var ids = coolingDownDetourIds ?? Array.Empty<string>();
        var ticks = detourCooldownUntilUtcTicks ?? Array.Empty<long>();
        var count = Math.Min(ids.Length, ticks.Length);
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(ids[index], detourId, StringComparison.Ordinal))
            {
                continue;
            }

            var nextIds = new string[count - 1];
            var nextTicks = new long[count - 1];
            if (index > 0)
            {
                Array.Copy(ids, 0, nextIds, 0, index);
                Array.Copy(ticks, 0, nextTicks, 0, index);
            }
            if (index < count - 1)
            {
                Array.Copy(ids, index + 1, nextIds, index, count - index - 1);
                Array.Copy(ticks, index + 1, nextTicks, index, count - index - 1);
            }

            coolingDownDetourIds = nextIds;
            detourCooldownUntilUtcTicks = nextTicks;
            return;
        }
    }

    private static bool Contains(string[] values, string target)
    {
        if (values == null || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value, target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] AppendUnique(string[] values, string value)
    {
        if (Contains(values, value))
        {
            return values;
        }

        var source = values ?? Array.Empty<string>();
        var result = new string[source.Length + 1];
        Array.Copy(source, result, source.Length);
        result[result.Length - 1] = value;
        return result;
    }
}
