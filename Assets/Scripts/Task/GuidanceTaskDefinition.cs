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
    /// <summary>提供给模型理解用户意图的自然语言任务描述。</summary>
    public string userTask = "我想参观青铜神树。在到达它之前，请带我浏览几件有助于理解三星堆文化的相关展品，并说明它们与目标展品之间的联系，最后带我抵达青铜神树。";
    /// <summary>任务开始时视为已到达的首个展品 ID。</summary>
    public string startExhibitId = string.Empty;
    /// <summary>任务预期结束的展品 ID。</summary>
    public string finalTargetId = "E5";
    /// <summary>最终目标解锁前至少需要到达的中间展品数量。</summary>
    public int minimumIntermediateVisits = 3;
    /// <summary>达到后必须前往最终目标的中间展品数量上限。</summary>
    public int maximumIntermediateVisits = 3;
    /// <summary>包含起点在内的有序展品 ID；控制器按数组顺序推进。</summary>
    public string[] routeTargetIds = Array.Empty<string>();
    /// <summary>返回原路线前允许连续使用的最大 NavigableDetour 数量。</summary>
    public int maxConsecutiveDetours = 2;
}
