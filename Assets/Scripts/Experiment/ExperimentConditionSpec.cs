using System;
using UnityEngine;

[Serializable]
public sealed class ExperimentConditionSpec
{
    public string participantId = "P001";
    public string condition = "ours_full";
    public string taskId = "T2_main_target";
    public string physicalSpaceId = "narrow_room";
    public string startExhibitId = "E1";
    public string semanticNextTargetId = "E5";
    public string finalTargetId = "E5";
    public string userTask;
    public string[] routeTargetIds = { "E1", "E3", "E5" };
    public Vector2 simulatedPhysicalPosition = new Vector2(1.8f, 4.3f);
    public float simulatedPhysicalYawDegrees;

    public GuidanceTaskDefinition ToTaskDefinition()
    {
        return new GuidanceTaskDefinition
        {
            taskId = taskId,
            participantId = participantId,
            condition = condition,
            userTask = userTask,
            startExhibitId = startExhibitId,
            finalTargetId = finalTargetId,
            routeTargetIds = routeTargetIds != null ? (string[])routeTargetIds.Clone() : Array.Empty<string>()
        };
    }
}
