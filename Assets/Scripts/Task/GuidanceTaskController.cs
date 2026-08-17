using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 按 GuidanceTaskDefinition 初始化并推进展品路线，向请求模块提供当前任务上下文。
/// 到达、暂停和恢复会更新 GuidanceRuntimeState，并通知实验面板与日志订阅者。
/// </summary>
public sealed class GuidanceTaskController : MonoBehaviour
{
    [SerializeField] private GuidanceTaskDefinition defaultTask = new GuidanceTaskDefinition();

    private readonly GuidanceRuntimeState runtimeState = new GuidanceRuntimeState();
    private int routeIndex;

    /// <summary>
    /// 任务开始、到达有效目标、暂停或恢复后触发一次；参数为控制器持有的当前可变状态实例。
    /// </summary>
    public event Action<GuidanceRuntimeState> StateChanged;

    /// <summary>当前任务运行状态；由本控制器持有并在任务生命周期内复用。</summary>
    public GuidanceRuntimeState RuntimeState => runtimeState;
    /// <summary>当前任务使用的实验 Condition ID。</summary>
    public string CurrentCondition => defaultTask.condition;

    /// <summary>校验当前任务的所有展品 ID 是否存在于唯一静态语义图。</summary>
    public void ValidateSemanticReferences(StaticSceneSemanticGraph graph, IList<string> errors)
    {
        if (graph == null)
        {
            errors?.Add("task_references_graph_missing");
            return;
        }

        ValidateExhibitReference(graph, errors, defaultTask.startExhibitId, "task.start_exhibit_id");
        ValidateExhibitReference(graph, errors, defaultTask.finalTargetId, "task.final_target_id");
        if (defaultTask.routeTargetIds == null || defaultTask.routeTargetIds.Length == 0)
        {
            errors?.Add("task.route_target_ids_missing");
            return;
        }

        foreach (var targetId in defaultTask.routeTargetIds)
        {
            ValidateExhibitReference(graph, errors, targetId, "task.route_target_id");
        }
    }

    private static void ValidateExhibitReference(
        StaticSceneSemanticGraph graph,
        IList<string> errors,
        string exhibitId,
        string field)
    {
        if (string.IsNullOrWhiteSpace(exhibitId) || !graph.TryGetExhibit(exhibitId, out _))
        {
            errors?.Add("unknown_exhibit_reference:" + field + ":" + (exhibitId ?? string.Empty));
        }
    }

    /// <summary>
    /// 从配置路线的首个展品开始任务，并将第二个展品设为下一目标。
    /// </summary>
    public void BeginDefaultTask()
    {
        if (!ExperimentConditionMatrix.IsFormalTaskSupported(defaultTask.taskId))
        {
            throw new InvalidOperationException($"正式实验仅允许启动 T2_main_target，当前任务为 {defaultTask.taskId}。");
        }

        routeIndex = 0;
        runtimeState.Begin(
            defaultTask.taskId,
            defaultTask.startExhibitId,
            GetRouteTarget(1),
            defaultTask.finalTargetId,
            defaultTask.maxConsecutiveDetours);
        StateChanged?.Invoke(runtimeState);
    }

    /// <summary>
    /// 替换当前任务定义，但不开始任务；正式任务只能由 StartTask 显式启动。
    /// </summary>
    public void ConfigureTask(GuidanceTaskDefinition taskDefinition)
    {
        if (taskDefinition == null)
        {
            return;
        }

        if (!ExperimentConditionMatrix.IsFormalTaskSupported(taskDefinition.taskId))
        {
            throw new ArgumentException("正式实验仅允许配置 T2_main_target。", nameof(taskDefinition));
        }

        defaultTask = taskDefinition;
        routeIndex = 0;
        runtimeState.Reset();
        StateChanged?.Invoke(runtimeState);
    }

    /// <summary>使用当前任务定义显式开始任务。</summary>
    public void StartTask()
    {
        BeginDefaultTask();
    }

