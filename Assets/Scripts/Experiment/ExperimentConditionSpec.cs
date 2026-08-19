using System;
using UnityEngine;

[Serializable]
/// <summary>
/// 汇总一次实验运行所需的参与者、条件、任务路线和模拟物理位姿。
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
    /// <summary>兼容旧路线字段；正式三条件不固定最终展品。</summary>
    public string finalTargetId = string.Empty;
    /// <summary>提供给后端的自然语言任务描述。</summary>
    public string userTask;
    /// <summary>静态语义图中的主题目标 ID。</summary>
    public string themeGoalId = "TH_RITUAL";
    /// <summary>完成主题目标所需的最小覆盖比例。</summary>
    [Range(0f, 1f)] public float themeCoverageThreshold = 0.6f;
    /// <summary>主题导览允许执行的最大展品步数。</summary>
    [Min(1)] public int maxThemeSteps = 8;
    /// <summary>包含起点在内的有序路线展品 ID。</summary>
    public string[] routeTargetIds = { "E1" };
    /// <summary>模拟用户在 physical XZ 平面的位置，单位：米。</summary>
    public Vector2 simulatedPhysicalPosition = new Vector2(1.8f, 4.3f);
    /// <summary>模拟用户以 physical +Z 为 0 度的水平朝向，单位：度。</summary>
    public float simulatedPhysicalYawDegrees;

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
            themeGoalId = themeGoalId,
            themeCoverageThreshold = themeCoverageThreshold,
            maxThemeSteps = maxThemeSteps,
            startExhibitId = startExhibitId,
            finalTargetId = finalTargetId,
            maxConsecutiveDetours = 2,
            routeTargetIds = routeTargetIds != null ? (string[])routeTargetIds.Clone() : Array.Empty<string>()
        };
    }
}
