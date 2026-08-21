using System;
using UnityEngine;

[Serializable]
/// <summary>
/// 汇总一次实验运行所需的参与者、条件、任务路线和物理空间。
/// 配置由 ExperimentConditionMatrix 创建，并提供给会话、任务和空间模块。
/// </summary>
public sealed class ExperimentConditionSpec
{
    /// <summary>参与者 ID，默认 P001，仅用于开发和调试。</summary>
    public string participantId = "P001";
    /// <summary>实验条件标签，必须与后端和日志分析约定一致。</summary>
    public string condition = "B1_semantic_only";
    /// <summary>任务 ID，必须存在于 ExperimentConditionMatrix.TaskIds。</summary>
    public string taskId = "T2_main_target";
    /// <summary>physical space 配置 ID。</summary>
    public string physicalSpaceId = "narrow_room";
    /// <summary>任务开始时的展品 ID。</summary>
    public string startExhibitId = "E1";
    /// <summary>初次请求建议使用的语义目标 ID。</summary>
    public string semanticNextTargetId = string.Empty;
    /// <summary>任务不可变的最终目标展品 ID。</summary>
    public string finalTargetId = "E5";
    /// <summary>提供给后端的自然语言任务描述。</summary>
    public string userTask;
    /// <summary>最终目标解锁前至少需要到达的中间展品数量。</summary>
    [Min(0)] public int minimumIntermediateVisits = 3;
    /// <summary>达到后必须前往最终目标的中间展品数量上限。</summary>
    [Min(1)] public int maximumIntermediateVisits = 4;
    /// <summary>包含起点在内的有序路线展品 ID。</summary>
    public string[] routeTargetIds = { "E1" };
    /// <summary>
    /// 转换为任务控制器使用的定义，并复制路线数组以隔离后续修改。
    /// </summary>
    /// <returns>不包含物理空间输入的任务定义。</returns>
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
            minimumIntermediateVisits = minimumIntermediateVisits,
            maximumIntermediateVisits = maximumIntermediateVisits,
            maxConsecutiveDetours = 2,
            routeTargetIds = routeTargetIds != null ? (string[])routeTargetIds.Clone() : Array.Empty<string>()
        };
    }
}