    /// <summary>
    /// 从当前 Candidate-Set Reservation 生成后端请求上下文；本方法不会隐式启动任务或提交目标。
    /// </summary>
    /// <param name="reservation">已冻结且仍处于 Reserved 的候选预留。</param>
    /// <returns>包含实验条件、任务位置和参与者信息的请求上下文。</returns>
    public GuidanceRequestContext BuildRequestContext(CandidateSetReservation reservation)
    {
        if (reservation == null || !runtimeState.MatchesActiveCandidateSetReservation(reservation))
        {
            return null;
        }

        var semanticCandidates = new GuidanceSemanticCandidate[reservation.ModelCandidateIds.Count];
        for (var i = 0; i < semanticCandidates.Length; i++)
        {
            reservation.TryGetCandidate(reservation.ModelCandidateIds[i], out var candidate);
            semanticCandidates[i] = GuidanceSemanticCandidate.From(candidate);
        }

        return new GuidanceRequestContext
        {
            request_id = reservation.RequestId,
            decision_id = reservation.DecisionId,
            candidate_set_id = reservation.CandidateSetId,
            state_version = reservation.TaskStateVersion,
            calibration_id = reservation.CalibrationId,
            risk_snapshot_id = reservation.RiskSnapshotId,
            condition = defaultTask.condition,
            task_id = runtimeState.taskId,
            current_exhibit = runtimeState.currentExhibitId,
            final_target = runtimeState.finalTargetId,
            preferred_task_target = runtimeState.nextTargetId,
            candidate_ids = Copy(reservation.CandidateIds),
            model_candidate_ids = Copy(reservation.ModelCandidateIds),
            allowed_candidate_ids = Copy(reservation.AllowedCandidateIds),
            semantic_candidates = semanticCandidates,
            participant_id = defaultTask.participantId,
            user_task = defaultTask.userTask
        };
    }

