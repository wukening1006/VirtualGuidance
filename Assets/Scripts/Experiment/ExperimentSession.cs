using System;
using System.Diagnostics;

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
    /// <summary>ISO 8601 round-trip 格式的 UTC 结束时间。</summary>
    public string endedAtUtc;
    /// <summary>会话结束原因。</summary>
    public string endReason;
    /// <summary>研究员暂停次数。</summary>
    public int pauseCount;
    /// <summary>累计暂停秒数。</summary>
    public double totalPausedSeconds;
    /// <summary>显式活动标记；结束后即使 sessionId 保留也为 false。</summary>
    public bool isActive;

    [NonSerialized] private DateTime? pauseStartedAtUtc;
    [NonSerialized] private long startedTimestamp;

    /// <summary>会话是否已生成可用于日志关联的 ID。</summary>
    public bool IsActive => isActive && !string.IsNullOrWhiteSpace(sessionId);

    /// <summary>从 Session 开始到当前时刻的单调时间，单位：毫秒。</summary>
    public long ElapsedMilliseconds
    {
        get
        {
            if (startedTimestamp <= 0)
            {
                return 0;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
            return Math.Max(0L, elapsedTicks * 1000L / Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// 从当前实验条件创建会话快照，并以 UTC 毫秒和随机后缀生成唯一 sessionId。
    /// </summary>
    /// <returns>可供日志模块使用的活动会话。</returns>
    public static ExperimentSession Start(ExperimentConditionSpec spec)
    {
        var timestamp = DateTime.UtcNow;
        return new ExperimentSession
        {
            sessionId = $"{Safe(spec.participantId)}_{Safe(spec.condition)}_{Safe(spec.physicalSpaceId)}_{Safe(spec.taskId)}_{timestamp:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
            participantId = spec.participantId,
            condition = spec.condition,
            taskId = spec.taskId,
            physicalSpaceId = spec.physicalSpaceId,
            startedAtUtc = timestamp.ToString("o"),
            startedTimestamp = Stopwatch.GetTimestamp(),
            isActive = true
        };
    }

    /// <summary>开始一次暂停；重复调用不会重复计数。</summary>
    public void BeginPause()
    {
        if (!IsActive || pauseStartedAtUtc.HasValue)
        {
            return;
        }

        pauseStartedAtUtc = DateTime.UtcNow;
        pauseCount++;
    }

    /// <summary>结束当前暂停并累计持续时间。</summary>
    public void EndPause()
    {
        if (!pauseStartedAtUtc.HasValue)
        {
            return;
        }

        totalPausedSeconds += Math.Max(0d, (DateTime.UtcNow - pauseStartedAtUtc.Value).TotalSeconds);
        pauseStartedAtUtc = null;
    }

    /// <summary>关闭会话并记录结束信息。</summary>
    public void End(string reason)
    {
        if (!IsActive)
        {
            return;
        }

        EndPause();
        endedAtUtc = DateTime.UtcNow.ToString("o");
        endReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        isActive = false;
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
