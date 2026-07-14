using System;

[Serializable]
public sealed class GuidanceTaskDefinition
{
    public string taskId = "T2_main_target";
    public string participantId = "P001";
    public string condition = "ours_full";
    public string userTask;
    public string startExhibitId = "E1";
    public string finalTargetId = "E5";
    public string[] routeTargetIds = { "E1", "E3", "E5" };
}
