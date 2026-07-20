using System;

[Serializable]
/// <summary>
/// 保存导览任务的当前阶段和路线位置，由任务控制器推进并提供给请求与日志模块。
/// 每次有效状态转换都会递增版本号，以便拒绝基于旧状态生成的响应。
/// </summary>
public sealed class GuidanceRuntimeState
{
    /// <summary>当前任务运行阶段。</summary>
    public GuidanceRuntimePhase phase = GuidanceRuntimePhase.Idle;
    /// <summary>当前任务 ID，必须与实验条件和后端 schema 使用相同取值。</summary>
    public string taskId = "T2_main_target";
    /// <summary>用户当前已到达的展品 ID。</summary>
    public string currentExhibitId = "E1";
    /// <summary>路线中的下一目标 ID；为空表示路线已完成。</summary>
    public string nextTargetId = "E3";
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
    /// <summary>最近一次 Guidance Target 决策对应的状态版本。</summary>
    public int decisionStateVersion;
    /// <summary>当前任务中已创建的 Target Reservation 代际。</summary>
    public int guidanceGeneration;

    [NonSerialized] private TargetReservation activeReservation;

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
        decisionStateVersion = 0;
        guidanceGeneration = 0;
        activeReservation = null;
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

        if (target.TargetRole == NavigationTargetRole.NavigableDetour)
        {
            var sameActiveDetour = string.Equals(activeDetourId, target.TargetId, StringComparison.Ordinal);
            if (!sameActiveDetour &&
                (HasVisitedDetour(target.TargetId) || consecutiveDetourCount >= maxConsecutiveDetours))
            {
                return false;
            }

            if (!sameActiveDetour)
            {
                activeDetourId = target.TargetId;
                consecutiveDetourCount++;
            }
        }
        else if (target.TargetRole == NavigationTargetRole.TurnCueAnchor &&
                 HasCompletedTurnCue(target.TargetId))
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
    }

    private void AbandonActiveReservationWithoutVersion()
    {
        activeReservation?.Abandon();
        activeReservation = null;
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
    }

    private void ClearRedirectionHistory()
    {
        guidanceTargetId = string.Empty;
        activeDetourId = string.Empty;
        visitedDetourIds = Array.Empty<string>();
        completedTurnCueAnchorIds = Array.Empty<string>();
        consecutiveDetourCount = 0;
        decisionStateVersion = 0;
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
