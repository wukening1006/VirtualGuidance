using System;

[Serializable]
public sealed class GuidanceRuntimeState
{
    public GuidanceRuntimePhase phase = GuidanceRuntimePhase.Idle;
    public string taskId = "T2_main_target";
    public string currentExhibitId = "E1";
    public string nextTargetId = "E3";
    public string finalTargetId = "E5";
    public int stateVersion;

    public bool IsCompleted => phase == GuidanceRuntimePhase.Completed;

    public void Begin(string nextTaskId, string startExhibitId, string firstTargetId, string finalTarget)
    {
        taskId = EmptyFallback(nextTaskId, taskId);
        currentExhibitId = EmptyFallback(startExhibitId, "E1");
        nextTargetId = EmptyFallback(firstTargetId, finalTarget);
        finalTargetId = EmptyFallback(finalTarget, nextTargetId);
        phase = GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    public void AdvanceTo(string arrivedTargetId, string followingTargetId)
    {
        currentExhibitId = EmptyFallback(arrivedTargetId, currentExhibitId);
        nextTargetId = followingTargetId;
        phase = string.IsNullOrWhiteSpace(nextTargetId) ? GuidanceRuntimePhase.Completed : GuidanceRuntimePhase.Navigating;
        stateVersion++;
    }

    public void PauseForReset()
    {
        phase = GuidanceRuntimePhase.PausedForReset;
        stateVersion++;
    }

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
