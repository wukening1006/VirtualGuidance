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
    Error
}
