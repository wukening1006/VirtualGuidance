using System;
using UnityEngine;

/// <summary>
/// 表示实验条件对某项能力的使用方式。
/// </summary>
public enum ConditionCapabilityMode
{
    /// <summary>该条件不使用此能力。</summary>
    Disabled,
    /// <summary>该条件正常启用此能力。</summary>
    Enabled,
    /// <summary>该条件使用固定脚本替代动态能力。</summary>
    Scripted
}

/// <summary>
/// 冻结单个实验 Condition 的能力边界，供 Unity 决策层、Gain 和日志共同读取。
/// </summary>
public sealed class ExperimentConditionPolicy
{
    /// <summary>当前策略契约版本。</summary>
    public const string CurrentVersion = "condition-policy-v1";

    /// <summary>Condition ID。</summary>
    public string Condition { get; }
    /// <summary>是否向决策策略提供 Raw Physical Data。</summary>
    public ConditionCapabilityMode UseRawPhysicalData { get; }
    /// <summary>是否向决策策略提供风险标签。</summary>
    public ConditionCapabilityMode UseRiskLabels { get; }
    /// <summary>是否使用 RDW Affordance。</summary>
    public ConditionCapabilityMode UseRdwAffordance { get; }
    /// <summary>是否在实验决策层提前筛选安全候选。</summary>
    public ConditionCapabilityMode UseSafeCandidateFilter { get; }
    /// <summary>是否允许语义重定向。</summary>
    public ConditionCapabilityMode UseSemanticRedirection { get; }
    /// <summary>是否启用 Rotation Gain。</summary>
    public ConditionCapabilityMode UseRotationGain { get; }
    /// <summary>是否启用模型输出 Verifier。</summary>
    public ConditionCapabilityMode UseModelVerifier { get; }

    /// <summary>
    /// 创建一份不可变 Condition 能力策略。
    /// </summary>
    public ExperimentConditionPolicy(
        string condition,
        ConditionCapabilityMode rawPhysicalData,
        ConditionCapabilityMode riskLabels,
        ConditionCapabilityMode rdwAffordance,
        ConditionCapabilityMode safeCandidateFilter,
        ConditionCapabilityMode semanticRedirection,
        ConditionCapabilityMode rotationGain,
        ConditionCapabilityMode modelVerifier)
    {
        Condition = condition;
        UseRawPhysicalData = rawPhysicalData;
        UseRiskLabels = riskLabels;
        UseRdwAffordance = rdwAffordance;
        UseSafeCandidateFilter = safeCandidateFilter;
        UseSemanticRedirection = semanticRedirection;
        UseRotationGain = rotationGain;
        UseModelVerifier = modelVerifier;
    }
}

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

    /// <summary>
    /// 获取指定 Condition 的冻结能力策略；未知值按最保守的全部禁用策略处理。
    /// </summary>
    public static ExperimentConditionPolicy GetPolicy(string condition)
    {
        switch (condition)
        {
            case "B1_mllm_only":
                return Policy(condition, false, false, ConditionCapabilityMode.Disabled, false, false, false, false);
            case "B2_raw_physical_data":
                return Policy(condition, true, false, ConditionCapabilityMode.Disabled, false, false, false, false);
            case "B3_risk_labels":
                return Policy(condition, false, true, ConditionCapabilityMode.Disabled, false, false, false, false);
            case "B4_scripted_reset":
                return Policy(condition, false, true, ConditionCapabilityMode.Scripted, false, false, false, false);
            case "B5_ours_without_verifier":
                return Policy(condition, true, true, ConditionCapabilityMode.Enabled, true, true, true, false);
            case "ours_full":
                return Policy(condition, true, true, ConditionCapabilityMode.Enabled, true, true, true, true);
            default:
                return Policy(string.IsNullOrWhiteSpace(condition) ? "unknown" : condition, false, false, ConditionCapabilityMode.Disabled, false, false, false, false);
        }
    }

    private static ExperimentConditionPolicy Policy(
        string condition,
        bool enabledRaw,
        bool enabledRisk,
        ConditionCapabilityMode rdwAffordance,
        bool safeCandidateFilter,
        bool semanticRedirection,
        bool rotationGain,
        bool modelVerifier)
    {
        return new ExperimentConditionPolicy(
            condition,
            enabledRaw ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled,
            enabledRisk ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled,
            rdwAffordance,
            safeCandidateFilter ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled,
            semanticRedirection ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled,
            rotationGain ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled,
            modelVerifier ? ConditionCapabilityMode.Enabled : ConditionCapabilityMode.Disabled);
    }

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
                return new Vector2(1.0f, 2.5f);
            case "obstacle_room":
                return new Vector2(1.0f, 1.0f);
            case "l_shaped_room":
                return new Vector2(1.0f, 1.0f);
            default:
                return new Vector2(2.5f, 2.5f);
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
