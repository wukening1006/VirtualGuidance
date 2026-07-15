using System;

[Serializable]
/// <summary>
/// 定义一次导览任务的实验身份、起终点和有序路线，由任务控制器读取。
/// ID 与 condition 值必须和后端 schema、实验矩阵及展品元数据保持一致。
/// </summary>
public sealed class GuidanceTaskDefinition
{
    /// <summary>任务 ID，例如 T2_main_target。</summary>
    public string taskId = "T2_main_target";
    /// <summary>参与者 ID，用于请求追踪和实验日志。</summary>
    public string participantId = "P001";
    /// <summary>实验条件标签，例如 ours_full。</summary>
    public string condition = "ours_full";
    /// <summary>提供给后端的可选自然语言任务描述。</summary>
    public string userTask;
    /// <summary>任务开始时视为已到达的首个展品 ID。</summary>
    public string startExhibitId = "E1";
    /// <summary>任务预期结束的展品 ID。</summary>
    public string finalTargetId = "E5";
    /// <summary>包含起点在内的有序展品 ID；控制器按数组顺序推进。</summary>
    public string[] routeTargetIds = { "E1", "E3", "E5" };
}
