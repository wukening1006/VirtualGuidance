using System;
using System.Collections.Generic;
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
        "B1_semantic_only",
        "B2_semantic_only_continuous",
        "ours_rdw_grounded_topk"
    };

    /// <summary>当前正式实验允许启动的任务 ID 集合。</summary>
    public static readonly string[] TaskIds =
    {
        "T1_overview",
        "T2_main_target",
        "T3_compare",
        "T4_next_step"
    };

    /// <summary>兼容旧调用方的冻结任务集合；当前四个任务均已正式启用。</summary>
    public static readonly string[] FrozenTaskIds = Array.Empty<string>();

    /// <summary>Assets/Configs 中可切换的 physical space ID 集合。</summary>
    public static readonly string[] PhysicalSpaces =
    {
        "wide_room",
        "narrow_room",
        "obstacle_room",
        "l_shaped_room"
    };

    /// <summary>
    /// 按三个可循环索引生成实验条件，并填充任务路线与物理空间 ID。
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
            userTask = UserTaskFor(taskId)
        };

        ApplyRoute(spec, taskId);
        return spec;
    }

    /// <summary>
    /// 校验矩阵内所有正式任务的展品引用是否存在于唯一静态语义图。
    /// 此方法不改变矩阵内容，也不推导或补充路线。
    /// </summary>
    public static void ValidateSemanticReferences(StaticSceneSemanticGraph graph, IList<string> errors)
    {
        if (graph == null)
        {
            errors?.Add("experiment_matrix_graph_missing");
            return;
        }

        for (var taskIndex = 0; taskIndex < TaskIds.Length; taskIndex++)
        {
            var spec = Build(0, taskIndex, 0, "semantic_validation");
            if (!string.IsNullOrWhiteSpace(spec.startExhibitId))
            {
                ValidateExhibitReference(graph, errors, spec.startExhibitId, spec.taskId, "start_exhibit_id");
            }
            if (!string.IsNullOrWhiteSpace(spec.semanticNextTargetId))
            {
                ValidateExhibitReference(graph, errors, spec.semanticNextTargetId, spec.taskId, "semantic_next_target_id");
            }
            ValidateExhibitReference(graph, errors, spec.finalTargetId, spec.taskId, "final_target_id");

            foreach (var exhibitId in spec.routeTargetIds ?? Array.Empty<string>())
            {
                ValidateExhibitReference(graph, errors, exhibitId, spec.taskId, "route_target_id");
            }
        }
    }

    /// <summary>条件、任务与 physical space 的组合总数。</summary>
    public static int TotalCombinations => Conditions.Length * TaskIds.Length * PhysicalSpaces.Length;

    /// <summary>返回正式三条件的统一能力 Profile。</summary>
    public static ExperimentProfile GetProfile(string condition)
    {
        switch (condition)
        {
            case "B1_semantic_only":
                return new ExperimentProfile(
                    condition,
                    ExperimentPlannerMode.SemanticOnly,
                    false,
                    RdwPlanningMode.Legacy,
                    ExperimentResetMode.Normal);
            case "B2_semantic_only_continuous":
                return new ExperimentProfile(
                    condition,
                    ExperimentPlannerMode.SemanticOnly,
                    true,
                    RdwPlanningMode.Legacy,
                    ExperimentResetMode.Normal);
            case "ours_rdw_grounded_topk":
                return new ExperimentProfile(
                    condition,
                    ExperimentPlannerMode.RdwGroundedTopK,
                    true,
                    RdwPlanningMode.RdwConstrained,
                    ExperimentResetMode.SemanticContextualized);
            default:
                return null;
        }
    }

    /// <summary>
    /// 获取指定 Condition 的冻结能力策略；未知值按最保守的全部禁用策略处理。
    /// </summary>
    public static ExperimentConditionPolicy GetPolicy(string condition)
    {
        switch (condition)
        {
            case "B1_semantic_only":
                return Policy(condition, false, false, ConditionCapabilityMode.Disabled, false, false, false, true);
            case "B2_semantic_only_continuous":
                // B1/B2 仅允许 Continuous RDW 开关不同，不能额外开启 Guidance Rotation Gain。
                return Policy(condition, false, false, ConditionCapabilityMode.Disabled, false, false, false, true);
            case "ours_rdw_grounded_topk":
                return Policy(condition, true, true, ConditionCapabilityMode.Enabled, true, true, true, true);
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
        // 正式任务不预设起点或静态路线；首个目标由 Planner 从当前实时位置选择。
        spec.startExhibitId = string.Empty;
        spec.semanticNextTargetId = string.Empty;
        spec.finalTargetId = FinalTargetFor(taskId);
        spec.minimumIntermediateVisits = 3;
        spec.maximumIntermediateVisits = 3;
        spec.routeTargetIds = Array.Empty<string>();
    }

    private static string UserTaskFor(string taskId)
    {
        switch (taskId)
        {
            case "T1_overview":
                return "从人物、礼仪与自然象征入手，逐步建立对古蜀祭祀与精神世界的整体认识，最后抵达古蜀祭祀世界。";
            case "T2_main_target":
                return "沿着自然意象与宇宙观线索探索相关展品，理解古蜀人如何表达天地与自然，最后抵达青铜神树。";
            case "T3_compare":
                return "沿着人物形象与身份表达的线索进行比较，关注青铜、覆金以及仪式角色之间的差异，最后抵达戴金面罩青铜人头像。";
            case "T4_next_step":
                return "从普通人物形象逐步走向夸张、神圣的人物表达，关注面部特征与祭祀语境的变化，最后抵达青铜纵目面具。";
            default:
                return string.Empty;
        }
    }

    /// <summary>返回正式任务固定的最终目标展品 ID。</summary>
    public static string FinalTargetFor(string taskId)
    {
        switch (taskId)
        {
            case "T1_overview": return "E8";
            case "T2_main_target": return "E5";
            case "T3_compare": return "E6";
            case "T4_next_step": return "E3";
            default: return string.Empty;
        }
    }

    /// <summary>检查任务是否属于当前正式实验允许启动的四个正式任务。</summary>
    public static bool IsFormalTaskSupported(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        foreach (var formalTaskId in TaskIds)
        {
            if (string.Equals(taskId, formalTaskId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void ValidateExhibitReference(
        StaticSceneSemanticGraph graph,
        IList<string> errors,
        string exhibitId,
        string taskId,
        string field)
    {
        if (!string.IsNullOrWhiteSpace(exhibitId) && !graph.TryGetExhibit(exhibitId, out _))
        {
            errors?.Add(
                "unknown_exhibit_reference:experiment_matrix." + taskId + "." + field + ":" +
                (exhibitId ?? string.Empty));
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
