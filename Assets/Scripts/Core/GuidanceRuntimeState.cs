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

    /// <summary>当前任务是否已经到达完成阶段。</summary>
    public bool IsCompleted => phase == GuidanceRuntimePhase.Completed;

    /// <summary>
    /// 使用任务与路线起点初始化运行状态，并进入 Navigating 阶段。
    /// </summary>
    public void Begin(string nextTaskId, string startExhibitId, string firstTargetId, string finalTarget)
    {
        taskId = EmptyFallback(nextTaskId, taskId);
        currentExhibitId = EmptyFallback(startExhibitId, "E1");
        nextTargetId = EmptyFallback(firstTargetId, finalTarget);
        finalTargetId = EmptyFallback(finalTarget, nextTargetId);
        phase = GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    /// <summary>
    /// 接受已确认到达的目标并推进路线；没有后续目标时进入 Completed。
    /// </summary>
    public void AdvanceTo(string arrivedTargetId, string followingTargetId)
    {
        currentExhibitId = EmptyFallback(arrivedTargetId, currentExhibitId);
        nextTargetId = followingTargetId;
        phase = string.IsNullOrWhiteSpace(nextTargetId) ? GuidanceRuntimePhase.Completed : GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    /// <summary>
    /// 将任务标记为等待物理 reset，使在途 guidance 响应因版本变化而失效。
    /// </summary>
    public void PauseForReset()
    {
        phase = GuidanceRuntimePhase.PausedForReset;
        stateVersion++;
    }

    /// <summary>
    /// 仅从 PausedForReset 恢复，并依据是否仍有下一目标返回导航或完成阶段。
    /// </summary>
    public void Resume()
    {
        if (phase != GuidanceRuntimePhase.PausedForReset)
        {
            return;
        }

        phase = string.IsNullOrWhiteSpace(nextTargetId) ? GuidanceRuntimePhase.Completed : GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
