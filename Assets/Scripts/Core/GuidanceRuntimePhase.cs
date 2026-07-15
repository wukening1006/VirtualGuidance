using System;

[Serializable]
/// <summary>
/// 标识导览任务当前所处的运行阶段，用于约束请求、路线推进及 reset 后恢复行为。
/// </summary>
public enum GuidanceRuntimePhase
{
    /// <summary>任务尚未初始化，首次构建请求时可启动默认任务。</summary>
    Idle,
    /// <summary>任务已启动且仍有下一路线目标。</summary>
    Navigating,
    /// <summary>物理安全条件要求暂停，等待完成显式 reset。</summary>
    PausedForReset,
    /// <summary>路线中已无下一目标，任务结束。</summary>
    Completed
}
