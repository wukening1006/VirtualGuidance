using System;
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

    private void Awake()
    {
        BeginDefaultTask();
    }

    /// <summary>
    /// 从配置路线的首个展品开始任务，并将第二个展品设为下一目标。
    /// </summary>
    public void BeginDefaultTask()
    {
        routeIndex = 0;
        runtimeState.Begin(defaultTask.taskId, defaultTask.startExhibitId, GetRouteTarget(1), defaultTask.finalTargetId);
        StateChanged?.Invoke(runtimeState);
    }

    /// <summary>
    /// 替换当前任务定义并立即重新开始任务；null 输入保持现有任务不变。
    /// </summary>
    public void ConfigureTask(GuidanceTaskDefinition taskDefinition)
    {
        if (taskDefinition == null)
        {
            return;
        }

        defaultTask = taskDefinition;
        BeginDefaultTask();
    }

    /// <summary>
    /// 从当前任务状态生成后端请求上下文；Idle 状态会先启动默认任务。
    /// </summary>
    /// <param name="semanticNextTargetOverride">安全选择器给出的目标；为空时使用路线下一目标。</param>
    /// <returns>包含实验条件、任务位置和参与者信息的请求上下文。</returns>
    public GuidanceRequestContext BuildRequestContext(string semanticNextTargetOverride = null)
    {
        if (runtimeState.phase == GuidanceRuntimePhase.Idle)
        {
            BeginDefaultTask();
        }

        return new GuidanceRequestContext
        {
            condition = defaultTask.condition,
            task_id = runtimeState.taskId,
            current_exhibit = runtimeState.currentExhibitId,
            semantic_next_target = string.IsNullOrWhiteSpace(semanticNextTargetOverride)
                ? runtimeState.nextTargetId
                : semanticNextTargetOverride,
            participant_id = defaultTask.participantId,
            user_task = defaultTask.userTask
        };
    }

    /// <summary>
    /// 仅接受与当前 nextTargetId 一致的到达事件，并将路线推进一个节点。
    /// </summary>
    public void HandleArrived(string targetId)
    {
        if (runtimeState.IsCompleted || string.IsNullOrWhiteSpace(targetId))
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
        runtimeState.PauseForReset();
        StateChanged?.Invoke(runtimeState);
    }

    /// <summary>尝试从 reset 暂停恢复，并发布恢复后的任务状态。</summary>
    public void ResumeAfterReset()
    {
        runtimeState.Resume();
        StateChanged?.Invoke(runtimeState);
    }

    private string GetRouteTarget(int index)
    {
        if (defaultTask.routeTargetIds == null || index < 0 || index >= defaultTask.routeTargetIds.Length)
        {
            return string.Empty;
        }

        return defaultTask.routeTargetIds[index];
    }
}
