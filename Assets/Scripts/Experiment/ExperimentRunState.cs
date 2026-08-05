using System;

[Serializable]
public enum ExperimentRunState
{
    Preparing,
    Ready,
    Starting,
    Running,
    Paused,
    SafetyHold,
    Completing,
    Ended,
    Error,
    /// <summary>后端暂时不可用，实验冻结并等待自动恢复。</summary>
    BackendHold
}
