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
    /// <summary>实验条件标签；正式运行默认使用 Semantic-only B1。</summary>
    public string condition = "B1_semantic_only";
    /// <summary>提供给后端的可选自然语言任务描述。</summary>
    public string userTask;
    /// <summary>静态语义图中的主题目标 ID。</summary>
    public string themeGoalId = "TH_RITUAL";
    /// <summary>主题完成所需的最小覆盖比例。</summary>
    public float themeCoverageThreshold = 0.6f;
    /// <summary>主题导览允许执行的最大展品步数。</summary>
    public int maxThemeSteps = 8;
    /// <summary>任务开始时视为已到达的首个展品 ID。</summary>
    public string startExhibitId = "E1";
    /// <summary>任务预期结束的展品 ID。</summary>
    public string finalTargetId = string.Empty;
    /// <summary>包含起点在内的有序展品 ID；控制器按数组顺序推进。</summary>
    public string[] routeTargetIds = { "E1" };
    /// <summary>返回原路线前允许连续使用的最大 NavigableDetour 数量。</summary>
    public int maxConsecutiveDetours = 2;
}
