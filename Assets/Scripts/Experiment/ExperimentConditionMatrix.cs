using System;
using UnityEngine;

public static class ExperimentConditionMatrix
{
    public static readonly string[] Conditions =
    {
        "B1_mllm_only",
        "B2_raw_physical_data",
        "B3_risk_labels",
        "B4_scripted_reset",
        "B5_ours_without_verifier",
        "ours_full"
    };

    public static readonly string[] TaskIds =
    {
        "T1_overview",
        "T2_main_target",
        "T3_compare",
        "T4_next_step"
    };

    public static readonly string[] PhysicalSpaces =
    {
        "wide_room",
        "narrow_room",
        "obstacle_room",
        "l_shaped_room"
    };

    public static ExperimentConditionSpec Build(
        int conditionIndex,
        int taskIndex,
        int spaceIndex,
        string participantId)
    {
        var condition = Conditions[Wrap(conditionIndex, Conditions.Length)];
        var taskId = TaskIds[Wrap(taskIndex, TaskIds.Length)];
        var spaceId = PhysicalSpaces[Wrap(spaceIndex, PhysicalSpaces.Length)];
        var spec = new ExperimentConditionSpec
        {
            participantId = string.IsNullOrWhiteSpace(participantId) ? "P001" : participantId,
            condition = condition,
            taskId = taskId,
            physicalSpaceId = spaceId,
            userTask = UserTaskFor(taskId),
            simulatedPhysicalPosition = PoseFor(spaceId),
            simulatedPhysicalYawDegrees = 0f
        };

        ApplyRoute(spec, taskId);
        return spec;
    }

    public static int TotalCombinations => Conditions.Length * TaskIds.Length * PhysicalSpaces.Length;

    private static void ApplyRoute(ExperimentConditionSpec spec, string taskId)
    {
        switch (taskId)
        {
            case "T1_overview":
                spec.startExhibitId = "E1";
                spec.semanticNextTargetId = "E2";
                spec.finalTargetId = "E2";
                spec.routeTargetIds = new[] { "E1", "E2" };
                break;
            case "T3_compare":
                spec.startExhibitId = "E1";
                spec.semanticNextTargetId = "E7";
                spec.finalTargetId = "E7";
                spec.routeTargetIds = new[] { "E1", "E4", "E7" };
                break;
            case "T4_next_step":
                spec.startExhibitId = "E1";
                spec.semanticNextTargetId = "E2";
                spec.finalTargetId = "E8";
                spec.routeTargetIds = new[] { "E1", "E2", "E5", "E8" };
                break;
            default:
                spec.startExhibitId = "E1";
                spec.semanticNextTargetId = "E5";
                spec.finalTargetId = "E5";
                spec.routeTargetIds = new[] { "E1", "E3", "E5" };
                break;
        }
    }

    private static string UserTaskFor(string taskId)
    {
        switch (taskId)
        {
            case "T1_overview":
                return "Get an overview of the gallery.";
            case "T3_compare":
                return "Compare related exhibits before choosing the next target.";
            case "T4_next_step":
                return "Continue to the next meaningful exhibit in the sequence.";
            default:
                return "Guide the participant to the key target exhibit.";
        }
    }

    private static Vector2 PoseFor(string physicalSpaceId)
    {
        switch (physicalSpaceId)
        {
            case "narrow_room":
                return new Vector2(1.8f, 4.3f);
            case "obstacle_room":
                return new Vector2(2.0f, 1.2f);
            case "l_shaped_room":
                return new Vector2(1.0f, 1.0f);
            default:
                return new Vector2(1.0f, 2.0f);
        }
    }

    private static int Wrap(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return ((index % count) + count) % count;
    }
}