    /// <summary>
    /// 仅接受与当前 nextTargetId 一致的到达事件，并将路线推进一个节点。
    /// </summary>
    public void HandleArrived(string targetId)
    {
        if (!runtimeState.IsNavigating || string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        // 忽略旧目标或非当前目标的到达事件，防止异步检测使路线跨节点推进。
        if (!string.Equals(targetId, runtimeState.nextTargetId, StringComparison.Ordinal))
        {
            return;
        }

        if (defaultTask.routeTargetIds == null || defaultTask.routeTargetIds.Length == 0)
        {
            return;
        }

        routeIndex = Mathf.Min(routeIndex + 1, defaultTask.routeTargetIds.Length - 1);
        runtimeState.AdvanceTo(targetId, GetRouteTarget(routeIndex + 1));
        StateChanged?.Invoke(runtimeState);
    }

    /// <summary>将任务置为等待物理 reset，并发布一次状态变化。</summary>
    public void PauseForReset()
    {
        var previousVersion = runtimeState.stateVersion;
        runtimeState.PauseForReset();
        if (runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
    }

    /// <summary>尝试从 reset 暂停恢复，并发布恢复后的任务状态。</summary>
    public void ResumeAfterReset()
    {
        var previousVersion = runtimeState.stateVersion;
        runtimeState.Resume();
        if (runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
    }

    /// <summary>设置当前 Guidance Target，并应用 Detour 防循环规则。</summary>
    public bool TrySetGuidanceTarget(GuidanceTarget target)
    {
        var previousVersion = runtimeState.stateVersion;
        var accepted = runtimeState.TrySetGuidanceTarget(target);
        if (accepted && runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return accepted;
    }

    /// <summary>使用明确决策 ID 创建新的目标预留。</summary>
    public bool TryReserveGuidanceTarget(GuidanceTarget target, string decisionId)
    {
        var previousVersion = runtimeState.stateVersion;
        var accepted = runtimeState.TryReserveGuidanceTarget(target, decisionId);
        if (accepted && runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return accepted;
    }

    /// <summary>冻结 Candidate Set；该操作不修改 Current Subgoal 或任务状态版本。</summary>
    public bool TryReserveCandidateSet(
        NavigationCandidateSet candidateSet,
        string candidateSetId,
        string requestId,
        string decisionId,
        PhysicalRiskState riskState,
        out CandidateSetReservation reservation)
    {
        return runtimeState.TryReserveCandidateSet(
            candidateSet,
            candidateSetId,
            requestId,
            decisionId,
            defaultTask.condition,
            riskState,
            out reservation);
    }

    /// <summary>验证模型选择属于活动 Candidate Set，不修改 Current Subgoal。</summary>
    public bool TryValidateCandidateSelection(
        CandidateSetReservation reservation,
        string selectedTargetId,
        out NavigationCandidate selectedCandidate)
    {
        return runtimeState.TryValidateCandidateSelection(reservation, selectedTargetId, out selectedCandidate);
    }

    /// <summary>原子提交已验证候选并发布唯一一次任务状态变化。</summary>
    public bool CommitCurrentSubgoal(
        CandidateSetReservation reservation,
        NavigationCandidate selectedCandidate,
        out TargetReservation targetReservation)
    {
        var previousVersion = runtimeState.stateVersion;
        var committed = runtimeState.CommitCurrentSubgoal(reservation, selectedCandidate, out targetReservation);
        if (committed && runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return committed;
    }

    /// <summary>废弃未提交 Candidate Set；不改变任务版本或 Current Subgoal。</summary>
    public bool AbandonCandidateSetReservation()
    {
        return runtimeState.AbandonCandidateSetReservation();
    }

    /// <summary>确认当前目标预留进入 Guidance 执行。</summary>
    public bool CommitReservation(TargetReservation reservation)
    {
        return runtimeState.CommitReservation(reservation);
    }

    /// <summary>放弃当前目标预留并发布新的任务版本。</summary>
    public bool AbandonReservation()
    {
        var abandoned = runtimeState.AbandonReservation();
        if (abandoned)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return abandoned;
    }

    /// <summary>完成 NavigableDetour，但不推进原 RouteSubgoal。</summary>
    public bool HandleDetourArrived(string detourId)
    {
        var completed = runtimeState.CompleteDetour(detourId);
        if (completed)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return completed;
    }

    /// <summary>完成 TurnCueAnchor，只记录转向反馈而不改变任务位置。</summary>
    public bool HandleTurnCueCompleted(string anchorId)
    {
        var completed = runtimeState.CompleteTurnCue(anchorId);
        if (completed)
        {
            StateChanged?.Invoke(runtimeState);
        }
        return completed;
    }

    /// <summary>研究员暂停任务；暂停期间到达事件不会推进路线。</summary>
    public void PauseForResearcher()
    {
        var previousVersion = runtimeState.stateVersion;
        runtimeState.PauseForResearcher();
        if (runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
    }

    /// <summary>研究员恢复任务。</summary>
    public void ResumeAfterResearcherPause()
    {
        var previousVersion = runtimeState.stateVersion;
        runtimeState.Resume();
        if (runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
    }

    /// <summary>结束任务并废弃所有在途状态快照。</summary>
    public void EndTask()
    {
        var previousVersion = runtimeState.stateVersion;
        runtimeState.End();
        if (runtimeState.stateVersion != previousVersion)
        {
            StateChanged?.Invoke(runtimeState);
        }
    }

    private string GetRouteTarget(int index)
    {
        if (defaultTask.routeTargetIds == null || index < 0 || index >= defaultTask.routeTargetIds.Length)
        {
            return string.Empty;
        }

        return defaultTask.routeTargetIds[index];
    }

    private static string[] Copy(System.Collections.Generic.IReadOnlyList<string> values)
    {
        var result = new string[values != null ? values.Count : 0];
        for (var i = 0; i < result.Length; i++) result[i] = values[i];
        return result;
    }
}
