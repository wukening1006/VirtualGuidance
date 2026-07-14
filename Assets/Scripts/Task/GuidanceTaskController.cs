using System;
using UnityEngine;

public sealed class GuidanceTaskController : MonoBehaviour
{
    [SerializeField] private GuidanceTaskDefinition defaultTask = new GuidanceTaskDefinition();

    private readonly GuidanceRuntimeState runtimeState = new GuidanceRuntimeState();
    private int routeIndex;

    public event Action<GuidanceRuntimeState> StateChanged;

    public GuidanceRuntimeState RuntimeState => runtimeState;

    private void Awake()
    {
        BeginDefaultTask();
    }

    public void BeginDefaultTask()
    {
        routeIndex = 0;
        runtimeState.Begin(defaultTask.taskId, defaultTask.startExhibitId, GetRouteTarget(1), defaultTask.finalTargetId);
        StateChanged?.Invoke(runtimeState);
    }

    public void ConfigureTask(GuidanceTaskDefinition taskDefinition)
    {
        if (taskDefinition == null)
        {
            return;
        }

        defaultTask = taskDefinition;
        BeginDefaultTask();
    }

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

    public void HandleArrived(string targetId)
    {
        if (runtimeState.IsCompleted || string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

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

    public void PauseForReset()
    {
        runtimeState.PauseForReset();
        StateChanged?.Invoke(runtimeState);
    }

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
