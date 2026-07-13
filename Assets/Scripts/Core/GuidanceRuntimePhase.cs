using System;

[Serializable]
public enum GuidanceRuntimePhase
{
    Idle,
    Navigating,
    PausedForReset,
    Completed
}
