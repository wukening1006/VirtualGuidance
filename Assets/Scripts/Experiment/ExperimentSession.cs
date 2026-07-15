using System;

[Serializable]
/// <summary>
/// 保存一次实验运行的稳定身份与开始时间，由 ExperimentLogger 持有并写入每条日志。
/// 会话字段在创建后作为本次日志的回退上下文使用。
/// </summary>
public sealed class ExperimentSession
{
    /// <summary>由参与者、条件、空间、任务及 UTC 时间组成的文件安全 ID。</summary>
    public string sessionId;
    /// <summary>本次会话的参与者 ID。</summary>
    public string participantId;
    /// <summary>本次会话的实验条件标签。</summary>
    public string condition;
    /// <summary>本次会话的任务 ID。</summary>
    public string taskId;
    /// <summary>本次会话的 physical space ID。</summary>
    public string physicalSpaceId;
    /// <summary>ISO 8601 round-trip 格式的 UTC 开始时间。</summary>
    public string startedAtUtc;

    /// <summary>会话是否已生成可用于日志关联的 ID。</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(sessionId);

    /// <summary>
    /// 从当前实验条件创建会话快照，并以 UTC 秒级时间生成 sessionId。
    /// </summary>
    /// <returns>可供日志模块使用的活动会话。</returns>
    public static ExperimentSession Start(ExperimentConditionSpec spec)
    {
        var timestamp = DateTime.UtcNow;
        return new ExperimentSession
        {
            sessionId = $"{Safe(spec.participantId)}_{Safe(spec.condition)}_{Safe(spec.physicalSpaceId)}_{Safe(spec.taskId)}_{timestamp:yyyyMMdd_HHmmss}",
            participantId = spec.participantId,
            condition = spec.condition,
            taskId = spec.taskId,
            physicalSpaceId = spec.physicalSpaceId,
            startedAtUtc = timestamp.ToString("o")
        };
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
