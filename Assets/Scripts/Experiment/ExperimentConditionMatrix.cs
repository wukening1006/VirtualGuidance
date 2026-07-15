using System;
using UnityEngine;

/// <summary>
/// 定义实验条件、任务和 physical space 的笛卡尔组合，并生成可复现的条件配置。
/// Dashboard 使用循环索引遍历矩阵，生成结果提供给任务、风险输入和日志模块。
/// </summary>
public static class ExperimentConditionMatrix
{
    /// <summary>后端与日志共用的实验条件标签集合。</summary>
    public static readonly string[] Conditions =
    {
        "B1_mllm_only",
        "B2_raw_physical_data",
        "B3_risk_labels",
        "B4_scripted_reset",
        "B5_ours_without_verifier",
        "ours_full"
    };

    /// <summary>实验覆盖的任务 ID 集合。</summary>
    public static readonly string[] TaskIds =
    {
        "T1_overview",
        "T2_main_target",
        "T3_compare",
        "T4_next_step"
    };

    /// <summary>Assets/Configs 中可切换的 physical space ID 集合。</summary>
    public static readonly string[] PhysicalSpaces =
    {
        "wide_room",
        "narrow_room",
        "obstacle_room",
        "l_shaped_room"
    };

    /// <summary>
    /// 按三个可循环索引生成实验条件，并填充任务路线和空间对应的模拟物理起点。
    /// </summary>
    /// <returns>可直接应用到实验会话的独立条件配置。</returns>
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

    /// <summary>条件、任务与 physical space 的组合总数。</summary>
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
        // 二维分量对应 physical tracking space 的 XZ 平面，数值单位为米。
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

        // 双重取模让 Dashboard 的负索引与正向溢出都能稳定循环。
        return ((index % count) + count) % count;
    }
}
